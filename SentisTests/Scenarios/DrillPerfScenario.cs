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
using VRage.Game.Entity;
using VRageMath;
using MyObjectBuilder_Drill = Sandbox.Common.ObjectBuilders.MyObjectBuilder_Drill;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Ship drilling benchmark. The operator's DRILL_TEST ship (36 large drills over one conveyor
    /// network into a cargo container, reactor, cockpit, no thrusters) is spawned
    /// <see cref="GridCount"/> times on a lattice next to where it was parked on the planet, each
    /// copy hovering a few metres over the ground with the same attitude. The drills are switched
    /// on at random moments within one drill cycle, as players would, and the ships are pushed
    /// into the ground at <see cref="DescendMps"/> - the harness plays the thrusters. The drilling
    /// is the profiling window. The dug-out terrain is reverted to the generated planet at the
    /// start and the end of the run, so every run drills the same rock.
    /// </summary>
    public sealed class DrillPerfScenario : TestScenario
    {
        public const string ScenarioName = "drill_perf";
        internal const string ResourceName = "SentisTests.Resources.DrillTest.xml";
        private const string GridPrefix = "drill-perf-";
        private const int GridCount = 32;
        private const int GridsPerRow = 8;
        private const double LatticeStep = 40.0;
        // The lattice starts this far from the authored ship, so the operator's own copy and the
        // ground under it are never touched.
        private const double LatticeOffset = 120.0;
        private const double ClearanceM = 3.0;
        private const double DescendMps = 0.5;
        private const double ApproachSeconds = 10;
        private const double WindowSeconds = 120;
        private const int LogEverySeconds = 10;
        private const double SpawnSettleSeconds = 10;
        private const int UnloadEveryFrames = 10;
        // Thrust the harness may use, in multiples of the ship's weight, and how fast it closes
        // the gap to the wanted velocity (1/s).
        private const double MaxThrustG = 1.5;
        private const double VelocityGain = 2.0;
        // Terrain reverted around each copy: this far sideways and above the start, and down to
        // the deepest the ship can get in the run plus a margin.
        private const double RevertSideM = 20;
        private const double RevertAboveM = 10;
        private const double RevertBelowMarginM = 20;

        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private readonly List<Vector3D> _sites = new List<Vector3D>();
        private MyPlanet _planet;
        private Vector3D _center;
        private bool _captured;
        private bool _initialFreezerEnabled;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(ApproachSeconds + WindowSeconds) + 400;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused("drill_perf start");
            _initialFreezerEnabled = RuntimePluginControls.FreezerEnabled;
            _captured = true;
            RuntimePluginControls.SetFreezerEnabled(false);
            Note("Freezer disabled for the benchmark");

            var template = WorldApi.LoadAuthoredGrid(ResourceName, WorldApi.EntityPrefix + GridPrefix + "template");
            var pose = template.PositionAndOrientation.Value;
            var authored = (Vector3D)pose.Position;
            _planet = MyGamePruningStructure.GetClosestPlanet(authored);
            Check(_planet != null, "no planet near the authored drill ship");
            var planetCenter = _planet.PositionComp.GetPosition();
            var up0 = Vector3D.Normalize(authored - planetCenter);
            var forward0 = (Vector3D)(Vector3)pose.Forward;
            var gridUp0 = (Vector3D)(Vector3)pose.Up;
            var east = Vector3D.Normalize(forward0 - up0 * Vector3D.Dot(forward0, up0));
            var north = Vector3D.Cross(up0, east);
            _center = TestRunner.RunOrigin ?? authored + east * LatticeOffset;

            // Where the drill heads sit relative to the grid origin, along the local vertical; the
            // copies are placed so the lowest head hovers ClearanceM over the ground below it.
            var gridSize = template.GridSizeEnum == MyCubeSize.Large ? 2.5 : 0.5;
            var drillCells = template.CubeBlocks.OfType<MyObjectBuilder_Drill>().Select(b => (Vector3I)b.Min).ToList();
            Check(drillCells.Count > 0, "DRILL_TEST carries no drills");
            var lowestDrillY = drillCells.Min(c => c.Y);
            Note("planet " + _planet.StorageName + ", authored ship " + (authored - planetCenter).Length().ToString("F0") +
                 " m from its centre, " + drillCells.Count + " drills, grid up · vertical = " +
                 Vector3D.Dot(gridUp0, up0).ToString("F3"));

            for (var i = 0; i < GridCount; i++)
            {
                var guess = _center + east * ((i % GridsPerRow - (GridsPerRow - 1) / 2.0) * LatticeStep) +
                            north * ((i / GridsPerRow - (GridCount / GridsPerRow - 1) / 2.0) * LatticeStep);
                var surface = _planet.GetClosestSurfacePointGlobal(ref guess);
                var up = Vector3D.Normalize(surface - planetCenter);
                var forward = Rotate(forward0, up0, up);
                var gridUp = Rotate(gridUp0, up0, up);
                // Grid-local Y is gridUp; the drills point along grid Down and their lowest cell
                // is the Min row, whose bottom face is half a cell below its centre.
                var bottomAboveOrigin = (lowestDrillY - 0.5) * gridSize * Vector3D.Dot(gridUp, up);
                var position = surface + up * (ClearanceM - bottomAboveOrigin);
                _sites.Add(surface);

                var ob = WorldApi.LoadAuthoredGrid(ResourceName, WorldApi.EntityPrefix + GridPrefix + i.ToString("D2"));
                ob.PositionAndOrientation = new MyPositionAndOrientation(position, (Vector3)forward, (Vector3)gridUp);
                ob.IsStatic = false;
                ob.LinearVelocity = Vector3.Zero;
                ob.AngularVelocity = Vector3.Zero;
                foreach (var drill in ob.CubeBlocks.OfType<MyObjectBuilder_Drill>()) drill.Enabled = false;
                RevertTerrain(i);
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                _grids.Add(grid);
                yield return null;
            }

            var drills = new List<List<MyShipDrill>>(GridCount);
            foreach (var grid in _grids)
            {
                WorldApi.EnsureDistributor(grid);
                var gridDrills = WorldApi.FindFunctionals<MyShipDrill>(grid);
                Check(gridDrills.Count == drillCells.Count, grid.DisplayName + " has " + gridDrills.Count + " drills");
                drills.Add(gridDrills);
            }

            var settle = WaitForSeconds(SpawnSettleSeconds, "spawned drill ships settle");
            while (settle.MoveNext())
            {
                HoldAll(0);
                yield return settle.Current;
            }
            Check(_grids.All(g => g.Physics != null && !g.IsStatic), "a drill ship lost its physics");
            Check(drills.All(d => d.All(x => x.ResourceSink.IsPowered)), "not every drill is powered after spawn");

            var inventories = _grids.Select(InventoriesOf).ToList();
            var startOre = inventories.Sum(CountOre);
            // A ship fills its drills and cargo in well under a minute and then stops cutting, so
            // the cargo is unloaded as if a conveyor led to a base: one ship every
            // UnloadEveryFrames frames, round robin.
            var cargo = _grids.Select(g => WorldApi.FindFunctionals<MyCargoContainer>(g)
                .Select(c => c.GetInventory(0)).First(inv => inv != null)).ToList();
            var unloaded = 0.0;
            var startFloating = CountFloatingOre();

            // A player switches the drills of a ship on as a group, and ships start whenever their
            // pilots do: one random frame per ship within one 90-frame drill cycle.
            var random = new Random(12345);
            var startFrame = _grids.Select(_ => random.Next(0, 90)).ToArray();
            var frame = 0;
            var started = DateTime.UtcNow;
            var windowStarted = DateTime.MinValue;
            var lastLog = DateTime.UtcNow;
            Note("switching " + GridCount * drillCells.Count + " drills on over 90 frames and descending at " + DescendMps + " m/s");
            while (true)
            {
                for (var g = 0; g < GridCount; g++)
                {
                    if (frame == startFrame[g])
                        foreach (var drill in drills[g]) ((Sandbox.ModAPI.IMyFunctionalBlock)drill).Enabled = true;
                }
                HoldAll(DescendMps, startFrame, frame);
                if (frame % UnloadEveryFrames == 0)
                {
                    var unload = cargo[frame / UnloadEveryFrames % GridCount];
                    unloaded += CountOre(new List<MyInventory> { unload });
                    unload.Clear();
                }
                frame++;

                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                if (windowStarted == DateTime.MinValue && elapsed >= ApproachSeconds)
                {
                    windowStarted = DateTime.UtcNow;
                    TickMetrics.Take();
                    FrameProbe.Take();
                    Note("PROFILE WINDOW START: " + GridCount + " ships drilling");
                }
                if (windowStarted != DateTime.MinValue)
                {
                    var inWindow = (DateTime.UtcNow - windowStarted).TotalSeconds;
                    if (inWindow >= WindowSeconds) break;
                    if ((DateTime.UtcNow - lastLog).TotalSeconds >= LogEverySeconds)
                    {
                        lastLog = DateTime.UtcNow;
                        Note("drilling: " + inWindow.ToString("F0") + "s, ore " + ((inventories.Sum(CountOre) + unloaded - startOre) / 1000).ToString("F1") +
                             " t in ships, floating ore objects " + (CountFloatingOre() - startFloating) + ", depth below start " +
                             DepthSummary(planetCenter) + ", drills working " + drills.Sum(d => d.Count(x => x.IsWorking)));
                    }
                }
                yield return null;
            }

            var metrics = TickMetrics.Take();
            var simWork = FrameProbe.Take();
            var mined = inventories.Sum(CountOre) + unloaded - startOre;
            var floating = CountFloatingOre() - startFloating;
            var depth = DepthSummary(planetCenter);
            Check(mined > 0, "the ships mined no ore");
            Note("PROFILE WINDOW END: " + GridCount + " ships mined " + (mined / 1000).ToString("F1") + " t in " + WindowSeconds +
                 "s, floating ore objects " + floating + ", depth below start " + depth + " | " + CutPhysicsCounters() + " | " + metrics.Format() + " | " + simWork);

            foreach (var gridDrills in drills)
            foreach (var drill in gridDrills)
                ((Sandbox.ModAPI.IMyFunctionalBlock)drill).Enabled = false;
            // Let the cutouts already running on worker threads land before the terrain is reset.
            for (var i = 0; i < 180; i++)
            {
                HoldAll(0);
                yield return null;
            }
        }

        /// <summary>Cells DrillCutPhysics (SentisOptimisations) kept, invalidated or left empty, if it is loaded.</summary>
        private static string CutPhysicsCounters()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Optimizer.Optimizations.DrillCutPhysics")).FirstOrDefault(t => t != null);
            if (type == null) return "drill cut physics: not loaded";
            long Read(string name) => (long)(type.GetField(name)?.GetValue(null) ?? 0L);
            return "drill cut physics: kept " + Read("KeptCells") + ", invalidated " + Read("InvalidatedCells") +
                   ", empty unchanged " + Read("UnchangedEmptyCells") + ", untouched " + Read("UntouchedCells") +
                   ", meshes held " + Read("HeldMeshCount") + ", shape updates " + Read("ShapeUpdatesApplied");
        }

        /// <summary>
        /// The harness plays the thrusters with dampeners and the gyroscopes: a force that holds
        /// the ship against gravity and steers its velocity toward <paramref name="speed"/> straight
        /// down, capped at <see cref="MaxThrustG"/> of its weight, so a ship sitting on rock presses
        /// on it with a bounded force, as a pilot holding "down" would, instead of being driven into
        /// it at a fixed speed. Rotation is held.
        /// </summary>
        private void HoldAll(double speed, int[] startFrame = null, int frame = 0)
        {
            var planetCenter = _planet.PositionComp.GetPosition();
            for (var g = 0; g < _grids.Count; g++)
            {
                var physics = _grids[g].Physics;
                if (physics == null) continue;
                var position = _grids[g].PositionComp.GetPosition();
                var down = Vector3D.Normalize(planetCenter - position);
                var moving = startFrame != null && frame >= startFrame[g];
                var gravity = (Vector3D)Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(position);
                var desired = down * (moving ? speed : 0);
                var thrust = (-gravity + (desired - (Vector3D)physics.LinearVelocity) * VelocityGain) * physics.Mass;
                var limit = MaxThrustG * physics.Mass * gravity.Length();
                if (thrust.Length() > limit) thrust = Vector3D.Normalize(thrust) * limit;
                physics.AddForce(VRage.Game.Components.MyPhysicsForceType.APPLY_WORLD_FORCE, (Vector3)thrust, null, null);
                physics.AngularVelocity = Vector3.Zero;
            }
        }

        private string DepthSummary(Vector3D planetCenter)
        {
            var depths = new List<double>(_grids.Count);
            for (var g = 0; g < _grids.Count; g++)
            {
                if (_grids[g].Closed) continue;
                var start = (_sites[g] - planetCenter).Length();
                depths.Add(start - (_grids[g].PositionComp.GetPosition() - planetCenter).Length());
            }
            depths.Sort();
            return depths.Count == 0 ? "-" : "min " + depths[0].ToString("F1") + " / median " + depths[depths.Count / 2].ToString("F1") +
                                             " / max " + depths[depths.Count - 1].ToString("F1") + " m";
        }

        /// <summary>Rotates v by the rotation that takes unit vector from onto unit vector to.</summary>
        private static Vector3D Rotate(Vector3D v, Vector3D from, Vector3D to)
        {
            var axis = Vector3D.Cross(from, to);
            var sin = axis.Length();
            var cos = Vector3D.Dot(from, to);
            if (sin < 1e-12) return v;
            axis /= sin;
            var angle = Math.Atan2(sin, cos);
            return v * Math.Cos(angle) + Vector3D.Cross(axis, v) * Math.Sin(angle) +
                   axis * Vector3D.Dot(axis, v) * (1 - Math.Cos(angle));
        }

        /// <summary>
        /// Resets the terrain around copy <paramref name="index"/> to the generated planet: deletes
        /// the stored voxel edits in a box around its column, so the planet's generator shows
        /// through again. Voxel maps are axis aligned, 1 m voxels.
        /// </summary>
        private void RevertTerrain(int index)
        {
            var site = _sites[index];
            var up = Vector3D.Normalize(site - _planet.PositionComp.GetPosition());
            var depth = DescendMps * (ApproachSeconds + WindowSeconds) + RevertBelowMarginM;
            var box = BoundingBoxD.CreateInvalid();
            foreach (var h in new[] { RevertAboveM, -depth })
            foreach (var dx in new[] { -RevertSideM, RevertSideM })
            foreach (var dy in new[] { -RevertSideM, RevertSideM })
            foreach (var dz in new[] { -RevertSideM, RevertSideM })
                box.Include(site + up * h + new Vector3D(dx, dy, dz));
            var corner = _planet.PositionLeftBottomCorner;
            var min = Vector3I.Floor(box.Min - corner) + _planet.StorageMin;
            var max = Vector3I.Ceiling(box.Max - corner) + _planet.StorageMin;
            min = Vector3I.Max(min, Vector3I.Zero);
            max = Vector3I.Min(max, _planet.Storage.Size - 1);
            _planet.Storage.DeleteRange(VRage.Voxels.MyStorageDataTypeFlags.ContentAndMaterial, min, max, true);
        }

        private int CountFloatingOre()
        {
            var sphere = new BoundingSphereD(_center, LatticeStep * GridsPerRow);
            var found = MyEntities.GetTopMostEntitiesInSphere(ref sphere);
            try { return found.Count(e => e is MyFloatingObject); }
            finally { found.Clear(); }
        }

        private static List<MyInventory> InventoriesOf(MyCubeGrid grid)
        {
            var result = new List<MyInventory>();
            foreach (var block in grid.GetFatBlocks())
            {
                if (!block.HasInventory) continue;
                for (var i = 0; i < block.InventoryCount; i++)
                {
                    var inventory = block.GetInventory(i);
                    if (inventory != null) result.Add(inventory);
                }
            }
            return result;
        }

        private static double CountOre(List<MyInventory> inventories)
        {
            double total = 0;
            foreach (var inventory in inventories)
            foreach (var item in inventory.GetItems())
                if (item.Content is MyObjectBuilder_Ore) total += (double)item.Amount;
            return total;
        }

        public override void Cleanup()
        {
            try
            {
                RestoreRuntimeConfig();
                if (_planet != null)
                {
                    RemoveFloatingObjects();
                    for (var i = 0; i < _sites.Count; i++) RevertTerrain(i);
                }
            }
            finally { base.Cleanup(); }
        }

        private void RemoveFloatingObjects()
        {
            var sphere = new BoundingSphereD(_center, LatticeStep * GridsPerRow);
            var found = MyEntities.GetTopMostEntitiesInSphere(ref sphere);
            foreach (var entity in found.OfType<MyFloatingObject>().ToList())
                if (!entity.MarkedForClose) entity.Close();
            found.Clear();
        }

        public override void CleanupLeftovers()
        {
            try
            {
                RestoreRuntimeConfig();
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!(grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + GridPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                    grid.Close();
                }
            }
            finally { base.CleanupLeftovers(); }
        }

        private void RestoreRuntimeConfig()
        {
            if (!_captured) return;
            RuntimePluginControls.SetFreezerEnabled(_initialFreezerEnabled);
            _captured = false;
        }
    }
}
