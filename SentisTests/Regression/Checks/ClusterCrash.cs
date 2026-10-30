using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Big ships crashing in many clusters at once, nothing detached (SentisClusters runs each cluster's entities on a
    /// thread of its own; the contacts, the blocks broken off, the grids split and the deformation all happen there).
    /// The Spitfire (welder_perf's ship, 2757 blocks), dampeners off:
    /// <list type="bullet">
    /// <item><see cref="Pairs"/> pairs in space 8 km apart along the world's axis, each a cluster of its own: the two
    /// ships of a pair nose to nose, flown at each other at the large ship's top speed;</item>
    /// <item><see cref="Falls"/> ships over the planet's sites (a cluster each), thrown down at the top speed.</item>
    /// </list>
    /// Every ship is given its speed in one frame, and placed so that all meet their pair or the ground in the same
    /// moment (<see cref="ContactSeconds"/> later). A fake player by every place keeps it stepped. Passes when the server
    /// went through it: no exception in the clusters' threads, no error or fatal in the log, every pair met (blocks
    /// broken or the speed gone) and every falling ship ended on the ground or broke up on it, not under it. The frames
    /// are noted. Focus: <c>pairs=5 falls=5</c>. Not part of the regression: the load is its own.
    /// </summary>
    public sealed class ClusterCrashCheck : RegressionCheck
    {
        public const string ScenarioName = "cluster_crash";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;
        protected override bool NeedsDamage => true;
        protected override bool NeedsFallRestore => true;
        private const string Spitfire = "SentisTests.Resources.Spitfire.xml";
        private const double Apart = 8000;
        private const double ContactSeconds = 2;
        private int Pairs = 5;
        private int Falls = 5;

        private sealed class Crash
        {
            public string Name;
            public List<MyCubeGrid> Ships = new List<MyCubeGrid>();
            public List<int> BlocksBefore = new List<int>();
            public Site Planet;
            public Vector3D At;
        }

        protected override IEnumerator Check()
        {
            Check(Integrations.IsLoaded("SentisClusters") || ClusterStressCheck.Command("!clusters status").Any(), "SentisClusters is not loaded");
            var focus = TestRunner.Focus ?? "";
            int Arg(string name, int otherwise) => Regex.Match(focus, name + @"=(\d+)") is var m && m.Success ? int.Parse(m.Groups[1].Value) : otherwise;
            Pairs = Math.Max(0, Arg("pairs", 5));
            Falls = Math.Max(0, Arg("falls", 5));
            var speed = MyGridPhysics.LargeShipMaxLinearVelocity();
            var before = ClusterStressCheck.Exceptions();
            var errorsAt = ClusterStressCheck.LogMark();
            var crashes = new List<Crash>();

            // the pairs: nose to nose along the site's forward, the noses 2 * speed * ContactSeconds apart
            for (var k = 0; k < Pairs; k++)
            {
                var crash = new Crash { Name = "pair " + k, At = Site.Origin + Site.Up * (Apart * k) };
                crashes.Add(crash);
                if (k == 0) yield return Arrive(crash.At + Site.Right * 400);
                else yield return Join(crash.At + Site.Right * 400, i => { });
                var a = Spawn("crash-" + k + "a", crash.At, Site.Forward, Site.Up);
                var b = Spawn("crash-" + k + "b", crash.At, -Site.Forward, Site.Up);
                // each moved back so that its nose (the box's end along its flight) is speed * ContactSeconds off the middle
                Place(a, crash.At, Site.Forward, speed * ContactSeconds);
                Place(b, crash.At, -Site.Forward, speed * ContactSeconds);
                crash.Ships.Add(a);
                crash.Ships.Add(b);
                yield return null;
            }
            // the falls: over the planet's sites, the ship's lowest point speed * ContactSeconds over the ground
            for (var k = 0; k < Falls; k++)
            {
                var site = Sites.Planet(k);
                var ground = site.Ground(site.Origin);
                var crash = new Crash { Name = "fall " + k, At = ground, Planet = site };
                crashes.Add(crash);
                if (crashes.Count == 1) yield return Arrive(ground + site.Right * 300 + site.Up * 2);
                else yield return Join(ground + site.Right * 300 + site.Up * 2, i => { });
                var ship = Spawn("crash-fall-" + k, ground + site.Up * 500, site.Forward, site.Up);
                Place(ship, ground, -site.Up, speed * ContactSeconds);
                crash.Ships.Add(ship);
                yield return null;
            }
            foreach (var crash in crashes) crash.BlocksBefore.AddRange(crash.Ships.Select(s => s.BlocksCount));
            var clusters = crashes.Select(c => ClusterId(c.Ships[0])).ToList();
            Note($"{Pairs} pairs and {Falls} falls of the Spitfire ({crashes[0].Ships[0].BlocksCount} blocks) at {F(speed)} m/s, in clusters {string.Join(", ", clusters)}");
            Check(clusters.Distinct().Count() == crashes.Count, "the crashes are not each in a cluster of their own: " + string.Join(", ", clusters));
            yield return Seconds(1);

            // all at once: one frame gives every ship its speed (again the next frames: the game's own first steps of a
            // fresh grid may take some off)
            TickMetrics.Take();
            for (var frame = 0; frame < 3; frame++)
            {
                foreach (var crash in crashes)
                    if (crash.Planet != null) crash.Ships[0].Physics.LinearVelocity = (Vector3)(-crash.Planet.Up * speed);
                    else
                    {
                        crash.Ships[0].Physics.LinearVelocity = (Vector3)(Site.Forward * speed);
                        crash.Ships[1].Physics.LinearVelocity = (Vector3)(-Site.Forward * speed);
                    }
                yield return null;
            }
            yield return Seconds(ContactSeconds + 8);
            var impact = TickMetrics.Take();
            Note("the impacts' 10 s: " + impact.Format());
            yield return Seconds(10);

            var failures = new List<string>();
            var report = new List<string>();
            foreach (var crash in crashes)
            {
                var lost = crash.Ships.Select((s, i) => s.MarkedForClose ? crash.BlocksBefore[i] : crash.BlocksBefore[i] - s.BlocksCount).ToList();
                var speeds = crash.Ships.Select(s => s.MarkedForClose ? 0 : Motion.Speed(s)).ToList();
                var pieces = MyEntities.GetEntities().OfType<MyCubeGrid>().Count(g => !g.MarkedForClose && Vector3D.Distance(g.PositionComp.GetPosition(), crash.At) < 1500);
                var line = $"{crash.Name}: blocks lost {string.Join("/", lost)}, speed {string.Join("/", speeds.Select(v => F(v)))} m/s, {pieces} grids there";
                if (crash.Planet != null)
                {
                    var ship = crash.Ships[0];
                    var at = ship.PositionComp.WorldAABB.Center;
                    var over = ship.MarkedForClose ? 0 : Vector3D.Dot(at - crash.Planet.Ground(at), crash.Planet.Up);
                    line += $", {F(over)} m over the ground";
                    if (!ship.MarkedForClose && over < -5) failures.Add($"{crash.Name}: the ship is {F(-over)} m under the ground");
                    if (lost[0] == 0 && speeds[0] > speed * 0.5) failures.Add($"{crash.Name}: the ship never hit the ground ({F(speeds[0])} m/s, {F(over)} m over it)");
                }
                else if (lost.Sum() == 0 && speeds.Any(v => v > speed * 0.8))
                    failures.Add($"{crash.Name}: the ships never met (no block lost, {string.Join("/", speeds.Select(v => F(v)))} m/s)");
                report.Add(line);
                Note(line);
            }

            var thrown = ClusterStressCheck.Exceptions() - before;
            if (thrown > 0) failures.Add(thrown + " exceptions in the clusters' threads");
            var errors = ClusterStressCheck.LogSince(errorsAt).Where(l => l.Contains("[ERROR]") || l.Contains("[FATAL]"))
                .Where(l => !l.Contains("urlacl") && !l.Contains("SentisAnticheat")).ToList();
            if (errors.Count > 0) failures.Add(errors.Count + " errors in the log: " + string.Join(" | ", errors.Take(3).Select(l => l.Length > 300 ? l.Substring(0, 300) : l)));
            Check(failures.Count == 0, string.Join(" || ", failures.Take(8)) + " ||| " + string.Join("; ", report));
            Result($"{Pairs} pairs head on and {Falls} falls at {F(speed)} m/s at once, nothing detached: exceptions 0, errors 0; impacts {impact.Format()}; " +
                   string.Join("; ", report));
        }

        private MyCubeGrid Spawn(string name, Vector3D at, Vector3D forward, Vector3D up)
        {
            var ob = WorldApi.LoadGridTemplate(Spitfire, WorldApi.EntityPrefix + name, at, (Vector3)forward, (Vector3)up);
            ob.DampenersEnabled = false;
            var ship = WorldApi.SpawnGrid(ob);
            Track(ship);
            return ship;
        }

        /// <summary>The ship moved along <paramref name="flight"/> so that its box's front end is <paramref name="gap"/> short of the point.</summary>
        private static void Place(MyCubeGrid ship, Vector3D point, Vector3D flight, double gap)
        {
            var box = ship.PositionComp.WorldAABB;
            var corners = box.GetCorners();
            var front = corners.Max(c => Vector3D.Dot(c - point, flight));
            var matrix = ship.WorldMatrix;
            matrix.Translation -= flight * (front + gap);
            ship.PositionComp.SetWorldMatrix(ref matrix);
        }
    }
}
