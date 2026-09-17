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
using VRageMath;
using BuildCheckResult = Sandbox.ModAPI.BuildCheckResult;
using SpaceWelder = SpaceEngineers.Game.Entities.Blocks.MyShipWelder;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Reproduces a client-safe 1000-block heavy-armor projection workload. The reactor-backed
    /// projector grid comes from the operator's save; its embedded hologram is the verified solid
    /// 10x10x10 cube touching the platform at exactly one buildable block. Only the whole fixture is translated
    /// away from the operator's original TEST_PERF_* grids. The welder ship is parked outside the
    /// hologram and given one deliberately huge detector sphere, so every projection scan stresses
    /// the real vanilla welder path without moving the hologram or steering the ship.
    /// </summary>
    public sealed class WelderPerfScenario : TestScenario
    {
        public const string ScenarioName = "welder_perf";
        private const string ProjectionResource = "SentisTests.Resources.PerfProjection.xml";
        private const string WelderResource = "SentisTests.Resources.PerfWelderShip.xml";
        private const float RadiusMultiplier = 100f;
        private const int WelderCount = 3;
        private const int ExpectedRuntimeBlocks = 1000;
        private const int MaxWeldSeconds = 900;

        private bool _captured;
        private bool _initialFreezerEnabled;
        private float _initialWelderMultiplier;
        private Vector3D? _fixturePosition;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 420;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused("welder_perf start");
            _initialFreezerEnabled = RuntimePluginControls.FreezerEnabled;
            _initialWelderMultiplier = RuntimePluginControls.WelderRadiusMultiplier;
            _captured = true;

            RuntimePluginControls.SetFreezerEnabled(false);
            RuntimePluginControls.SetWelderRadiusMultiplier(RadiusMultiplier);
            Note("Freezer disabled; live welder radius multiplier set to x" + RadiusMultiplier);

            var platformOb = WorldApi.LoadAuthoredGrid(ProjectionResource,
                WorldApi.EntityPrefix + "perf-projection");
            var authoredPose = platformOb.PositionAndOrientation.Value;
            // Preserve every projector setting and the blueprint's position relative to the platform.
            // Translate only the root grid so the fixture cannot overlap the operator's originals.
            // Keep it beyond PlayersSyncDistance during profiling so client rendering and
            // replication do not contaminate the simulation-thread welding measurements.
            var fixtureOrigin = TestRunner.RunOrigin ??
                new Vector3D(authoredPose.Position.X + 12000.0, authoredPose.Position.Y, authoredPose.Position.Z);
            platformOb.PositionAndOrientation = new MyPositionAndOrientation(
                fixtureOrigin, authoredPose.Forward, authoredPose.Up);
            platformOb.IsStatic = true;

            var projectedOb = platformOb.CubeBlocks.OfType<MyObjectBuilder_ProjectorBase>()
                .SelectMany(p => p.ProjectedGrids ?? new List<MyObjectBuilder_CubeGrid>())
                .FirstOrDefault();
            Check(projectedOb != null && projectedOb.CubeBlocks != null && projectedOb.CubeBlocks.Count > 0,
                "the authored projector carries no blueprint");

            var platform = WorldApi.SpawnGrid(platformOb);
            Track(platform);
            _fixturePosition = WorldApi.PositionOf(platform);
            yield return WaitForTicks(60);

            var platformDistributor = WorldApi.EnsureDistributor(platform);
            Check(WorldApi.ChargeBatteries(platform) > 0f,
                "the authored projection platform has no chargeable batteries");
            var projector = WorldApi.FindFunctional<MyProjectorBase>(platform);
            Check(projector != null, "the authored platform has no projector");
            projector.Enabled = true;

            var waitProjection = Wait(() =>
            {
                WorldApi.ChargeBatteries(platform);
                platformDistributor.MarkForUpdate();
                platformDistributor.UpdateBeforeSimulation();
                return projector.IsWorking && projector.ProjectedGrid != null;
            }, "authored projection active (" + WorldApi.DescribePower(platform) + ")", 90);
            while (waitProjection.MoveNext()) yield return waitProjection.Current;

            var preview = projector.ProjectedGrid;
            var expected = preview.CubeBlocks.Count;
            var buildable = preview.CubeBlocks.Cast<Sandbox.Game.Entities.Cube.MySlimBlock>()
                .Count(block => projector.CanBuild(block, true) == BuildCheckResult.OK);
            Check(buildable > 0, "authored hologram has no buildable contact block");
            Note("authored hologram unchanged: " + expected + " cells, " + buildable + " buildable now");

            var projectionBounds = preview.PositionComp.WorldAABB;
            var shipPositions = new[]
            {
                new Vector3D(projectionBounds.Max.X + 20.0, projectionBounds.Center.Y, projectionBounds.Center.Z),
                new Vector3D(projectionBounds.Min.X - 20.0, projectionBounds.Center.Y, projectionBounds.Center.Z),
                new Vector3D(projectionBounds.Center.X, projectionBounds.Max.Y + 20.0, projectionBounds.Center.Z),
            };
            var ships = new List<MyCubeGrid>();
            for (var index = 0; index < WelderCount; index++)
            {
                var shipOb = WorldApi.LoadAuthoredGrid(WelderResource,
                    WorldApi.EntityPrefix + "perf-welder-" + index);
                var shipPose = shipOb.PositionAndOrientation.Value;
                shipOb.PositionAndOrientation = new MyPositionAndOrientation(
                    shipPositions[index], shipPose.Forward, shipPose.Up);
                shipOb.IsStatic = true;
                var spawnedShip = WorldApi.SpawnGrid(shipOb);
                ships.Add(spawnedShip);
                Track(spawnedShip);
            }
            yield return WaitForTicks(120);

            // Use the materialized preview definitions, not object-builder SubtypeName. Forty-two
            // turret-style cells in this authored blueprint encode their identity only in xsi:type;
            // grouping the XML builders by SubtypeName silently omits all of their components and
            // strands the connected cells behind them. Double stock also covers conveyor staging.
            var needs = RuntimeComponentsNeeded(preview.CubeBlocks, 2);
            var welders = new List<SpaceWelder>();
            foreach (var ship in ships)
            {
                WorldApi.EnsureDistributor(ship);
                Check(WorldApi.ChargeBatteries(ship) > 0f, "an authored welder ship has no charged battery");
                var welder = WorldApi.FindFunctional<SpaceWelder>(ship);
                var container = WorldApi.FindFunctional<MyCargoContainer>(ship);
                Check(welder != null, "an authored ship has no welder");
                Check(container != null, "an authored ship has no cargo container");
                Note("stocking authored cargo for " + needs.Count + " component types: " +
                     WorldApi.StockComponents(container.GetInventory(), needs));
                var missingAfterStock = needs
                    .Where(kvp => WorldApi.CountComponent(container.GetInventory(), kvp.Key) < kvp.Value)
                    .Select(kvp => kvp.Key + "=" + WorldApi.CountComponent(container.GetInventory(), kvp.Key) +
                                   "/" + kvp.Value)
                    .ToList();
                Check(missingAfterStock.Count == 0,
                    "cargo did not accept the complete projection kit: " + string.Join(", ", missingAfterStock));
                welders.Add(welder);
            }

            var waitRadius = Wait(() =>
            {
                var ready = true;
                foreach (var ship in ships) WorldApi.ChargeBatteries(ship);
                foreach (var welder in welders)
                    ready &= Math.Abs(WorldApi.SensorSphere(welder).Radius - welder.DetectorSphere.Radius) < 0.01 &&
                             WorldApi.SensorSphere(welder).Radius > 200.0;
                return ready;
            }, "x100 detector radius applied to all authored welders", 30);
            while (waitRadius.MoveNext()) yield return waitRadius.Current;

            foreach (var welder in welders)
            {
                var sensor = WorldApi.SensorSphere(welder);
                var farthest = FarthestCornerDistance(sensor.Center, projectionBounds);
                Check(sensor.Radius >= farthest,
                    "welder sphere does not cover hologram: radius=" + sensor.Radius.ToString("F1") +
                    " farthest=" + farthest.ToString("F1"));
                welder.Enabled = true;
            }
            var waitWorking = Wait(() =>
            {
                foreach (var ship in ships) WorldApi.ChargeBatteries(ship);
                return welders.All(w => w.IsWorking);
            }, "all authored welders working", 60);
            while (waitWorking.MoveNext()) yield return waitWorking.Current;
            TickMetrics.Take(); // discard spawn/stocking/JIT frames; measure welding only
            FrameProbe.Take();
            // One coroutine step is the start barrier: all three tools are armed in one sim tick.
            foreach (var welder in welders)
                if (!WorldApi.ToolIsActivated(welder)) WorldApi.ToolStartShooting(welder);

            var fixtureCenter = projectionBounds.Center;
            const double fixtureRadius = 400.0;
            var basePhysicalBlocks = PhysicalFixtureGrids(fixtureCenter, fixtureRadius, preview)
                .Sum(WorldApi.CountBlocks);
            var baseFinishedBlocks = PhysicalFixtureGrids(fixtureCenter, fixtureRadius, preview)
                .Sum(WorldApi.CountFinished);
            var started = DateTime.UtcNow;
            var lastLog = DateTime.MinValue;
            Note("PROFILE WINDOW START: " + WelderCount + " welders armed in one tick; detector r=" +
                 WorldApi.SensorSphere(welders[0]).Radius.ToString("F1") + " m; probe sum=" +
                 welders.Sum(WorldApi.ProbeProjectedBlocks));
            TickMetrics.Take(); // the start probe is a full vanilla scan; keep it out of the window
            FrameProbe.Take();

            var built = 0;
            var harnessFrame = 0;
            while ((DateTime.UtcNow - started).TotalSeconds < MaxWeldSeconds)
            {
                // The harness runs inside the measured frame: keep its own work sparse so the
                // metrics describe welding, not the test bookkeeping.
                if (harnessFrame++ % 30 != 0)
                {
                    yield return null;
                    continue;
                }
                WorldApi.ChargeBatteries(platform);
                foreach (var ship in ships) WorldApi.ChargeBatteries(ship);
                platformDistributor.MarkForUpdate();
                platformDistributor.UpdateBeforeSimulation();

                var physical = PhysicalFixtureGrids(fixtureCenter, fixtureRadius, projector.ProjectedGrid);
                built = physical.Sum(WorldApi.CountBlocks) - basePhysicalBlocks;
                var finished = physical.Sum(WorldApi.CountFinished) - baseFinishedBlocks;
                var allRuntimeBlocksFinished = finished == built;
                if (projector.ProjectedGrid == null && built >= ExpectedRuntimeBlocks && allRuntimeBlocksFinished) break;
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 10)
                {
                    lastLog = DateTime.UtcNow;
                    Note("profiling: elapsed=" + (DateTime.UtcNow - started).TotalSeconds.ToString("F0") +
                         "s, built=" + built + "/" + expected +
                         ", finished=" + finished + ", physical-grids=" + physical.Count);
                }
                yield return null;
            }

            var weldingMetrics = TickMetrics.Take();
            var simWork = FrameProbe.Take();
            var finalPhysical = PhysicalFixtureGrids(fixtureCenter, fixtureRadius, projector.ProjectedGrid);
            built = finalPhysical.Sum(WorldApi.CountBlocks) - basePhysicalBlocks;
            var finalFinished = finalPhysical.Sum(WorldApi.CountFinished) - baseFinishedBlocks;
            Check(projector.ProjectedGrid == null && built >= ExpectedRuntimeBlocks && finalFinished == built,
                "real welder did not finish projection in " + MaxWeldSeconds + "s: runtime blocks=" + built +
                ", raw preview cells=" + expected + ", finished=" +
                finalFinished + ", physical-grids=" + finalPhysical.Count + ", probe=" +
                welders.Sum(WorldApi.ProbeProjectedBlocks));
            Note("PROFILE WINDOW END: projection complete; runtime blocks=" + built +
                 ", raw preview cells=" + expected + " in " +
                 (DateTime.UtcNow - started).TotalSeconds.ToString("F1") + "s | " +
                 weldingMetrics.Format() + " | " + simWork);
        }

        private static double FarthestCornerDistance(Vector3D center, BoundingBoxD box)
        {
            var dx = Math.Max(Math.Abs(center.X - box.Min.X), Math.Abs(center.X - box.Max.X));
            var dy = Math.Max(Math.Abs(center.Y - box.Min.Y), Math.Abs(center.Y - box.Max.Y));
            var dz = Math.Max(Math.Abs(center.Z - box.Min.Z), Math.Abs(center.Z - box.Max.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static Dictionary<string, int> RuntimeComponentsNeeded(
            IEnumerable<Sandbox.Game.Entities.Cube.MySlimBlock> blocks, int multiplier)
        {
            var needs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in blocks)
            {
                var components = block.BlockDefinition?.Components;
                if (components == null) continue;
                foreach (var component in components)
                {
                    var subtype = component.Definition?.Id.SubtypeName;
                    if (string.IsNullOrEmpty(subtype)) continue;
                    int previous;
                    needs.TryGetValue(subtype, out previous);
                    needs[subtype] = previous + component.Count * Math.Max(1, multiplier);
                }
            }
            return needs;
        }

        private static List<MyCubeGrid> PhysicalFixtureGrids(Vector3D center, double radius, MyCubeGrid preview)
        {
            return WorldApi.GridsInSphere(center, radius)
                .Where(grid => grid != null && grid != preview && !grid.MarkedForClose && !grid.Closed)
                .ToList();
        }

        public override void Cleanup()
        {
            try { RestoreRuntimeConfig(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                RestoreRuntimeConfig();
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    var namedTestGrid = (grid.Name ?? "").StartsWith(WorldApi.EntityPrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                        (grid.DisplayName ?? "").StartsWith(WorldApi.EntityPrefix,
                            StringComparison.OrdinalIgnoreCase);
                    var nearbyDebris = _fixturePosition.HasValue && grid.BlocksCount <= 4 &&
                        Vector3D.DistanceSquared(grid.PositionComp.GetPosition(), _fixturePosition.Value) <= 150 * 150;
                    if (namedTestGrid || nearbyDebris)
                    {
                        Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                        grid.Close();
                    }
                }
            }
            finally { base.CleanupLeftovers(); }
        }

        private void RestoreRuntimeConfig()
        {
            if (!_captured) return;
            RuntimePluginControls.SetWelderRadiusMultiplier(_initialWelderMultiplier);
            RuntimePluginControls.SetFreezerEnabled(_initialFreezerEnabled);
            _captured = false;
        }
    }
}
