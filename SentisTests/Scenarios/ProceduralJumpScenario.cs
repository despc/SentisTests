using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A player jumping into space where nobody has been: the procedural asteroids round the new spot are made then.
    /// A fake client with a character is moved a few hundred kilometres at a time (a jump drive does the same), and
    /// the longest frame after each jump is measured, with the asteroids that appeared.
    ///
    /// A measure, not a check of behaviour: it fails only when there is nothing to measure (procedural asteroids off).
    /// </summary>
    public sealed class ProceduralJumpScenario : TestScenario
    {
        public const string ScenarioName = "procedural_jump";
        private const int Jumps = 6;
        private const double JumpM = 300000;
        private const double AfterJumpSeconds = 8;
        private static readonly Vector3D Start = new Vector3D(3000000, 1500000, -2000000);
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            Check(MyProceduralWorldGenerator.Static != null && MyProceduralWorldGenerator.Static.Enabled && MySession.Static.Settings.ProceduralDensity > 0,
                "procedural asteroids are off in this world");
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            // a random direction each run: space nobody has been to
            var rng = new Random();
            var direction = Vector3D.Normalize(new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5));
            var at = Start + direction * rng.Next(0, 1000000);
            FakeClients.Add(1, Network, index => (at, 0, 0), withCharacters: true);
            var settle = WaitForSeconds(AfterJumpSeconds, "the first spot");
            while (settle.MoveNext()) yield return settle.Current;
            Check(FakeClients.HasLiveCharacter(0), "the fake client has no character");

            var results = new System.Collections.Generic.List<string>();
            var worstAll = 0.0;
            for (var jump = 1; jump <= Jumps; jump++)
            {
                at += direction * JumpM;
                var before = MySession.Static.VoxelMaps.Instances.Count;
                FakeClients.MoveTo(0, at);
                var worst = 0.0;
                var watch = Stopwatch.StartNew();
                var last = watch.Elapsed.TotalMilliseconds;
                while (watch.Elapsed.TotalSeconds < AfterJumpSeconds)
                {
                    yield return null;
                    var now = watch.Elapsed.TotalMilliseconds;
                    worst = Math.Max(worst, now - last);
                    last = now;
                }
                var asteroids = MySession.Static.VoxelMaps.Instances.Count - before;
                worstAll = Math.Max(worstAll, worst);
                results.Add($"{worst:0} ms (+{asteroids} voxel maps)");
                Note($"jump {jump}: the longest frame {worst:0.0} ms, {asteroids} voxel maps more");
            }

            Note("PROCEDURAL JUMP RESULT | " + Jumps + " jumps of " + JumpM / 1000 + " km | worst frame " + worstAll.ToString("0") +
                 " ms | per jump: " + string.Join(", ", results));
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);
        }
    }
}
