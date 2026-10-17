using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisOptimisations' timing of a physics cluster (PhysicsCost, the count its physics guard and SentisWatcher go
    /// by: now and then one cluster stepped alone with a stopwatch, then the others together) steps every cluster once a
    /// frame, no more and no less. Two ships in space far apart, each in a cluster of its own kept stepped by a fake
    /// player near it, coast at <see cref="Speed"/> m/s with nothing acting on them. The run goes first with the
    /// clusters' timing off, then on: a frame that stepped a cluster twice or not at all would move its ship 1/60 s more
    /// or less, and one in <c>MeasureGapFrames</c> (30) of those shows as 3% of the distance. Passes when the clusters
    /// were timed, each ship went the same distance a frame both ways, to <see cref="Tolerance"/>, and a frame with a
    /// cluster timed took less than <see cref="MaxExtraMs"/> more physics (by PhysicsLoadMonitor) than the others. The
    /// frames' cost both ways is noted. Then a third run with the monitor off on SO's page (<c>DisablePhysicsMonitor</c>):
    /// no cluster timed, the ships as with the timing off.
    /// </summary>
    public sealed class PhysicsClusterTimingScenario : TestScenario
    {
        public const string ScenarioName = "physics_cluster_timing";
        private const string Prefix = "pclt-";
        private const string Spitfire = "SentisTests.Resources.Spitfire.xml";
        private const float Speed = 50;
        private const int PhaseSeconds = 12;
        private const double Tolerance = 0.003;
        private const double MaxExtraMs = 3;

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<MyCubeGrid> _ships = new List<MyCubeGrid>();
        private FieldInfo _timingSwitch;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations");
            SkipUnless(assembly != null, "SentisOptimisations is not loaded");
            var cost = assembly.GetType("Optimizer.Optimizations.PhysicsCost", true);
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _timingSwitch = cost.GetField("ClusterTiming", any);
            var measured = cost.GetField("ClustersMeasured", any);
            Check(_timingSwitch != null && measured != null, "SentisOptimisations has no timing of the physics clusters");
            long Read(string name) => (long)(cost.GetField(name, any)?.GetValue(null) ?? throw new MissingFieldException("PhysicsCost", name));
            _config.Set("FreezerEnabled", false);

            // ------------------------------------------------------------- two places in space, far apart
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            var spots = new List<Vector3D>();
            for (var i = 0; i < 60 && spots.Count < 2; i++)
            {
                var probe = new Vector3D(-300000 - i * 25000, 260000 + i * 10000, -180000 + i * 20000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() > 1e-6) continue;
                if (planets.Any(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) < p.MaximumRadius + 10000)) continue;
                if (spots.Any(s => Vector3D.Distance(s, probe) < 150000)) continue;
                spots.Add(probe);
            }
            Check(spots.Count == 2, "no two places in space without gravity");

            FakeClients.RemoveAll();
            FakeClients.Add(2, new FakeClients.NetworkProfile { RttMs = 30 }, i => (spots[i], 0, 0), withCharacters: true);
            var forward = Vector3.Forward;
            var up = Vector3.Up;
            for (var i = 0; i < 2; i++)
            {
                var ob = WorldApi.LoadGridTemplate(Spitfire, WorldApi.EntityPrefix + Prefix + i, spots[i] + new Vector3D(150, 0, 0), forward, up);
                ob.DampenersEnabled = false;
                Sandbox.ModAPI.MyAPIGateway.Entities.RemapObjectBuilder(ob);
                _ships.Add(WorldApi.SpawnGrid(ob));
            }
            var arrive = WaitForSeconds(5, "the players and the ships are there");
            while (arrive.MoveNext()) yield return arrive.Current;
            Check(_ships.All(s => s.Physics != null && !s.IsStatic), "a ship has no dynamic physics");
            var worlds = _ships.Select(s => s.Physics.HavokWorld).ToList();
            Check(worlds[0] != null && worlds[1] != null && worlds[0] != worlds[1], "the two ships are not in two clusters");
            foreach (var ship in _ships)
                ship.Physics.LinearVelocity = Vector3.Right * Speed;
            var settle = WaitForSeconds(2, "the ships coast");
            while (settle.MoveNext()) yield return settle.Current;

            // ------------------------------------------------------------- off, then on: the distance a frame
            long ticksTimed0 = 0, framesTimed0 = 0, ticksPlain0 = 0, framesPlain0 = 0;
            var monitorOff = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType("SentisOptimisationsPlugin.MainConfig")?.GetProperty("DisablePhysicsMonitor") != null);
            var phases = monitorOff ? 3 : 2;
            var perFrame = new double[3][];
            var costs = new string[3];
            long count = 0, countOff = 0;
            for (var phase = 0; phase < phases; phase++)
            {
                var on = phase == 1;
                var off = phase == 2;
                _timingSwitch.SetValue(null, phase > 0);
                if (off) _config.Set("DisablePhysicsMonitor", true);
                yield return null;
                var before = (long)measured.GetValue(null);
                var frame0 = Sandbox.MySandboxGame.Static.SimulationFrameCounter;
                var at0 = _ships.Select(s => s.PositionComp.GetPosition()).ToList();
                TickMetrics.Take();
                if (on)
                {
                    ticksTimed0 = Read("StepTicksTimed"); framesTimed0 = Read("StepFramesTimed");
                    ticksPlain0 = Read("StepTicksPlain"); framesPlain0 = Read("StepFramesPlain");
                }
                var wait = WaitForSeconds(PhaseSeconds, off ? "the physics monitor off" : "the clusters' timing " + (on ? "on" : "off"));
                while (wait.MoveNext()) yield return wait.Current;
                costs[phase] = TickMetrics.Take().Format();
                var frames = Sandbox.MySandboxGame.Static.SimulationFrameCounter - frame0;
                perFrame[phase] = _ships.Select((s, i) => Vector3D.Distance(s.PositionComp.GetPosition(), at0[i]) / frames).ToArray();
                if (on) count = (long)measured.GetValue(null) - before;
                if (off) countOff = (long)measured.GetValue(null) - before;
                Note($"CLUSTER TIMING {(off ? "MONITOR OFF" : on ? "ON " : "OFF")} | {frames} frames, a frame: ship 0 {perFrame[phase][0] * 60:0.000} m/s, ship 1 {perFrame[phase][1] * 60:0.000} m/s" +
                     (on ? $", clusters timed {count}" : off ? $", clusters timed {countOff}" : "") + " | " + costs[phase]);
            }
            double Ms(long ticks, long frames) => frames > 0 ? ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames : 0;
            var timedFrames = Read("StepFramesTimed") - framesTimed0;
            var plainFrames = Read("StepFramesPlain") - framesPlain0;
            var timedMs = Ms(Read("StepTicksTimed") - ticksTimed0, timedFrames);
            var plainMs = Ms(Read("StepTicksPlain") - ticksPlain0, plainFrames);
            Note($"CLUSTER TIMING COST | the physics step {plainMs:0.000} ms on {plainFrames} frames, {timedMs:0.000} ms on the {timedFrames} with a cluster stepped alone (+{timedMs - plainMs:0.000} ms)");
            Check(timedMs - plainMs < MaxExtraMs, $"a frame with a cluster timed costs {timedMs - plainMs:0.00} ms more physics, over {MaxExtraMs} ms");

            Check(count >= PhaseSeconds * 60 / 30 / 2, $"only {count} clusters timed in {PhaseSeconds} s of timed frames");
            for (var i = 0; i < 2; i++)
            {
                var off = perFrame[0][i];
                var on = perFrame[1][i];
                Check(off > 0, $"ship {i} did not move");
                var change = on / off - 1;
                Check(Math.Abs(change) <= Tolerance,
                    $"ship {i} went {change:+0.00%;-0.00%} a frame with the clusters timed: its cluster was stepped {(change > 0 ? "more" : "less")} than once a frame");
                if (!monitorOff) continue;
                var vanilla = perFrame[2][i] / off - 1;
                Check(Math.Abs(vanilla) <= Tolerance, $"ship {i} went {vanilla:+0.00%;-0.00%} a frame with the physics monitor off");
            }
            if (monitorOff) Check(countOff == 0, $"{countOff} clusters timed with the physics monitor off");
            else Note("SentisOptimisations has no DisablePhysicsMonitor: the third run skipped");
        }

        public override void Cleanup()
        {
            try
            {
                _timingSwitch?.SetValue(null, true);
                foreach (var ship in _ships)
                    if (!ship.Closed) ship.Close();
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
