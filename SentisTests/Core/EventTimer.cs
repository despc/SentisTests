using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Torch.Managers.PatchManager;

namespace SentisTests.Core
{
    /// <summary>
    /// What a slow game event is made of: a few "root" methods (a block placed, a player respawned) and the methods
    /// they call, each timed; when a root takes longer than <see cref="SlowMs"/> the tree of its calls goes to the log,
    /// "BuildBlocksRequest 22.1 ms [BuildBlocksSuccess 18.0 [...], ...]". Game thread only; the stand only.
    /// </summary>
    public static class EventTimer
    {
        public const double SlowMs = 5;
        private const int Slots = 192;

        /// <summary>Type, method, and whether it is a root (an event that is logged when slow).</summary>
        private static readonly (string Type, string Method, bool Root)[] Targets =
        {
            ("Sandbox.Game.Entities.MyCubeGrid", "BuildBlocksRequest", true),
            ("Sandbox.Game.Entities.MyCubeGrid", "IsWithinWorldLimits", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "BuildBlocksSuccess", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "AfterBuildBlocksSuccess", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "BuildBlockSuccess", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "BuildBlock", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "AddBlock", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "CanPlaceBlock", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "UpdateBlockNeighbours", false),
            ("Sandbox.Game.Entities.Cube.MySlimBlock", "Init", false),
            ("Sandbox.Game.Entities.Cube.MyGridPhysics", "AddBlock", false),
            ("Sandbox.Game.Entities.Cube.MyGridShape", "UpdateShape", false),
            ("Sandbox.Game.Entities.MyEntities", "RaiseEntityCreated", false),
            ("Sandbox.Game.Entities.MyEntities", "Add", false),
            ("SentisGameplayImprovements.PcuLimiter", "GroupPcu", false),
            ("SpaceEngineers.Game.World.MySpaceRespawnComponent", "HandleRespawnRequest", true),
            ("SpaceEngineers.Game.World.MySpaceRespawnComponent", "SpawnInRespawn", false),
            ("SpaceEngineers.Game.World.MySpaceRespawnComponent", "SpawnAtShip", false),
            ("SpaceEngineers.Game.World.MySpaceRespawnComponent", "SpawnInCockpit", false),
            ("SpaceEngineers.Game.World.MySpaceRespawnComponent", "PutPlayerInRespawnGrid", false),
            ("SpaceEngineers.Game.World.MySpaceRespawnComponent", "SpawnInSuit", false),
            ("SpaceEngineers.Game.World.MySpaceRespawnComponent", "FindRespawnById", false),
            ("SpaceEngineers.Game.World.MySpaceRespawnComponent", "GetAvailableRespawnPoints", false),
            ("SpaceEngineers.Game.World.MySpaceRespawnComponent", "GetSpawnPositionNearPlanet", false),
            ("SpaceEngineers.Game.World.MySpaceRespawnComponent", "FindPositionAbovePlanet", false),
            ("Sandbox.Game.Entities.Character.MyCharacter", "CreateCharacter", false),
            ("Sandbox.Game.Entities.Character.MyCharacter", "Init", false),
            ("Sandbox.Game.World.MyPlayer", "SpawnAt", false),
            ("Sandbox.Game.World.MyPlayer", "SpawnIntoCharacter", false),
            ("Sandbox.Game.World.MyPrefabManager", "SpawnPrefabInternal", false),
            ("Sandbox.Game.Entities.MyEntities", "CreateFromObjectBuilderAndAdd", false),
            ("Sandbox.Game.Entities.Character.MyCharacter", "Die", false),
            ("Sandbox.Game.Entities.Character.MyCharacter", "OnSuicideRequest", true),
            ("SpaceEngineers.Game.Entities.MySpaceBuildComponent", "GetBlocksPlacementMaterials", false),
            ("SpaceEngineers.Game.Entities.MySpaceBuildComponent", "HasBuildingMaterials", false),
            ("SpaceEngineers.Game.Entities.MySpaceBuildComponent", "AfterSuccessfulBuild", false),
            ("Sandbox.Game.SessionComponents.MySessionComponentGameInventory", "ValidateItem", false),
            ("SentisGameplayImprovements.BuildBlockPatch", "BuildBlocksRequest", false),
            ("Sandbox.Game.Entities.MyEntities", "FindFreePlace", false),
            ("Sandbox.Game.Multiplayer.MyPlayerCollection", "SetPlayerCharacter", false),
            ("Sandbox.Game.Multiplayer.MyPlayerCollection", "RevivePlayer", false),
            ("Sandbox.Game.Multiplayer.MyPlayerCollection", "SetControlledEntity", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "CanPlaceWithConnectivity", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "CheckConnectivity", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "ChangeBlockOwner", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "RecalculateOwners", false),
            ("Sandbox.Game.Entities.MyCubeBlock", "ChangeOwner", false),
            ("Sandbox.Game.Entities.MyCubeBlock", "OnOwnershipChanged", false),
            ("Sandbox.Game.Entities.Cube.MyCubeGridOwnershipManager", "RecalculateOwnersThreadSafe", false),
            ("VRage.Network.MyReplicationServer", "DispatchEvent", false),
            ("Sandbox.Game.World.MySession", "SendVicinityInformation", false),
            ("Sandbox.Game.World.MyPrefabManager", "SpawnPrefab", false),
            ("Sandbox.Game.Multiplayer.MyGpsCollection", "SendAddGpsRequest", false),
            ("SpaceEngineers.Game.GUI.MyGuiScreenMedicals", "RefreshRespawnPointsRequest", true),
            ("VRage.Game.Components.MyRespawnComponent", "CanPlayerSpawn", false),
            ("Sandbox.Game.World.MyPlayer", "CanSpawnAt", false),
            ("Sandbox.Game.Entities.MyEntities", "DeleteRememberedEntities", true),
            ("VRage.Game.Entity.MyEntity", "Delete", false),
            ("VRage.Game.Entity.MyEntity", "BeforeDelete", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "BeforeDelete", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "OnRemovedFromScene", false),
            ("VRage.Game.Entity.MyEntity", "OnRemovedFromScene", false),
            ("Sandbox.Game.Entities.MyCubeBlock", "OnRemovedFromScene", false),
            ("Sandbox.Game.Entities.MyCubeBlock", "Closing", false),
            ("Sandbox.Engine.Physics.MyPhysicsBody", "Close", false),
            ("Sandbox.Game.Entities.Cube.MyGridPhysics", "Close", false),
            ("Sandbox.Engine.Physics.MyPhysicsBody", "Deactivate", false),
            ("Sandbox.Game.Entities.MyEntities", "Remove", false),
            ("Sandbox.Game.Entities.MyGamePruningStructure", "Remove", false),
            ("VRage.Network.MyReplicationServer", "Destroy", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "UnregisterBlocks", false),
            ("Sandbox.Game.Entities.Cube.MyCubeGridRenderData", "OnRemovedFromRender", false),
            ("Sandbox.Game.Entities.MyCubeGridGroups", "RemoveNode", false),
            ("Sandbox.Game.World.MyPrefabManager+CreateGridsData", "OnGridsCreated", true),
            ("Sandbox.Game.World.MyPrefabManager", "SpawnPrefabInternalSetProperties", false),
            ("VRage.Game.Entity.MyEntity", "OnAddedToScene", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "OnAddedToScene", false),
            ("Sandbox.Game.Entities.Cube.MyGridPhysics", "Activate", false),
            ("Sandbox.Engine.Physics.MyPhysicsBody", "Activate", false),
            ("Sandbox.Game.Entities.MyEntities", "UpdateOnceBeforeFrame", false),
            ("Sandbox.Game.Entities.MyCockpit", "AttachPilot", false),
            ("Sandbox.Game.World.Generator.MyProceduralWorldModule", "GetObjectSeeds", false),
            ("Sandbox.Game.World.Generator.MyStationCellGenerator", "GenerateProceduralCell", false),
            ("Sandbox.Game.World.Generator.MyProceduralWorldModule", "OverlapAllBoundingSphere", false),
            ("Sandbox.Game.SessionComponents.MySessionComponentEconomy", "TryFillStoryDatapad", false),
            ("Sandbox.Game.Entities.Character.MyCharacter", "SetPlayer", false),
            ("Sandbox.Game.MyInventory", "AddItems", false),
            ("Sandbox.Game.Entities.MyCockpit", "OnControlAcquired", false),
            ("Sandbox.Game.Entities.MyShipController", "OnControlAcquired", false),
            ("Sandbox.Engine.Physics.MyPhysics", "Simulate", true),
            ("Sandbox.Engine.Physics.MyPhysics", "ExecuteParallelRayCasts", false),
            ("Sandbox.Engine.Physics.MyPhysics", "StepVDB", false),
            ("Sandbox.Engine.Physics.MyPhysics", "StepWorlds", false),
            ("Sandbox.Engine.Physics.MyPhysics", "UpdateActiveRigidBodies", false),
            ("Sandbox.Engine.Physics.MyPhysics", "UpdateCharacters", false),
            ("Sandbox.Engine.Physics.MyPhysics", "EnsureClusterSpace", false),
            ("Sandbox.Engine.Physics.MyPhysics", "ProcessCollisionFilterRefreshes", false),
            ("Sandbox.Engine.Voxels.MyVoxelPhysicsBody", "RequestShapeBlocking", false),
            ("Sandbox.Engine.Voxels.MyVoxelPhysicsBody", "RequestShapeBatchBlockingInternal", false),
            ("Sandbox.Engine.Voxels.MyVoxelPhysicsBody", "OnBatchTaskComplete", false),
            ("Sandbox.Engine.Voxels.MyVoxelPhysicsBody", "CreateRigidBodies", false),
            ("Sandbox.Engine.Voxels.MyVoxelPhysicsBody", "UpdateRigidBodyShape", false),
            ("VRage.Game.Entity.MyEntity", "Close", false),
            ("VRage.Game.Entity.MyEntity", "CallAndClearOnClosing", false),
            ("VRage.Game.Entity.MyEntity", "CallAndClearOnClose", false),
            ("VRage.Game.Components.MyHierarchyComponent`1", "Delete", false),
            ("Sandbox.Game.Entities.MyEntities", "RaiseEntityRemove", false),
            ("Sandbox.Game.Entities.MyEntities", "UnregisterForUpdate", false),
            ("Sandbox.Game.Entities.MyEntities", "UnregisterForDraw", false),
            ("Sandbox.Game.Entities.MyEntities", "RemoveName", false),
            ("VRage.Game.Entity.MyEntityIdentifier", "RemoveEntity", false),
            ("Sandbox.Game.Components.MyRenderComponent", "RemoveRenderObjects", false),
            ("VRage.Game.Components.MyRenderComponentBase", "RemoveRenderObjects", false),
            ("Sandbox.Game.Entities.MyCubeBlock", "OnClose", false),
            ("Sandbox.Game.Entities.MyCubeBlock", "BeforeDelete", false),
            ("Sandbox.Game.Entities.MyFunctionalBlock", "OnClose", false),
            ("Sandbox.Game.Entities.MyEntities", "RemoveFromClosedEntities", false),
            ("Sandbox.Game.AI.MyAIComponent", "PlayerCreated", true),
            ("SpaceEngineers.Game.AI.MySpaceFaunaComponent", "SpawnBot", true),
            ("Sandbox.Game.AI.MyAIComponent", "CreateBot", false),
            ("Sandbox.Game.AI.MyAIComponent", "SpawnNewBot", false),
            ("Sandbox.Game.AI.MyBotFactoryBase", "CreateBot", false),
            ("Sandbox.Game.AI.MyAgentBot", "Spawn", false),
            ("Sandbox.Game.AI.MyBotCollection", "AddBot", false),
            ("Sandbox.Game.Multiplayer.MyPlayerCollection", "OnRespawnRequest", false),
            ("Sandbox.Game.AI.BehaviorTree.MyBehaviorTreeCollection", "AssignBotToBehaviorTree", false),
            ("Sandbox.Game.Multiplayer.MyPlayerCollection", "CreateNewPlayer", false),
            ("Sandbox.Game.Multiplayer.MyPlayerCollection", "CreateNewIdentity", false),
            ("Sandbox.Game.Multiplayer.MyPlayerCollection", "RaisePlayerCreated", false),
            ("Sandbox.Game.AI.MyBotFactoryBase", "CreateActions", false),
            ("Sandbox.Game.Multiplayer.MyFactionCollection", "SendJoinRequest", false),
            ("Sandbox.Game.Multiplayer.MyFactionCollection", "TryGetOrCreateFactionByTag", false),
            ("Sandbox.Game.Multiplayer.MyFactionCollection", "FactionStateChangeRequest", false),
            ("Sandbox.Game.Multiplayer.MyFactionCollection", "FactionStateChangeSuccess", false),
            ("Sandbox.Game.Multiplayer.MyFactionCollection", "ApplyFactionStateChange", false),
            ("Sandbox.Game.Multiplayer.MyFactionCollection", "AddPlayerToFaction", false),
            ("Sandbox.Game.Multiplayer.MyFactionCollection", "AddPlayerToFactionInternal", false),
            ("Sandbox.Game.Multiplayer.MyFactionCollection", "SetReputationBetweenPlayerAndFaction", false),
            ("Sandbox.Game.World.MyFaction", "AcceptJoin", false),
            ("Sandbox.Game.World.MyFaction", "AddJoinRequest", false),
            ("Sandbox.Game.AI.BehaviorTree.MyBehaviorTreeCollection", "SetBehaviorName", false),
            ("Sandbox.Game.AI.MyBotFactoryBase", "CreateLogic", false),
            ("Sandbox.Game.AI.MyAgentBot", ".ctor", false),
            ("Sandbox.Game.AI.MyAgentBot", "Init", false),
            ("SpaceEngineers.Game.AI.MyWolfActions", ".ctor", false),
            ("SpaceEngineers.Game.AI.MyWolfLogic", ".ctor", false),
            ("SpaceEngineers.Game.AI.MyWolfTarget", ".ctor", false),
            ("Sandbox.Game.AI.Navigation.MyBotNavigation", ".ctor", false),
            ("Sandbox.Game.AI.MyAnimalBot", ".ctor", false),
            ("SpaceEngineers.Game.AI.MyAnimalBot", ".ctor", false),
            ("VRage.Game.Models.MyModels", "GetModelOnlyData", false),
            ("VRage.Game.Models.MyModels", "GetModelOnlyDummies", false),
            ("VRage.Game.Models.MyModels", "GetModelOnlyModelInfo", false),
            ("VRage.Game.Models.MyModels", "GetModel", false),
            ("VRage.Game.Models.MyModel", "LoadData", false),
            ("VRage.Game.Models.MyModel", "LoadOnlyDummies", false),
            ("VRage.Game.Models.MyModel", "LoadOnlyModelInfo", false),
            ("Sandbox.Game.Entities.MyCubeBlock", "Init", false),
            ("Sandbox.Game.Entities.MyCubeBlockFactory", "CreateCubeBlock", false),
            ("Sandbox.Game.Entities.Cube.MyCubeBlockFactory", "CreateCubeBlock", false),
            ("Sandbox.Game.Entities.MyCubeGrid", "CreateFatBlock", false),
            ("Sandbox.MySandboxGame", "Update", true),
            ("VRage.Platform.Windows.MyVRagePlatform", "Update", false),
            ("VRage.Platform.Windows.Sys.MyWindowsSystem", "get_RemainingMemoryForGame", false),
            ("Sandbox.MySandboxGame", "ProcessInvoke", false),
            ("Sandbox.Engine.MyGeneralStats", "Update", false),
            ("Sandbox.Game.World.MySession", "Update", false),
            ("Sandbox.Engine.Networking.MyGameService", "Update", false),
            ("Sandbox.Engine.Multiplayer.MyTransportLayer", "Tick", false),
            ("Sandbox.Engine.Networking.MyNetworkReader", "Process", false),
            ("Sandbox.Engine.Networking.MyNetworkMonitor", "Update", false),
            ("Sandbox.Graphics.GUI.MyGuiSandbox", "Update", false),
            ("Sandbox.Graphics.GUI.MyGuiSandbox", "HandleInput", false),
            ("Sandbox.MySandboxGame", "ProcessRenderOutput", false),
            ("Sandbox.Engine.Multiplayer.MyMultiplayerBase", "ReportReplicatedObjects", false),
            ("VRage.Game.Components.MySessionComponentBase", "UpdateAfterSimulation", false),
        };

        private static readonly string[] Names = new string[Slots];
        private static readonly bool[] IsRoot = new bool[Slots];

        // the calls open now, and the finished ones of the root under way: (slot, depth, ticks)
        private static int _gc0, _gc1, _gc2;
        private static readonly List<(int Slot, long Start)> Open = new List<(int, long)>();
        private static readonly List<(int Slot, int Depth, long Ticks)> Done = new List<(int, int, long)>();

        private static int _used;
        private static PatchManager _patchManager;

        /// <summary>The handlers an event has now, timed as calls of whatever raises it (after the world is loaded).</summary>
        public static void TimeHandlers(Delegate handlers)
        {
            if (_patchManager == null || handlers == null || !Installed) return;
            var ctx = _patchManager.AcquireContext();
            var names = new List<string>();
            foreach (var handler in handlers.GetInvocationList())
            {
                var target = handler.Method;
                if (_used >= Slots || target.IsAbstract || target.GetMethodBody() == null) continue;
                Names[_used] = target.DeclaringType?.Name + "." + target.Name;
                var slot = SlotType(_used);
                var pattern = ctx.GetPattern(target);
                pattern.Prefixes.Add(slot.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic));
                pattern.Suffixes.Add(slot.GetMethod("Suffix", BindingFlags.Static | BindingFlags.NonPublic));
                names.Add(Names[_used]);
                _used++;
            }
            _patchManager.Commit();
            SentisTestsPlugin.Log.Info("EventTimer: handlers timed: " + string.Join(", ", names));
        }

        /// <summary>The patches are in (<see cref="Install"/> ran).</summary>
        public static bool Installed { get; private set; }

        public static void Install(PatchManager patchManager)
        {
            if (patchManager == null || Installed) return;
            Installed = true;
            _patchManager = patchManager;
            var ctx = patchManager.AcquireContext();
            var used = 0;
            var missing = new List<string>();
            foreach (var (typeName, method, root) in Targets)
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName, false)).FirstOrDefault(t => t != null);
                if (type == null) { missing.Add(typeName); continue; }
                var found = method == ".ctor"
                    ? type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Cast<MethodBase>().ToList()
                    : type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(m => m.Name == method && !m.IsAbstract && !m.IsGenericMethodDefinition).Cast<MethodBase>().ToList();
                if (found.Count == 0) { missing.Add(type.Name + "." + method); continue; }
                foreach (var target in found)
                {
                    if (used >= Slots) { missing.Add(type.Name + "." + method + " (no slot)"); continue; }
                    Names[used] = type.Name + "." + method;
                    IsRoot[used] = root;
                    var slot = SlotType(used);
                    var pattern = ctx.GetPattern(target);
                    pattern.Transpilers.Add(LeaveTargets.KeepLeavesMethod);
                    pattern.Prefixes.Add(slot.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic));
                    pattern.Suffixes.Add(slot.GetMethod("Suffix", BindingFlags.Static | BindingFlags.NonPublic));
                    used++;
                }
            }
            patchManager.Commit();
            _used = used;
            SentisTestsPlugin.Log.Info($"EventTimer: {used} methods timed" + (missing.Count > 0 ? "; not found: " + string.Join(", ", missing) : ""));
        }

        private static void Enter(int slot)
        {
            if (Sandbox.MySandboxGame.Static?.UpdateThread != Thread.CurrentThread) return;
            if (Open.Count == 0 && !IsRoot[slot]) return;           // only inside a root
            if (Open.Count == 0)
            {
                Done.Clear();
                _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            }
            Open.Add((slot, Stopwatch.GetTimestamp()));
        }

        private static void Exit(int slot)
        {
            if (Open.Count == 0 || Sandbox.MySandboxGame.Static?.UpdateThread != Thread.CurrentThread) return;
            // (a call that threw left no exit: closed with the one that exits now)
            var at = Open.FindLastIndex(o => o.Slot == slot);
            if (at < 0) return;
            var now = Stopwatch.GetTimestamp();
            for (var i = Open.Count - 1; i >= at; i--)
            {
                Done.Add((Open[i].Slot, i, now - Open[i].Start));
                Open.RemoveAt(i);
            }
            if (Open.Count > 0) return;
            var ms = Done[Done.Count - 1].Ticks * 1000.0 / Stopwatch.Frequency;
            // the whole game frame is a root too: only its long ones
            var slow = Names[Done[Done.Count - 1].Slot] == "MySandboxGame.Update" ? 15 : SlowMs;
            if (ms >= slow && SentisTestsPlugin.Config?.EventTimerLogs == true)
            {
                var gc = (GC.CollectionCount(0) - _gc0) + "/" + (GC.CollectionCount(1) - _gc1) + "/" + (GC.CollectionCount(2) - _gc2);
                SentisTestsPlugin.Log.Info("EventTimer: " + Tree() + (gc == "0/0/0" ? "" : " gc " + gc));
            }
            Done.Clear();
        }

        private static string Ms(long ticks) =>
            (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms";

        /// <summary>The finished calls as a tree: a call's children are the calls one deeper finished before it, after its previous sibling.</summary>
        private static string Tree()
        {
            var sb = new StringBuilder();
            // what calls made many times were made of, summed over all of them, all the way down
            void Summed(List<int> members)
            {
                var inner = new List<int>();
                foreach (var member in members)
                    for (var j = member - 1; j >= 0 && Done[j].Depth > Done[member].Depth; j--)
                        if (Done[j].Depth == Done[member].Depth + 1) inner.Add(j);
                if (inner.Count == 0) return;
                sb.Append(" [");
                var firstInner = true;
                foreach (var g in inner.GroupBy(c => Done[c].Slot).OrderByDescending(g => g.Sum(c => Done[c].Ticks)))
                {
                    if (!firstInner) sb.Append(", ");
                    firstInner = false;
                    var part = g.ToList();
                    sb.Append(Names[g.Key]).Append(part.Count > 1 ? " x" + part.Count : "").Append(' ').Append(Ms(part.Sum(c => Done[c].Ticks)));
                    Summed(part);
                }
                sb.Append(']');
            }

            void Write(int index)
            {
                var (slot, depth, ticks) = Done[index];
                sb.Append(Names[slot]).Append(' ').Append(Ms(ticks));
                var children = new List<int>();
                for (var j = index - 1; j >= 0 && Done[j].Depth > depth; j--)
                    if (Done[j].Depth == depth + 1) children.Add(j);
                if (children.Count == 0) return;
                children.Reverse();
                sb.Append(" [");
                var first = true;
                // the same method called many times: summed, one entry
                foreach (var group in children.GroupBy(c => Done[c].Slot))
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    var list = group.ToList();
                    if (list.Count == 1) Write(list[0]);
                    else
                    {
                        sb.Append(Names[group.Key]).Append(" x").Append(list.Count).Append(' ').Append(Ms(list.Sum(c => Done[c].Ticks)));
                        Summed(list);
                    }
                }
                sb.Append(']');
            }
            Write(Done.Count - 1);
            return sb.ToString();
        }

        private static Type SlotType(int i)
        {
            switch (i) { case 0: return typeof(S0); case 1: return typeof(S1); case 2: return typeof(S2); case 3: return typeof(S3); case 4: return typeof(S4); case 5: return typeof(S5); case 6: return typeof(S6); case 7: return typeof(S7); case 8: return typeof(S8); case 9: return typeof(S9); case 10: return typeof(S10); case 11: return typeof(S11); case 12: return typeof(S12); case 13: return typeof(S13); case 14: return typeof(S14); case 15: return typeof(S15); case 16: return typeof(S16); case 17: return typeof(S17); case 18: return typeof(S18); case 19: return typeof(S19); case 20: return typeof(S20); case 21: return typeof(S21); case 22: return typeof(S22); case 23: return typeof(S23); case 24: return typeof(S24); case 25: return typeof(S25); case 26: return typeof(S26); case 27: return typeof(S27); case 28: return typeof(S28); case 29: return typeof(S29); case 30: return typeof(S30); case 31: return typeof(S31); case 32: return typeof(S32); case 33: return typeof(S33); case 34: return typeof(S34); case 35: return typeof(S35); case 36: return typeof(S36); case 37: return typeof(S37); case 38: return typeof(S38); case 39: return typeof(S39); case 40: return typeof(S40); case 41: return typeof(S41); case 42: return typeof(S42); case 43: return typeof(S43); case 44: return typeof(S44); case 45: return typeof(S45); case 46: return typeof(S46); case 47: return typeof(S47); case 48: return typeof(S48); case 49: return typeof(S49); case 50: return typeof(S50); case 51: return typeof(S51); case 52: return typeof(S52); case 53: return typeof(S53); case 54: return typeof(S54); case 55: return typeof(S55); case 56: return typeof(S56); case 57: return typeof(S57); case 58: return typeof(S58); case 59: return typeof(S59); case 60: return typeof(S60); case 61: return typeof(S61); case 62: return typeof(S62); case 63: return typeof(S63); case 64: return typeof(S64); case 65: return typeof(S65); case 66: return typeof(S66); case 67: return typeof(S67); case 68: return typeof(S68); case 69: return typeof(S69); case 70: return typeof(S70); case 71: return typeof(S71); case 72: return typeof(S72); case 73: return typeof(S73); case 74: return typeof(S74); case 75: return typeof(S75); case 76: return typeof(S76); case 77: return typeof(S77); case 78: return typeof(S78); case 79: return typeof(S79); case 80: return typeof(S80); case 81: return typeof(S81); case 82: return typeof(S82); case 83: return typeof(S83); case 84: return typeof(S84); case 85: return typeof(S85); case 86: return typeof(S86); case 87: return typeof(S87); case 88: return typeof(S88); case 89: return typeof(S89); case 90: return typeof(S90); case 91: return typeof(S91); case 92: return typeof(S92); case 93: return typeof(S93); case 94: return typeof(S94); case 95: return typeof(S95); case 96: return typeof(S96); case 97: return typeof(S97); case 98: return typeof(S98); case 99: return typeof(S99); case 100: return typeof(S100); case 101: return typeof(S101); case 102: return typeof(S102); case 103: return typeof(S103); case 104: return typeof(S104); case 105: return typeof(S105); case 106: return typeof(S106); case 107: return typeof(S107); case 108: return typeof(S108); case 109: return typeof(S109); case 110: return typeof(S110); case 111: return typeof(S111); case 112: return typeof(S112); case 113: return typeof(S113); case 114: return typeof(S114); case 115: return typeof(S115); case 116: return typeof(S116); case 117: return typeof(S117); case 118: return typeof(S118); case 119: return typeof(S119); case 120: return typeof(S120); case 121: return typeof(S121); case 122: return typeof(S122); case 123: return typeof(S123); case 124: return typeof(S124); case 125: return typeof(S125); case 126: return typeof(S126); case 127: return typeof(S127); case 128: return typeof(S128); case 129: return typeof(S129); case 130: return typeof(S130); case 131: return typeof(S131); case 132: return typeof(S132); case 133: return typeof(S133); case 134: return typeof(S134); case 135: return typeof(S135); case 136: return typeof(S136); case 137: return typeof(S137); case 138: return typeof(S138); case 139: return typeof(S139); case 140: return typeof(S140); case 141: return typeof(S141); case 142: return typeof(S142); case 143: return typeof(S143); case 144: return typeof(S144); case 145: return typeof(S145); case 146: return typeof(S146); case 147: return typeof(S147); case 148: return typeof(S148); case 149: return typeof(S149); case 150: return typeof(S150); case 151: return typeof(S151); case 152: return typeof(S152); case 153: return typeof(S153); case 154: return typeof(S154); case 155: return typeof(S155); case 156: return typeof(S156); case 157: return typeof(S157); case 158: return typeof(S158); case 159: return typeof(S159); case 160: return typeof(S160); case 161: return typeof(S161); case 162: return typeof(S162); case 163: return typeof(S163); case 164: return typeof(S164); case 165: return typeof(S165); case 166: return typeof(S166); case 167: return typeof(S167); case 168: return typeof(S168); case 169: return typeof(S169); case 170: return typeof(S170); case 171: return typeof(S171); case 172: return typeof(S172); case 173: return typeof(S173); case 174: return typeof(S174); case 175: return typeof(S175); case 176: return typeof(S176); case 177: return typeof(S177); case 178: return typeof(S178); case 179: return typeof(S179); case 180: return typeof(S180); case 181: return typeof(S181); case 182: return typeof(S182); case 183: return typeof(S183); case 184: return typeof(S184); case 185: return typeof(S185); case 186: return typeof(S186); case 187: return typeof(S187); case 188: return typeof(S188); case 189: return typeof(S189); case 190: return typeof(S190); case 191: return typeof(S191); default: throw new ArgumentOutOfRangeException(nameof(i)); }
        }

        // one class a slot: a hook is a static method, and each must know which method it times
        private static class S0 { private static void Prefix() => Enter(0); private static void Suffix() => Exit(0); }
        private static class S1 { private static void Prefix() => Enter(1); private static void Suffix() => Exit(1); }
        private static class S2 { private static void Prefix() => Enter(2); private static void Suffix() => Exit(2); }
        private static class S3 { private static void Prefix() => Enter(3); private static void Suffix() => Exit(3); }
        private static class S4 { private static void Prefix() => Enter(4); private static void Suffix() => Exit(4); }
        private static class S5 { private static void Prefix() => Enter(5); private static void Suffix() => Exit(5); }
        private static class S6 { private static void Prefix() => Enter(6); private static void Suffix() => Exit(6); }
        private static class S7 { private static void Prefix() => Enter(7); private static void Suffix() => Exit(7); }
        private static class S8 { private static void Prefix() => Enter(8); private static void Suffix() => Exit(8); }
        private static class S9 { private static void Prefix() => Enter(9); private static void Suffix() => Exit(9); }
        private static class S10 { private static void Prefix() => Enter(10); private static void Suffix() => Exit(10); }
        private static class S11 { private static void Prefix() => Enter(11); private static void Suffix() => Exit(11); }
        private static class S12 { private static void Prefix() => Enter(12); private static void Suffix() => Exit(12); }
        private static class S13 { private static void Prefix() => Enter(13); private static void Suffix() => Exit(13); }
        private static class S14 { private static void Prefix() => Enter(14); private static void Suffix() => Exit(14); }
        private static class S15 { private static void Prefix() => Enter(15); private static void Suffix() => Exit(15); }
        private static class S16 { private static void Prefix() => Enter(16); private static void Suffix() => Exit(16); }
        private static class S17 { private static void Prefix() => Enter(17); private static void Suffix() => Exit(17); }
        private static class S18 { private static void Prefix() => Enter(18); private static void Suffix() => Exit(18); }
        private static class S19 { private static void Prefix() => Enter(19); private static void Suffix() => Exit(19); }
        private static class S20 { private static void Prefix() => Enter(20); private static void Suffix() => Exit(20); }
        private static class S21 { private static void Prefix() => Enter(21); private static void Suffix() => Exit(21); }
        private static class S22 { private static void Prefix() => Enter(22); private static void Suffix() => Exit(22); }
        private static class S23 { private static void Prefix() => Enter(23); private static void Suffix() => Exit(23); }
        private static class S24 { private static void Prefix() => Enter(24); private static void Suffix() => Exit(24); }
        private static class S25 { private static void Prefix() => Enter(25); private static void Suffix() => Exit(25); }
        private static class S26 { private static void Prefix() => Enter(26); private static void Suffix() => Exit(26); }
        private static class S27 { private static void Prefix() => Enter(27); private static void Suffix() => Exit(27); }
        private static class S28 { private static void Prefix() => Enter(28); private static void Suffix() => Exit(28); }
        private static class S29 { private static void Prefix() => Enter(29); private static void Suffix() => Exit(29); }
        private static class S30 { private static void Prefix() => Enter(30); private static void Suffix() => Exit(30); }
        private static class S31 { private static void Prefix() => Enter(31); private static void Suffix() => Exit(31); }
        private static class S32 { private static void Prefix() => Enter(32); private static void Suffix() => Exit(32); }
        private static class S33 { private static void Prefix() => Enter(33); private static void Suffix() => Exit(33); }
        private static class S34 { private static void Prefix() => Enter(34); private static void Suffix() => Exit(34); }
        private static class S35 { private static void Prefix() => Enter(35); private static void Suffix() => Exit(35); }
        private static class S36 { private static void Prefix() => Enter(36); private static void Suffix() => Exit(36); }
        private static class S37 { private static void Prefix() => Enter(37); private static void Suffix() => Exit(37); }
        private static class S38 { private static void Prefix() => Enter(38); private static void Suffix() => Exit(38); }
        private static class S39 { private static void Prefix() => Enter(39); private static void Suffix() => Exit(39); }
        private static class S40 { private static void Prefix() => Enter(40); private static void Suffix() => Exit(40); }
        private static class S41 { private static void Prefix() => Enter(41); private static void Suffix() => Exit(41); }
        private static class S42 { private static void Prefix() => Enter(42); private static void Suffix() => Exit(42); }
        private static class S43 { private static void Prefix() => Enter(43); private static void Suffix() => Exit(43); }
        private static class S44 { private static void Prefix() => Enter(44); private static void Suffix() => Exit(44); }
        private static class S45 { private static void Prefix() => Enter(45); private static void Suffix() => Exit(45); }
        private static class S46 { private static void Prefix() => Enter(46); private static void Suffix() => Exit(46); }
        private static class S47 { private static void Prefix() => Enter(47); private static void Suffix() => Exit(47); }
        private static class S48 { private static void Prefix() => Enter(48); private static void Suffix() => Exit(48); }
        private static class S49 { private static void Prefix() => Enter(49); private static void Suffix() => Exit(49); }
        private static class S50 { private static void Prefix() => Enter(50); private static void Suffix() => Exit(50); }
        private static class S51 { private static void Prefix() => Enter(51); private static void Suffix() => Exit(51); }
        private static class S52 { private static void Prefix() => Enter(52); private static void Suffix() => Exit(52); }
        private static class S53 { private static void Prefix() => Enter(53); private static void Suffix() => Exit(53); }
        private static class S54 { private static void Prefix() => Enter(54); private static void Suffix() => Exit(54); }
        private static class S55 { private static void Prefix() => Enter(55); private static void Suffix() => Exit(55); }
        private static class S56 { private static void Prefix() => Enter(56); private static void Suffix() => Exit(56); }
        private static class S57 { private static void Prefix() => Enter(57); private static void Suffix() => Exit(57); }
        private static class S58 { private static void Prefix() => Enter(58); private static void Suffix() => Exit(58); }
        private static class S59 { private static void Prefix() => Enter(59); private static void Suffix() => Exit(59); }
        private static class S60 { private static void Prefix() => Enter(60); private static void Suffix() => Exit(60); }
        private static class S61 { private static void Prefix() => Enter(61); private static void Suffix() => Exit(61); }
        private static class S62 { private static void Prefix() => Enter(62); private static void Suffix() => Exit(62); }
        private static class S63 { private static void Prefix() => Enter(63); private static void Suffix() => Exit(63); }
        private static class S64 { private static void Prefix() => Enter(64); private static void Suffix() => Exit(64); }
        private static class S65 { private static void Prefix() => Enter(65); private static void Suffix() => Exit(65); }
        private static class S66 { private static void Prefix() => Enter(66); private static void Suffix() => Exit(66); }
        private static class S67 { private static void Prefix() => Enter(67); private static void Suffix() => Exit(67); }
        private static class S68 { private static void Prefix() => Enter(68); private static void Suffix() => Exit(68); }
        private static class S69 { private static void Prefix() => Enter(69); private static void Suffix() => Exit(69); }
        private static class S70 { private static void Prefix() => Enter(70); private static void Suffix() => Exit(70); }
        private static class S71 { private static void Prefix() => Enter(71); private static void Suffix() => Exit(71); }
        private static class S72 { private static void Prefix() => Enter(72); private static void Suffix() => Exit(72); }
        private static class S73 { private static void Prefix() => Enter(73); private static void Suffix() => Exit(73); }
        private static class S74 { private static void Prefix() => Enter(74); private static void Suffix() => Exit(74); }
        private static class S75 { private static void Prefix() => Enter(75); private static void Suffix() => Exit(75); }
        private static class S76 { private static void Prefix() => Enter(76); private static void Suffix() => Exit(76); }
        private static class S77 { private static void Prefix() => Enter(77); private static void Suffix() => Exit(77); }
        private static class S78 { private static void Prefix() => Enter(78); private static void Suffix() => Exit(78); }
        private static class S79 { private static void Prefix() => Enter(79); private static void Suffix() => Exit(79); }
        private static class S80 { private static void Prefix() => Enter(80); private static void Suffix() => Exit(80); }
        private static class S81 { private static void Prefix() => Enter(81); private static void Suffix() => Exit(81); }
        private static class S82 { private static void Prefix() => Enter(82); private static void Suffix() => Exit(82); }
        private static class S83 { private static void Prefix() => Enter(83); private static void Suffix() => Exit(83); }
        private static class S84 { private static void Prefix() => Enter(84); private static void Suffix() => Exit(84); }
        private static class S85 { private static void Prefix() => Enter(85); private static void Suffix() => Exit(85); }
        private static class S86 { private static void Prefix() => Enter(86); private static void Suffix() => Exit(86); }
        private static class S87 { private static void Prefix() => Enter(87); private static void Suffix() => Exit(87); }
        private static class S88 { private static void Prefix() => Enter(88); private static void Suffix() => Exit(88); }
        private static class S89 { private static void Prefix() => Enter(89); private static void Suffix() => Exit(89); }
        private static class S90 { private static void Prefix() => Enter(90); private static void Suffix() => Exit(90); }
        private static class S91 { private static void Prefix() => Enter(91); private static void Suffix() => Exit(91); }
        private static class S92 { private static void Prefix() => Enter(92); private static void Suffix() => Exit(92); }
        private static class S93 { private static void Prefix() => Enter(93); private static void Suffix() => Exit(93); }
        private static class S94 { private static void Prefix() => Enter(94); private static void Suffix() => Exit(94); }
        private static class S95 { private static void Prefix() => Enter(95); private static void Suffix() => Exit(95); }
        private static class S96 { private static void Prefix() => Enter(96); private static void Suffix() => Exit(96); }
        private static class S97 { private static void Prefix() => Enter(97); private static void Suffix() => Exit(97); }
        private static class S98 { private static void Prefix() => Enter(98); private static void Suffix() => Exit(98); }
        private static class S99 { private static void Prefix() => Enter(99); private static void Suffix() => Exit(99); }
        private static class S100 { private static void Prefix() => Enter(100); private static void Suffix() => Exit(100); }
        private static class S101 { private static void Prefix() => Enter(101); private static void Suffix() => Exit(101); }
        private static class S102 { private static void Prefix() => Enter(102); private static void Suffix() => Exit(102); }
        private static class S103 { private static void Prefix() => Enter(103); private static void Suffix() => Exit(103); }
        private static class S104 { private static void Prefix() => Enter(104); private static void Suffix() => Exit(104); }
        private static class S105 { private static void Prefix() => Enter(105); private static void Suffix() => Exit(105); }
        private static class S106 { private static void Prefix() => Enter(106); private static void Suffix() => Exit(106); }
        private static class S107 { private static void Prefix() => Enter(107); private static void Suffix() => Exit(107); }
        private static class S108 { private static void Prefix() => Enter(108); private static void Suffix() => Exit(108); }
        private static class S109 { private static void Prefix() => Enter(109); private static void Suffix() => Exit(109); }
        private static class S110 { private static void Prefix() => Enter(110); private static void Suffix() => Exit(110); }
        private static class S111 { private static void Prefix() => Enter(111); private static void Suffix() => Exit(111); }
        private static class S112 { private static void Prefix() => Enter(112); private static void Suffix() => Exit(112); }
        private static class S113 { private static void Prefix() => Enter(113); private static void Suffix() => Exit(113); }
        private static class S114 { private static void Prefix() => Enter(114); private static void Suffix() => Exit(114); }
        private static class S115 { private static void Prefix() => Enter(115); private static void Suffix() => Exit(115); }
        private static class S116 { private static void Prefix() => Enter(116); private static void Suffix() => Exit(116); }
        private static class S117 { private static void Prefix() => Enter(117); private static void Suffix() => Exit(117); }
        private static class S118 { private static void Prefix() => Enter(118); private static void Suffix() => Exit(118); }
        private static class S119 { private static void Prefix() => Enter(119); private static void Suffix() => Exit(119); }
        private static class S120 { private static void Prefix() => Enter(120); private static void Suffix() => Exit(120); }
        private static class S121 { private static void Prefix() => Enter(121); private static void Suffix() => Exit(121); }
        private static class S122 { private static void Prefix() => Enter(122); private static void Suffix() => Exit(122); }
        private static class S123 { private static void Prefix() => Enter(123); private static void Suffix() => Exit(123); }
        private static class S124 { private static void Prefix() => Enter(124); private static void Suffix() => Exit(124); }
        private static class S125 { private static void Prefix() => Enter(125); private static void Suffix() => Exit(125); }
        private static class S126 { private static void Prefix() => Enter(126); private static void Suffix() => Exit(126); }
        private static class S127 { private static void Prefix() => Enter(127); private static void Suffix() => Exit(127); }
        private static class S128 { private static void Prefix() => Enter(128); private static void Suffix() => Exit(128); }
        private static class S129 { private static void Prefix() => Enter(129); private static void Suffix() => Exit(129); }
        private static class S130 { private static void Prefix() => Enter(130); private static void Suffix() => Exit(130); }
        private static class S131 { private static void Prefix() => Enter(131); private static void Suffix() => Exit(131); }
        private static class S132 { private static void Prefix() => Enter(132); private static void Suffix() => Exit(132); }
        private static class S133 { private static void Prefix() => Enter(133); private static void Suffix() => Exit(133); }
        private static class S134 { private static void Prefix() => Enter(134); private static void Suffix() => Exit(134); }
        private static class S135 { private static void Prefix() => Enter(135); private static void Suffix() => Exit(135); }
        private static class S136 { private static void Prefix() => Enter(136); private static void Suffix() => Exit(136); }
        private static class S137 { private static void Prefix() => Enter(137); private static void Suffix() => Exit(137); }
        private static class S138 { private static void Prefix() => Enter(138); private static void Suffix() => Exit(138); }
        private static class S139 { private static void Prefix() => Enter(139); private static void Suffix() => Exit(139); }
        private static class S140 { private static void Prefix() => Enter(140); private static void Suffix() => Exit(140); }
        private static class S141 { private static void Prefix() => Enter(141); private static void Suffix() => Exit(141); }
        private static class S142 { private static void Prefix() => Enter(142); private static void Suffix() => Exit(142); }
        private static class S143 { private static void Prefix() => Enter(143); private static void Suffix() => Exit(143); }
        private static class S144 { private static void Prefix() => Enter(144); private static void Suffix() => Exit(144); }
        private static class S145 { private static void Prefix() => Enter(145); private static void Suffix() => Exit(145); }
        private static class S146 { private static void Prefix() => Enter(146); private static void Suffix() => Exit(146); }
        private static class S147 { private static void Prefix() => Enter(147); private static void Suffix() => Exit(147); }
        private static class S148 { private static void Prefix() => Enter(148); private static void Suffix() => Exit(148); }
        private static class S149 { private static void Prefix() => Enter(149); private static void Suffix() => Exit(149); }
        private static class S150 { private static void Prefix() => Enter(150); private static void Suffix() => Exit(150); }
        private static class S151 { private static void Prefix() => Enter(151); private static void Suffix() => Exit(151); }
        private static class S152 { private static void Prefix() => Enter(152); private static void Suffix() => Exit(152); }
        private static class S153 { private static void Prefix() => Enter(153); private static void Suffix() => Exit(153); }
        private static class S154 { private static void Prefix() => Enter(154); private static void Suffix() => Exit(154); }
        private static class S155 { private static void Prefix() => Enter(155); private static void Suffix() => Exit(155); }
        private static class S156 { private static void Prefix() => Enter(156); private static void Suffix() => Exit(156); }
        private static class S157 { private static void Prefix() => Enter(157); private static void Suffix() => Exit(157); }
        private static class S158 { private static void Prefix() => Enter(158); private static void Suffix() => Exit(158); }
        private static class S159 { private static void Prefix() => Enter(159); private static void Suffix() => Exit(159); }
        private static class S160 { private static void Prefix() => Enter(160); private static void Suffix() => Exit(160); }
        private static class S161 { private static void Prefix() => Enter(161); private static void Suffix() => Exit(161); }
        private static class S162 { private static void Prefix() => Enter(162); private static void Suffix() => Exit(162); }
        private static class S163 { private static void Prefix() => Enter(163); private static void Suffix() => Exit(163); }
        private static class S164 { private static void Prefix() => Enter(164); private static void Suffix() => Exit(164); }
        private static class S165 { private static void Prefix() => Enter(165); private static void Suffix() => Exit(165); }
        private static class S166 { private static void Prefix() => Enter(166); private static void Suffix() => Exit(166); }
        private static class S167 { private static void Prefix() => Enter(167); private static void Suffix() => Exit(167); }
        private static class S168 { private static void Prefix() => Enter(168); private static void Suffix() => Exit(168); }
        private static class S169 { private static void Prefix() => Enter(169); private static void Suffix() => Exit(169); }
        private static class S170 { private static void Prefix() => Enter(170); private static void Suffix() => Exit(170); }
        private static class S171 { private static void Prefix() => Enter(171); private static void Suffix() => Exit(171); }
        private static class S172 { private static void Prefix() => Enter(172); private static void Suffix() => Exit(172); }
        private static class S173 { private static void Prefix() => Enter(173); private static void Suffix() => Exit(173); }
        private static class S174 { private static void Prefix() => Enter(174); private static void Suffix() => Exit(174); }
        private static class S175 { private static void Prefix() => Enter(175); private static void Suffix() => Exit(175); }
        private static class S176 { private static void Prefix() => Enter(176); private static void Suffix() => Exit(176); }
        private static class S177 { private static void Prefix() => Enter(177); private static void Suffix() => Exit(177); }
        private static class S178 { private static void Prefix() => Enter(178); private static void Suffix() => Exit(178); }
        private static class S179 { private static void Prefix() => Enter(179); private static void Suffix() => Exit(179); }
        private static class S180 { private static void Prefix() => Enter(180); private static void Suffix() => Exit(180); }
        private static class S181 { private static void Prefix() => Enter(181); private static void Suffix() => Exit(181); }
        private static class S182 { private static void Prefix() => Enter(182); private static void Suffix() => Exit(182); }
        private static class S183 { private static void Prefix() => Enter(183); private static void Suffix() => Exit(183); }
        private static class S184 { private static void Prefix() => Enter(184); private static void Suffix() => Exit(184); }
        private static class S185 { private static void Prefix() => Enter(185); private static void Suffix() => Exit(185); }
        private static class S186 { private static void Prefix() => Enter(186); private static void Suffix() => Exit(186); }
        private static class S187 { private static void Prefix() => Enter(187); private static void Suffix() => Exit(187); }
        private static class S188 { private static void Prefix() => Enter(188); private static void Suffix() => Exit(188); }
        private static class S189 { private static void Prefix() => Enter(189); private static void Suffix() => Exit(189); }
        private static class S190 { private static void Prefix() => Enter(190); private static void Suffix() => Exit(190); }
        private static class S191 { private static void Prefix() => Enter(191); private static void Suffix() => Exit(191); }
    }
}
