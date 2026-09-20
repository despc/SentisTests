using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The operator's GRINDER_TEST (80 large ship grinders on conveyors into one large container,
    /// a reactor, no thrusters) grinding SHIP_TO_GRIND (2757 blocks) for <see cref="GrindSeconds"/>,
    /// <see cref="Pairs"/> pairs of them in a row <see cref="PairSpacingM"/> apart.
    /// Both are copied <see cref="OffsetM"/> away from the originals, keeping how they stood. The
    /// target is made static (a wreck or a station: a free 2757-block ship would drift off the
    /// grinders), and the grinder is pressed into it every frame at up to <see cref="PushMps"/>,
    /// the way a player holds it on thrust. Two fake players watch from 150 m (with selective physics
    /// updates nothing is stepped for nobody, and the removed blocks are replicated to them).
    /// Measured: <see cref="IdleSeconds"/> with the grinders off, then the grinding; dotTrace attaches
    /// on PROFILE WINDOW START, <see cref="ProfileAfterSeconds"/> into it.
    /// </summary>
    public sealed class GrinderPerfScenario : TestScenario
    {
        public const string ScenarioName = "grinder_perf";
        private const string Prefix = "grind-";
        internal const string GrinderResource = "SentisTests.Resources.GrinderTest.xml";
        internal const string TargetResource = "SentisTests.Resources.ShipToGrind.xml";
        private const double OffsetM = 3000;
        private const double SettleSeconds = 5;
        private const double IdleSeconds = 10;
        private const double GrindSeconds = 120;
        private const double ProfileAfterSeconds = 30;
        private const double LogEverySeconds = 20;
        private const float PushMps = 0.5f;
        private const float PushAccel = 2f;
        private const int Players = 2;
        private const double StartGapM = 1.5;
        private const int Pairs = 5;
        private const double PairSpacingM = 500;
        private const double PlayerDistanceM = 150;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(GrindSeconds + 300);

        private sealed class Pair
        {
            public MyCubeGrid Grinder, Target;
            public List<MyShipGrinder> Grinders;
            public Vector3 Dir;
            public double Gap;
            public int StartBlocks;
            public Vector3D TargetStart;
            public double StartItems;
            public int Left => Target.Closed ? 0 : Target.CubeBlocks.Count;
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var pairs = new List<Pair>();
            for (var i = 0; i < Pairs; i++)
            {
                pairs.Add(SpawnPair(i, new Vector3D(PairSpacingM * i, OffsetM, 0)));
                yield return null;
            }

            // The watchers stand by the middle pair; the others are within their sync distance.
            var middle = pairs[pairs.Count / 2];
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(middle.Dir));
            var watch = middle.Grinder.PositionComp.WorldAABB.Center + side * PlayerDistanceM;
            FakeClients.Add(Players, Network, p => (watch + side * (10 * p), 0, 0), withCharacters: true);

            var settle = WaitForSeconds(SettleSeconds, "grids settle");
            while (settle.MoveNext()) yield return settle.Current;
            foreach (var pair in pairs)
            {
                pair.StartBlocks = pair.Target.CubeBlocks.Count;
                pair.TargetStart = pair.Target.PositionComp.GetPosition();
                pair.StartItems = Items(pair.Grinder);
            }
            var gravity = Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(middle.Grinder.PositionComp.GetPosition()).Length();
            Note(Pairs + " pairs, " + PairSpacingM + " m apart: grinder " + middle.Grinder.CubeBlocks.Count + " blocks (" + middle.Grinders.Count +
                 " grinders), target " + middle.StartBlocks + " blocks, static " + pairs.All(p => p.Target.IsStatic) + ", all target blocks off; heads moved " +
                 string.Join("/", pairs.Select(p => (p.Gap - StartGapM).ToString("F1"))) + " m closer; gravity " + gravity.ToString("F2") + " m/s2");

            // Grinders off, the grinders pressed onto the targets all the same: the cost of the scene alone.
            TickMetrics.Take();
            FrameProbe.Take();
            var idle = DateTime.UtcNow;
            while ((DateTime.UtcNow - idle).TotalSeconds < IdleSeconds)
            {
                foreach (var pair in pairs) Push(pair.Grinder, pair.Dir);
                yield return null;
            }
            var idleProbe = FrameProbe.Take();
            TickMetrics.Take();

            // SentisGameplayImprovements zeroes every damage for its first DisableAnyDamageAfterStartTime
            // seconds of simulation: grinding right after a server start takes nothing off.
            var noDamageUntil = NoDamageSeconds();
            var protectedWait = Wait(() => Sandbox.MySandboxGame.Static.SimulationFrameCounter / 60 >= (ulong)noDamageUntil,
                "damage enabled (" + noDamageUntil + " s after start)", noDamageUntil + 10);
            while (protectedWait.MoveNext())
            {
                foreach (var pair in pairs) Push(pair.Grinder, pair.Dir);
                yield return protectedWait.Current;
            }

            foreach (var g in pairs.SelectMany(p => p.Grinders)) g.Enabled = true;
            var started = DateTime.UtcNow;
            var lastLog = started;
            var windowStarted = DateTime.MinValue;
            while ((DateTime.UtcNow - started).TotalSeconds < GrindSeconds && pairs.Any(p => !p.Target.Closed))
            {
                foreach (var pair in pairs) Push(pair.Grinder, pair.Dir);
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                if (windowStarted == DateTime.MinValue && elapsed >= ProfileAfterSeconds)
                {
                    windowStarted = DateTime.UtcNow;
                    TickMetrics.Take();
                    FrameProbe.Take();
                    Note("PROFILE WINDOW START: " + pairs.Sum(p => p.Grinders.Count) + " grinders on " + pairs.Sum(p => p.Left) + " blocks");
                }
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= LogEverySeconds)
                {
                    lastLog = DateTime.UtcNow;
                    Note("grinding " + elapsed.ToString("F0") + "s: ground off " + string.Join("/", pairs.Select(p => p.StartBlocks - p.Left)) +
                         " blocks, items taken " + pairs.Sum(p => Items(p.Grinder) - p.StartItems).ToString("F0") + ", grinders working " +
                         pairs.Sum(p => p.Grinders.Count(g => g.IsWorking)) + ", targets " +
                         (pairs.All(p => p.Target.Closed || p.Target.IsStatic) ? "static" : "DYNAMIC") + ", moved max " +
                         pairs.Max(p => Vector3D.Distance(p.Target.PositionComp.GetPosition(), p.TargetStart)).ToString("F2") + " m");
                }
                yield return null;
            }
            foreach (var g in pairs.SelectMany(p => p.Grinders)) g.Enabled = false;
            var metrics = TickMetrics.Take();
            var probe = FrameProbe.Take();
            Note("GRINDER RESULT | " + (DateTime.UtcNow - started).TotalSeconds.ToString("F0") + " s, " + Pairs + " pairs, " +
                 pairs.Sum(p => p.Grinders.Count) + " grinders, ground off " + pairs.Sum(p => p.StartBlocks - p.Left) + " blocks (" +
                 string.Join("/", pairs.Select(p => p.StartBlocks - p.Left)) + "), items taken " +
                 pairs.Sum(p => Items(p.Grinder) - p.StartItems).ToString("F0") +
                 " | idle, grinders off: " + Summary(idleProbe) + " | grinding (profile window): " + Summary(probe) + " | " + metrics.Format() + " | " + probe);
            Check(pairs.All(p => p.StartBlocks - p.Left > 0), "a pair ground nothing off");
            Check(pairs.All(p => p.Target.Closed || p.Target.IsStatic), "a target stopped being static");
        }

        /// <summary>
        /// One grinder and its target, moved rigidly by <paramref name="offset"/> from where they were
        /// built. The target becomes a station with every block off; the grinder is put with its heads
        /// <see cref="StartGapM"/> short of the target.
        /// </summary>
        private Pair SpawnPair(int index, Vector3D offset)
        {
            var pair = new Pair();
            foreach (var ob in WorldApi.LoadAuthoredGroup(GrinderResource, WorldApi.EntityPrefix + Prefix + "grinder-" + index))
            {
                Shift(ob, offset);
                pair.Grinder = WorldApi.SpawnGrid(ob);
                Track(pair.Grinder);
            }
            foreach (var ob in WorldApi.LoadAuthoredGroup(TargetResource, WorldApi.EntityPrefix + Prefix + "target-" + index))
            {
                Shift(ob, offset);
                pair.Target = WorldApi.SpawnGrid(ob);
                Track(pair.Target);
                // A station, converted the way a player does it: a grid spawned static became a ship
                // again once grinding split it, and the grinders pushed it away.
                pair.Target.ConvertToStatic();
                // Everything on the target off, power included: its turrets shot the fake players'
                // characters (the test identity is hostile to the owner).
                foreach (var block in pair.Target.GetFatBlocks().OfType<Sandbox.Game.Entities.Cube.MyFunctionalBlock>())
                    block.Enabled = false;
            }
            Check(pair.Grinder != null && pair.Target != null, "grids not loaded");
            pair.Grinders = pair.Grinder.GetFatBlocks().OfType<MyShipGrinder>().ToList();
            Check(pair.Grinders.Count > 0, "no grinders on " + pair.Grinder.DisplayName);
            // The grinders cut along their forward axis: press the grinder that way.
            pair.Dir = Vector3.Normalize(pair.Grinders.Aggregate(Vector3D.Zero, (a, g) => a + g.WorldMatrix.Forward));
            // Start with the heads just short of the target: the two minutes are grinding, not the
            // 10-20 m drive up to it.
            pair.Gap = Gap(pair.Grinders, pair.Target, pair.Dir);
            if (pair.Gap > StartGapM && pair.Gap < 100)
                pair.Grinder.PositionComp.SetPosition(pair.Grinder.PositionComp.GetPosition() + (Vector3D)pair.Dir * (pair.Gap - StartGapM));
            return pair;
        }

        /// <summary>
        /// How far the grinders are from the target along the grind axis: for each grinder, the nearest
        /// target block ahead of it within a block's width of its line.
        /// </summary>
        private static double Gap(List<MyShipGrinder> grinders, MyCubeGrid target, Vector3 dir)
        {
            var blocks = target.CubeBlocks.Select(b => target.GridIntegerToWorld(b.Position)).ToList();
            var best = double.MaxValue;
            foreach (var g in grinders)
            {
                var at = g.PositionComp.GetPosition();
                foreach (var b in blocks)
                {
                    var along = Vector3D.Dot(b - at, dir);
                    if (along <= 0 || along >= best) continue;
                    if ((b - at - (Vector3D)dir * along).Length() < 2.5) best = along;
                }
            }
            // Block centres: the faces are half a block nearer on both sides.
            return best == double.MaxValue ? 0 : best - 2.5;
        }

        /// <summary>SentisGameplayImprovements' DisableAnyDamageAfterStartTime, 0 without the plugin.</summary>
        private static int NoDamageSeconds()
        {
            var plugin = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("SentisGameplayImprovements.SentisGameplayImprovementsPlugin")).FirstOrDefault(t => t != null);
            var config = plugin?.GetProperty("Config", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            return config?.GetType().GetProperty("DisableAnyDamageAfterStartTime")?.GetValue(config) is int seconds ? seconds : 0;
        }

        /// <summary>Holds the grinder on its axis and eases it forward up to PushMps, like thrusters would.</summary>
        private static void Push(MyCubeGrid grinder, Vector3 dir)
        {
            var physics = grinder.Physics;
            if (physics == null || grinder.Closed) return;
            var along = Vector3.Dot(physics.LinearVelocity, dir);
            physics.LinearVelocity = dir * Math.Min(along + PushAccel / 60f, PushMps);
            physics.AngularVelocity = Vector3.Zero;
        }

        /// <summary>Items in all the grinder's inventories (the reactor's uranium cancels out in a difference).</summary>
        private static double Items(MyCubeGrid grid) =>
            grid.GetFatBlocks().Where(b => b.HasInventory)
                .SelectMany(b => Enumerable.Range(0, b.InventoryCount).Select(i => b.GetInventory(i) as MyInventory))
                .Where(inv => inv != null)
                .Sum(inv => inv.GetItems().Sum(i => (double)i.Amount));

        private static void Shift(MyObjectBuilder_CubeGrid ob, Vector3D offset)
        {
            var m = ob.PositionAndOrientation.Value.GetMatrix();
            m.Translation += offset;
            ob.PositionAndOrientation = new MyPositionAndOrientation(m);
        }

        private static string Summary(string probe)
        {
            string Pick(string name)
            {
                var m = System.Text.RegularExpressions.Regex.Match(probe, System.Text.RegularExpressions.Regex.Escape(name) + @"=\d+ms/\d+calls\([\d.]+ms each, ([\d.]+)ms/frame");
                return m.Success ? m.Groups[1].Value : "?";
            }
            var sim = System.Text.RegularExpressions.Regex.Match(probe, @"sim-work frames=\d+ avg=([\d.]+)ms p50=[\d.]+ p95=[\d.]+ p99=([\d.]+)");
            return "frame avg " + (sim.Success ? sim.Groups[1].Value : "?") + " ms, p99 " + (sim.Success ? sim.Groups[2].Value : "?") +
                   ", physics " + Pick("physics") + ", entities.before " + Pick("entities.before") + ", entities.after " + Pick("entities.after") +
                   ", replication.sendUpdate " + Pick("replication.sendUpdate");
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!(grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                    grid.Close();
                }
            }
            finally { base.CleanupLeftovers(); }
        }
    }
}
