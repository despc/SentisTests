using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Does the grid a projector stands on reach the people looking at it?
    ///
    /// Reported from the game: the welding ships appear at once while the platform being welded -
    /// the one carrying the projector and its blueprint - never finishes streaming. The two differ
    /// in one thing: a projector's object builder carries the whole blueprint inside it, so that
    /// grid's builder is far bigger and far more expensive to make than its block count suggests.
    ///
    /// The bench puts the same rig in front of fake clients and asks the server's own per-client
    /// replicable table what each of them was given: the platform, the ship, and the preview grid
    /// the projector draws - which is a real entity on the server and must never be sent at all.
    /// </summary>
    public sealed class ProjectionStreamScenario : TestScenario
    {
        public const string ScenarioName = "projection_stream";
        private const string ProjectionResource = "SentisTests.Resources.PerfProjection.xml";
        private const string ShipResource = "SentisTests.Resources.PerfWelderShip.xml";
        /// <summary>The mixed slab's platform: the same thing with a blueprint an order smaller.</summary>
        private const string SmallProjectionResource = "SentisTests.Resources.PlatformWithProjection.xml";
        private const int Clients = 4;
        private const double ArriveSeconds = 120;
        private const double LogEverySeconds = 10;
        private const double WatchMetres = 60;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 60 };

        private bool _captured;
        private bool _initialFreezerEnabled;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(ArriveSeconds + 300);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            _initialFreezerEnabled = RuntimePluginControls.FreezerEnabled;
            _captured = true;
            RuntimePluginControls.SetFreezerEnabled(false);

            // ------------------------------------------------------------- the rig
            var platformOb = WorldApi.LoadAuthoredGrid(ProjectionResource, WorldApi.EntityPrefix + "stream-projection");
            var pose = platformOb.PositionAndOrientation.Value;
            var origin = TestRunner.RunOrigin ??
                         new Vector3D(pose.Position.X + 14000.0, pose.Position.Y, pose.Position.Z);
            platformOb.PositionAndOrientation = new MyPositionAndOrientation(origin, pose.Forward, pose.Up);
            platformOb.IsStatic = true;

            var platform = WorldApi.SpawnGrid(platformOb);
            Track(platform);
            yield return WaitForTicks(60);
            WorldApi.ChargeBatteries(platform);

            var projector = WorldApi.FindFunctional<MyProjectorBase>(platform);
            Check(projector != null, "the authored platform has no projector");
            projector.Enabled = true;

            var distributor = WorldApi.EnsureDistributor(platform);
            var waitProjection = Wait(() =>
            {
                WorldApi.ChargeBatteries(platform);
                distributor.MarkForUpdate();
                distributor.UpdateBeforeSimulation();
                return projector.IsWorking && projector.ProjectedGrid != null;
            }, "the projection comes up", 90);
            while (waitProjection.MoveNext()) yield return waitProjection.Current;

            var preview = projector.ProjectedGrid;
            var shipOb = WorldApi.LoadAuthoredGrid(ShipResource, WorldApi.EntityPrefix + "stream-ship");
            var bounds = platform.PositionComp.WorldAABB;
            shipOb.PositionAndOrientation = new MyPositionAndOrientation(
                new Vector3D(bounds.Max.X + 25.0, bounds.Center.Y, bounds.Center.Z),
                shipOb.PositionAndOrientation.Value.Forward, shipOb.PositionAndOrientation.Value.Up);
            var ship = WorldApi.SpawnGrid(shipOb);
            Track(ship);
            yield return WaitForTicks(60);

            // The same rig with a small blueprint, to tell "a projector" from "a big projection".
            var smallOb = WorldApi.LoadAuthoredGrid(SmallProjectionResource, WorldApi.EntityPrefix + "stream-small");
            var smallPose = smallOb.PositionAndOrientation.Value;
            smallOb.PositionAndOrientation = new MyPositionAndOrientation(
                new Vector3D(origin.X, origin.Y - 120.0, origin.Z), smallPose.Forward, smallPose.Up);
            smallOb.IsStatic = true;
            var small = WorldApi.SpawnGrid(smallOb);
            Track(small);
            yield return WaitForTicks(60);
            WorldApi.ChargeBatteries(small);
            var smallProjector = WorldApi.FindFunctional<MyProjectorBase>(small);
            if (smallProjector != null) smallProjector.Enabled = true;
            var smallDistributor = WorldApi.EnsureDistributor(small);
            var waitSmall = Wait(() =>
            {
                WorldApi.ChargeBatteries(small);
                smallDistributor.MarkForUpdate();
                smallDistributor.UpdateBeforeSimulation();
                return smallProjector == null || (smallProjector.IsWorking && smallProjector.ProjectedGrid != null);
            }, "the small projection comes up", 60);
            while (waitSmall.MoveNext()) yield return waitSmall.Current;

            Note("platform " + platform.BlocksCount + " blocks with a " + preview.CubeBlocks.Count +
                 " block blueprint in its projector, small platform " + small.BlocksCount + " blocks with " +
                 (smallProjector?.ProjectedGrid?.CubeBlocks.Count ?? 0) + ", ship " + ship.BlocksCount + " blocks");

            // ------------------------------------------------------------- the audience
            var watchFrom = bounds.Center + new Vector3D(0, WatchMetres, 0);
            FakeClients.Add(Clients, Network, p => (watchFrom + new Vector3D(p * 3.0, 0, 0), 0, 0), withCharacters: true);
            Note(Clients + " clients watching from " + WatchMetres.ToString("F0") + " m");

            // ------------------------------------------------------------- what arrives
            var wanted = new List<MyEntity> { platform, small, ship };
            var started = DateTime.UtcNow;
            var lastLog = DateTime.MinValue;
            var arrivedAt = DateTime.MinValue;
            while ((DateTime.UtcNow - started).TotalSeconds < ArriveSeconds)
            {
                if (FakeClients.ArrivedCount(wanted) == 0)
                {
                    arrivedAt = DateTime.UtcNow;
                    break;
                }

                if ((DateTime.UtcNow - lastLog).TotalSeconds >= LogEverySeconds)
                {
                    lastLog = DateTime.UtcNow;
                    Note((DateTime.UtcNow - started).TotalSeconds.ToString("F0") + "s | " + FakeClients.Arrivals(wanted) +
                         " | streams deferred by the budget: " + RuntimePluginControls.StreamsDeferred +
                         " | " + RuntimePluginControls.TakeGridStreamBuilderStats());
                }

                yield return WaitForTicks(30);
            }

            var waited = arrivedAt == DateTime.MinValue
                ? (DateTime.UtcNow - started).TotalSeconds
                : (arrivedAt - started).TotalSeconds;
            var previewSent = FakeClients.ArrivedCount(new List<MyEntity> { preview }) < Clients;

            Note("PROJECTION STREAM RESULT | after " + waited.ToString("F1") + " s: " + FakeClients.Arrivals(wanted) +
                 " | the projector's preview grid " + (previewSent ? "IS being sent to clients" : "stays on the server") +
                 " | " + FakeClients.Take(waited));

            Check(arrivedAt != DateTime.MinValue,
                "not everything reached the clients in " + ArriveSeconds.ToString("F0") + " s: " +
                FakeClients.Arrivals(wanted));
            Check(!previewSent, "the projector's preview grid is being replicated to clients");
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                if (_captured)
                {
                    _captured = false;
                    RuntimePluginControls.SetFreezerEnabled(_initialFreezerEnabled);
                }
            }
            finally { base.Cleanup(); }
        }
    }
}
