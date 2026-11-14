using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisOptimisations' ConveyorRecomputeDebounce: a base being welded has its conveyor network recomputed once the
    /// welding stops, not after every block. A test base - cargo A, eight conveyors, cargo B, a battery:
    ///  1. a conveyor added every half a second for 4 s, then a cargo on top of the line (as a welder or a projector adds
    ///     them): the network is not flagged for a recompute while blocks keep coming; it is flagged 3 s after the last one
    ///     (180 frames, give or take the 10 frames of SO's tick), recomputed, and the new cargo is then in the game's own
    ///     pull and push lists; "can A bring an ore to the new cargo" is true all along (SO's ConveyorReach follows the network
    ///     as it is), as vanilla's own search says;
    ///  2. a conveyor ground off: flagged at once, as vanilla;
    ///  3. cargo B given to someone else: flagged at once, as vanilla.
    /// A fake player stands by the base: a grid nobody is near is frozen by SO, its conveyor system with it.
    /// </summary>
    public sealed class ConveyorRecomputeScenario : TestScenario
    {
        public const string ScenarioName = "conveyor_recompute";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var debounce = Integrations.TypeOf(Integrations.Optimisations, "Optimizer.Optimizations.ConveyorRecomputeDebounce");
            var reach = Integrations.TypeOf(Integrations.Optimisations, "Optimizer.Optimizations.ConveyorReach");
            Check(debounce != null && reach != null, "SentisOptimisations' ConveyorRecomputeDebounce or ConveyorReach not found: a build without them");
            var isWaiting = debounce.GetMethod("IsWaiting", Any);
            long Counter(string name) => Convert.ToInt64(debounce.GetField(name, Any)?.GetValue(null) ?? -1L);
            var vanillaFlag = reach.GetField("Vanilla", Any);
            var system = typeof(MyGridConveyorSystem);
            var needsField = system.GetField("m_needsRecomputation", Any);
            var recomputingField = system.GetField("m_isRecomputingGraph", Any);
            var connectionsField = system.GetField("m_conveyorConnections", Any);
            Check(isWaiting != null && vanillaFlag != null && needsField != null && recomputingField != null && connectionsField != null,
                "the members this scenario reads are not there");

            var owner = WorldApi.TestIdentityId();
            var origin = TestRunner.RunOrigin ?? new Vector3D(-40000, 25000, 70000);
            var specs = new List<BlockSpec> { new BlockSpec("LargeBlockSmallContainer", new Vector3I(0, 0, 0)) };
            for (var x = 1; x <= 8; x++) specs.Add(new BlockSpec("LargeBlockConveyor", new Vector3I(x, 0, 0)));
            specs.Add(new BlockSpec("LargeBlockSmallContainer", new Vector3I(9, 0, 0)));
            specs.Add(new BlockSpec(WorldApi.FindSubtype(MyCubeSize.Large, "batteryblock"), new Vector3I(0, -1, 0)));
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + "conveyor-recompute", MyCubeSize.Large, true, origin, specs);
            foreach (var block in ob.CubeBlocks) block.Owner = owner;
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            // a player by the base: SO's freezer stops a grid nobody is near, and with it its conveyor system's updates
            FakeClients.RemoveAll();
            yield return WaitForTicks(10);
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 50 }, i => (origin + new Vector3D(0, 15, 0), 0, 0), withCharacters: true);
            var conveyors = grid.GridSystems.ConveyorSystem;
            bool Needs() => (bool)needsField.GetValue(conveyors);
            bool Recomputing() => (bool)recomputingField.GetValue(conveyors);
            bool Waiting() => (bool)isWaiting.Invoke(null, new object[] { grid });
            yield return Wait(() => !Needs() && !Recomputing(), "the new base's network recomputed", 30);
            var a = grid.GetCubeBlock(new Vector3I(0, 0, 0)).FatBlock;
            var b = grid.GetCubeBlock(new Vector3I(9, 0, 0)).FatBlock;
            var ore = MyDefinitionId.Parse("MyObjectBuilder_Ore/Iron");

            MySlimBlock Add(string subtype, Vector3I at)
            {
                var block = (MyObjectBuilder_CubeBlock)VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen.CreateNewObject(
                    subtype == "LargeBlockConveyor" ? typeof(Sandbox.Common.ObjectBuilders.MyObjectBuilder_Conveyor) : typeof(MyObjectBuilder_CargoContainer));
                block.SubtypeName = subtype;
                block.Min = at;
                block.Owner = owner;
                block.BuiltBy = owner;
                block.IntegrityPercent = 1;
                block.BuildPercent = 1;
                block.EntityId = 0;
                return ((IMyCubeGrid)grid).AddBlock(block, false) as MySlimBlock;
            }

            bool Ask(MyCubeBlock from, MyCubeBlock to, bool vanilla)
            {
                vanillaFlag.SetValue(null, vanilla);
                try
                {
                    return MyGridConveyorSystem.ComputeCanTransfer(from as IMyConveyorEndpointBlock, to as IMyConveyorEndpointBlock, ore);
                }
                finally
                {
                    vanillaFlag.SetValue(null, false);
                }
            }

            // 1. welding: a conveyor every half a second on top of the line, then a cargo on top of the last one
            var deferred0 = Counter("Deferred");
            var released0 = Counter("Released");
            var flaggedWhileWelding = 0;
            var waitingAll = true;
            MyCubeBlock added = null;
            var askedWrong = 0;
            for (var k = 0; k < 9; k++)
            {
                var slim = k < 8 ? Add("LargeBlockConveyor", new Vector3I(1 + k, 1, 0)) : Add("LargeBlockSmallContainer", new Vector3I(8, 2, 0));
                Check(slim != null, "a block was not added to the test base");
                if (k == 8) added = slim.FatBlock;
                for (var f = 0; f < 30; f++)
                {
                    yield return null;
                    if (Needs()) flaggedWhileWelding++;
                    if (f > 2 && !Waiting()) waitingAll = false;
                    if (added != null && Ask(a, added, false) != Ask(a, added, true)) askedWrong++;
                }
            }
            var lastAdd = MySession.Static.GameplayFrameCounter - 30;
            var flaggedAt = -1;
            for (var f = 0; f < 400 && flaggedAt < 0; f++)
            {
                if (Needs() || Recomputing()) flaggedAt = MySession.Static.GameplayFrameCounter;
                else yield return null;
            }
            var afterLast = flaggedAt < 0 ? -1 : flaggedAt - lastAdd;
            Note($"CONVEYOR RECOMPUTE | flagged {afterLast} frames after the last block; needs {Needs()}, recomputing {Recomputing()}, waiting {Waiting()}");
            for (var second = 0; second < 30 && (Needs() || Recomputing()); second++)
            {
                yield return WaitForTicks(60);
                if (second % 5 == 4) Note($"CONVEYOR RECOMPUTE | {second + 1} s on: needs {Needs()}, recomputing {Recomputing()}, waiting {Waiting()}, lines {conveyors.NeedsUpdateLines}");
            }
            yield return Wait(() => !Needs() && !Recomputing(), "the welded base's network recomputed", 5);
            var listed = ((IDictionary)connectionsField.GetValue(conveyors)).Contains(added);
            var reachesSo = Ask(a, added, false);
            var reachesVanilla = Ask(a, added, true);
            var deferred = Counter("Deferred") - deferred0;
            var released = Counter("Released") - released0;
            Note($"CONVEYOR RECOMPUTE | welding 9 blocks over 4.5 s: flagged on {flaggedWhileWelding} frames while welding, waiting all along {waitingAll}, " +
                 $"{deferred} recomputes put off; flagged {afterLast} frames after the last block ({released} released); the new cargo in the game's lists {listed}; " +
                 $"A to it: SO {reachesSo}, vanilla {reachesVanilla}, differing while welding {askedWrong}");
            Check(flaggedWhileWelding == 0, $"the network was flagged for a recompute on {flaggedWhileWelding} frames while blocks kept coming");
            Check(waitingAll && deferred > 0, "the recompute did not wait while the base was being welded");
            Check(afterLast >= 170 && afterLast <= 200, $"the recompute was flagged {afterLast} frames after the last block, not 3 s (180 frames)");
            Check(listed, "the cargo welded last is not in the game's pull and push lists after the recompute");
            Check(reachesSo && reachesVanilla && askedWrong == 0, "A to the new cargo: SO's answer is not vanilla's, or not reachable");

            // 2. a conveyor ground off: at once
            grid.RazeBlock(new Vector3I(1, 1, 0));
            var removalFlag = -1;
            for (var f = 0; f < 30 && removalFlag < 0; f++)
            {
                yield return null;
                if (Needs() || Recomputing()) removalFlag = f + 1;
            }
            Note($"CONVEYOR RECOMPUTE | a conveyor ground off: flagged after {removalFlag} frames");
            Check(removalFlag > 0 && removalFlag <= 10, "a conveyor ground off did not have the network recomputed at once");
            yield return Wait(() => !Needs() && !Recomputing() && !Waiting(), "the network recomputed after the removal", 30);

            // 3. B given to someone else: at once
            var stranger = MySession.Static.Players.GetAllIdentities().FirstOrDefault(i => i.DisplayName == "ST conveyor stranger")
                           ?? MySession.Static.Players.CreateNewIdentity("ST conveyor stranger");
            b.ChangeOwner(stranger.IdentityId, MyOwnershipShareModeEnum.None);
            var ownerFlag = -1;
            for (var f = 0; f < 120 && ownerFlag < 0; f++)
            {
                yield return null;
                if (Needs() || Recomputing()) ownerFlag = f + 1;
            }
            var waitingAfterOwner = Waiting();
            Note($"CONVEYOR RECOMPUTE | cargo B given to a stranger: flagged after {ownerFlag} frames, waiting {waitingAfterOwner}");
            Check(ownerFlag > 0 && ownerFlag <= 110, "a block given to someone else did not have the network recomputed at once");
            Check(!waitingAfterOwner, "a block given to someone else left the recompute waiting");
            b.ChangeOwner(owner, MyOwnershipShareModeEnum.Faction);
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
