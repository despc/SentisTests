using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;

namespace SentisTests.Core
{
    /// <summary>
    /// Measures real simulation work per server frame: the time spent inside
    /// <c>MySandboxGame.Update</c>, which excludes the loop's sleep to 60 Hz (TickMetrics' gap
    /// metric includes that sleep, so it cannot tell jitter from load). Frames over the 16.67 ms
    /// budget are attributed to a few hooked sections so a spike can be traced to its source.
    /// </summary>
    public static class FrameProbe
    {
        public const double BudgetMs = 1000.0 / 60.0;
        private const int WorstKept = 12;
        // Breakdowns are kept for frames above half the budget, so the tail is explained before it
        // turns into a missed frame.
        private const double ReportAboveMs = BudgetMs / 2;

        private enum Section { Tools, ProjectorBuild, Physics, Refinery, ConveyorPull, ConveyorPush, RefineryUpdateProduction, RefineryRebuildQueue, RefineryRebuildQueue2, InventoryTransfer, QueueInsert, QueueClear, RefineryProcess, InvTransferOrRemove, InvAddItems, ObCreate, InvFitsBlueprint, QueueRemoveRequest, SinkSetRequired, EntitiesBefore, EntitiesAfter, SessionComponents, Harness, Bridge, ReplicationBefore, ReplicationSend, NetProcess, FakeClients, ReplFilterStateSync, ReplAddForClient, ReplRefreshReplicable, ReplGridSerialize, ReplClientAcks, ReplApplyDirty, SgInventory, SgProperty, SgPhysics, SgCreateClientData, ReplStreamingEntry, ReplRemoveForClient, GridGetObjectBuilder, InvRefreshClientData, ReplDirtyIndex, DrillUpdate10, DrillAfterSim, DrillUpdate100, MiningSchedule, DrillCutFinish, DrillResults, VoxelNotify, WheelSystem, SuspensionUpdate, MechUpdateBefore, WheelUpdateBefore, WheelUpdateAfter, WheelContact, GrindActivate, GrindSphere, GrindDecrease, GrindMoveItems, GridRaze, GridUpdateVisual, GridSendStockpile, GridSendIntegrity, GridDisconnects, GridInventoryMass, GrindAnimation, GrindEmptyInv, GrindSpawnStockpile, DamageBefore, DamageAfter, DamageDestroyed, FactionReputation, ThrustUpdate, ThrustRecompute, ThrustThrusts, ThrustBlock100, PowerDistributor, GasGenerator100, GasTank100, ConveyorPullRequest, GasGenerator, GasTank, GasSetCapacities, GasCheckProducing, VoxelSave, VoxelStreamSerialize, Count }

        private static readonly string[] SectionNames = { "tools10", "projector.Build", "physics", "refinery.tick", "conveyor.pull", "conveyor.push", "refinery.updateProduction", "refinery.rebuildQueue", "sgi.rebuildQueue", "inventory.transfer", "queue.insert", "queue.clear", "refinery.process", "inv.transferOrRemove", "inv.addItems", "ob.createNewObject", "inv.fitsBlueprint", "queue.removeRequest", "sink.setRequired", "entities.before", "entities.after", "session.components", "harness", "bridge", "replication.updateBefore", "replication.sendUpdate", "net.receiveProcess", "fakeClients.tick", "repl.filterStateSync", "repl.addForClient", "repl.refreshReplicable", "repl.gridSerialize", "repl.clientAcks", "repl.applyDirtyGroups", "sg.inventory.serialize", "sg.property.serialize", "sg.physics.serialize", "sg.createClientData", "repl.sendStreamingEntry", "repl.removeForClient", "grid.getObjectBuilder", "inv.refreshClientData", "repl.dirtyIndex", "drill.update10", "drill.afterSim", "drill.update100", "mining.schedule", "drill.cutFinish", "drill.results", "voxel.notifyChanged", "wheels.system", "suspension.update", "mech.updateBefore", "wheel.updateBefore", "wheel.updateAfter", "wheel.contact", "grind.activate", "grind.sphere", "grind.decrease", "grind.moveItems", "grid.raze", "grid.updateVisual", "grid.sendStockpile", "grid.sendIntegrity", "grid.disconnects", "grid.inventoryMass", "grind.animation", "grind.emptyInventories", "grind.spawnStockpile", "damage.before", "damage.after", "damage.destroyed", "faction.reputation", "thrust.update", "thrust.recompute", "thrust.thrusts", "thrust.block100", "power.distributor", "gas.generator100", "gas.tank100", "conveyor.pullRequest", "gas.generator", "gas.tank", "gas.setCapacities", "gas.checkProducing", "voxel.save", "voxel.streamSerialize" };

        // Sections timed inside another section; excluded from the top-level sum behind "other".
        private static readonly bool[] Nested = { false, true, false, false, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, false, true, false, false, false, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, false, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true };
        private static readonly long[] _sectionTicks = new long[(int)Section.Count];
        private static readonly long[] _sectionStart = new long[(int)Section.Count];
        private static readonly int[] _sectionDepth = new int[(int)Section.Count];
        // Whole-window totals, so the steady load is explained and not only the worst frames.
        private static readonly long[] _windowTicks = new long[(int)Section.Count];
        private static readonly long[] _windowCalls = new long[(int)Section.Count];
        private static readonly long[] _windowAllocBytes = new long[(int)Section.Count];
        private static readonly long[] _sectionAllocStart = new long[(int)Section.Count];
        private static long _frameAllocStart;
        private static long _windowFrameAllocBytes;
        private static long _emptyPulls;
        // Frames by the highest GC generation collected inside them (-1: none): count and busy ms.
        private static readonly long[] _gcFrames = new long[4];
        private static readonly double[] _gcFrameMs = new double[4];

        private static readonly List<double> _busyMs = new List<double>();
        // Per-frame section ticks kept for every frame, so the report can compare what frames over
        // the budget do differently from ordinary ones, not only the handful of worst ones.
        private static readonly List<double> _frameMsList = new List<double>();
        private static readonly List<int> _frameGcBucket = new List<int>();
        private static readonly List<long[]> _frameSections = new List<long[]>();
        private static readonly List<long> _frameAlloc = new List<long>();
        private static readonly List<string> _worst = new List<string>();
        private static readonly List<double> _worstMs = new List<double>();
        private static readonly List<double> _buildMs = new List<double>();
        private static readonly List<string> _slowBuilds = new List<string>();
        private static long _buildStart;
        private static readonly int[] _gcAtFrameStart = new int[3];
        private static int _refineryTicksThisFrame;
        private static readonly List<int> _refineryTicksPerFrame = new List<int>();
        private static long _frameStart;
        private static long _frameIndex;
        private static int _gameThreadId;
        private static bool _installed;

        public static void Install(PatchManager patchManager)
        {
            if (_installed || patchManager == null) return;
            var ctx = patchManager.AcquireContext();
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                     BindingFlags.NonPublic;

            Hook(ctx, typeof(MySandboxGame).GetMethod("Update", any, null, Type.EmptyTypes, null),
                nameof(FramePrefix), nameof(FrameSuffix));
            Hook(ctx, typeof(MyShipToolBase).GetMethod("UpdateAfterSimulation10", any, null, Type.EmptyTypes, null),
                nameof(ToolsPrefix), nameof(ToolsSuffix));
            Hook(ctx, typeof(MyProjectorBase).GetMethod("Build", any),
                nameof(BuildPrefix), nameof(BuildSuffix));
            Hook(ctx, typeof(MyPhysics).GetMethod("Simulate", any, null, Type.EmptyTypes, null),
                nameof(PhysicsPrefix), nameof(PhysicsSuffix));
            Hook(ctx, typeof(MyRefinery).GetMethod("DoUpdateTimerTick", any, null, Type.EmptyTypes, null),
                nameof(RefineryPrefix), nameof(RefinerySuffix));
            Hook(ctx, typeof(MyGridConveyorSystem).GetMethod("PullItems", any),
                nameof(PullPrefix), nameof(PullSuffix));
            Hook(ctx, typeof(MyGridConveyorSystem).GetMethod("PushAnyRequest", any),
                nameof(PushPrefix), nameof(PushSuffix));
            Hook(ctx, typeof(MyRefinery).GetMethod("UpdateProduction", any),
                nameof(UpdateProductionPrefix), nameof(UpdateProductionSuffix));
            var sgiRebuild = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("SentisGameplayImprovements.RefineryPatchs", false))
                .FirstOrDefault(type => type != null)?.GetMethod("DoUpdateTimerTickPatch", any);
            if (sgiRebuild != null) Hook(ctx, sgiRebuild, nameof(SgiRebuildPrefix), nameof(SgiRebuildSuffix));
            Hook(ctx, typeof(Sandbox.Game.MyInventory).GetMethod("TransferItemsInternal", any),
                nameof(TransferPrefix), nameof(TransferSuffix));
            Hook(ctx, typeof(MyProductionBlock).GetMethod("InsertQueueItemRequest", any, null,
                    new[] { typeof(int), typeof(Sandbox.Definitions.MyBlueprintDefinitionBase), typeof(VRage.MyFixedPoint) }, null),
                nameof(QueueInsertPrefix), nameof(QueueInsertSuffix));
            Hook(ctx, typeof(MyProductionBlock).GetMethod("ClearQueue", any),
                nameof(QueueClearPrefix), nameof(QueueClearSuffix));
            Hook(ctx, typeof(Sandbox.Game.MyInventory).GetMethod("TransferOrRemove", any),
                nameof(TransferOrRemovePrefix), nameof(TransferOrRemoveSuffix));
            Hook(ctx, typeof(Sandbox.Game.MyInventory).GetMethod("AddItems", any, null,
                    new[] { typeof(VRage.MyFixedPoint), typeof(VRage.ObjectBuilders.MyObjectBuilder_Base) }, null),
                nameof(AddItemsPrefix), nameof(AddItemsSuffix));
            Hook(ctx, typeof(VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen).GetMethod("CreateNewObject", any, null,
                    new[] { typeof(VRage.Game.MyDefinitionId) }, null),
                nameof(ObCreatePrefix), nameof(ObCreateSuffix));
            Hook(ctx, typeof(Sandbox.Game.MyInventory).GetMethod("ComputeAmountThatFits", any, null,
                    new[] { typeof(Sandbox.Definitions.MyBlueprintDefinitionBase) }, null),
                nameof(FitsPrefix), nameof(FitsSuffix));
            // Totals only (marked nested): these contain the refinery and physics sections.
            Hook(ctx, typeof(Sandbox.Game.Entities.MyEntities).GetMethod("UpdateBeforeSimulation", BindingFlags.Static | BindingFlags.Public),
                nameof(EntitiesBeforePrefix), nameof(EntitiesBeforeSuffix));
            Hook(ctx, typeof(Sandbox.Game.Entities.MyEntities).GetMethod("UpdateAfterSimulation", BindingFlags.Static | BindingFlags.Public),
                nameof(EntitiesAfterPrefix), nameof(EntitiesAfterSuffix));
            Hook(ctx, typeof(Sandbox.Game.World.MySession).GetMethod("UpdateComponents", any, null, Type.EmptyTypes, null),
                nameof(SessionComponentsPrefix), nameof(SessionComponentsSuffix));
            Hook(ctx, typeof(Sandbox.Game.EntityComponents.MyResourceSinkComponent).GetMethod("SetRequiredInputByType", any),
                nameof(SinkPrefix), nameof(SinkSuffix));
            Hook(ctx, typeof(MyRefinery).GetMethod("ProcessQueueItems", any),
                nameof(ProcessPrefix), nameof(ProcessSuffix));
            Hook(ctx, typeof(MyProductionBlock).GetMethod("RemoveQueueItemRequest", any),
                nameof(QueueRemovePrefix), nameof(QueueRemoveSuffix));
            Hook(ctx, typeof(VRage.Network.MyReplicationServer).GetMethod("UpdateBefore", BindingFlags.Instance | BindingFlags.Public),
                nameof(ReplicationBeforePrefix), nameof(ReplicationBeforeSuffix));
            Hook(ctx, typeof(VRage.Network.MyReplicationServer).GetMethod("SendUpdate", BindingFlags.Instance | BindingFlags.Public),
                nameof(ReplicationSendPrefix), nameof(ReplicationSendSuffix));
            Hook(ctx, typeof(Sandbox.Engine.Networking.MyNetworkWriter).Assembly
                    .GetType("Sandbox.Engine.Networking.MyNetworkReader")?.GetMethod("Process", BindingFlags.Static | BindingFlags.Public),
                nameof(NetProcessPrefix), nameof(NetProcessSuffix));
            var replicationServer = typeof(VRage.Network.MyReplicationServer);
            var groupsAssembly = typeof(Sandbox.Game.Entities.MyCubeGrid).Assembly;
            Hook(ctx, replicationServer.GetMethod("FilterStateSync", any), nameof(ReplFilterPrefix), nameof(ReplFilterSuffix));
            Hook(ctx, replicationServer.GetMethod("AddForClient", any), nameof(ReplAddPrefix), nameof(ReplAddSuffix));
            Hook(ctx, replicationServer.GetMethod("RefreshReplicable", any), nameof(ReplRefreshPrefix), nameof(ReplRefreshSuffix));
            Hook(ctx, typeof(Sandbox.Game.Entities.MyCubeGrid).GetMethod("GetObjectBuilder", any, null, new[] { typeof(bool) }, null),
                nameof(GridBuilderPrefix), nameof(GridBuilderSuffix));
            Hook(ctx, groupsAssembly.GetType("Sandbox.Game.Replication.StateGroups.MyEntityInventoryStateGroup")?
                    .GetMethod("RefreshClientData", BindingFlags.Instance | BindingFlags.Public),
                nameof(InvRefreshPrefix), nameof(InvRefreshSuffix));
            Hook(ctx, replicationServer.GetMethod("SendStreamingEntry", any), nameof(ReplStreamPrefix), nameof(ReplStreamSuffix));
            Hook(ctx, replicationServer.GetMethod("RemoveForClient", any), nameof(ReplRemovePrefix), nameof(ReplRemoveSuffix));
            Hook(ctx, replicationServer.GetMethod("OnClientAcks", any), nameof(ReplAcksPrefix), nameof(ReplAcksSuffix));
            Hook(ctx, replicationServer.GetMethod("ApplyDirtyGroups", any), nameof(ReplDirtyPrefix), nameof(ReplDirtySuffix));
            // SentisOptimisations applies dirty groups in its own prefix and skips the game's method.
            var dirtyIndex = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Optimizer.Optimizations.StateGroupClients"))
                .FirstOrDefault(t => t != null)?.GetMethod("ApplyDirtyGroupsPrefix", any);
            if (dirtyIndex != null) Hook(ctx, dirtyIndex, nameof(ReplDirtyIndexPrefix), nameof(ReplDirtyIndexSuffix));
            Hook(ctx, typeof(MyShipDrill).GetMethod("UpdateBeforeSimulation10", any, null, Type.EmptyTypes, null),
                nameof(DrillUpdate10Prefix), nameof(DrillUpdate10Suffix));
            Hook(ctx, typeof(MyShipDrill).GetMethod("UpdateAfterSimulation", any, null, Type.EmptyTypes, null),
                nameof(DrillAfterSimPrefix), nameof(DrillAfterSimSuffix));
            Hook(ctx, typeof(MyShipDrill).GetMethod("UpdateAfterSimulation100", any, null, Type.EmptyTypes, null),
                nameof(DrillUpdate100Prefix), nameof(DrillUpdate100Suffix));
            var mining = typeof(MyShipDrill).Assembly.GetType("Sandbox.Game.GameSystems.MyShipMiningSystem");
            Hook(ctx, mining?.GetMethod("ScheduleCutouts", any), nameof(MiningSchedulePrefix), nameof(MiningScheduleSuffix));
            Hook(ctx, mining?.GetNestedType("ClusterCutOut", any)?.GetMethod("Finish", any),
                nameof(DrillCutFinishPrefix), nameof(DrillCutFinishSuffix));
            Hook(ctx, typeof(MyDrillBase).GetMethod("OnDrillResults", any), nameof(DrillResultsPrefix), nameof(DrillResultsSuffix));
            Hook(ctx, typeof(MyShipDrill).Assembly.GetType("Sandbox.Engine.Voxels.MyVoxelGenerator")?.GetMethod("NotifyVoxelChanged", any),
                nameof(VoxelNotifyPrefix), nameof(VoxelNotifySuffix));
            // What a connecting client makes the server pay for a dug planet: the storage is
            // serialized and compressed whole, and every dig throws that blob away again.
            Hook(ctx, typeof(MyShipDrill).Assembly.GetType("Sandbox.Engine.Voxels.MyStorageBase")?.GetMethod("Save", any),
                nameof(VoxelSavePrefix), nameof(VoxelSaveSuffix));
            Hook(ctx, typeof(MyShipDrill).Assembly.GetType("Sandbox.Game.Replication.MyVoxelReplicable")?.GetMethod("Serialize", any),
                nameof(VoxelStreamSerializePrefix), nameof(VoxelStreamSerializeSuffix));
            var sandbox = typeof(MyShipDrill).Assembly;
            Hook(ctx, sandbox.GetType("Sandbox.Game.GameSystems.MyGridWheelSystem")?.GetMethod("Update", any, null, Type.EmptyTypes, null),
                nameof(WheelSystemPrefix), nameof(WheelSystemSuffix));
            Hook(ctx, sandbox.GetType("Sandbox.Game.Entities.Cube.MyMotorSuspension")?.GetMethod("Update", any, null, Type.EmptyTypes, null),
                nameof(SuspensionUpdatePrefix), nameof(SuspensionUpdateSuffix));
            Hook(ctx, sandbox.GetType("Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase")?.GetMethod("UpdateBeforeSimulation", any, null, Type.EmptyTypes, null),
                nameof(MechUpdateBeforePrefix), nameof(MechUpdateBeforeSuffix));
            var wheel = sandbox.GetType("Sandbox.Game.Entities.Blocks.MyWheel");
            Hook(ctx, wheel?.GetMethod("UpdateBeforeSimulation", any, null, Type.EmptyTypes, null), nameof(WheelUpdateBeforePrefix), nameof(WheelUpdateBeforeSuffix));
            Hook(ctx, wheel?.GetMethod("UpdateAfterSimulation", any, null, Type.EmptyTypes, null), nameof(WheelUpdateAfterPrefix), nameof(WheelUpdateAfterSuffix));
            Hook(ctx, wheel?.GetMethod("ContactPointCallback", any), nameof(WheelContactPrefix), nameof(WheelContactSuffix));
            var cubeGrid = typeof(Sandbox.Game.Entities.MyCubeGrid);
            var slimBlock = typeof(Sandbox.Game.Entities.Cube.MySlimBlock);
            var grinder = typeof(Sandbox.Game.Weapons.MyShipGrinder);
            Hook(ctx, grinder.GetMethod("Activate", any), nameof(GrindActivatePrefix), nameof(GrindActivateSuffix));
            Hook(ctx, cubeGrid.GetMethod("GetBlocksInsideSphereInternal", any), nameof(GrindSpherePrefix), nameof(GrindSphereSuffix));
            Hook(ctx, slimBlock.GetMethod("DecreaseMountLevel", any), nameof(GrindDecreasePrefix), nameof(GrindDecreaseSuffix));
            Hook(ctx, slimBlock.GetMethod("MoveItemsFromConstructionStockpile", any), nameof(GrindMoveItemsPrefix), nameof(GrindMoveItemsSuffix));
            Hook(ctx, cubeGrid.GetMethod("RazeBlock", any, null, new[] { typeof(VRageMath.Vector3I), typeof(ulong) }, null), nameof(GridRazePrefix), nameof(GridRazeSuffix));
            Hook(ctx, slimBlock.GetMethod("UpdateVisual", any), nameof(GridUpdateVisualPrefix), nameof(GridUpdateVisualSuffix));
            Hook(ctx, cubeGrid.GetMethod("SendStockpileChanged", any), nameof(GridSendStockpilePrefix), nameof(GridSendStockpileSuffix));
            Hook(ctx, cubeGrid.GetMethod("SendIntegrityChanged", any), nameof(GridSendIntegrityPrefix), nameof(GridSendIntegritySuffix));
            Hook(ctx, cubeGrid.GetMethod("DetectDisconnects", any), nameof(GridDisconnectsPrefix), nameof(GridDisconnectsSuffix));
            Hook(ctx, cubeGrid.GetMethod("UpdateInventoryMass", any), nameof(GridInventoryMassPrefix), nameof(GridInventoryMassSuffix));
            Hook(ctx, grinder.GetMethod("StartAnimation", any), nameof(GrindAnimationPrefix), nameof(GrindAnimationSuffix));
            Hook(ctx, grinder.GetMethod("StopAnimation", any), nameof(GrindAnimationPrefix), nameof(GrindAnimationSuffix));
            var damage = typeof(Sandbox.Game.GameSystems.MyDamageSystem);
            Hook(ctx, grinder.GetMethod("EmptyBlockInventories", any), nameof(GrindEmptyInvPrefix), nameof(GrindEmptyInvSuffix));
            Hook(ctx, slimBlock.GetMethod("SpawnConstructionStockpile", any), nameof(GrindSpawnStockpilePrefix), nameof(GrindSpawnStockpileSuffix));
            Hook(ctx, damage.GetMethod("RaiseBeforeDamageApplied", any), nameof(DamageBeforePrefix), nameof(DamageBeforeSuffix));
            Hook(ctx, damage.GetMethod("RaiseAfterDamageApplied", any), nameof(DamageAfterPrefix), nameof(DamageAfterSuffix));
            Hook(ctx, damage.GetMethod("RaiseDestroyed", any), nameof(DamageDestroyedPrefix), nameof(DamageDestroyedSuffix));
            Hook(ctx, typeof(Sandbox.Game.Multiplayer.MyFactionCollection).GetMethod("DamageFactionPlayerReputation", any), nameof(FactionReputationPrefix), nameof(FactionReputationSuffix));
            var thrustComp = typeof(Sandbox.Game.GameSystems.MyEntityThrustComponent);
            Hook(ctx, First(thrustComp, "UpdateBeforeSimulation"), nameof(ThrustUpdatePrefix), nameof(ThrustUpdateSuffix));
            Hook(ctx, First(thrustComp, "RecomputeThrustParameters"), nameof(ThrustRecomputePrefix), nameof(ThrustRecomputeSuffix));
            Hook(ctx, First(thrustComp, "UpdateThrusts"), nameof(ThrustThrustsPrefix), nameof(ThrustThrustsSuffix));
            Hook(ctx, First(typeof(Sandbox.Game.Entities.MyThrust), "UpdateBeforeSimulation100"), nameof(ThrustBlock100Prefix), nameof(ThrustBlock100Suffix));
            Hook(ctx, typeof(Sandbox.Game.EntityComponents.MyResourceDistributorComponent).GetMethod("UpdateBeforeSimulation", any, null, System.Type.EmptyTypes, null),
                nameof(PowerDistributorPrefix), nameof(PowerDistributorSuffix));
            Hook(ctx, First(typeof(Sandbox.Game.Entities.Blocks.MyGasGenerator), "UpdateAfterSimulation100"), nameof(GasGenerator100Prefix), nameof(GasGenerator100Suffix));
            Hook(ctx, First(typeof(Sandbox.Game.Entities.Blocks.MyGasTank), "UpdateAfterSimulation100"), nameof(GasTank100Prefix), nameof(GasTank100Suffix));
            Hook(ctx, First(typeof(MyGridConveyorSystem), "PullItem"), nameof(ConveyorPullRequestPrefix), nameof(ConveyorPullRequestSuffix));
            Hook(ctx, First(typeof(Sandbox.Game.Entities.Blocks.MyGasGenerator), "UpdateAfterSimulation"), nameof(GasGeneratorPrefix), nameof(GasGeneratorSuffix));
            Hook(ctx, First(typeof(Sandbox.Game.Entities.Blocks.MyGasTank), "UpdateAfterSimulation"), nameof(GasTankPrefix), nameof(GasTankSuffix));
            Hook(ctx, First(typeof(Sandbox.Game.Entities.Blocks.MyGasGenerator), "SetRemainingCapacities"), nameof(GasSetCapacitiesPrefix), nameof(GasSetCapacitiesSuffix));
            Hook(ctx, First(typeof(Sandbox.Game.Entities.Blocks.MyGasGenerator), "CheckProducigState"), nameof(GasCheckProducingPrefix), nameof(GasCheckProducingSuffix));
            Hook(ctx, typeof(Sandbox.Game.Entities.MyCubeGrid).Assembly.GetType("Sandbox.Game.Replication.MyCubeGridReplicable")?
                    .GetMethod("Serialize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
                nameof(ReplGridSerializePrefix), nameof(ReplGridSerializeSuffix));
            Hook(ctx, groupsAssembly.GetType("Sandbox.Game.Replication.StateGroups.MyEntityInventoryStateGroup")?.GetMethod("Serialize", BindingFlags.Instance | BindingFlags.Public),
                nameof(SgInventoryPrefix), nameof(SgInventorySuffix));
            Hook(ctx, groupsAssembly.GetType("Sandbox.Game.Replication.StateGroups.MyPropertySyncStateGroup")?.GetMethod("Serialize", BindingFlags.Instance | BindingFlags.Public),
                nameof(SgPropertyPrefix), nameof(SgPropertySuffix));
            Hook(ctx, groupsAssembly.GetType("Sandbox.Game.Replication.StateGroups.MyEntityPhysicsStateGroup")?.GetMethod("Serialize", BindingFlags.Instance | BindingFlags.Public),
                nameof(SgPhysicsPrefix), nameof(SgPhysicsSuffix));
            Hook(ctx, groupsAssembly.GetType("Sandbox.Game.Replication.StateGroups.MyEntityInventoryStateGroup")?.GetMethod("CreateClientData", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(VRage.Network.Endpoint) }, null),
                nameof(SgCreatePrefix), nameof(SgCreateSuffix));
            Hook(ctx, groupsAssembly.GetType("Sandbox.Game.Replication.StateGroups.MyPropertySyncStateGroup")?.GetMethod("CreateClientData", BindingFlags.Instance | BindingFlags.Public),
                nameof(SgCreatePrefix), nameof(SgCreateSuffix));
            patchManager.Commit();
            _installed = true;
        }

        /// <summary>The method declared on the type itself; overloads take the one with most parameters.</summary>
        private static MethodInfo First(Type type, string name)
        {
            MethodInfo best = null;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (method.Name == name && (best == null || method.GetParameters().Length > best.GetParameters().Length))
                    best = method;
            return best;
        }

        private static void Hook(PatchContext ctx, MethodInfo target, string prefix, string suffix)
        {
            if (target == null)
            {
                SentisTestsPlugin.Log.Warn("FrameProbe: hook target not found for " + prefix);
                return;
            }
            var pattern = ctx.GetPattern(target);
            pattern.Prefixes.Add(typeof(FrameProbe).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic));
            pattern.Suffixes.Add(typeof(FrameProbe).GetMethod(suffix, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void FramePrefix()
        {
            _gameThreadId = Thread.CurrentThread.ManagedThreadId;
            Array.Clear(_sectionTicks, 0, _sectionTicks.Length);
            for (var g = 0; g < 3; g++) _gcAtFrameStart[g] = GC.CollectionCount(g);
            _refineryTicksThisFrame = 0;
            _frameAllocStart = GC.GetAllocatedBytesForCurrentThread();
            _frameStart = Stopwatch.GetTimestamp();
        }

        private static void FrameSuffix()
        {
            if (_frameStart == 0) return;
            var ms = (Stopwatch.GetTimestamp() - _frameStart) * 1000.0 / Stopwatch.Frequency;
            _frameStart = 0;
            var frameAlloc = GC.GetAllocatedBytesForCurrentThread() - _frameAllocStart;
            _windowFrameAllocBytes += frameAlloc;
            _frameIndex++;
            _busyMs.Add(ms);
            _refineryTicksPerFrame.Add(_refineryTicksThisFrame);
            var gcBucket = 0;
            for (var g = 2; g >= 0; g--)
                if (GC.CollectionCount(g) != _gcAtFrameStart[g]) { gcBucket = g + 1; break; }
            _gcFrames[gcBucket]++;
            _gcFrameMs[gcBucket] += ms;
            _frameMsList.Add(ms);
            _frameGcBucket.Add(gcBucket);
            _frameSections.Add((long[])_sectionTicks.Clone());
            _frameAlloc.Add(frameAlloc);
            if (ms <= ReportAboveMs) return;
            if (_worstMs.Count >= WorstKept && ms <= _worstMs.Min()) return;

            var sb = new StringBuilder();
            sb.Append("#").Append(_frameIndex).Append(' ').Append(ms.ToString("F2")).Append("ms [");
            if (_refineryTicksThisFrame > 0) sb.Append("refineryTicks=").Append(_refineryTicksThisFrame).Append(' ');
            double attributed = 0;
            for (var i = 0; i < (int)Section.Count; i++)
            {
                var sectionMs = _sectionTicks[i] * 1000.0 / Stopwatch.Frequency;
                if (!Nested[i]) attributed += sectionMs;
                if (sectionMs < 0.005) continue;
                sb.Append(SectionNames[i]).Append('=').Append(sectionMs.ToString("F2")).Append(' ');
            }
            sb.Append("other=").Append(Math.Max(0, ms - attributed).ToString("F2"))
                .Append(" gcDelta0/1/2=").Append(GC.CollectionCount(0) - _gcAtFrameStart[0]).Append('/')
                .Append(GC.CollectionCount(1) - _gcAtFrameStart[1]).Append('/')
                .Append(GC.CollectionCount(2) - _gcAtFrameStart[2]).Append(']');

            if (_worstMs.Count >= WorstKept)
            {
                var min = _worstMs.IndexOf(_worstMs.Min());
                _worstMs.RemoveAt(min);
                _worst.RemoveAt(min);
            }
            _worstMs.Add(ms);
            _worst.Add(sb.ToString());
        }

        private static void Begin(Section s)
        {
            if (Thread.CurrentThread.ManagedThreadId != _gameThreadId) return;
            if (_sectionDepth[(int)s]++ == 0)
            {
                _sectionAllocStart[(int)s] = GC.GetAllocatedBytesForCurrentThread();
                _sectionStart[(int)s] = Stopwatch.GetTimestamp();
            }
        }

        private static void End(Section s)
        {
            if (Thread.CurrentThread.ManagedThreadId != _gameThreadId) return;
            if (_sectionDepth[(int)s] == 0) return;
            if (--_sectionDepth[(int)s] == 0)
            {
                var elapsed = Stopwatch.GetTimestamp() - _sectionStart[(int)s];
                _sectionTicks[(int)s] += elapsed;
                _windowTicks[(int)s] += elapsed;
                _windowCalls[(int)s]++;
                _windowAllocBytes[(int)s] += GC.GetAllocatedBytesForCurrentThread() - _sectionAllocStart[(int)s];
            }
        }

        private static void ToolsPrefix() => Begin(Section.Tools);
        private static void ToolsSuffix() => End(Section.Tools);
        private static void BuildPrefix(MySlimBlock cubeBlock)
        {
            Begin(Section.ProjectorBuild);
            _buildStart = Stopwatch.GetTimestamp();
        }

        private static void BuildSuffix(MySlimBlock cubeBlock)
        {
            End(Section.ProjectorBuild);
            if (Thread.CurrentThread.ManagedThreadId != _gameThreadId || _buildStart == 0) return;
            var ms = (Stopwatch.GetTimestamp() - _buildStart) * 1000.0 / Stopwatch.Frequency;
            _buildStart = 0;
            _buildMs.Add(ms);
            if (ms > 5.0 && _slowBuilds.Count < 20)
                _slowBuilds.Add("#" + (_frameIndex + 1) + " n=" + _buildMs.Count + " " + ms.ToString("F2") + "ms " +
                                cubeBlock?.BlockDefinition?.Id.SubtypeName + "@" + cubeBlock?.Position);
        }
        private static void RefineryPrefix()
        {
            if (Thread.CurrentThread.ManagedThreadId == _gameThreadId && _sectionDepth[(int)Section.Refinery] == 0)
                _refineryTicksThisFrame++;
            Begin(Section.Refinery);
        }
        private static void RefinerySuffix() => End(Section.Refinery);
        private static void PullPrefix() => Begin(Section.ConveyorPull);
        private static void PullSuffix(VRage.MyFixedPoint __result)
        {
            if (Thread.CurrentThread.ManagedThreadId == _gameThreadId && __result == 0) _emptyPulls++;
            End(Section.ConveyorPull);
        }
        private static void UpdateProductionPrefix() => Begin(Section.RefineryUpdateProduction);
        private static void UpdateProductionSuffix() => End(Section.RefineryUpdateProduction);
        private static void RebuildQueuePrefix() => Begin(Section.RefineryRebuildQueue);
        private static void RebuildQueueSuffix() => End(Section.RefineryRebuildQueue);
        private static void SgiRebuildPrefix() => Begin(Section.RefineryRebuildQueue2);
        private static void SgiRebuildSuffix() => End(Section.RefineryRebuildQueue2);
        private static void TransferPrefix() => Begin(Section.InventoryTransfer);
        private static void TransferSuffix() => End(Section.InventoryTransfer);
        private static void QueueInsertPrefix() => Begin(Section.QueueInsert);
        private static void QueueInsertSuffix() => End(Section.QueueInsert);
        private static void QueueClearPrefix() => Begin(Section.QueueClear);
        private static void QueueClearSuffix() => End(Section.QueueClear);
        private static void TransferOrRemovePrefix() => Begin(Section.InvTransferOrRemove);
        private static void TransferOrRemoveSuffix() => End(Section.InvTransferOrRemove);
        private static void AddItemsPrefix() => Begin(Section.InvAddItems);
        private static void AddItemsSuffix() => End(Section.InvAddItems);
        private static void ObCreatePrefix() => Begin(Section.ObCreate);
        private static void ObCreateSuffix() => End(Section.ObCreate);
        private static void FitsPrefix() => Begin(Section.InvFitsBlueprint);
        private static void FitsSuffix() => End(Section.InvFitsBlueprint);
        private static void EntitiesBeforePrefix() => Begin(Section.EntitiesBefore);
        private static void EntitiesBeforeSuffix() => End(Section.EntitiesBefore);
        private static void EntitiesAfterPrefix() => Begin(Section.EntitiesAfter);
        private static void EntitiesAfterSuffix() => End(Section.EntitiesAfter);
        private static void SessionComponentsPrefix() => Begin(Section.SessionComponents);
        private static void SessionComponentsSuffix() => End(Section.SessionComponents);
        private static void SinkPrefix() => Begin(Section.SinkSetRequired);
        private static void SinkSuffix() => End(Section.SinkSetRequired);
        private static void ProcessPrefix() => Begin(Section.RefineryProcess);
        private static void ProcessSuffix() => End(Section.RefineryProcess);
        private static void QueueRemovePrefix() => Begin(Section.QueueRemoveRequest);
        private static void QueueRemoveSuffix() => End(Section.QueueRemoveRequest);
        private static void PushPrefix() => Begin(Section.ConveyorPush);
        private static void PushSuffix() => End(Section.ConveyorPush);
        private static void PhysicsPrefix() => Begin(Section.Physics);
        private static void PhysicsSuffix() => End(Section.Physics);
        public static void HarnessBegin() => Begin(Section.Harness);
        public static void HarnessEnd() => End(Section.Harness);
        public static void FakeClientsBegin() => Begin(Section.FakeClients);
        public static void FakeClientsEnd() => End(Section.FakeClients);
        private static void ReplicationBeforePrefix() => Begin(Section.ReplicationBefore);
        private static void ReplicationBeforeSuffix() => End(Section.ReplicationBefore);
        private static void ReplicationSendPrefix() => Begin(Section.ReplicationSend);
        private static void ReplicationSendSuffix() => End(Section.ReplicationSend);
        private static void NetProcessPrefix() => Begin(Section.NetProcess);
        private static void NetProcessSuffix() => End(Section.NetProcess);
        private static void ReplFilterPrefix() => Begin(Section.ReplFilterStateSync);
        private static void ReplFilterSuffix() => End(Section.ReplFilterStateSync);
        private static void ReplAddPrefix() => Begin(Section.ReplAddForClient);
        private static void ReplAddSuffix() => End(Section.ReplAddForClient);
        private static void ReplRefreshPrefix() => Begin(Section.ReplRefreshReplicable);
        private static void ReplRefreshSuffix() => End(Section.ReplRefreshReplicable);
        private static void ReplAcksPrefix() => Begin(Section.ReplClientAcks);
        private static void ReplAcksSuffix() => End(Section.ReplClientAcks);
        private static void ReplDirtyPrefix() => Begin(Section.ReplApplyDirty);
        private static void ReplDirtySuffix() => End(Section.ReplApplyDirty);
        private static void GridBuilderPrefix() => Begin(Section.GridGetObjectBuilder);
        private static void GridBuilderSuffix() => End(Section.GridGetObjectBuilder);
        private static void InvRefreshPrefix() => Begin(Section.InvRefreshClientData);
        private static void InvRefreshSuffix() => End(Section.InvRefreshClientData);
        private static void ReplStreamPrefix() => Begin(Section.ReplStreamingEntry);
        private static void ReplStreamSuffix() => End(Section.ReplStreamingEntry);
        private static void ReplRemovePrefix() => Begin(Section.ReplRemoveForClient);
        private static void ReplRemoveSuffix() => End(Section.ReplRemoveForClient);
        private static void SgInventoryPrefix(object __instance, VRage.Network.MyClientInfo forClient)
        {
            Begin(Section.SgInventory);
            ClassifyInventoryWrite(__instance, forClient);
        }

        // Who the inventory writes go to: owner type, its parent type, and whether the client is
        // looking at something, owns the entity (its character or what it controls) or neither -
        // with the number of distinct inventory/client pairs, so a few inventories sent all the time
        // can be told from many sent now and then.
        private static readonly Dictionary<(Type, Type, int), long> _inventoryWrites = new Dictionary<(Type, Type, int), long>();
        private static readonly HashSet<(object, ulong)> _inventoryPairs = new HashSet<(object, ulong)>();
        private static readonly string[] InventoryWriteKinds = { "idle", "looking", "own" };

        private static void ClassifyInventoryWrite(object group, VRage.Network.MyClientInfo forClient)
        {
            if (Thread.CurrentThread.ManagedThreadId != _gameThreadId || !(group is VRage.Network.IMyStateGroup stateGroup)) return;
            var owner = stateGroup.Owner;
            var parent = owner?.GetParent();
            var state = forClient.State;
            var kind = 0;
            if (state != null && (owner == state.ControlledReplicable || owner == state.CharacterReplicable ||
                                  parent != null && (parent == state.ControlledReplicable || parent == state.CharacterReplicable)))
                kind = 2;
            else if (state is Sandbox.Engine.Multiplayer.MyClientState clientState && clientState.ContextEntity != null)
                kind = 1;
            var key = (owner?.GetType(), parent?.GetType(), kind);
            _inventoryWrites.TryGetValue(key, out var count);
            _inventoryWrites[key] = count + 1;
            _inventoryPairs.Add((group, forClient.EndpointId.Id.Value));
        }

        private static void AppendInventoryWrites(StringBuilder sb)
        {
            if (_inventoryWrites.Count == 0) return;
            sb.Append(" | inventory writes: ").Append(_inventoryWrites.Values.Sum()).Append(" to ")
                .Append(_inventoryPairs.Count).Append(" inventory/client pairs;");
            foreach (var pair in _inventoryWrites.OrderByDescending(p => p.Value).Take(8))
                sb.Append(' ').Append(pair.Key.Item1?.Name ?? "-").Append('/').Append(pair.Key.Item2?.Name ?? "-")
                    .Append('/').Append(InventoryWriteKinds[pair.Key.Item3]).Append('=').Append(pair.Value);
            _inventoryWrites.Clear();
            _inventoryPairs.Clear();
        }
        private static void SgInventorySuffix() => End(Section.SgInventory);
        private static void ReplDirtyIndexPrefix() => Begin(Section.ReplDirtyIndex);
        private static void ReplDirtyIndexSuffix() => End(Section.ReplDirtyIndex);
        private static void SgPropertyPrefix() => Begin(Section.SgProperty);
        private static void SgPropertySuffix() => End(Section.SgProperty);
        private static void SgPhysicsPrefix() => Begin(Section.SgPhysics);
        private static void SgPhysicsSuffix() => End(Section.SgPhysics);
        private static void SgCreatePrefix() => Begin(Section.SgCreateClientData);
        private static void SgCreateSuffix() => End(Section.SgCreateClientData);
        private static void ReplGridSerializePrefix() => Begin(Section.ReplGridSerialize);
        private static void ReplGridSerializeSuffix() => End(Section.ReplGridSerialize);
        private static void DrillUpdate10Prefix() => Begin(Section.DrillUpdate10);
        private static void DrillUpdate10Suffix() => End(Section.DrillUpdate10);
        private static void DrillAfterSimPrefix() => Begin(Section.DrillAfterSim);
        private static void DrillAfterSimSuffix() => End(Section.DrillAfterSim);
        private static void DrillUpdate100Prefix() => Begin(Section.DrillUpdate100);
        private static void DrillUpdate100Suffix() => End(Section.DrillUpdate100);
        private static void MiningSchedulePrefix() => Begin(Section.MiningSchedule);
        private static void MiningScheduleSuffix() => End(Section.MiningSchedule);
        private static void DrillCutFinishPrefix() => Begin(Section.DrillCutFinish);
        private static void DrillCutFinishSuffix() => End(Section.DrillCutFinish);
        private static void DrillResultsPrefix() => Begin(Section.DrillResults);
        private static void DrillResultsSuffix() => End(Section.DrillResults);
        private static void VoxelNotifyPrefix() => Begin(Section.VoxelNotify);
        private static void VoxelNotifySuffix() => End(Section.VoxelNotify);
        private static void VoxelSavePrefix() => Begin(Section.VoxelSave);
        private static void VoxelSaveSuffix() => End(Section.VoxelSave);
        private static void VoxelStreamSerializePrefix() => Begin(Section.VoxelStreamSerialize);
        private static void VoxelStreamSerializeSuffix() => End(Section.VoxelStreamSerialize);
        private static void WheelSystemPrefix() => Begin(Section.WheelSystem);
        private static void WheelSystemSuffix() => End(Section.WheelSystem);
        private static void SuspensionUpdatePrefix() => Begin(Section.SuspensionUpdate);
        private static void SuspensionUpdateSuffix() => End(Section.SuspensionUpdate);
        private static void MechUpdateBeforePrefix() => Begin(Section.MechUpdateBefore);
        private static void MechUpdateBeforeSuffix() => End(Section.MechUpdateBefore);
        private static void WheelUpdateBeforePrefix() => Begin(Section.WheelUpdateBefore);
        private static void WheelUpdateBeforeSuffix() => End(Section.WheelUpdateBefore);
        private static void WheelUpdateAfterPrefix() => Begin(Section.WheelUpdateAfter);
        private static void WheelUpdateAfterSuffix() => End(Section.WheelUpdateAfter);
        private static void WheelContactPrefix() => Begin(Section.WheelContact);
        private static void WheelContactSuffix() => End(Section.WheelContact);
        private static void GrindActivatePrefix() => Begin(Section.GrindActivate);
        private static void GrindActivateSuffix() => End(Section.GrindActivate);
        private static void GrindSpherePrefix() => Begin(Section.GrindSphere);
        private static void GrindSphereSuffix() => End(Section.GrindSphere);
        private static void GrindDecreasePrefix() => Begin(Section.GrindDecrease);
        private static void GrindDecreaseSuffix() => End(Section.GrindDecrease);
        private static void GrindMoveItemsPrefix() => Begin(Section.GrindMoveItems);
        private static void GrindMoveItemsSuffix() => End(Section.GrindMoveItems);
        private static void GridRazePrefix() => Begin(Section.GridRaze);
        private static void GridRazeSuffix() => End(Section.GridRaze);
        private static long _visualStart;
        private static int _slowVisualLogged;

        private static void GridUpdateVisualPrefix()
        {
            Begin(Section.GridUpdateVisual);
            _visualStart = Stopwatch.GetTimestamp();
        }

        /// <summary>A slow block visual update, logged with the block (the first 40 per session).</summary>
        private static void GridUpdateVisualSuffix(Sandbox.Game.Entities.Cube.MySlimBlock __instance)
        {
            End(Section.GridUpdateVisual);
            if (Thread.CurrentThread.ManagedThreadId != _gameThreadId || _slowVisualLogged >= 40) return;
            var ms = (Stopwatch.GetTimestamp() - _visualStart) * 1000.0 / Stopwatch.Frequency;
            if (ms < 5) return;
            _slowVisualLogged++;
            SentisTestsPlugin.Log.Info("FrameProbe: slow UpdateVisual " + ms.ToString("F1") + " ms: " + __instance.BlockDefinition.Id.SubtypeName +
                                       ", build ratio " + __instance.BuildLevelRatio.ToString("F2") + ", fat " + (__instance.FatBlock?.GetType().Name ?? "none") +
                                       ", construction model " + (__instance.FatBlock?.Model?.AssetName ?? "-"));
        }
        private static void GridSendStockpilePrefix() => Begin(Section.GridSendStockpile);
        private static void GridSendStockpileSuffix() => End(Section.GridSendStockpile);
        private static void GridSendIntegrityPrefix() => Begin(Section.GridSendIntegrity);
        private static void GridSendIntegritySuffix() => End(Section.GridSendIntegrity);
        private static void GridDisconnectsPrefix() => Begin(Section.GridDisconnects);
        private static void GridDisconnectsSuffix() => End(Section.GridDisconnects);
        private static void GridInventoryMassPrefix() => Begin(Section.GridInventoryMass);
        private static void GridInventoryMassSuffix() => End(Section.GridInventoryMass);
        private static void GrindAnimationPrefix() => Begin(Section.GrindAnimation);
        private static void GrindAnimationSuffix() => End(Section.GrindAnimation);
        private static void GrindEmptyInvPrefix() => Begin(Section.GrindEmptyInv);
        private static void GrindEmptyInvSuffix() => End(Section.GrindEmptyInv);
        private static void GrindSpawnStockpilePrefix() => Begin(Section.GrindSpawnStockpile);
        private static void GrindSpawnStockpileSuffix() => End(Section.GrindSpawnStockpile);
        private static void DamageBeforePrefix() => Begin(Section.DamageBefore);
        private static void DamageBeforeSuffix() => End(Section.DamageBefore);
        private static void DamageAfterPrefix() => Begin(Section.DamageAfter);
        private static void DamageAfterSuffix() => End(Section.DamageAfter);
        private static void DamageDestroyedPrefix() => Begin(Section.DamageDestroyed);
        private static void DamageDestroyedSuffix() => End(Section.DamageDestroyed);
        private static void FactionReputationPrefix() => Begin(Section.FactionReputation);
        private static void FactionReputationSuffix() => End(Section.FactionReputation);
        private static void ThrustUpdatePrefix() => Begin(Section.ThrustUpdate);
        private static void ThrustUpdateSuffix() => End(Section.ThrustUpdate);
        private static void ThrustRecomputePrefix() => Begin(Section.ThrustRecompute);
        private static void ThrustRecomputeSuffix() => End(Section.ThrustRecompute);
        private static void ThrustThrustsPrefix() => Begin(Section.ThrustThrusts);
        private static void ThrustThrustsSuffix() => End(Section.ThrustThrusts);
        private static void ThrustBlock100Prefix() => Begin(Section.ThrustBlock100);
        private static void ThrustBlock100Suffix() => End(Section.ThrustBlock100);
        private static void PowerDistributorPrefix() => Begin(Section.PowerDistributor);
        private static void PowerDistributorSuffix() => End(Section.PowerDistributor);
        private static void GasGenerator100Prefix() => Begin(Section.GasGenerator100);
        private static void GasGenerator100Suffix() => End(Section.GasGenerator100);
        private static void GasTank100Prefix() => Begin(Section.GasTank100);
        private static void GasTank100Suffix() => End(Section.GasTank100);
        private static void ConveyorPullRequestPrefix() => Begin(Section.ConveyorPullRequest);
        private static void ConveyorPullRequestSuffix() => End(Section.ConveyorPullRequest);
        private static void GasGeneratorPrefix() => Begin(Section.GasGenerator);
        private static void GasGeneratorSuffix() => End(Section.GasGenerator);
        private static void GasTankPrefix() => Begin(Section.GasTank);
        private static void GasTankSuffix() => End(Section.GasTank);
        private static void GasSetCapacitiesPrefix() => Begin(Section.GasSetCapacities);
        private static void GasSetCapacitiesSuffix() => End(Section.GasSetCapacities);
        private static void GasCheckProducingPrefix() => Begin(Section.GasCheckProducing);
        private static void GasCheckProducingSuffix() => End(Section.GasCheckProducing);
        public static void BridgeBegin() => Begin(Section.Bridge);
        public static void BridgeEnd() => End(Section.Bridge);

        private static Type _gcSchedulerType;

        /// <summary>Scheduled gen0 collections run by SentisOptimisations (measured there, reset here).</summary>
        private static void AppendScheduledGc(StringBuilder sb)
        {
            if (_gcSchedulerType == null)
                _gcSchedulerType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Optimizer.Optimizations.GcScheduler", false))
                    .FirstOrDefault(type => type != null);
            if (_gcSchedulerType == null) return;
            Func<string, object> get = name => _gcSchedulerType.GetField(name)?.GetValue(null);
            var count = Convert.ToInt64(get("Collections") ?? 0L);
            var total = Convert.ToDouble(get("TotalPauseMs") ?? 0.0);
            sb.AppendFormat("| scheduled gen0={0} pause avg={1:F2}ms max={2:F2}ms, frames over 16.7 with pause={3} ",
                count, count > 0 ? total / count : 0, Convert.ToDouble(get("MaxPauseMs") ?? 0.0),
                Convert.ToInt64(get("FramesOverBudgetWithPause") ?? 0L));
            foreach (var name in new[] { "Collections", "FramesOverBudgetWithPause" })
                _gcSchedulerType.GetField(name)?.SetValue(null, 0L);
            foreach (var name in new[] { "TotalPauseMs", "MaxPauseMs" })
                _gcSchedulerType.GetField(name)?.SetValue(null, 0.0);
        }

        /// <summary>
        /// Compares frames over the budget with ordinary ones section by section: what the heavy
        /// frames spend their extra time on, separately for frames with and without a GC collection.
        /// </summary>
        private static void AppendOverBudgetBreakdown(StringBuilder sb, double[] frameMs, int[] frameGc,
            long[][] frameSections, long[] frameAlloc)
        {
            var n = frameMs.Length;
            if (n == 0 || frameSections.Length != n) return;
            var heavyNoGc = new List<int>();
            var heavyGc = new List<int>();
            var normal = new List<int>();
            for (var f = 0; f < n; f++)
            {
                if (frameMs[f] <= BudgetMs) { normal.Add(f); continue; }
                if (frameGc[f] == 0) heavyNoGc.Add(f); else heavyGc.Add(f);
            }
            if (heavyNoGc.Count + heavyGc.Count == 0) return;
            // Sections are timed inside each other in places; report every section that differs,
            // but compute "other" from the non-nested ones only, like the worst-frame lines.
            Func<List<int>, double[]> avgPerFrame = idx =>
            {
                var acc = new double[(int)Section.Count];
                foreach (var f in idx)
                    for (var i = 0; i < (int)Section.Count; i++)
                        acc[i] += frameSections[f][i];
                for (var i = 0; i < (int)Section.Count; i++)
                    acc[i] = acc[i] * 1000.0 / Stopwatch.Frequency / Math.Max(1, idx.Count);
                return acc;
            };
            var normalAvg = avgPerFrame(normal);
            sb.Append(" | over-budget breakdown:");
            void Bucket(string label, List<int> idx)
            {
                if (idx.Count == 0) return;
                var heavyAvg = avgPerFrame(idx);
                double heavyMs = 0, normalMs = 0, alloc = 0;
                foreach (var f in idx) { heavyMs += frameMs[f]; alloc += frameAlloc[f]; }
                foreach (var f in normal) normalMs += frameMs[f];
                heavyMs /= idx.Count; normalMs /= Math.Max(1, normal.Count);
                sb.Append(' ').Append(label).Append("(n=").Append(idx.Count)
                    .Append(", avg ").Append(heavyMs.ToString("F1")).Append("ms vs ").Append(normalMs.ToString("F1"))
                    .Append("ms, alloc ").Append(alloc / 1024.0 / idx.Count).Append("KB/frame):");
                var deltas = new List<(int idx, double delta)>();
                for (var i = 0; i < (int)Section.Count; i++)
                {
                    var delta = heavyAvg[i] - normalAvg[i];
                    if (Math.Abs(delta) < 0.05) continue;
                    if (heavyAvg[i] < 0.02 && normalAvg[i] < 0.02) continue;
                    deltas.Add((i, delta));
                }
                foreach (var d in deltas.OrderByDescending(x => Math.Abs(x.delta)).Take(8))
                    sb.Append(' ').Append(SectionNames[d.idx]).Append(d.delta >= 0 ? '+' : '−')
                        .Append(Math.Abs(d.delta).ToString("F2"));
                double Other(double[] a)
                {
                    var attributed = 0.0;
                    for (var i = 0; i < (int)Section.Count; i++) if (!Nested[i]) attributed += a[i];
                    return attributed;
                }
                sb.Append(" other+").Append((Other(heavyAvg) - Other(normalAvg)).ToString("F2"));
            }
            Bucket("noGc", heavyNoGc);
            Bucket("withGc", heavyGc);
        }

        /// <summary>Formats statistics for frames since the last call and clears the window.</summary>
        public static string Take()
        {
            var frames = _busyMs.ToArray();
            var worst = _worst.Zip(_worstMs, (text, ms) => new { text, ms })
                .OrderByDescending(x => x.ms).Select(x => x.text).ToList();
            var frameMs = _frameMsList.ToArray();
            var frameGc = _frameGcBucket.ToArray();
            var frameSections = _frameSections.ToArray();
            var frameAlloc = _frameAlloc.ToArray();
            _frameMsList.Clear();
            _frameGcBucket.Clear();
            _frameSections.Clear();
            _frameAlloc.Clear();
            var refineryTicks = _refineryTicksPerFrame.ToArray();
            _refineryTicksPerFrame.Clear();
            var builds = _buildMs.ToArray();
            var slowBuilds = string.Join("; ", _slowBuilds);
            _buildMs.Clear();
            _slowBuilds.Clear();
            _busyMs.Clear();
            _worst.Clear();
            _worstMs.Clear();
            if (!_installed) return "sim-work: probe not installed";
            if (frames.Length == 0) return "sim-work: no frames";

            Array.Sort(frames);
            Func<double, double> pct = p => frames[(int)Math.Min(frames.Length - 1, Math.Floor(p * (frames.Length - 1)))];
            var over = frames.Count(f => f > BudgetMs);
            var over33 = frames.Count(f => f > 2 * BudgetMs);
            var sb = new StringBuilder();
            sb.AppendFormat("sim-work frames={0} avg={1:F2}ms p50={2:F2} p95={3:F2} p99={4:F2} p99.9={5:F2} max={6:F2}ms over16.7/33.3={7}/{8}",
                frames.Length, frames.Average(), pct(0.5), pct(0.95), pct(0.99), pct(0.999), frames[frames.Length - 1],
                over, over33);
            var tickFrames = refineryTicks.Count(n => n > 0);
            if (tickFrames > 0)
            {
                var sortedTicks = refineryTicks.Where(n => n > 0).OrderBy(n => n).ToArray();
                sb.AppendFormat(" | refinery ticks={0} in {1}/{2} frames, per busy frame p50={3} max={4}",
                    refineryTicks.Sum(), tickFrames, refineryTicks.Length, sortedTicks[sortedTicks.Length / 2],
                    sortedTicks[sortedTicks.Length - 1]);
            }
            if (builds.Length > 0)
            {
                Array.Sort(builds);
                sb.AppendFormat(" | builds={0} avg={1:F3}ms p99={2:F3} max={3:F2}ms >5ms: {4}", builds.Length,
                    builds.Average(), builds[(int)Math.Floor(0.99 * (builds.Length - 1))], builds[builds.Length - 1],
                    slowBuilds.Length == 0 ? "none" : slowBuilds);
            }
            sb.Append(" | totals:");
            for (var i = 0; i < (int)Section.Count; i++)
            {
                if (_windowCalls[i] == 0) continue;
                var totalMs = _windowTicks[i] * 1000.0 / Stopwatch.Frequency;
                sb.AppendFormat(" {0}={1:F0}ms/{2}calls({3:F3}ms each, {4:F2}ms/frame, {5:F0}KB alloc)", SectionNames[i],
                    totalMs, _windowCalls[i], totalMs / _windowCalls[i], totalMs / frames.Length,
                    _windowAllocBytes[i] / 1024.0);
            }
            if (_windowCalls[(int)Section.ConveyorPull] > 0) sb.Append(" emptyPulls=").Append(_emptyPulls);
            sb.AppendFormat(" | GC mode: server={0} latency={1}; frames by GC: ", System.Runtime.GCSettings.IsServerGC,
                System.Runtime.GCSettings.LatencyMode);
            string[] gcNames = { "none", "gen0", "gen1", "gen2" };
            for (var g = 0; g < 4; g++)
                if (_gcFrames[g] > 0)
                    sb.AppendFormat("{0}={1} (avg {2:F2}ms) ", gcNames[g], _gcFrames[g], _gcFrameMs[g] / _gcFrames[g]);
            AppendScheduledGc(sb);
            AppendInventoryWrites(sb);
            Array.Clear(_gcFrames, 0, _gcFrames.Length);
            Array.Clear(_gcFrameMs, 0, _gcFrameMs.Length);
            Array.Clear(_windowTicks, 0, _windowTicks.Length);
            Array.Clear(_windowCalls, 0, _windowCalls.Length);
            sb.AppendFormat(" gameThreadAlloc={0:F0}MB", _windowFrameAllocBytes / 1048576.0);
            Array.Clear(_windowAllocBytes, 0, _windowAllocBytes.Length);
            _windowFrameAllocBytes = 0;
            _emptyPulls = 0;
            AppendOverBudgetBreakdown(sb, frameMs, frameGc, frameSections, frameAlloc);
            if (worst.Count > 0)
                sb.Append(" | worst: ").Append(string.Join("; ", worst));
            return sb.ToString();
        }
    }
}
