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
        public const double SlowMs = 2;
        private const int Slots = 96;

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
            ("Sandbox.Game.Entities.MyCubeBlockFactory", "CreateCubeBlock", false),
            ("Sandbox.Game.Entities.Cube.MyGridPhysics", "AddBlock", false),
            ("Sandbox.Game.Entities.Cube.MyGridShape", "UpdateShape", false),
            ("Sandbox.Game.GameSystems.Conveyors.MyGridConveyorSystem", "Add", false),
            ("Sandbox.Game.GameSystems.MyGridResourceDistributorSystem", "AddSink", false),
            ("Sandbox.Game.Entities.MySurvivalBuildComponent", "GetBlocksPlacementMaterials", false),
            ("Sandbox.Game.Entities.MySurvivalBuildComponent", "HasBuildingMaterials", false),
            ("Sandbox.Game.Entities.MySurvivalBuildComponent", "AfterSuccessfulBuild", false),
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
            ("Sandbox.Game.World.MyPlayerCollection", "SetControlledEntity", false),
            ("Sandbox.Game.Entities.Character.MyCharacter", "Die", false),
            ("Sandbox.Game.Entities.Character.MyCharacter", "OnSuicideRequest", true),
            ("SpaceEngineers.Game.Entities.MySpaceBuildComponent", "GetBlocksPlacementMaterials", false),
            ("SpaceEngineers.Game.Entities.MySpaceBuildComponent", "HasBuildingMaterials", false),
            ("SpaceEngineers.Game.Entities.MySpaceBuildComponent", "AfterSuccessfulBuild", false),
            ("Sandbox.Game.SessionComponents.MySessionComponentGameInventory", "ValidateItem", false),
            ("SentisGameplayImprovements.Limits.BuildBlockPatch", "BuildBlocksRequest", false),
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
            ("Sandbox.Game.GameSystems.MyCubeGridSystems", "UpdatePower", false),
        };

        private static readonly string[] Names = new string[Slots];
        private static readonly bool[] IsRoot = new bool[Slots];

        // the calls open now, and the finished ones of the root under way: (slot, depth, ticks)
        private static readonly List<(int Slot, long Start)> Open = new List<(int, long)>();
        private static readonly List<(int Slot, int Depth, long Ticks)> Done = new List<(int, int, long)>();

        public static void Install(PatchManager patchManager)
        {
            if (patchManager == null) return;
            var ctx = patchManager.AcquireContext();
            var used = 0;
            var missing = new List<string>();
            foreach (var (typeName, method, root) in Targets)
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName, false)).FirstOrDefault(t => t != null);
                if (type == null) { missing.Add(typeName); continue; }
                var found = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == method && !m.IsAbstract && !m.IsGenericMethodDefinition).ToList();
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
            SentisTestsPlugin.Log.Info($"EventTimer: {used} methods timed" + (missing.Count > 0 ? "; not found: " + string.Join(", ", missing) : ""));
        }

        private static void Enter(int slot)
        {
            if (Sandbox.MySandboxGame.Static?.UpdateThread != Thread.CurrentThread) return;
            if (Open.Count == 0 && !IsRoot[slot]) return;           // only inside a root
            if (Open.Count == 0) Done.Clear();
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
            if (ms >= SlowMs) SentisTestsPlugin.Log.Info("EventTimer: " + Tree());
            Done.Clear();
        }

        private static string Ms(long ticks) =>
            (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms";

        /// <summary>The finished calls as a tree: a call's children are the calls one deeper finished before it, after its previous sibling.</summary>
        private static string Tree()
        {
            var sb = new StringBuilder();
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
                    else sb.Append(Names[group.Key]).Append(" x").Append(list.Count).Append(' ').Append(Ms(list.Sum(c => Done[c].Ticks)));
                }
                sb.Append(']');
            }
            Write(Done.Count - 1);
            return sb.ToString();
        }

        private static Type SlotType(int i)
        {
            switch (i) { case 0: return typeof(S0); case 1: return typeof(S1); case 2: return typeof(S2); case 3: return typeof(S3); case 4: return typeof(S4); case 5: return typeof(S5); case 6: return typeof(S6); case 7: return typeof(S7); case 8: return typeof(S8); case 9: return typeof(S9); case 10: return typeof(S10); case 11: return typeof(S11); case 12: return typeof(S12); case 13: return typeof(S13); case 14: return typeof(S14); case 15: return typeof(S15); case 16: return typeof(S16); case 17: return typeof(S17); case 18: return typeof(S18); case 19: return typeof(S19); case 20: return typeof(S20); case 21: return typeof(S21); case 22: return typeof(S22); case 23: return typeof(S23); case 24: return typeof(S24); case 25: return typeof(S25); case 26: return typeof(S26); case 27: return typeof(S27); case 28: return typeof(S28); case 29: return typeof(S29); case 30: return typeof(S30); case 31: return typeof(S31); case 32: return typeof(S32); case 33: return typeof(S33); case 34: return typeof(S34); case 35: return typeof(S35); case 36: return typeof(S36); case 37: return typeof(S37); case 38: return typeof(S38); case 39: return typeof(S39); case 40: return typeof(S40); case 41: return typeof(S41); case 42: return typeof(S42); case 43: return typeof(S43); case 44: return typeof(S44); case 45: return typeof(S45); case 46: return typeof(S46); case 47: return typeof(S47); case 48: return typeof(S48); case 49: return typeof(S49); case 50: return typeof(S50); case 51: return typeof(S51); case 52: return typeof(S52); case 53: return typeof(S53); case 54: return typeof(S54); case 55: return typeof(S55); case 56: return typeof(S56); case 57: return typeof(S57); case 58: return typeof(S58); case 59: return typeof(S59); case 60: return typeof(S60); case 61: return typeof(S61); case 62: return typeof(S62); case 63: return typeof(S63); case 64: return typeof(S64); case 65: return typeof(S65); case 66: return typeof(S66); case 67: return typeof(S67); case 68: return typeof(S68); case 69: return typeof(S69); case 70: return typeof(S70); case 71: return typeof(S71); case 72: return typeof(S72); case 73: return typeof(S73); case 74: return typeof(S74); case 75: return typeof(S75); case 76: return typeof(S76); case 77: return typeof(S77); case 78: return typeof(S78); case 79: return typeof(S79); case 80: return typeof(S80); case 81: return typeof(S81); case 82: return typeof(S82); case 83: return typeof(S83); case 84: return typeof(S84); case 85: return typeof(S85); case 86: return typeof(S86); case 87: return typeof(S87); case 88: return typeof(S88); case 89: return typeof(S89); case 90: return typeof(S90); case 91: return typeof(S91); case 92: return typeof(S92); case 93: return typeof(S93); case 94: return typeof(S94); case 95: return typeof(S95); default: throw new ArgumentOutOfRangeException(nameof(i)); }
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
    }
}
