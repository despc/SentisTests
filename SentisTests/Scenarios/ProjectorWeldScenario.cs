using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Ingame;
using VRage.Game;
using VRageMath;
using BuildCheckResult = Sandbox.ModAPI.BuildCheckResult;
using IMyProjectorIngame = Sandbox.ModAPI.Ingame.IMyProjector;
using SpaceWelder = SpaceEngineers.Game.Entities.Blocks.MyShipWelder;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// End-to-end "construction ship" test, 100% vanilla welding:
    ///  1. static 13x13 platform with a powered projector and the operator's blueprint
    ///     (10x10x1 = 100 armor blocks) embedded in the projector's object builder - the
    ///     engine parks the hologram touching the deck, so exactly one block is weldable at
    ///     start and connectivity opens the rest as it is built;
    ///  2. a separate welder ship (prefab MyShipWelder loaded with steel) is a normal
    ///     ACTIVATED tool: the scenario never calls any build/weld API, it only flies and
    ///     turns the ship so the welder's real sensor sphere hovers over the next
    ///     engine-buildable projected block; the vanilla welder pipeline does all the welding;
    ///  3. asserts all 100 blocks exist at full integrity and that the welder cargo paid for
    ///     them (Survival mode consumes real components).
    /// </summary>
    public class ProjectorWeldScenario : TestScenario
    {
        public const string ScenarioName = "projector_weld";

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        /// <summary>
        /// Whether somebody stands on the site while it is built. Normally yes - a site with nobody
        /// at it is a site the freezer takes, and the welding stops. A scenario about freezing says
        /// no: it is waiting for exactly that.
        /// </summary>
        protected virtual bool KeepSiteAwake => true;

        private const int SlabX = 10;  // hologram footprint, blocks
        private const int SlabZ = 10;
        private const int SlabY = 1;
        private const int DeckSide = 13;

        private static Vector3D PlatformPos = new Vector3D(0, 120, 0); // visible from world spawn
        private static readonly Vector3D ShipSpawnOffset = new Vector3D(80, 25, 0);

        public override string Name { get { return ScenarioName; } }

        public override int TimeoutSeconds { get { return 900; } }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused("projector_weld start");
            Note("resolving block subtypes");
            var armor = WorldApi.FindSubtype(MyCubeSize.Large, "blockarmorblock");
            var heavyArmor = WorldApi.FindSubtype(MyCubeSize.Large, "heavyblockarmorblock");
            var projectorType = WorldApi.FindSubtype(MyCubeSize.Large, "projector");
            var welderType = WorldApi.FindSubtype(MyCubeSize.Large, "shipwelder");
            var batteryType = WorldApi.FindSubtype(MyCubeSize.Large, "batteryblock");
            var cockpitType = WorldApi.FindSubtype(MyCubeSize.Large, "blockcockpit");
            var lightType = WorldApi.FindSubtype(MyCubeSize.Large, "light");
            var gyroType = WorldApi.FindSubtype(MyCubeSize.Large, "gyro");

            // -------------------------------------------------------- blueprint
            // The operator's blueprint: a 10x1x10 armor slab WELDED INTO the projector's
            // object builder before spawn (exactly like the manual blueprint file). The engine
            // parks the hologram at ProjectionOffset=(-2,0,0) so it touches the deck at one
            // spot: exactly one block is weldable first and connectivity opens neighbours as
            // welding progresses - the honest vanilla build order.
            var blueprint = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + "target",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = false,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>(),
            };
            for (var x = 0; x < SlabX; x++)
                for (var by = 0; by < SlabY; by++)
                    for (var z = 0; z < SlabZ; z++)
                        blueprint.CubeBlocks.Add(new MyObjectBuilder_CubeBlock
                        {
                            SubtypeName = armor,
                            Min = new SerializableVector3I(x, by, z),
                            BuiltBy = WorldApi.TestIdentityId(),
                        });
            var expected = blueprint.CubeBlocks.Count;
            
            // ---------------------------------------------------------- platform
            var platformBlocks = new List<BlockSpec>();
            for (var x = 0; x < DeckSide; x++)
                for (var z = 0; z < DeckSide; z++)
                    platformBlocks.Add(new BlockSpec(armor, new Vector3I(x, 0, z)));
            // projector at the deck edge (12,1,6), batteries on two far corners, light as power canary
            platformBlocks.Add(new BlockSpec(projectorType, new Vector3I(DeckSide - 1, 1, DeckSide / 2)));
            // the two heavy-armor bridge blocks from the operator's blueprint: they weld the
            // platform to the projection so the slab actually has something to connect to
            platformBlocks.Add(new BlockSpec(heavyArmor, new Vector3I(12, 1, 9)));
            platformBlocks.Add(new BlockSpec(heavyArmor, new Vector3I(13, 1, 9)));
            platformBlocks.Add(new BlockSpec(batteryType, new Vector3I(0, 1, 0)));
            platformBlocks.Add(new BlockSpec(batteryType, new Vector3I(0, 1, DeckSide - 1)));
            platformBlocks.Add(new BlockSpec(lightType, new Vector3I(0, 1, DeckSide - 2)));

            Note("spawning static " + DeckSide + "x" + DeckSide + " platform with the embedded " +
                 expected + "-block blueprint");
            var platformOb = WorldApi.GridOb(WorldApi.EntityPrefix + "platform", MyCubeSize.Large, true,
                PlatformPos = TestRunner.RunOrigin ?? PlatformPos, platformBlocks);
            foreach (var b in platformOb.CubeBlocks)
            {
                var pb = b as VRage.Game.MyObjectBuilder_ProjectorBase;
                if (pb == null) continue;
                pb.ProjectedGrids = new List<MyObjectBuilder_CubeGrid> { blueprint };
                pb.ProjectionOffset = new Vector3I(-2, 0, 0);
                pb.ProjectionsRemaining = 50;
                pb.Scale = 1;
            }
            var platform = WorldApi.SpawnGrid(platformOb);
            Track(platform);

            yield return WaitForTicks(60);

            var platformDist = WorldApi.EnsureDistributor(platform);
            var charged = WorldApi.ChargeBatteries(platform);
            Check(charged > 0, "no chargeable batteries on the platform");
            Note("platform batteries charged to " + charged.ToString("F0") + " MW");

            var projector = WorldApi.FindFunctional<MyProjectorBase>(platform);
            Check(projector != null, "projector block not found on platform");

            yield return Wait(() =>
            {
                WorldApi.ChargeBatteries(platform);
                platformDist.MarkForUpdate();
                platformDist.UpdateBeforeSimulation();
                return projector.IsWorking && projector.ProjectedGrid != null;
            }, "projector powered and projection loaded from the blueprint (" +
               WorldApi.DescribePower(platform) + ")", 90);

            // verify the blueprint geometry: at least one block must be weldable right now;
            // connectivity opens the rest as welding progresses
            var preview0 = projector.ProjectedGrid;
            Check(preview0 != null, "projection is not active");
            var buildable0 = 0;
            foreach (var slimObj in preview0.CubeBlocks)
                if (projector.CanBuild((Sandbox.Game.Entities.Cube.MySlimBlock)slimObj, true) == BuildCheckResult.OK)
                    buildable0++;
            Note("projection from blueprint: " + preview0.CubeBlocks.Count + " preview blocks, " +
                 buildable0 + " weldable now (connectivity opens the rest)");
            Check(buildable0 >= 1, "the blueprint projection has no weldable block at all");

            var previewPos = WorldApi.PositionOf(preview0);
            Note("slab hologram parked at " + previewPos.ToString("F1"));

            // ------------------------------------------------------ welder ship
            // The operator's construction boat: a compact cross with the welder on TOP
            // (local +Y), so the sensor points along the ship's up-axis. The slab parks
            // flush on the deck (no room underneath), and its top face is open - so the
            // boat spawns FLIPPED above the slab (local up = world down) and welds the
            // top faces with the sensor pointing DOWN.
            //
            // No attitude control in flight: the pose is fixed at spawn, the angular
            // velocity is zeroed every tick, and only a capped position servo moves the
            // boat. That is what kills the "circling like a lunatic" behaviour.
            var upDir = (int)VRageMath.Base6Directions.Direction.Up;
            var shipBlocks = new List<BlockSpec>
            {
                new BlockSpec(batteryType, new Vector3I(0, 0, 0)),
                new BlockSpec(cockpitType, new Vector3I(0, 0, -1)),
                new BlockSpec(welderType,  new Vector3I(0, 1, 0), upDir),   // sensor = ship up
                new BlockSpec(batteryType, new Vector3I(0, -1, 0)),
                new BlockSpec(batteryType, new Vector3I(1, -1, 0)),
                new BlockSpec(batteryType, new Vector3I(-1, -1, 0)),
                new BlockSpec(gyroType,    new Vector3I(0, -1, -1)),
            };

            var slabBb = preview0.PositionComp.WorldAABB;
            var slabTopY = slabBb.Max.Y;
            var slabCenter = (slabBb.Min + slabBb.Max) * 0.5;

            // Somebody has to be standing there. A site with nobody at it is a site the freezer
            // takes: the grids come off the update lists, the welder stops being activated, and the
            // slab stops halfway - seen here at 6 blocks of 100 with the tool reporting six
            // projected blocks in front of it and a container full of steel behind it.
            FakeClients.RemoveAll();
            if (KeepSiteAwake)
                FakeClients.Add(1, Network, p => (slabCenter + new Vector3D(0, 40, 0), 0, 0), withCharacters: true);
            // spawn straight over the slab: the approach stays over open sky, nothing to clip
            var shipPos = new Vector3D(slabCenter.X, slabTopY + 18.0, slabCenter.Z);
            Note("spawning construction boat at " + shipPos.ToString("F0") +
                 " (slab top=" + slabTopY.ToString("F1") + "), flipped - sensor faces the deck");
            var ship = WorldApi.SpawnGrid(WorldApi.GridOb(WorldApi.EntityPrefix + "welder-ship", MyCubeSize.Large, false,
                shipPos, shipBlocks, forward: new Vector3(0, 0, -1), up: new Vector3(0, -1, 0)));
            Track(ship);

            // the body must actually be simulated for the velocity servo to integrate
            ship.Physics.ForceActivate();
            ship.Physics.Activate();
            ship.Physics.LowSimulationQuality = false;

            yield return WaitForTicks(120); // fat-block promotion must finish before the distributor pass

            WorldApi.EnsureDistributor(ship);
            Note("ship batteries charged to " + WorldApi.ChargeBatteries(ship).ToString("F0") + " MW");

            var welder = WorldApi.FindFunctional<SpaceWelder>(ship);
            Check(welder != null, "welder block missing from the prefab");

            var sensor = WorldApi.SensorSphere(welder);
            // where the sensor sits relative to the grid center - fixed, because the pose is locked
            var sensorOffset = sensor.Center - WorldApi.PositionOf(ship);
            Note("welder " + welder.BlockDefinition.Id.SubtypeName +
                 ", sensor r=" + sensor.Radius.ToString("F1") + " m, offset from center=" +
                 sensorOffset.ToString("F1"));

            WorldApi.EnsureDistributor(ship); // re-wire power so the new block's sink is registered

            var needs = WorldApi.ComponentsNeeded(blueprint.CubeBlocks, 2);
            Note("welder cargo plan: " + string.Join(", ", needs.Select(kvp => kvp.Key + " x" + kvp.Value)));
            Note("welder stocked: " + WorldApi.StockComponents(welder.GetInventory(), needs));
            var welderSteel0 = WorldApi.CountSteel(welder.GetInventory());

            yield return Wait(() => { WorldApi.ChargeBatteries(ship); return welder.IsFunctional; },
                "welder functional (" + WorldApi.DescribePower(ship) + ")", 90);

            var preparation = BeforeWelding(ship, welder);
            while (preparation.MoveNext())
                yield return preparation.Current;

            welder.Enabled = true;
            yield return Wait(() => welder.IsWorking, "welder working (" + WorldApi.DescribePower(ship) + ")", 60);

            // MyShipToolBase.UpdateActivationState() raises m_isActivated off power/working
            // callbacks; make sure it actually happened, otherwise call the vanilla StartShooting.
            if (!WorldApi.ToolIsActivated(welder))
            {
                welder.Enabled = false;
                yield return WaitForTicks(5);
                welder.Enabled = true;
                yield return WaitForTicks(10);
                if (!WorldApi.ToolIsActivated(welder))
                {
                    WorldApi.ToolStartShooting(welder);
                    Note("welder m_isActivated was false - invoked vanilla StartShooting()");
                }
            }
            Note("welder armed: activated=" + WorldApi.ToolIsActivated(welder) +
                 " (engine's own UpdateAfterSimulation10 -> ActivateCommon does the work)");

            // -------------------------------------------------------- welding
            // The boat hovers over the slab so the DOWN-facing sensor sphere overlaps the
            // block's top face (sphere-vs-AABB, the same reach test the vanilla tool uses).
            // The scenario only translates the ship; the vanilla welder does every build.
            // Target = nearest unfinished slab block (finish it) else nearest buildable
            // projected block (start it). Connectivity opens the rest as they are built.
            var baseTotal = WorldApi.CountBlocks(platform);
            var baseFinished = WorldApi.CountFinished(platform);
            var vanillaBuilds = 0;
            var weldingStart = DateTime.UtcNow;
            var lastLogAt = DateTime.UtcNow;
            var lastProgressAt = DateTime.UtcNow;
            var lastProgressCount = 0;
            // Sensor centre this far above the slab top face. Half a metre more than the old 1.2:
            // parked lower, the welder hull flies into the very projected block it is building and
            // the game's build check then rejects that block - which shows up as the ship parking
            // over the last remaining block forever, build check cancelled, nothing happening.
            var sensorGap = 1.7;
            var slabArea = new BoundingBoxD(slabBb.Min - new Vector3D(1.5, 1.5, 1.5), slabBb.Max + new Vector3D(1.5, 1.5, 1.5));
            var holdCenter = new Vector3D(slabCenter.X, slabTopY + sensorGap, slabCenter.Z);

            // capped PD position servo + hard attitude lock (translation only, never rotates)
            void HoldAt(Vector3D sensorTarget)
            {
                var desired = sensorTarget - sensorOffset;
                var err = desired - WorldApi.PositionOf(ship);
                var velDes = err * 1.5;
                var vLen = velDes.Length();
                const double maxSpeed = 10.0;
                if (vLen > maxSpeed) velDes = velDes / vLen * maxSpeed;
                var vCur = new Vector3D(ship.Physics.LinearVelocity.X, ship.Physics.LinearVelocity.Y, ship.Physics.LinearVelocity.Z);
                var v = vCur + (velDes - vCur) * 0.2f;
                ship.Physics.LinearVelocity = new Vector3((float)v.X, (float)v.Y, (float)v.Z);
                ship.Physics.AngularVelocity = Vector3.Zero;   // pose stays exactly as spawned
            }

            while (true)
            {
                var built = WorldApi.CountBlocks(platform) - baseTotal;
                var finished = WorldApi.CountFinished(platform) - baseFinished;
                if (finished >= expected) break;

                // keep both grids powered: the projector dies in seconds without a top-up,
                // and a dead projector makes every CanBuild fail
                WorldApi.ChargeBatteries(ship);
                WorldApi.ChargeBatteries(platform);
                platformDist.MarkForUpdate();
                platformDist.UpdateBeforeSimulation();

                // 1) finish whatever the welder already started (nearest unfinished slab block)
                // 2) else start the nearest buildable projected block
                var shipPosNow = WorldApi.PositionOf(ship);
                double bestD = double.MaxValue;
                Vector3D bestC = Vector3D.Zero;
                bool found = false;
                foreach (var slim in platform.CubeBlocks)
                {
                    if (slim.IsFullIntegrity) continue;
                    var wc = (slim.WorldAABB.Min + slim.WorldAABB.Max) * 0.5;
                    if (wc.X < slabArea.Min.X || wc.X > slabArea.Max.X ||
                        wc.Y < slabArea.Min.Y || wc.Y > slabArea.Max.Y ||
                        wc.Z < slabArea.Min.Z || wc.Z > slabArea.Max.Z) continue;
                    var d = Vector3D.DistanceSquared(shipPosNow, wc);
                    if (d < bestD) { bestD = d; bestC = wc; found = true; }
                }
                if (!found)
                {
                    var pg = projector.ProjectedGrid;
                    if (pg != null)
                    {
                        foreach (var slimObj in pg.CubeBlocks)
                        {
                            var s = (Sandbox.Game.Entities.Cube.MySlimBlock)slimObj;
                            BuildCheckResult r;
                            try { r = projector.CanBuild(s, true); } catch { continue; }
                            if (r != BuildCheckResult.OK) continue;
                            var wc = (s.WorldAABB.Min + s.WorldAABB.Max) * 0.5;
                            var d = Vector3D.DistanceSquared(shipPosNow, wc);
                            if (d < bestD) { bestD = d; bestC = wc; found = true; }
                        }
                    }
                }

                if (found)
                {
                    HoldAt(new Vector3D(bestC.X, slabTopY + sensorGap, bestC.Z));
                    if (finished > lastProgressCount)
                    {
                        lastProgressCount = finished;
                        lastProgressAt = DateTime.UtcNow;
                    }
                }
                else
                {
                    HoldAt(holdCenter);
                    // normal while the last blocks wait for connectivity; the 8s note covers it
                }

                if (finished > vanillaBuilds) vanillaBuilds = finished;

                if ((DateTime.UtcNow - lastLogAt).TotalSeconds > 8)
                {
                    lastLogAt = DateTime.UtcNow;
                    Note("welding: built=" + built + "/" + expected + " (finished=" + finished + ")" +
                         ", steel=" + WorldApi.CountSteel(welder.GetInventory()) +
                         ", probe sees " + WorldApi.ProbeProjectedBlocks(welder) + " projected" +
                         (found ? "" : ", NO TARGET"));
                }

                if ((DateTime.UtcNow - lastProgressAt).TotalSeconds > 90)
                {
                    throw new Core.ScenarioFailedException(
                        "vanilla welder stalled: no finished blocks for 90s (built=" + built + "/" + expected +
                        ", finished=" + finished + ", steel=" + WorldApi.CountSteel(welder.GetInventory()) +
                        ", probe=" + WorldApi.ProbeProjectedBlocks(welder) +
                        ", projector=" + (projector.IsWorking ? "working" : "NOT working") + ")");
                }

                yield return null;
            }

            // final sweep: wait for the last blocks to reach full integrity
            yield return Wait(() => WorldApi.CountFinished(platform) - baseFinished >= expected,
                "all " + expected + " slab blocks finished", 120);

            var welderSteelUsed = welderSteel0 - WorldApi.CountSteel(welder.GetInventory());
            var realBuilt = WorldApi.CountBlocks(platform) - baseTotal;
            Note("slab welded in " + (DateTime.UtcNow - weldingStart).TotalSeconds.ToString("F1") +
                 "s | vanilla welder built " + realBuilt + " blocks | steel used=" + welderSteelUsed);

            // ------------------------------------------------------ verification
            yield return WaitForTicks(60);

            var finalCount = WorldApi.CountBlocks(platform);
            Note("platform now carries " + finalCount + " blocks (" + WorldApi.CountFinished(platform) +
                 " finished, was " + baseTotal + " total)");
            Check(finalCount - baseTotal == expected,
                "expected " + expected + " new blocks on the platform, got " + (finalCount - baseTotal));
            var allFull = platform.GetBlocks().All(b => b.IsFullIntegrity || b.BlockDefinition.Id.SubtypeName != armor);
            Check(allFull, "welded blocks must be at full integrity");
            if (!Sandbox.Game.World.MySession.Static.CreativeMode)
                Check(welderSteelUsed > 0,
                    "survival weld must consume components from the welder cargo (used=" + welderSteelUsed + ")");

            Note("PASS: the vanilla welder ship built the full slab " +
                 "(" + realBuilt + " blocks, steel used=" + welderSteelUsed + ")");
        }

        protected virtual IEnumerator BeforeWelding(MyCubeGrid ship, SpaceWelder welder)
        {
            yield break;
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

    }
}
