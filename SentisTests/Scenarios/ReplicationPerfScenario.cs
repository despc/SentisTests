using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Replication load with many clients. Spawns the 64 static Refinery_test grids of refinery_perf
    /// and 10 dynamic MixedShip grids that keep flying circles over them (see <see cref="GridFlight"/>),
    /// then connects in-process fake clients (see <see cref="FakeClients"/>), each with a player and a floating
    /// character that follows it, one by one, every
    /// <see cref="JoinIntervalSeconds"/>, with emulated round trip time, jitter and unreliable packet
    /// loss. The clients fly on circles over the same area, inside sync distance of everything.
    ///
    /// Windows: a baseline without clients, the join phase (checkpoints every 16 clients), a steady
    /// window with all clients, and a mass join where everyone reconnects at once, each with server frame metrics, replication sections and
    /// traffic per message type.
    ///
    /// The freezer is turned off for the test, so its sleeping and waking grids do not change the
    /// replication load between runs.
    /// </summary>
    public sealed class ReplicationPerfScenario : TestScenario
    {
        public const string ScenarioName = "replication_perf";
        private const string GridPrefix = "replication-perf-";
        private const string ShipResource = "SentisTests.Resources.MixedShip.xml";
        private const int GridCount = 64;
        private const int GridsPerRow = 8;
        private const double LatticeStep = 250.0;
        private const int ShipCount = 10;
        private const int ClientCount = 64;
        private const int CheckpointEvery = 16;
        private const double JoinIntervalSeconds = 2;
        private const double SettleSeconds = 15;
        private const double BaselineSeconds = 30;
        private const double SteadySeconds = 60;
        private const int JoinTimeoutSeconds = 180;
        private const double ClientSpeedMps = 50;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile
        {
            RttMs = 120,
            JitterMs = 20,
            UnreliableLossPercent = 1,
        };

        private bool _captured;
        private Sandbox.Game.Entities.MyCubeGrid _firstGrid;
        private bool _initialFreezerEnabled;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds =>
            300 + (int)(ClientCount * JoinIntervalSeconds) + JoinTimeoutSeconds + (int)(BaselineSeconds + SteadySeconds);

        /// <summary>
        /// A client that opens something must be served its inventories at once, not after the idle
        /// wait (see IdleInventorySync). Looks at what the server has queued for one client before and
        /// after putting a grid in front of it.
        /// </summary>
        private IEnumerator CheckTerminalOpening()
        {
            const int Soon = 5;
            var state = FakeClients.StateOf(0);
            var idle = RuntimePluginControls.InventoryQueueState(state, Soon);
            FakeClients.LookAt(0, _firstGrid);
            for (var i = 0; i < 3; i++) yield return null;
            var opened = RuntimePluginControls.InventoryQueueState(state, Soon);
            FakeClients.LookAt(0, null);
            Note("TERMINAL OPENING | of " + idle.Queued + " queued inventories " + idle.Due + " were due within " + Soon +
                 " frames while idle, " + opened.Due + " of " + opened.Queued + " after opening");
            Check(idle.Queued > 100, "only " + idle.Queued + " inventories were queued for the client, this proves nothing");
            Check(idle.Due * 4 < idle.Queued, idle.Due + " of " + idle.Queued +
                  " inventories were already due for an idle client, they are not being held back");
            Check(opened.Due * 10 >= opened.Queued * 9, "after opening, only " + opened.Due + " of " + opened.Queued +
                  " inventories were pulled forward");
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused("replication_perf start");
            _initialFreezerEnabled = RuntimePluginControls.FreezerEnabled;
            _captured = true;
            RuntimePluginControls.SetFreezerEnabled(false);
            var leftovers = FakeClients.PurgeLeftovers();
            if (leftovers > 0) Note("removed " + leftovers + " fake client identities left by earlier runs");

            var template = WorldApi.LoadAuthoredGrid(RefineryPerfScenario.ResourceName, WorldApi.EntityPrefix + GridPrefix + "template");
            var pose = template.PositionAndOrientation.Value;
            var origin = TestRunner.RunOrigin ?? new Vector3D(pose.Position.X + 12000.0, pose.Position.Y, pose.Position.Z);
            for (var i = 0; i < GridCount; i++)
            {
                var ob = WorldApi.LoadAuthoredGrid(RefineryPerfScenario.ResourceName, WorldApi.EntityPrefix + GridPrefix + i.ToString("D2"));
                ob.PositionAndOrientation = new MyPositionAndOrientation(
                    origin + new Vector3D((i % GridsPerRow) * LatticeStep, 0, (i / GridsPerRow) * LatticeStep), pose.Forward, pose.Up);
                ob.IsStatic = true;
                var grid = WorldApi.SpawnGrid(ob);
                if (_firstGrid == null) _firstGrid = grid;
                Track(grid);
                yield return null;
            }
            var center = origin + new Vector3D((GridsPerRow - 1) * LatticeStep / 2, 0, (GridCount / GridsPerRow - 1) * LatticeStep / 2);

            // Ships on separate altitudes above the grid field, so their circles never cross.
            var rng = new Random(7);
            var shipBlocks = 0;
            for (var i = 0; i < ShipCount; i++)
            {
                var radius = 300 + i * 90.0;
                var angle = rng.NextDouble() * Math.PI * 2;
                var altitude = 250 + i * 60.0;
                var shipCenter = center + new Vector3D(0, altitude, 0);
                var position = shipCenter + new Vector3D(Math.Cos(angle), 0, Math.Sin(angle)) * radius;
                var ob = WorldApi.LoadGridTemplate(ShipResource, WorldApi.EntityPrefix + GridPrefix + "ship-" + i.ToString("D2"),
                    position, Vector3.Forward, Vector3.Up);
                var ship = WorldApi.SpawnGrid(ob);
                Track(ship);
                shipBlocks += ship.BlocksCount;
                var spin = new Vector3(0, (float)(0.05 + rng.NextDouble() * 0.2), 0);
                GridFlight.Add(ship, shipCenter, radius, 30 + rng.NextDouble() * 50, spin);
                yield return null;
            }
            Note("spawned " + GridCount + " static grids and " + ShipCount + " flying ships (" + shipBlocks + " blocks); sync distance " +
                 MySession.Static.Settings.SyncDistance + "m; settling " + SettleSeconds + "s");
            var settle = WaitForSeconds(SettleSeconds, "spawned grids settle");
            while (settle.MoveNext()) yield return settle.Current;

            TickMetrics.Take();
            FrameProbe.Take();
            FakeClients.Take(1);
            Note("BASELINE WINDOW START (0 clients, " + GridFlight.Count + " ships flying)");
            var baselineWatch = Stopwatch.StartNew();
            var baseline = WaitForSeconds(BaselineSeconds, "baseline without clients");
            while (baseline.MoveNext()) yield return baseline.Current;
            Note("BASELINE WINDOW END | " + FakeClients.Take(baselineWatch.Elapsed.TotalSeconds) + " | " +
                 TickMetrics.Take().Format() + " | " + FrameProbe.Take());

            Check(GridFlight.Count == ShipCount, "only " + GridFlight.Count + " of " + ShipCount + " ships are still flying");
            FakeClients.Take(1);
            RuntimePluginControls.SetGridStreamBuilderVerify(true);
            RuntimePluginControls.SetStateGroupIndexVerify(true);
            Note("JOIN WINDOW START, verifying cached grid builders (1 client every " + JoinIntervalSeconds + "s up to " + ClientCount + ", " + Network + ")");
            var joinWatch = Stopwatch.StartNew();
            var checkpointWatch = Stopwatch.StartNew();
            while (FakeClients.Count < ClientCount)
            {
                FakeClients.Add(1, Network, index => (
                    center + new Vector3D(rng.NextDouble() * 400 - 200, rng.NextDouble() * 200 - 100, rng.NextDouble() * 400 - 200),
                    300 + rng.NextDouble() * 1200,
                    ClientSpeedMps), withCharacters: true);
                var gap = Stopwatch.StartNew();
                while (gap.Elapsed.TotalSeconds < JoinIntervalSeconds) yield return null;
                if (FakeClients.Count % CheckpointEvery == 0)
                {
                    Note("JOIN CHECKPOINT " + FakeClients.Count + " clients | " + FakeClients.Take(checkpointWatch.Elapsed.TotalSeconds) + " | " +
                         TickMetrics.Take().Format() + " | " + FrameProbe.Take());
                    checkpointWatch.Restart();
                }
            }
            while (FakeClients.PendingReplicables() > 0)
            {
                if (joinWatch.Elapsed.TotalSeconds > ClientCount * JoinIntervalSeconds + JoinTimeoutSeconds)
                    throw new ScenarioFailedException(FakeClients.PendingReplicables() + " replicables still pending: " +
                                                      FakeClients.Take(checkpointWatch.Elapsed.TotalSeconds));
                yield return null;
            }
            RuntimePluginControls.SetGridStreamBuilderVerify(false);
            RuntimePluginControls.SetStateGroupIndexVerify(false);
            var verify = RuntimePluginControls.GridStreamBuilderVerifyResult();
            var indexVerify = RuntimePluginControls.StateGroupIndexVerifyResult();
            Note("JOIN WINDOW END after " + joinWatch.Elapsed.TotalSeconds.ToString("F0") + "s | " +
                 RuntimePluginControls.TakeGridStreamBuilderStats() + ", cached vs fresh mismatches: " + verify.Mismatches +
                 (verify.FirstDifference != null ? " (" + verify.FirstDifference + ")" : "") +
                 " | " + RuntimePluginControls.TakeInventoryDeltaStats() +
                 " | " + RuntimePluginControls.TakeStateGroupStats() + ", index mismatches: " + indexVerify.Mismatches +
                 (indexVerify.FirstDifference != null ? " (" + indexVerify.FirstDifference + ")" : "") +
                 " | " + FakeClients.Take(checkpointWatch.Elapsed.TotalSeconds) + " | " + TickMetrics.Take().Format() + " | " + FrameProbe.Take());
            Check(verify.Mismatches == 0, verify.Mismatches + " cached grid builders differ from fresh ones: " + verify.FirstDifference);
            Check(indexVerify.Mismatches == 0, indexVerify.Mismatches + " state group client index mismatches: " + indexVerify.FirstDifference);

            Check(GridFlight.Count == ShipCount, "only " + GridFlight.Count + " of " + ShipCount + " ships are still flying");
            Note("STEADY WINDOW START (" + FakeClients.Count + " clients)");
            var steadyWatch = Stopwatch.StartNew();
            var steady = WaitForSeconds(SteadySeconds, "steady replication with " + FakeClients.Count + " clients");
            while (steady.MoveNext()) yield return steady.Current;
            Note("STEADY WINDOW END | " + RuntimePluginControls.TakeStateGroupStats() + " | " +
                 RuntimePluginControls.TakeInventoryDeltaStats() + " | " +
                 FakeClients.Take(steadyWatch.Elapsed.TotalSeconds) + " | " +
                 TickMetrics.Take().Format() + " | " + FrameProbe.Take());

            var opening = CheckTerminalOpening();
            while (opening.MoveNext()) yield return opening.Current;

            // Everyone reconnecting at once, like after a server restart: the same grids are streamed
            // to all clients within a few frames, which is what the grid builder cache is for.
            FakeClients.RemoveAll();
            var settleAfterLeave = WaitForSeconds(10, "clients left");
            while (settleAfterLeave.MoveNext()) yield return settleAfterLeave.Current;
            TickMetrics.Take();
            FrameProbe.Take();
            FakeClients.Take(1);
            RuntimePluginControls.TakeGridStreamBuilderStats();
            Note("MASS JOIN WINDOW START (" + ClientCount + " clients at once)");
            var massWatch = Stopwatch.StartNew();
            FakeClients.Add(ClientCount, Network, index => (
                center + new Vector3D(rng.NextDouble() * 400 - 200, rng.NextDouble() * 200 - 100, rng.NextDouble() * 400 - 200),
                300 + rng.NextDouble() * 1200,
                ClientSpeedMps), withCharacters: true);
            yield return WaitForTicks(120);
            while (FakeClients.PendingReplicables() > 0)
            {
                if (massWatch.Elapsed.TotalSeconds > JoinTimeoutSeconds)
                    throw new ScenarioFailedException(FakeClients.PendingReplicables() + " replicables still pending after a mass join");
                yield return null;
            }
            var massSeconds = massWatch.Elapsed.TotalSeconds;
            Note("MASS JOIN WINDOW END after " + massSeconds.ToString("F0") + "s | " + RuntimePluginControls.TakeGridStreamBuilderStats() +
                 " | " + FakeClients.Take(massSeconds) + " | " + TickMetrics.Take().Format() + " | " + FrameProbe.Take());
        }

        public override void Cleanup()
        {
            try { Restore(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                Restore();
                foreach (var grid in Sandbox.Game.Entities.MyEntities.GetEntities().OfType<Sandbox.Game.Entities.MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!(grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + GridPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                    grid.Close();
                }
            }
            finally { base.CleanupLeftovers(); }
        }

        private void Restore()
        {
            try { RuntimePluginControls.SetGridStreamBuilderVerify(false); }
            catch (Exception) { /* plugin may be missing */ }
            try { RuntimePluginControls.SetStateGroupIndexVerify(false); }
            catch (Exception) { /* plugin may be missing */ }
            FakeClients.RemoveAll();
            GridFlight.Clear();
            if (!_captured) return;
            RuntimePluginControls.SetFreezerEnabled(_initialFreezerEnabled);
            _captured = false;
        }
    }
}
