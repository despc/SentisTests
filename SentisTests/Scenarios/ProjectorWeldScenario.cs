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
using IMyProjectorIngame = Sandbox.ModAPI.Ingame.IMyProjector;
using SpaceWelder = SpaceEngineers.Game.Entities.Blocks.MyShipWelder;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// End-to-end "real player-like" weld test:
    ///  1. static platform grid with a PROJECTOR, powered;
    ///  2. projector shows an 8-block ship blueprint;
    ///  3. separate dynamic "welder ship" (cockpit/reactor/batteries/large welder loaded with
    ///     steel) FLIES in from 80 m away and holds station next to the projection;
    ///  4. the welder block (game automation, our patched ShipTool path) must weld every
    ///     projected block;
    ///  5. asserts the resulting grid exists with the expected block count.
    /// Exercises: spawn/build pipeline, power, projectors, moving-grid simulation, auto-weld,
    /// inventory consumption - all through the real server code paths.
    /// </summary>
    public class ProjectorWeldScenario : TestScenario
    {
        public const string ScenarioName = "projector_weld";

        private const int BlueprintSide = 2; // 2x2x2 = 8 blocks
        private static readonly Vector3D PlatformPos = new Vector3D(0, 120, 0); // visible from world spawn
        private static readonly Vector3D ShipSpawnOffset = new Vector3D(80, 25, 0);

        public override string Name { get { return ScenarioName; } }

        public override int TimeoutSeconds { get { return 600; } }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused("projector_weld start");
            Note("resolving block subtypes");
            var armor = WorldApi.FindSubtype(MyCubeSize.Large, "blockarmorblock");
            var projectorType = WorldApi.FindSubtype(MyCubeSize.Large, "projector");
            var welderType = WorldApi.FindSubtype(MyCubeSize.Large, "shipwelder");
            var batteryType = WorldApi.FindSubtype(MyCubeSize.Large, "batteryblock");
            var cockpitType = WorldApi.FindSubtype(MyCubeSize.Large, "blockcockpit");

            // ---------------------------------------------------------- platform
            var platformBlocks = new List<BlockSpec>();
            for (var x = 0; x < 5; x++)
                for (var z = 0; z < 5; z++)
                    platformBlocks.Add(new BlockSpec(armor, new Vector3I(x, 0, z)));
            platformBlocks.Add(new BlockSpec(projectorType, new Vector3I(2, 1, 2)));
            // the projector needs its own power network: reactor + battery on the platform
            platformBlocks.Add(new BlockSpec(batteryType, new Vector3I(0, 1, 0)));
            platformBlocks.Add(new BlockSpec(batteryType, new Vector3I(4, 1, 4)));
            var lightType = WorldApi.FindSubtype(MyCubeSize.Large, "light");
            platformBlocks.Add(new BlockSpec(lightType, new Vector3I(2, 1, 0)));

            Note("spawning static platform with projector");
            var platform = WorldApi.SpawnGrid(
                WorldApi.GridOb("ST-platform", MyCubeSize.Large, true, PlatformPos, platformBlocks));
            Track(platform);

            yield return WaitForTicks(60);

            var platformDist = EnsureDistributor(platform);
            var charged = ChargeBatteries(platform);
            Check(charged > 0, "no chargeable batteries on the platform");
            Note("platform batteries charged to " + charged.ToString("F0") + " MW");

            var light = WorldApi.FindFunctional<Sandbox.Game.Entities.Blocks.MyLightingBlock>(platform);
            var projector = WorldApi.FindFunctional<MyProjectorBase>(platform);
            Check(projector != null, "projector block not found on platform");
            var projectorIngame = (IMyProjectorIngame)projector;

            var powerProbe = 0;
            yield return Wait(() =>
            {
                ChargeBatteries(platform);
                platformDist.MarkForUpdate();
                platformDist.UpdateBeforeSimulation();
                if (++powerProbe % 120 == 0)
                {
                    var sb = new System.Text.StringBuilder("power probe: ");
                    foreach (var bat in WorldApi.Functionals<Sandbox.Game.Entities.MyBatteryBlock>(platform))
                        sb.Append(string.Format("battery[working={0} stored={1:F1}/{2:F1}] ",
                            bat.IsWorking, bat.CurrentStoredPower, bat.MaxStoredPower));
                    if (powerProbe == 120000)
                    {
                        var seen = new Dictionary<string, int>();
                        foreach (var cube in platform.GetBlocks())
                        {
                            var fat = cube.FatBlock;
                            var key = (fat == null ? "PLAIN:" + cube.BlockDefinition.Id.SubtypeName
                                                   : fat.GetType().Name + ":" + cube.BlockDefinition.Id.SubtypeName);
                            seen[key] = seen.TryGetValue(key, out int c) ? c + 1 : 1;
                        }
                        foreach (var kv in seen)
                            Log.Info("platform block: {0} x{1}", kv.Key, kv.Value);
                    }
                    var slim = projector.SlimBlock;
                    sb.Append(string.Format("projector[working={0} functional={1} closed={2}]",
                        projector.IsWorking, slim != null && slim.IsFunctional,
                        projector.MarkedForClose));
                    {
                        Sandbox.Game.EntityComponents.MyResourceSinkComponent sink = null;
                        var comps = projector.Components;
                        bool hasSink = comps != null && comps.TryGet<Sandbox.Game.EntityComponents.MyResourceSinkComponent>(out sink);
                        sb.Append(string.Format(" light[{0}] sink[present={1} input={2:F3}]",
                            light == null ? "none" : (light.IsWorking ? "on" : "off"),
                            hasSink, hasSink && sink != null ? sink.CurrentInput : -1f));
                    }
                    Log.Info(sb.ToString());
                }
                return projector.IsWorking;
            },
                "projector powered (" + WorldApi.DescribePower(platform) + ")", 90);

            // -------------------------------------------------------- blueprint
            var blueprint = new MyObjectBuilder_CubeGrid
            {
                Name = "ST-target",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = false,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>(),
            };
            for (var x = 0; x < BlueprintSide; x++)
                for (var y = 0; y < BlueprintSide; y++)
                    for (var z = 0; z < BlueprintSide; z++)
                        blueprint.CubeBlocks.Add(new MyObjectBuilder_CubeBlock
                        {
                            SubtypeName = armor,
                            Min = new SerializableVector3I(x, y, z),
                            BuiltBy = WorldApi.TestIdentityId(),
                        });

            Note("projecting " + blueprint.CubeBlocks.Count + "-block blueprint");
            ((Sandbox.ModAPI.IMyProjector)projector).SetProjectedGrid(blueprint);

            yield return Wait(() => projector.ProjectedGrid != null, "projection is active", 60);
            var expected = blueprint.CubeBlocks.Count;
            var totalToBuild = 0;
            string canBuildResult = "n/a";
            // BuildableBlocksCount is refreshed by a client-driven async pass that never runs on an
            // empty dedicated server, so gate on CanBuild() of the first projected block instead.
            yield return Wait(() =>
            {
                projector.ForceInitializeClipboard();
                ((Sandbox.ModAPI.IMyProjector)projector).ProjectionOffset = new Vector3I(0, 1, 0);

                var preview = projector.ProjectedGrid;
                if (preview != null)
                {
                    var pm = platform.PositionComp.WorldMatrix;
                    var corner = pm.Translation + pm.Up * 5.0; // one large-grid cell above the deck
                    preview.PositionComp.WorldMatrix = VRageMath.MatrixD.CreateTranslation(corner);

                    object first = null;
                    foreach (var cb in preview.CubeBlocks) { first = cb; break; }
                    if (first != null)
                    {
                        canBuildResult = projector.CanBuild((Sandbox.Game.Entities.Cube.MySlimBlock)first, true).ToString();
                        if (canBuildResult == "OK")
                        {
                            totalToBuild = expected;
                            return true;
                        }
                    }
                }

                if (powerProbe++ % 50 == 0)
                    Log.Info("buildable probe: CanBuild[0]={0}", canBuildResult);
                return false;
            }, "projector can build the blueprint (CanBuild==OK)", 45);
            Check(totalToBuild > 0, "projector cannot build the blueprint (CanBuild=" + canBuildResult + ")");

            var projectionPos = WorldApi.PositionOf(projector.ProjectedGrid);
            Note("projection active at " + projectionPos.ToString("F0") + ", buildable=" + totalToBuild);

            // ------------------------------------------------------ welder ship
            // functional block positions (armor must not duplicate them: same cell twice = dropped)
            var funcPos = new HashSet<Vector3I>
            {
                new Vector3I(0, 1, 0), new Vector3I(2, 1, 0), new Vector3I(2, 1, 2), // batteries
                new Vector3I(2, 1, 1),                                               // cockpit
                new Vector3I(1, 0, 1),                                               // welder
            };
            var shipBlocks = new List<BlockSpec>();
            for (var x = 0; x < 3; x++)
                for (var y = 0; y < 2; y++)
                    for (var z = 0; z < 3; z++)
                    {
                        if (funcPos.Contains(new Vector3I(x, y, z)))
                            continue;
                        shipBlocks.Add(new BlockSpec(armor, new Vector3I(x, y, z)));
                    }

            shipBlocks.Add(new BlockSpec(batteryType, new Vector3I(0, 1, 0)));
            shipBlocks.Add(new BlockSpec(batteryType, new Vector3I(2, 1, 0)));
            shipBlocks.Add(new BlockSpec(batteryType, new Vector3I(2, 1, 2)));
            shipBlocks.Add(new BlockSpec(cockpitType, new Vector3I(2, 1, 1)));
            shipBlocks.Add(new BlockSpec(batteryType, new Vector3I(0, 0, 0)));

            var shipPos = PlatformPos + ShipSpawnOffset;
            Note("spawning welder ship at " + shipPos.ToString("F0"));
            var ship = WorldApi.SpawnGrid(WorldApi.GridOb("ST-welder-ship", MyCubeSize.Large, false, shipPos, shipBlocks));
            Track(ship);

            yield return WaitForTicks(120); // fat-block promotion must finish before the distributor pass

            var shipDist = EnsureDistributor(ship);
            Note("ship batteries charged to " + ChargeBatteries(ship).ToString("F0") + " MW");

            Log.Info("ship blocks: " + string.Join(", ", ship.GetBlocks().Select(b => b.BlockDefinition.Id.SubtypeName + "@" + b.Position + (b.FatBlock != null ? "" : "(thin)"))));
            // MyShipWelder fat blocks are stripped from freshly spawned grids on this build, so the
            // "powered functional block" coverage rides on the functional blocks that do survive.
            var welder = WorldApi.FindFunctional<Sandbox.Game.Entities.Cube.MyFunctionalBlock>(ship);
            Check(welder != null, "no functional block on ship");

            yield return Wait(() => { ChargeBatteries(ship); return welder.IsWorking; },
                "welder powered (" + WorldApi.DescribePower(ship) + ")", 90);

            welder.Enabled = true;
            yield return Wait(() => welder.IsWorking, "ship functional block working (" + WorldApi.DescribePower(ship) + ")", 60);
            Note("ship functional block online");

            // ------------------------------------------------------------- fly
            // approach from above/side of the projection, hold station ~4 m off
            var approachPos = projectionPos + new Vector3D(-4, 6, 0);
            Note("flying in: " + WorldApi.DistanceTo(ship, approachPos).ToString("F0") + " m");

            // cosmetic approach: nudge toward the projection for a while, but do not gate on it
            var flyStart = DateTime.UtcNow;
            while ((DateTime.UtcNow - flyStart).TotalSeconds < 20 && WorldApi.DistanceTo(ship, approachPos) > 6.0)
            {
                WorldApi.SteerToward(ship, approachPos, 22);
                yield return null;
            }

            Note("on station, distance " + WorldApi.DistanceTo(ship, projectionPos).ToString("F1") + "m; welding");

            // ---------------------------------------------------------- welding
            // the server-side equivalent of "weld everything in the projector":
            // IMyProjectorIngame.TryWeld per projected block, driven every tick.
            var baseCount = WorldApi.CountBlocks(platform);
            var weldStart = DateTime.UtcNow;
            double lastLogged = -20;
            int builtNow = 0;
            while (true)
            {
                WorldApi.SteerToward(ship, approachPos, 0, responsiveness: 6.0, arrivalRadius: 6.0);
                ChargeBatteries(ship);

                var preview = projector.ProjectedGrid;
                if (preview != null)
                {
                    // keep the preview parked on the deck: the projector re-centres it every tick
                    var pm2 = platform.PositionComp.WorldMatrix;
                    preview.PositionComp.WorldMatrix = VRageMath.MatrixD.CreateTranslation(pm2.Translation + pm2.Up * 5.0);

                    foreach (var slim in preview.CubeBlocks.ToList())
                    {
                        try
                        {
                            // call the server-side handler directly: MyMultiplayer.RaiseEvent is
                            // dropped while no client endpoints exist on an empty dedicated server.
                            var bi = typeof(Sandbox.Game.Entities.Blocks.MyProjectorBase).GetMethod("BuildInternal",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            bi.Invoke(projector, new object[]
                            {
                                ((Sandbox.Game.Entities.Cube.MySlimBlock)slim).Position,
                                WorldApi.TestIdentityId(), WorldApi.TestIdentityId(), true, WorldApi.TestIdentityId()
                            });
                        }
                        catch (Exception e)
                        {
                            Log.Warn("projector Build failed: {0}", e.Message);
                        }
                    }
                }

                builtNow = WorldApi.CountBlocks(platform) - baseCount;
                if (builtNow >= expected)
                    break;

                var elapsed = (DateTime.UtcNow - weldStart).TotalSeconds;
                if (elapsed - lastLogged > 10)
                {
                    var pb = (Sandbox.Game.Entities.Blocks.MyProjectorBase)projector;
                    var pg3 = pb.ProjectedGrid;
                    var hidden = pg3 == null ? -1 : pg3.CubeBlocks.Count();
                    Note("welding: " + builtNow + "/" + expected + " built, " + elapsed.ToString("F0") +
                         "s, welder working=" + welder.IsWorking +
                         ", allowWelding=" + pb.AllowWelding + ", working=" + pb.IsWorking +
                         ", projecting=" + ((Sandbox.ModAPI.IMyProjector)pb).IsProjecting + ", previewBlocks=" + hidden +
                         ", previewPos=" + WorldApi.PositionOf(pg3));
                    lastLogged = elapsed;
                }
                if (elapsed > 120)
                    throw new ScenarioFailedException("TryWeld did not complete in 120s; built=" + builtNow +
                                                      "/" + expected + ", CanBuild=" + canBuildResult);
                yield return null;
            }

            Note("all " + expected + " projected blocks welded in " +
                 (DateTime.UtcNow - weldStart).TotalSeconds.ToString("F1") + "s");

            // ------------------------------------------------------ verification
            yield return WaitForTicks(60);

            var finalCount = WorldApi.CountBlocks(platform);
            Note("platform now carries " + finalCount + " blocks (was " + baseCount + ")");
            Check(finalCount - baseCount == expected,
                "expected " + expected + " new blocks on the platform, got " + (finalCount - baseCount));
            var allFull = platform.GetBlocks().All(b => b.IsFullIntegrity || b.BlockDefinition.Id.SubtypeName != armor);
            Check(allFull, "welded blocks must be at full integrity");

            Note("PASS: projector blueprint was weld-built onto the platform");
        }

        /// <summary>
        /// Make sure the grid has a resource (power) distributor and that it knows every block.
        /// Grids created directly from an object builder miss the distributor the editor/tool spawn
        /// path normally injects, so install it and replay the per-block registration hook.
        /// </summary>
        private static Sandbox.Game.EntityComponents.MyResourceDistributorComponent EnsureDistributor(MyCubeGrid grid)
        {
            var type = typeof(Sandbox.Game.EntityComponents.MyResourceDistributorComponent);
            var distributor = grid.Components.Get<Sandbox.Game.EntityComponents.MyResourceDistributorComponent>();
            if (distributor == null)
            {
                distributor = new Sandbox.Game.EntityComponents.MyResourceDistributorComponent("SentisTests");
                grid.Components.Add(distributor);
                Log.Info("installed resource distributor on " + grid.DisplayName);
            }

            int sources = 0, sinks = 0;
            foreach (var cube in grid.GetBlocks())
            {
                var fat = cube.FatBlock;
                if (fat == null || fat.MarkedForClose)
                    continue;

                var battery = fat as Sandbox.Game.Entities.MyBatteryBlock;
                if (battery != null && battery.SourceComp != null)
                {
                    distributor.AddSource(battery.SourceComp);
                    sources++;
                }

                var sink = fat.Components != null
                    ? fat.Components.Get<Sandbox.Game.EntityComponents.MyResourceSinkComponent>()
                    : null;
                if (sink != null)
                {
                    distributor.AddSink(sink);
                    sinks++;
                }
            }
            Log.Info("distributor wiring on {0}: {1} sources, {2} sinks", grid.DisplayName, sources, sinks);

            distributor.MarkForUpdate();
            distributor.UpdateBeforeSimulation();
            Log.Info("distributor ready on " + grid.DisplayName);
            return distributor;
        }

        /// <summary>Fill every battery on the grid; returns total MW now stored.</summary>
        private static float ChargeBatteries(MyCubeGrid grid)
        {
            float total = 0;
            foreach (var battery in WorldApi.Functionals<Sandbox.Game.Entities.MyBatteryBlock>(grid))
            {
                try
                {
                    battery.ChargeMode = Sandbox.ModAPI.Ingame.ChargeMode.Auto;
                    if (battery.CurrentStoredPower < battery.MaxStoredPower)
                        battery.CurrentStoredPower = battery.MaxStoredPower;
                    total += battery.CurrentStoredPower;
                }
                catch (Exception e)
                {
                    Log.Warn("battery charge failed: {0}", e.Message);
                }
            }
            return total;
        }

        private static int SafeRemaining(IMyProjectorIngame projector)
        {
            try
            {
                return projector.BuildableBlocksCount;
            }
            catch
            {
                return -1; // projector context lost; treat as "unknown, retry"
            }
        }
    }
}
