using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
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
    /// A real ship welded from a projection by six welder ships at once, one on each side.
    ///
    /// The blueprint is the grid called <see cref="BlueprintName"/> that the operator placed in the
    /// world - a full warship with thrusters, turrets, reactors and conveyors, not a solid cube. It
    /// is copied into the projector of the reactor-backed platform from the save, in place of the
    /// blueprint that platform was authored with, and aligned so one of its armor blocks sits right
    /// against one of the platform's: that is the block the welders start from, and everything else
    /// grows out of it. The ship in the world is only read, never touched.
    ///
    /// The fixture is moved away from the operator's originals; the welder ships are parked outside
    /// the hologram with one deliberately huge detector sphere, so every projection scan stresses the
    /// real vanilla welder path without moving the hologram or steering the ships.
    /// </summary>
    public sealed class WelderPerfScenario : TestScenario
    {
        public const string ScenarioName = "welder_perf";
        private const string ProjectionResource = "SentisTests.Resources.PerfProjection.xml";
        private const string WelderResource = "SentisTests.Resources.PerfWelderShip.xml";
        private const string BlueprintName = "Spitfire Evolution (Vanilla)";
        private const float RadiusMultiplier = 100f;
        private const int WelderCount = 6;
        private const int MaxWeldSeconds = 1200;

        private bool _captured;
        private bool _initialFreezerEnabled;
        private float _initialWelderMultiplier;
        private bool _initialOwnAllDlcs;
        private Vector3D? _fixturePosition;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => MaxWeldSeconds + 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused("welder_perf start");
            _initialFreezerEnabled = RuntimePluginControls.FreezerEnabled;
            _initialWelderMultiplier = RuntimePluginControls.WelderRadiusMultiplier;
            _initialOwnAllDlcs = Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS;
            _captured = true;

            // The ship carries a few DLC blocks, and the welders belong to a test identity with no
            // Steam account behind it, which owns no DLC: the projector would never build those
            // blocks, and the ones behind them could never be reached. On a live server the
            // players' own DLCs decide that; here every DLC counts as owned for the run.
            Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS = true;

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

            var source = MyEntities.GetEntities().OfType<MyCubeGrid>()
                .FirstOrDefault(g => !g.MarkedForClose &&
                                     string.Equals(g.DisplayName, BlueprintName, StringComparison.OrdinalIgnoreCase));
            Check(source != null, "there is no grid called " + BlueprintName + " in the world");
            Check(source.GridSizeEnum == MyCubeSize.Large, BlueprintName + " is not a large grid, the platform is");
            Note(UseAsBlueprint(platformOb, source));

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
            var buildable = preview.CubeBlocks.Cast<MySlimBlock>()
                .Count(block => projector.CanBuild(block, true) == BuildCheckResult.OK);
            Check(buildable > 0, "the " + BlueprintName + " hologram has no buildable contact block");
            Note(BlueprintName + " hologram up: " + expected + " blocks, " + buildable + " buildable now");

            var projectionBounds = preview.PositionComp.WorldAABB;
            var shipPositions = new[]
            {
                new Vector3D(projectionBounds.Max.X + 20.0, projectionBounds.Center.Y, projectionBounds.Center.Z),
                new Vector3D(projectionBounds.Min.X - 20.0, projectionBounds.Center.Y, projectionBounds.Center.Z),
                new Vector3D(projectionBounds.Center.X, projectionBounds.Max.Y + 20.0, projectionBounds.Center.Z),
                new Vector3D(projectionBounds.Center.X, projectionBounds.Min.Y - 20.0, projectionBounds.Center.Z),
                new Vector3D(projectionBounds.Center.X, projectionBounds.Center.Y, projectionBounds.Max.Z + 20.0),
                new Vector3D(projectionBounds.Center.X, projectionBounds.Center.Y, projectionBounds.Min.Z - 20.0),
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
            // One coroutine step is the start barrier: all the tools are armed in one sim tick.
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
                if (projector.ProjectedGrid == null && built >= expected && allRuntimeBlocksFinished) break;
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 10)
                {
                    lastLog = DateTime.UtcNow;
                    Note("profiling: elapsed=" + (DateTime.UtcNow - started).TotalSeconds.ToString("F0") +
                         "s, built=" + built + "/" + expected +
                         ", finished=" + finished + ", physical-grids=" + physical.Count + " | " +
                         MixedWeldScenario.PluginCounters());
                }
                yield return null;
            }

            var weldingMetrics = TickMetrics.Take();
            var simWork = FrameProbe.Take();
            var finalPhysical = PhysicalFixtureGrids(fixtureCenter, fixtureRadius, projector.ProjectedGrid);
            built = finalPhysical.Sum(WorldApi.CountBlocks) - basePhysicalBlocks;
            var finalFinished = finalPhysical.Sum(WorldApi.CountFinished) - baseFinishedBlocks;
            Check(projector.ProjectedGrid == null && built >= expected && finalFinished == built,
                "real welder did not finish projection in " + MaxWeldSeconds + "s: runtime blocks=" + built +
                ", raw preview cells=" + expected + ", finished=" +
                finalFinished + ", physical-grids=" + finalPhysical.Count + ", probe=" +
                welders.Sum(WorldApi.ProbeProjectedBlocks));
            Note("PROFILE WINDOW END: projection complete; runtime blocks=" + built +
                 ", raw preview cells=" + expected + " in " +
                 (DateTime.UtcNow - started).TotalSeconds.ToString("F1") + "s | " +
                 weldingMetrics.Format() + " | " + simWork);
        }

        /// <summary>
        /// Puts a copy of <paramref name="source"/> into the platform's projector, in place of the
        /// blueprint the platform was authored with, and picks the offset that makes it touch.
        ///
        /// The projector lays the blueprint out in its own axes with the first block of the blueprint
        /// at its own cell less the offset. So an armor block of the ship goes first, and the offset
        /// is chosen to put it next to an armor block of the platform - on a face of the ship open to
        /// the outside, and with no block of the platform ending up inside the ship.
        /// </summary>
        private string UseAsBlueprint(MyObjectBuilder_CubeGrid platformOb, MyCubeGrid source)
        {
            var projectorOb = platformOb.CubeBlocks.OfType<MyObjectBuilder_ProjectorBase>().FirstOrDefault();
            Check(projectorOb != null, "the authored platform has no projector");
            var at = (Vector3I)projectorOb.Min;
            Matrix toPlatform;
            new MyBlockOrientation(projectorOb.BlockOrientation.Forward, projectorOb.BlockOrientation.Up)
                .GetMatrix(out toPlatform);
            var toBlueprint = Matrix.Transpose(toPlatform);

            // The platform as the projector sees it: its cells relative to the projector, turned into
            // the blueprint's axes.
            var platformCells = new List<Vector3I>();
            var platformArmor = new List<Vector3I>();
            foreach (var block in platformOb.CubeBlocks)
            {
                var definition = MyDefinitionManager.Static.GetCubeBlockDefinition(block.GetId());
                var min = (Vector3I)block.Min;
                Vector3I max;
                MySlimBlock.ComputeMax(definition,
                    new MyBlockOrientation(block.BlockOrientation.Forward, block.BlockOrientation.Up), ref min, out max);
                foreach (var cell in Cells(min, max))
                    platformCells.Add(Vector3I.Round(Vector3.TransformNormal((Vector3)(cell - at), toBlueprint)));
                if (min == max && IsArmorCube(definition))
                    platformArmor.Add(Vector3I.Round(Vector3.TransformNormal((Vector3)(min - at), toBlueprint)));
            }
            Check(platformArmor.Count > 0, "the authored platform has no armor block to weld from");

            var shipCells = new HashSet<Vector3I>();
            foreach (var block in source.CubeBlocks)
                foreach (var cell in Cells(block.Min, block.Max))
                    shipCells.Add(cell);

            // seed: an armor block of the ship, first in the blueprint; offset: where that puts it
            Vector3I? seed = null;
            var offset = Vector3I.Zero;
            foreach (var block in source.CubeBlocks.Where(b => b.Min == b.Max && IsArmorCube(b.BlockDefinition))
                         .OrderBy(b => b.Min.X).ThenBy(b => b.Min.Y).ThenBy(b => b.Min.Z))
            {
                foreach (var outward in Base6Directions.IntDirections)
                {
                    if (!OpenToOutside(block.Min, outward, shipCells, source.Min, source.Max)) continue;
                    foreach (var armor in platformArmor)
                    {
                        // the platform shifted so that this armor block of it lands next to the seed
                        var shift = block.Min + outward - armor;
                        if (platformCells.Any(cell => shipCells.Contains(cell + shift))) continue;
                        seed = block.Min;
                        offset = shift - block.Min;
                        break;
                    }
                    if (seed.HasValue) break;
                }
                if (seed.HasValue) break;
            }
            Check(seed.HasValue, "found no armor block of " + BlueprintName + " the platform can be put against");
            Check(Math.Abs(offset.X) <= 50 && Math.Abs(offset.Y) <= 50 && Math.Abs(offset.Z) <= 50,
                "the projection offset " + offset + " is beyond what a projector takes");

            var blueprint = (MyObjectBuilder_CubeGrid)source.GetObjectBuilder(true).Clone();
            var first = blueprint.CubeBlocks.First(b => (Vector3I)b.Min == seed.Value);
            blueprint.CubeBlocks.Remove(first);
            blueprint.CubeBlocks.Insert(0, first);
            // what the projector does to a blueprint handed to it through the API
            blueprint.IsStatic = false;
            blueprint.DestructibleBlocks = false;
            foreach (var block in blueprint.CubeBlocks)
            {
                block.Owner = 0;
                block.ShareMode = MyOwnershipShareModeEnum.None;
                block.EntityId = 0;
                var functional = block as MyObjectBuilder_FunctionalBlock;
                if (functional != null) functional.Enabled = false;
            }
            MyEntities.RemapObjectBuilder(blueprint);

            projectorOb.ProjectedGrid = null;
            projectorOb.ProjectedGrids = new List<MyObjectBuilder_CubeGrid> { blueprint };
            projectorOb.ProjectionOffset = offset;
            projectorOb.ProjectionRotation = Vector3I.Zero;
            return BlueprintName + " (" + source.BlocksCount + " blocks) goes into the projector, starting from the armor block at " +
                   seed.Value + ", projection offset " + offset;
        }

        private static bool IsArmorCube(MyCubeBlockDefinition definition)
        {
            var subtype = definition?.Id.SubtypeName ?? "";
            return definition != null && definition.Size == Vector3I.One &&
                   (subtype == "LargeBlockArmorBlock" || subtype == "LargeHeavyBlockArmorBlock");
        }

        /// <summary>Whether nothing of the ship stands between this cell and the outside in that direction.</summary>
        private static bool OpenToOutside(Vector3I cell, Vector3I direction, HashSet<Vector3I> ship, Vector3I min, Vector3I max)
        {
            for (var next = cell + direction;
                 next.X >= min.X && next.Y >= min.Y && next.Z >= min.Z && next.X <= max.X && next.Y <= max.Y && next.Z <= max.Z;
                 next += direction)
                if (ship.Contains(next)) return false;
            return true;
        }

        private static IEnumerable<Vector3I> Cells(Vector3I a, Vector3I b)
        {
            var min = Vector3I.Min(a, b);
            var max = Vector3I.Max(a, b);
            for (var x = min.X; x <= max.X; x++)
                for (var y = min.Y; y <= max.Y; y++)
                    for (var z = min.Z; z <= max.Z; z++)
                        yield return new Vector3I(x, y, z);
        }

        private static double FarthestCornerDistance(Vector3D center, BoundingBoxD box)
        {
            var dx = Math.Max(Math.Abs(center.X - box.Min.X), Math.Abs(center.X - box.Max.X));
            var dy = Math.Max(Math.Abs(center.Y - box.Min.Y), Math.Abs(center.Y - box.Max.Y));
            var dz = Math.Max(Math.Abs(center.Z - box.Min.Z), Math.Abs(center.Z - box.Max.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static Dictionary<string, int> RuntimeComponentsNeeded(
            IEnumerable<MySlimBlock> blocks, int multiplier)
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
            Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS = _initialOwnAllDlcs;
            _captured = false;
        }
    }
}
