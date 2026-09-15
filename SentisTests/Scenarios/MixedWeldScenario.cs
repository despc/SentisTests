using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Ingame;
using VRageMath;
using BuildCheckResult = Sandbox.ModAPI.BuildCheckResult;
using SpaceWelder = SpaceEngineers.Game.Entities.Blocks.MyShipWelder;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Mixed-block edition of projector_weld:
    ///  1. spawns the operator-authored projector platform and a 22-welder construction ship;
    ///  2. drives the ship over the mixed projection using vanilla welders and conveyors only;
    ///  3. asserts every projected block and functional subtype exists after construction;
    ///  4. keeps all supply state in the embedded ship blueprint and only observes consumption.
    /// </summary>
    public class MixedWeldScenario : TestScenario
    {
        public const string ScenarioName = "mixed_weld";

        // Cleanup may remove only grids created during this run; pre-existing player grids are never debris.
        private readonly HashSet<long> _preexistingGridIds = new HashSet<long>();
        private Vector3D? _platformPos;


        // ST-NEW-WELDER carries 22 tools (ST-WELDER had 5). Cross-checked against the template on
        // load, so a redrawn blueprint fails with a message that says what to bump instead of
        // dying on a stale magic number far from the cause.
        private const int WelderCount = 22;
        // Half of one large-grid cell. A cell's top face is derived from the block's centre plus
        // this, which is what keeps the hover height honest for the functional models that hang
        // metres over their own cell (their WorldAABB is not the cell).
        private const double CellHalf = 1.25;
        // The operator's welding boat, hand-built in the world and pulled out of the save with
        // tools/extract_authored.py: 55 blocks, 22 welders, one stocked container - and, unlike a
        // plain template, its saved position and attitude, because he lined it up against the plate
        // by eye and that placement is part of the fixture.
        private const string ShipTemplateResource = "SentisTests.Resources.MixedShip.xml";
        // The thing to be built is hand-built too, for the same reason: block mix, orientations
        // and conveyor ports are what the operator placed in the world under the name
        // MIXED_PROJECTION, not something this file gets to invent. 81 large-grid blocks - 54
        // armor, 4 cargo containers, 2 reactors and one each of battery, thruster, cockpit, gyro,
        // assembler, programmable block, timer, jump drive, merge block, piston, stator, store
        // block, safe zone, remote control, hydrogen engine, wind turbine, missile launcher, warhead
        // and decoy) is loaded inside that projector, together with the ProjectionOffset and
        // ProjectionRotation the operator set - see the rig section in Run().
        private const string PlatformTemplateResource = "SentisTests.Resources.PlatformWithProjection.xml";


        public override string Name { get { return ScenarioName; } }

        public override int TimeoutSeconds { get { return 1200; } }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused("mixed_weld start");
            _preexistingGridIds.Clear();
            foreach (var existing in MyEntities.GetEntities().OfType<MyCubeGrid>())
                _preexistingGridIds.Add(existing.EntityId);

            var welderType = WorldApi.FindSubtype(MyCubeSize.Large, "shipwelder");
            var batteryType = WorldApi.FindSubtype(MyCubeSize.Large, "batteryblock");
            var cockpitType = WorldApi.FindSubtype(MyCubeSize.Large, "blockcockpit");
            var lightType = WorldApi.FindSubtype(MyCubeSize.Large, "light");
            var gyroType = WorldApi.FindSubtype(MyCubeSize.Large, "gyro");
            // SE renamed the gas tank to a hydrogen tank; the entity class is still MyGasTank
            var gasTankType = WorldApi.FindSubtype(MyCubeSize.Large, "hydrogentank");

            // --------------------------------------------------- the rig from the save
            // The deck, the projector on it and the blueprint loaded inside that projector are ONE
            // grid the operator built in the world: PLATFORM_WITH_PROJECTION. Everything that decides
            // where the hologram sits - the projector's cell, its orientation, the ProjectionOffset
            // and ProjectionRotation he dialed in - is his, not something this file derives. That
            // matters: every offset this test computed for itself either hung the volume in the air
            // (census NotConnected=80) or pushed blueprint cells into the deck (the last three blocks
            // of an earlier run died as IntersectedWithGrid, which is permanent for a projected cell).
            // Both grids come out of the save with the placement the operator left them in, because
            // which side of the plate the boat sits on is not something to guess: it is what he
            // built and what the test has to reproduce.
            var platformOb = WorldApi.LoadAuthoredGrid(PlatformTemplateResource,
                WorldApi.EntityPrefix + "mixed-platform");
            var projectedOb = platformOb.CubeBlocks.OfType<VRage.Game.MyObjectBuilder_ProjectorBase>()
                .SelectMany(p => p.ProjectedGrids ?? new List<MyObjectBuilder_CubeGrid>())
                .FirstOrDefault();
            Check(projectedOb != null && projectedOb.CubeBlocks != null && projectedOb.CubeBlocks.Count > 0,
                "the projector in " + PlatformTemplateResource + " carries no blueprint");
            var expected = projectedOb.CubeBlocks.Count;

            // The rig is a test bench, not a vehicle. Saved grids come back dynamic, and a dynamic
            // bench gets shoved across the sector by the construction boat brushing it - which is
            // what looks like "the welder approaches from the wrong side and pushes the projection".
            // Pin it, so a collision can never masquerade as a welding failure.
            platformOb.IsStatic = true;
            var platform = WorldApi.SpawnGrid(platformOb);
            _platformPos = WorldApi.PositionOf(platform);
            Track(platform);

            yield return WaitForTicks(60);

            var platformDist = WorldApi.EnsureDistributor(platform);
            var charged = WorldApi.ChargeBatteries(platform);
            Check(charged > 0, "no chargeable batteries on the platform from the save");
            // counted here, before a single block of the blueprint exists, so the final check can
            // tell the deck's own power and light apart from the ones the welders built
            var ownBatteries = WorldApi.FindFunctionals<MyBatteryBlock>(platform).Count;
            var ownLights = WorldApi.FindFunctionals<MyLightingBlock>(platform).Count;

            var projector = WorldApi.FindFunctional<MyProjectorBase>(platform);
            Check(projector != null, "no projector on the platform from the save");
            if (!projector.Enabled)
            {
                projector.Enabled = true;
            }

            yield return Wait(() =>
            {
                WorldApi.ChargeBatteries(platform);
                platformDist.MarkForUpdate();
                platformDist.UpdateBeforeSimulation();
                return projector.IsWorking && projector.ProjectedGrid != null;
            }, "projector powered and mixed projection loaded (" +
               WorldApi.DescribePower(platform) + ")", 90);

            var preview0 = projector.ProjectedGrid;
            Check(preview0 != null, "projection is not active");
            // The projector drops any blueprint cell that would collide with the platform
            // (bridge/projector cells), so assert against what it ACTUALLY materialized,
            // not the raw blueprint count. Per-type expectations come from the same source.
            expected = preview0.CubeBlocks.Count;
            var projectedCounts = preview0.CubeBlocks
                .GroupBy(b => b.BlockDefinition.Id.SubtypeName)
                .ToDictionary(g => g.Key, g => g.Count());
            var buildable0 = 0;
            foreach (var slimObj in preview0.CubeBlocks)
                if (projector.CanBuild((Sandbox.Game.Entities.Cube.MySlimBlock)slimObj, true) == BuildCheckResult.OK)
                    buildable0++;
            Note("mixed projection: " + preview0.CubeBlocks.Count + " preview blocks, " +
                 buildable0 + " weldable now (connectivity opens the rest)");
            Check(buildable0 >= 1, "not one block of the mixed projection is weldable (projection at " +
                                   WorldApi.PositionOf(preview0).ToString("F1") + ", census=" +
                                   BuildCheckCensus(projector) + ") - the rig from the save should already " +
                                   "place the hologram against the deck");

            // ------------------------------------------------------ welder boat
            // The ship is no longer described in code: it is spawned verbatim from the embedded
            // template further down, so there is no layout, block orientation or conveyor wiring
            // left to hand-maintain here.

            // the slab parks flush on the deck; its top face is open, so the boat spawns
            // FLIPPED above it (local up = world down) and welds top faces, sensor down.
            // Geometry is derived from the BLOCK CENTERS, NOT the grid's WorldAABB: tall
            // functional models (ship welder, gyro, cockpit) hang several metres above their
            // cell and inflate the AABB, which would make the boat hover metres too high.
            // A FLAT 1-cell block (AABB ~2.5m tall, no model overhang) has Max.Y = the true
            // slab top; the tall functionals (welder/gyro/cockpit) overhang their cell and
            // are excluded from both the top and the XZ extent.
            var slabBb = preview0.PositionComp.WorldAABB;
            double fMinX = double.MaxValue, fMaxX = double.MinValue;
            double fMinZ = double.MaxValue, fMaxZ = double.MinValue;
            double slabTopY = double.MinValue;
            int flatCount = 0;
            foreach (var slimObj in preview0.CubeBlocks)
            {
                var bb = slimObj.WorldAABB;
                var c = (bb.Min + bb.Max) * 0.5;
                if (bb.Max.Y - bb.Min.Y < 3.0)   // flat 1-cell block
                {
                    if (c.X < fMinX) fMinX = c.X; if (c.X > fMaxX) fMaxX = c.X;
                    if (c.Z < fMinZ) fMinZ = c.Z; if (c.Z > fMaxZ) fMaxZ = c.Z;
                    // the highest flat cell, because this blueprint is several layers thick and
                    // the boat has to clear the whole volume (a single layer makes this the same
                    // value the old flat slab derived)
                    slabTopY = Math.Max(slabTopY, bb.Max.Y);
                    flatCount++;
                }
            }
            Check(flatCount > 0, "no flat 1-cell block in the projection to derive the slab top from");
            // The blueprint is a PLATE that grows out of the deck's face: the deck fills one side
            // of it, the other side is open space. The open side is the welding face - a VERTICAL
            // wall. A 17 m hull has room to ride out in front of that wall with the welder disc
            // standing vertical against it and the boat circling each block in the wall's own
            // plane. Worked from above or below, the hull lives in the plate's own band and ploughs
            // the built blocks - which is what the operator kept seeing.
            // The blueprint is a PLATE; the working side is its BOTTOM FACE (see calibration below).
            var openDir = new Vector3D(0, -1, 0);
            // OPERATOR CALIBRATION 2026-09-14 (live rig, confirmed by eye): the welder plane is
            // PARALLEL to the projection surface — the boat works from BELOW the plate, tools
            // pointing straight up, spawn pose exactly the identity basis (fwd +Z, up +Y). The old
            // "open side of the wall" reasoning put the disc vertical and welded nothing.
            var toolUp = -openDir;
            var shipFwd = new Vector3D(0, 0, 1);
            var spawnPos = slabBb.Center + openDir * 25.0;
            // The boat is the operator's own working ship, spawned verbatim out of the embedded
            // template: block orientations, the conveyor topology and the inventory flags are
            // exactly as built in game.
            var shipOb = WorldApi.LoadAuthoredGrid(ShipTemplateResource,
                WorldApi.EntityPrefix + "mixed-ship");
            // Matched on the runtime OB type name: the serializer hands back the derived
            // object-builder for each block, and that spares us hard-coding its namespace.
            var inTemplate = shipOb.CubeBlocks.Count(b => b.GetType().Name == "MyObjectBuilder_ShipWelder");
            Check(inTemplate == WelderCount,
                "the template declares " + inTemplate + " welders but the test expects " + WelderCount +
                " - set WelderCount to the blueprint's number");
            shipOb.PositionAndOrientation = new MyPositionAndOrientation(spawnPos, shipFwd, toolUp);
            var ship = WorldApi.SpawnGrid(shipOb);
            Track(ship);

            ship.Physics.ForceActivate();
            ship.Physics.Activate();
            ship.Physics.LowSimulationQuality = false;

            yield return WaitForTicks(120); // fat-block promotion must finish before the distributor pass

            WorldApi.EnsureDistributor(ship);
            WorldApi.ChargeBatteries(ship);

            var welders = WorldApi.FindFunctionals<SpaceWelder>(ship);
            Check(welders.Count == WelderCount,
                "expected " + WelderCount + " welders on the boat, found " + welders.Count);

            // Ownership is asserted, not assumed: vanilla Build() carries the welder's own OwnerId
            // into the block it starts, so tools owned by nobody cannot weld a single block even
            // when the hologram is connected, powered, in reach and fully stocked.
            var ownerId = WorldApi.PlayerIdentityId();
            var foreign = welders
                .Where(w => w.OwnerId != ownerId || w.BuiltBy != ownerId)
                .Select(w => w.EntityId + " owner=" + w.OwnerId + " builtBy=" + w.BuiltBy)
                .Take(3).ToList();
            Check(foreign.Count == 0, "welders are not owned and built by player " + ownerId +
                                      " (" + string.Join("; ", foreign) + ")");

            // OPERATOR DECISION 2026-09-15: the template arrives stocked (container holds the
            // bulk set, every welder its own working set) and we DO NOT TOUCH the inventories at
            // runtime anymore — AddItems kept fighting the conveyor for slots and interrupted
            // the tools' pickups. Everything needed is prepared ahead of time; here we only
            // READ the supply state and report it.
            var needs = WorldApi.ComponentsNeeded(projectedOb.CubeBlocks, 2);
            var container = WorldApi.FindFunctional<MyCargoContainer>(ship);
            Check(container != null, "cargo container not found on the boat");
            long containerBefore = 0;
            foreach (var st in needs.Keys) containerBefore += WorldApi.CountComponent(container.GetInventory(), st);
            long cargoBefore = 0;
            foreach (var w in welders)
                foreach (var st in needs.Keys) cargoBefore += WorldApi.CountComponent(w.GetInventory(), st);
            Note("supply (read-only): container=" + containerBefore +
                 ", welders=" + cargoBefore);

            yield return Wait(() =>
            {
                WorldApi.ChargeBatteries(ship);
                var ok = true;
                foreach (var w in welders)
                {
                    w.Enabled = true;
                    if (!w.IsWorking) ok = false;
                }
                return ok;
            }, "all " + WelderCount + " welders working (" + WorldApi.DescribePower(ship) + ")", 90);

            foreach (var w in welders)
            {
                if (!WorldApi.ToolIsActivated(w))
                {
                    w.Enabled = false;
                    yield return WaitForTicks(5);
                    w.Enabled = true;
                    yield return WaitForTicks(10);
                    if (!WorldApi.ToolIsActivated(w))
                        WorldApi.ToolStartShooting(w);
                }
            }

            // where the SENSOR CLUSTER sits relative to the grid center (fixed pose)
            var clusterOffset = Vector3D.Zero;
            foreach (var w in welders)
                clusterOffset += WorldApi.SensorSphere(w).Center;
            clusterOffset = clusterOffset / welders.Count - WorldApi.PositionOf(ship);
            // The direction the detectors sit in IS the approach axis: hull on one side of the
            // welding plane, plate on the other, exactly as the operator parked the two grids.
            var facing = Vector3D.Normalize(clusterOffset);
            var approach = -facing;
            // Control, not just a note: the welder disc must stand parallel to the wall being
            // welded, i.e. the tools look straight at the plate from its open side.
            Check(Math.Abs(Vector3D.Dot(openDir, facing)) > 0.9,
                "welder plane is not parallel to the welding wall: open side " + openDir.ToString("F0") +
                ", tools face " + facing.ToString("F1") + " - reline the ship in the world and re-extract");

            // -------------------------------------------------- preconfigured supply path
            // The container is stocked once in MixedShip.xml and all welders are saved with their
            // conveyor setting enabled. The scenario must not change inventory contents or toggle
            // conveyor state at runtime; welding itself is the only consumer of components.
            Check(welders.All(w => w.UseConveyorSystem),
                "all welders must have UseConveyorSystem=true in MixedShip.xml");

            // -------------------------------------------------------- welding
            // Boat hovers so the cluster covers the target block; the CENTER sensor sits
            // straight over it and the four satellites cover the neighbours - up to five
            // blocks get welded in parallel by five independent vanilla tools.
            var baseTotal = WorldApi.CountBlocks(platform);
            var baseFinished = WorldApi.CountFinished(platform);
            var weldingStart = DateTime.UtcNow;
            var lastLogAt = DateTime.UtcNow;
            var lastLoggedBuilt = -1;
            var lastLoggedFinished = -1;
            var lastProgressAt = DateTime.UtcNow;
            var lastProgressCount = 0;
            var lastBuiltCount = 0;
            var slabArea = new BoundingBoxD(slabBb.Min - new Vector3D(1.5, 1.5, 1.5), slabBb.Max + new Vector3D(1.5, 1.5, 1.5));
            // Standby spot: outboard of the welding plane, not above the deck.
            var holdCenter = slabBb.Center + approach * 12.0;

            // SERPENTINE, PROVEN ON THE LIVE RIG. The boat parks its welder
            // cluster under the plate at a locked pose and walks a boustrophedon over the projected
            // cells: columns along world X spaced one cell apart, along Z, alternating direction.
            // Pose facts measured with the operator on 2026-09-14:
            //   - welders BELOW the plate, welder top face 0.5 m under the plate bottom face;
            //   - the sensor that does the work sits at cell + (7.5, ?, 1.5) while the GRID origin
            //     sits at cell + (7.5, ?, -1.5); ship Y = projection bottom - 10.5;
            //   - dwell 3 s per cell was enough for ordinary cells; the same cell is revisited by
            //     the finish passes below, so a slow functional never dead-ends the run.
            var serpColumns = new List<List<Vector3D>>();
            {
                var pg0 = projector.ProjectedGrid;
                var xs = new SortedSet<long>();
                if (pg0 != null)
                    foreach (var slimObj in pg0.CubeBlocks)
                        xs.Add((long)Math.Round(((Sandbox.Game.Entities.Cube.MySlimBlock)slimObj).WorldAABB.Center.X * 4));
                foreach (var xKey in xs)
                {
                    double wx = xKey / 4.0;
                    var col = new List<Vector3D>();
                    if (pg0 != null)
                        foreach (var slimObj in pg0.CubeBlocks)
                        {
                            var c = ((Sandbox.Game.Entities.Cube.MySlimBlock)slimObj).WorldAABB.Center;
                            if (Math.Abs(c.X - wx) > 0.5) continue;
                            if (col.Any(p => Math.Abs(p.Z - c.Z) < 0.5)) continue;
                            col.Add(new Vector3D(wx, c.Y, c.Z));
                        }
                    col.Sort((a, b) => a.Z.CompareTo(b.Z));
                    if (col.Count > 0) serpColumns.Add(col);
                }
            }
            Note("serpentine plan: " + serpColumns.Count + " columns x " +
                 (serpColumns.Count > 0 ? serpColumns[0].Count : 0) + " cells, welded from below");
            const double serpDwellSeconds = 3.0;
            const double serpColumnShipDx = 7.5;    // grid origin offset from cell center, world X
            const double serpColumnShipDz = -1.5;   // grid origin offset from cell center, world Z
            const double serpShipYBelowProjBottom = 10.6; // locked pose, operator-calibrated 2026-09-14:
            // cell center Y=42.6 -> grid Y=32.0 (0.5 m closer to the plate than the first lock).
            double serpShipY = slabBb.Min.Y + CellHalf - serpShipYBelowProjBottom;
            int serpCol = 0;                 // current column index
            int serpRow = 0;                 // current cell index inside the column
            bool serpForward = true;         // Z direction of the current column, flips each column
            var serpArrivedAt = DateTime.UtcNow;
            var serpJumpCheckAt = DateTime.MinValue;   // nearest-buildable re-check cadence
            Vector3D SerpTarget()
            {
                var c = serpColumns[serpCol][serpForward ? serpRow : serpColumns[serpCol].Count - 1 - serpRow];
                return new Vector3D(c.X + serpColumnShipDx, serpShipY, c.Z + serpColumnShipDz);
            }
            void SerpAdvance()
            {
                serpRow++;
                if (serpRow >= serpColumns[serpCol].Count)
                {
                    serpRow = 0;
                    serpCol = (serpCol + 1) % serpColumns.Count;
                    serpForward = !serpForward;
                }
                serpArrivedAt = DateTime.UtcNow;
            }
            // Endgame: the buildable set shrinks to cells the walk has not reached yet, and the
            // 150 s window dies on the transit. The walk JUMPS to the nearest buildable cell -
            // but only when its current cell is already done. Jumping while the cell still has
            // work just chases the migrating frontier (the user watched it thrash). So: idle at
            // the current cell -> no buildable within 3 m of it -> jump to the nearest one that
            // is still within 40 m of the ship (the plate is ~23 x 30 m, diagonal ~38 m; the
            // old 20 m cap sat under the plate width and produced the long dead crawl after the
            // first pass). Once landed, stay until the work is welded.
            // If NOTHING is buildable at all, the wait is a connectivity race: the block only
            // flips to OK when its neighbours finish. Then the walk teleports over the nearest
            // NOT-FINISHED projected cell and hovers, so the tools are on top the instant
            // CanBuild turns green.
            void JumpToNearestBuildable()
            {
                var pg = projector.ProjectedGrid;
                if (pg == null || serpColumns.Count == 0) return;
                var shipPos = WorldApi.PositionOf(ship);
                var cur = serpColumns[serpCol][serpForward ? serpRow : serpColumns[serpCol].Count - 1 - serpRow];
                double nearestToShip = double.MaxValue;
                double nearestToCell = double.MaxValue;
                Vector3D? bestCell = null;
                foreach (var slimObj in pg.CubeBlocks)
                {
                    var slim = (Sandbox.Game.Entities.Cube.MySlimBlock)slimObj;
                    try { if (projector.CanBuild(slim, true) != BuildCheckResult.OK) continue; }
                    catch { continue; }
                    var wc = pg.GridIntegerToWorld(slim.Position);
                    var dShip = Vector3D.Distance(new Vector3D(shipPos.X, wc.Y, shipPos.Z), wc);
                    var dCell = Vector3D.Distance(new Vector3D(cur.X, wc.Y, cur.Z), wc);
                    if (dCell < nearestToCell) nearestToCell = dCell;
                    if (dShip < nearestToShip) { nearestToShip = dShip; bestCell = wc; }
                }
                bool fallback = false;
                if (!bestCell.HasValue)
                {
                    // connectivity-race hover: nearest projected cell whose platform counterpart
                    // does not exist yet or is still partial
                    fallback = true;
                    double bestD = double.MaxValue;
                    double bestCellDist = double.MaxValue;
                    var pl = platform.CubeBlocks;
                    foreach (var slimObj in pg.CubeBlocks)
                    {
                        var slim = (Sandbox.Game.Entities.Cube.MySlimBlock)slimObj;
                        var wc = pg.GridIntegerToWorld(slim.Position);
                        bool done = false;
                        foreach (var p in pl)
                        {
                            var pc = p.WorldAABB.Center;
                            if (Math.Abs(pc.X - wc.X) < 0.6 && Math.Abs(pc.Y - wc.Y) < 0.6 && Math.Abs(pc.Z - wc.Z) < 0.6)
                            {
                                done = p.IsFullIntegrity;
                                break;
                            }
                        }
                        if (done) continue;
                        var d = Vector3D.Distance(new Vector3D(shipPos.X, wc.Y, shipPos.Z), wc);
                        if (d < bestD)
                        {
                            bestD = d;
                            bestCell = wc;
                            bestCellDist = Vector3D.Distance(new Vector3D(cur.X, wc.Y, cur.Z), wc);
                        }
                    }
                    nearestToShip = bestD;
                    nearestToCell = bestCellDist;
                }
                if (!bestCell.HasValue) return;
                if (fallback)
                {
                    // connectivity-race hover: always point the walk at the nearest unfinished
                    // cell. If we are already ON that cell, extend the dwell so the walk does
                    // not SerpAdvance away while CanBuild waits to turn green; otherwise
                    // teleport to it (no distance cap - the whole plate is fair game).
                    if (nearestToCell < 3.0)
                    {
                        serpArrivedAt = DateTime.UtcNow;
                        return;
                    }
                    int bi2 = -1, bj2 = -1;
                    for (int i = 0; i < serpColumns.Count; i++)
                        for (int j = 0; j < serpColumns[i].Count; j++)
                            if (Vector3D.Distance(serpColumns[i][j], bestCell.Value) < 0.6) { bi2 = i; bj2 = j; }
                    if (bi2 < 0) return;
                    serpCol = bi2;
                    var cc2 = serpColumns[bi2].Count;
                    serpForward = bj2 * 2 < cc2;
                    serpRow = serpForward ? bj2 : cc2 - 1 - bj2;
                    serpArrivedAt = DateTime.UtcNow;
                    return;
                }
                if (nearestToCell < 3.0) return;      // the current cell still has buildable work: dwell
                if (nearestToShip > 40.0) return;     // beyond the plate: the boustrophedon gets there on its own
                if (nearestToShip < 3.0) return;      // the ship is already over the work
                int bi = -1, bj = -1;
                for (int i = 0; i < serpColumns.Count; i++)
                    for (int j = 0; j < serpColumns[i].Count; j++)
                        if (Vector3D.Distance(serpColumns[i][j], bestCell.Value) < 0.6) { bi = i; bj = j; }
                if (bi < 0) return;
                serpCol = bi;
                // the row index is read through serpForward (row or Count-1-row), so the stored
                // index must be remapped to whichever direction we walk: landing on the WRONG
                // end of the column is what made the walk thrash away from the work
                var colCount = serpColumns[bi].Count;
                serpForward = bj * 2 < colCount;
                serpRow = serpForward ? bj : colCount - 1 - bj;
                serpArrivedAt = DateTime.UtcNow;
            }
            var homeMatrix = ship.WorldMatrix;   // calibrated identity pose
            void HoldAt(Vector3D desired, Vector3D feedForward = default(Vector3D))
            {
                var err = desired - WorldApi.PositionOf(ship);
                var velDes = err * 1.5 + feedForward;
                var vLen = velDes.Length();
                const double maxSpeed = 10.0;
                if (vLen > maxSpeed) velDes = velDes / vLen * maxSpeed;
                var vCur = new Vector3D(ship.Physics.LinearVelocity.X, ship.Physics.LinearVelocity.Y, ship.Physics.LinearVelocity.Z);
                var v = vCur + (velDes - vCur) * 0.2f;
                ship.Physics.LinearVelocity = new Vector3((float)v.X, (float)v.Y, (float)v.Z);
                ship.Physics.AngularVelocity = Vector3.Zero;   // pose stays exactly as spawned
                // Hard pose lock: the serpentine offsets are calibrated for the spawn orientation
                // (identity basis). Nudge the matrix back onto the home basis whenever a collision
                // tilts it — the live rig never rotated during the 79/80 sweep.
                var m = ship.WorldMatrix;
                m.M11 += (homeMatrix.M11 - m.M11) * 0.3; m.M12 += (homeMatrix.M12 - m.M12) * 0.3;
                m.M13 += (homeMatrix.M13 - m.M13) * 0.3; m.M21 += (homeMatrix.M21 - m.M21) * 0.3;
                m.M22 += (homeMatrix.M22 - m.M22) * 0.3; m.M23 += (homeMatrix.M23 - m.M23) * 0.3;
                m.M31 += (homeMatrix.M31 - m.M31) * 0.3; m.M32 += (homeMatrix.M32 - m.M32) * 0.3;
                m.M33 += (homeMatrix.M33 - m.M33) * 0.3;
                ship.WorldMatrix = m;
            }

            while (true)
            {
                var built = WorldApi.CountBlocks(platform) - baseTotal;
                var finished = WorldApi.CountFinished(platform) - baseFinished;
                if (finished >= expected) break;

                WorldApi.ChargeBatteries(ship);
                WorldApi.ChargeBatteries(platform);
                platformDist.MarkForUpdate();
                platformDist.UpdateBeforeSimulation();

                // --- SERPENTINE WALK: dwell each projected cell, move on after serpDwellSeconds.
                // The committed block under the lead sensor is welded while we dwell; revisiting
                // columns (the walk cycles) finishes slow functionals — this is the live-proven
                // 79/80 pattern. No dynamic target picking: it was what made the boat oscillate.
                if (serpColumns.Count > 0)
                {
                    var want = SerpTarget();
                    var arrivedD = Vector3D.Distance(want, WorldApi.PositionOf(ship));
                    if (arrivedD < 0.6 && (DateTime.UtcNow - serpArrivedAt).TotalSeconds > serpDwellSeconds)
                        SerpAdvance();
                    // the last blocks wait in columns the walk has not reached yet: if the game
                    // says buildable work is within reach but the walk is elsewhere, jump the walk
                    if ((DateTime.UtcNow - serpJumpCheckAt).TotalSeconds > 5)
                    {
                        serpJumpCheckAt = DateTime.UtcNow;
                        JumpToNearestBuildable();
                    }
                    HoldAt(SerpTarget());
                }
                else
                {
                    HoldAt(holdCenter);
                }

                if (finished > lastProgressCount)
                {
                    lastProgressCount = finished;
                    lastProgressAt = DateTime.UtcNow;
                }
                // a block that just STARTED is progress too: it still has to reach full
                // integrity, and near the end the big functionals take a while
                if (built > lastBuiltCount)
                {
                    lastBuiltCount = built;
                    lastProgressAt = DateTime.UtcNow;
                }

                if ((DateTime.UtcNow - lastLogAt).TotalSeconds > 8 &&
                    (built != lastLoggedBuilt || finished != lastLoggedFinished))
                {
                    lastLogAt = DateTime.UtcNow;
                    lastLoggedBuilt = built;
                    lastLoggedFinished = finished;
                    long contLevel = 0;
                    foreach (var st in needs.Keys)
                        contLevel += WorldApi.CountComponent(container.GetInventory(), st);
                    Note("mixed welding: built=" + built + "/" + expected +
                         ", finished=" + finished + ", container=" + contLevel + "/" + containerBefore);
                }

                // a true stall: nothing started AND nothing finished for a long time. The window
                // is generous on purpose: slow functionals near the deck can sit minutes between
                // "started" and "finished" while the ship works on something else
                if ((DateTime.UtcNow - lastProgressAt).TotalSeconds > 150)
                {
                    throw new Core.ScenarioFailedException(
                        "vanilla welders stalled: no build progress for 150s (built=" + built + "/" + expected +
                        ", finished=" + finished + ", steel=" + WorldApi.CountSteel(welders[0].GetInventory()) +
                        ", probe=" + WeldersProbe(welders) +
                        ", projector=" + (projector.IsWorking ? "working" : "NOT working") +
                        ", census=" + BuildCheckCensus(projector) + ")");
                }

                yield return null;
            }

            // final sweep: wait for the last blocks to reach full integrity
            yield return Wait(() => WorldApi.CountFinished(platform) - baseFinished >= expected,
                "all " + expected + " mixed blocks finished", 120);

            var realBuilt = WorldApi.CountBlocks(platform) - baseTotal;
            Note("mixed slab welded in " + (DateTime.UtcNow - weldingStart).TotalSeconds.ToString("F1") +
                 "s | " + WelderCount + " vanilla welders built " + realBuilt + " blocks");

            // ------------------------------------------------------ verification
            yield return WaitForTicks(60);

            var finalCount = WorldApi.CountBlocks(platform);
            Check(finalCount - baseTotal == expected,
                "expected " + expected + " new blocks on the platform, got " + (finalCount - baseTotal));

            // the welded functionals must exist on the platform WITH their logic attached.
            // Expectations come from the PROJECTED set (what the welder was actually able to build).
            // the platform's own power and light, counted before any welding happened
            int Proj(string st) { int n; return projectedCounts.TryGetValue(st, out n) ? n : 0; }
            // Every subtype the projector materialized has to sit on the platform afterwards, in
            // the same count - the platform's own blocks only ever push these numbers up, so this
            // is a lower bound per subtype and it needs no list of what the blueprint contains.
            var shortList = new List<string>();
            foreach (var kvp in projectedCounts.OrderBy(k => k.Key))
            {
                var got = platform.CubeBlocks.Count(b => b.BlockDefinition.Id.SubtypeName == kvp.Key);
                if (got < kvp.Value) shortList.Add(kvp.Key + " " + got + "/" + kvp.Value);
            }
            Check(shortList.Count == 0, "welded mix is incomplete: " + string.Join("; ", shortList));

            var gyros = WorldApi.FindFunctionals<MyGyro>(platform).Count;
            var cockpits = WorldApi.FindFunctionals<MyCockpit>(platform).Count;
            var gasTanks = WorldApi.FindFunctionals<MyGasTank>(platform).Count;
            var cargo = WorldApi.FindFunctionals<MyCargoContainer>(platform).Count;
            var lights = WorldApi.FindFunctionals<MyLightingBlock>(platform).Count;
            var batteries = WorldApi.FindFunctionals<MyBatteryBlock>(platform).Count;
            // Per-subtype, taken from the projection itself: FindSubtype("blockcockpit") resolves
            // to a subtype the projection may not even use (it welds LargeBlockCockpitSeat), so a
            // name-guessed expectation is wrong by construction. The existence census above
            // covers every projected block; this one adds that the FUNCTIONAL ones arrived with
            // their logic (a FatBlock), not as structural shells. Structural subtypes (armor)
            // have no FatBlock by design and are intentionally not re-checked here.
            var projFunctional = new Dictionary<string, int>();
            foreach (var slimObj in preview0.CubeBlocks)
            {
                var slim = (Sandbox.Game.Entities.Cube.MySlimBlock)slimObj;
                if (!(slim.FatBlock is Sandbox.Game.Entities.Cube.MyFunctionalBlock)) continue;
                var st = slim.BlockDefinition.Id.SubtypeName;
                projFunctional.TryGetValue(st, out var n);
                projFunctional[st] = n + 1;
            }
            var missing = new List<string>();
            foreach (var kvp in projFunctional.OrderBy(k => k.Key))
            {
                var got = platform.CubeBlocks.Count(b => b.FatBlock != null && b.BlockDefinition.Id.SubtypeName == kvp.Key);
                if (got < kvp.Value) missing.Add(kvp.Key + " " + got + "/" + kvp.Value);
            }
            Check(missing.Count == 0,
                "welded functionals missing live logic: " + string.Join("; ", missing));
            Check(lights == ownLights + Proj(lightType),
                "expected " + (ownLights + Proj(lightType)) + " lights, found " + lights);
            Check(batteries >= ownBatteries + Proj(batteryType),
                "expected at least " + (ownBatteries + Proj(batteryType)) + " batteries, found " + batteries);

            // The weld must not BREAK the blocks' logic: every welded functional must keep its
            // working state, not just exist. Probe the cargo inventories and the battery storage.
            var allCargo = WorldApi.FindFunctionals<MyCargoContainer>(platform).ToList();
            var cargoWithInv = allCargo.Count(c => c.GetInventory() != null);
            Check(cargoWithInv == allCargo.Count,
                "welded cargo lost its inventory logic: " + cargoWithInv + "/" + allCargo.Count + " have an inventory");
            var allBatteries = WorldApi.FindFunctionals<MyBatteryBlock>(platform).ToList();
            var batteriesWithLogic = allBatteries.Count(b => b.MaxOutput > 0f);
            Check(batteriesWithLogic == allBatteries.Count,
                "batteries lost their storage logic: " + batteriesWithLogic + "/" + allBatteries.Count + " have capacity");

            // The mixed slab must have been paid for (Survival). Components come from the welders'
            // own cargo and, if the conveyor network feeds them, from the shared container, so
            // assert on the COMBINED drain. The container share is reported separately: a drop
            // there is the proof the conveyor system actually moved items.
            long containerAfter = 0, cargoAfter = 0;
            foreach (var st in needs.Keys)
            {
                containerAfter += WorldApi.CountComponent(container.GetInventory(), st);
                foreach (var w in welders) cargoAfter += WorldApi.CountComponent(w.GetInventory(), st);
            }
            var containerUsed = containerBefore - containerAfter;
            var cargoUsed = cargoBefore - cargoAfter;
            var used = containerUsed + cargoUsed;
            Note("components consumed: container=" + containerUsed + " (" + containerBefore + "->" + containerAfter +
                 "), welder cargo=" + cargoUsed + " (" + cargoBefore + "->" + cargoAfter + "), total=" + used);
            if (!Sandbox.Game.World.MySession.Static.CreativeMode)
                Check(used > 0,
                    "survival weld must consume components (container+welders, used=" + used + ")");

            Note("PASS: " + WelderCount + " vanilla welders built the mixed slab " +
                 "(" + realBuilt + " blocks incl. functionals, components used=" + used +
                 ", via conveyor=" + containerUsed + ")");
        }

        // ------------------------------------------------------------ cleanup
        // Tracking covers the two main grids. This hook removes only stale mixed_weld/debug rigs
        // and tiny grids created after this run began; pre-existing player grids are never debris.
        public override void CleanupLeftovers()
        {
            var platformPos = _platformPos;
            int removed = 0;

            foreach (var entity in MyEntities.GetEntities().ToList())
            {
                var grid = entity as MyCubeGrid;
                if (grid == null || grid.MarkedForClose || Tracked.Contains(grid)) continue;

                var name = grid.Name ?? "";
                var isNamedFixture = name.StartsWith(WorldApi.EntityPrefix + "mixed-",
                                         StringComparison.OrdinalIgnoreCase) ||
                                     name.StartsWith("STDBG-mixed-", StringComparison.OrdinalIgnoreCase);
                var isNewDebris = !_preexistingGridIds.Contains(grid.EntityId) &&
                                  platformPos.HasValue && grid.CubeBlocks != null &&
                                  grid.CubeBlocks.Count <= 4;
                if (isNewDebris)
                {
                    try
                    {
                        isNewDebris = (grid.PositionComp.GetPosition() - platformPos.Value).Length() <= 150;
                    }
                    catch { isNewDebris = false; }
                }

                if (!isNamedFixture && !isNewDebris) continue;
                try { grid.Delete(); removed++; }
                catch (Exception e) { Log.Warn("leftover sweep: cannot delete {0}: {1}", name, e.Message); }
            }

            if (removed > 0)
                Log.Info("[TEST:" + Name + "] leftover sweep removed " + removed + " grids");
        }

        // Full verdict of the game's own build check over the whole projected set: how many blocks
        // pass, and under which rejection reason the rest fail. Turns a silent stall into a name.
        private static string BuildCheckCensus(MyProjectorBase projector)
        {
            var pg = projector.ProjectedGrid;
            if (pg == null) return "no projected grid";
            var counts = new Dictionary<string, int>();
            foreach (var slimObj in pg.CubeBlocks)
            {
                string key;
                try { key = projector.CanBuild((Sandbox.Game.Entities.Cube.MySlimBlock)slimObj, true).ToString(); }
                catch (Exception ex) { key = "EX:" + ex.GetBaseException().GetType().Name; }
                int seen;
                counts.TryGetValue(key, out seen);
                counts[key] = seen + 1;
            }
            return string.Join(", ", counts.OrderBy(kvp => kvp.Key)
                .Select(kvp => kvp.Key + "=" + kvp.Value));
        }

        private static int buildableNow(MyProjectorBase projector)
        {
            int n = 0;
            var pg = projector.ProjectedGrid;
            if (pg == null) return 0;
            foreach (var slimObj in pg.CubeBlocks)
            {
                try { if (projector.CanBuild((Sandbox.Game.Entities.Cube.MySlimBlock)slimObj, true) == BuildCheckResult.OK) n++; }
                catch { }
            }
            return n;
        }

        private static int WeldersProbe(List<SpaceWelder> welders)
        {
            int n = 0;
            foreach (var w in welders) n += WorldApi.ProbeProjectedBlocks(w);
            return n;
        }
    }
}
