using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using SentisTests.Core;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Ray casts from one cluster of the physics into another with SentisClusters' detaching. A source S and a target T,
    /// an armour wall 5 by 5 blocks, 8 km apart along the world's X (two clusters). Every frame <see cref="RaysAFrame"/>
    /// rays of MyPhysics.CastRay - what a camera's Raycast, a script's scan and the game thread's other long casts use -
    /// go from S to points of the wall; hits on T are counted, phase by phase (<see cref="PhaseSeconds"/> s): T attached,
    /// T detached (SentisClusters' <c>!clusters detach</c>), T detached with a heavy load by it (<see cref="PileSize"/>
    /// grids knocking about: its world's step takes time), the load with T attached again. While a detached world steps,
    /// SentisClusters hides it from the queries of the other threads - those rays go through T. The check tells the share
    /// that hit and fails when a phase with T attached or T detached and light hits under <see cref="MinShare"/>; the heavy
    /// detached phase is told only.
    /// Not part of the regression: it switches detaching.
    /// </summary>
    public sealed class RaycastDetachCheck : RegressionCheck
    {
        public const string ScenarioName = "raycast_detach";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 600;
        private const int PhaseSeconds = 20;
        private const double Apart = 8000;
        private const int RaysAFrame = 20;
        private const int PileSize = 60;
        private const double MinShare = 0.99;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, -15));
            var source = Build("ray-source", Site.Origin, false, new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0));
            var wallParts = new List<Part>();
            for (var y = 0; y < 5; y++)
                for (var z = 0; z < 5; z++)
                    wallParts.Add(new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, y, z));
            var target = Build("ray-target", Site.At(Apart, 0, 0), false, wallParts);
            target.DestructibleBlocks = false;
            yield return Seconds(2);
            Check(Command("!clusters set Detach on").Any(), "SentisClusters did not answer (!clusters set)");

            var pile = new List<MyCubeGrid>();
            var report = new List<string>();
            var failures = new List<string>();
            var rng = new Random(7);
            foreach (var (name, detach, heavy) in new[] { ("T attached", false, false), ("T detached", true, false), ("T detached, heavy", true, true), ("T attached, heavy", false, true) })
            {
                Command("!clusters attach all");
                if (heavy && pile.Count == 0)
                {
                    // grids that keep knocking into each other by the target: its world's step takes time
                    var middle = target.PositionComp.WorldAABB.Center + Site.Forward * 150;
                    var size = Regex.Match(TestRunner.Focus ?? "", @"pile=(\d+)") is var m && m.Success ? int.Parse(m.Groups[1].Value) : PileSize;
                    for (var i = 0; i < size; i++)
                    {
                        var at = middle + new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) * (30 + size / 3.0);
                        var cube = Build("ray-pile-" + i, at, false, new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeHeavyBlockArmorBlock", 1, 0, 0),
                            new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 1, 0), new Part("CubeBlock/LargeHeavyBlockArmorBlock", 1, 1, 0));
                        cube.DestructibleBlocks = false;
                        pile.Add(cube);
                        if (i % 10 == 9) yield return null;
                    }
                    Note("a pile of " + pile.Count + " grids by the target");
                }
                if (detach)
                {
                    var at = target.PositionComp.GetPosition();
                    var answer = string.Join(" ", Command($"!clusters detach {at.X:F0} {at.Y:F0} {at.Z:F0}"));
                    Check(answer.Contains("detached"), name + ": " + answer);
                }
                yield return Seconds(2);
                long rays = 0, hits = 0, other = 0, none = 0;
                var castTicks = 0L;
                var frames = 0;
                var lastPull = Stopwatch.StartNew();
                var phase = Stopwatch.StartNew();
                while (phase.Elapsed.TotalSeconds < PhaseSeconds)
                {
                    if (heavy && lastPull.Elapsed.TotalSeconds >= 1)
                    {
                        lastPull.Restart();
                        var middle = target.PositionComp.WorldAABB.Center + Site.Forward * 150;
                        foreach (var g in pile)
                            if (!g.MarkedForClose && g.Physics != null)
                            {
                                var towards = middle - g.PositionComp.GetPosition();
                                g.Physics.LinearVelocity = (Vector3)(Vector3D.Normalize(towards) * 8 + new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) * 6);
                            }
                    }
                    var from = source.PositionComp.WorldAABB.Center;
                    var wall = target.PositionComp.WorldAABB;
                    var hitList = new List<MyPhysics.HitInfo>();
                    for (var r = 0; r < RaysAFrame; r++)
                    {
                        var point = wall.Center + new Vector3D(0, (rng.NextDouble() - 0.5) * wall.Size.Y * 0.8, (rng.NextDouble() - 0.5) * wall.Size.Z * 0.8);
                        var to = from + Vector3D.Normalize(point - from) * (Vector3D.Distance(from, point) + 50);
                        hitList.Clear();
                        var started = Stopwatch.GetTimestamp();
                        MyPhysics.CastRay(from, to, hitList);
                        castTicks += Stopwatch.GetTimestamp() - started;
                        rays++;
                        var hit = hitList.Select(h => h.HkHitInfo.GetHitEntity() as MyEntity).FirstOrDefault(e => e != null && e.GetTopMostParent() != source);
                        if (hit == null) none++;
                        else if (hit.GetTopMostParent() == target) hits++;
                        else other++;
                    }
                    frames++;
                    yield return null;
                }
                var share = rays == 0 ? 0 : hits / (double)rays;
                var line = name + ": " + hits + " of " + rays + " rays hit T (" + (share * 100).ToString("F1") + "%), " + none + " hit nothing, " + other + " hit something else; " +
                           (castTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, rays) * 1000).ToString("F1") + " us a ray; " + frames + " frames; " + Detached();
                report.Add(line);
                Note(line);
                // into a heavy detached cluster from outside, rays may go through while its world steps: told, not failed
                // (accepted: weapons reach 2 km, clusters part beyond 5 km). Rays into the others must all hit
                if (share < MinShare && !(detach && heavy)) failures.Add(name + ": " + (share * 100).ToString("F1") + "% of the rays hit T");
            }
            Command("!clusters attach all");
            foreach (var g in pile) if (!g.MarkedForClose) g.Close();
            Check(failures.Count == 0, string.Join(" | ", failures) + " || " + string.Join(" | ", report));
            Result(string.Join(" | ", report));
        }

        /// <summary>The detached clusters now and their tick: SentisClusters' status line of them.</summary>
        private static string Detached()
        {
            var status = string.Join("\n", Command("!clusters status"));
            var m = Regex.Match(status, @"detached clusters: [^\n]*");
            return m.Success ? m.Value : "no detached cluster";
        }

        private static List<string> Command(string text)
        {
            var answers = new List<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            commands?.HandleCommandFromServer(text, message => answers.Add(message.Message));
            return answers;
        }

        public override void Cleanup()
        {
            try { Command("!clusters attach all"); }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
