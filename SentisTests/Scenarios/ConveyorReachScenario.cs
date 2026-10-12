using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI.Ingame;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// "Can the conveyor bring this item from there to here" (IMyInventory.CanTransferItemTo / IsConnectedTo / a transfer
    /// through conveyors) answers as vanilla does with SentisOptimisations' ConveyorReach (one backward search of the
    /// network per destination, item and owner for a frame, instead of a search of the whole network per pair). The
    /// answer of each question is compared with vanilla's own search made right then (MyGridConveyorSystem
    /// .ComputeCanTransfer):
    ///  1. the world as it is: every inventory to every inventory of the biggest conveyor networks, for no item, an ore,
    ///     an ingot, a small and a large component (small conveyors carry no large item), a tool;
    ///  2. a test base, cargo A - conveyor - sorter - conveyor - cargo B (the sorter one way: B to A), a cargo on each side over the conveyors: the
    ///     sorter's filter, the sorter off, its power off, B given to a stranger, shared with all, given back, a conveyor
    ///     ground off. Each change is asked about in the same frame as the change, right after the same questions were
    ///     answered (and remembered) before it, and again in the next frames; what must and must not be reachable is
    ///     written out and checked too, not only the agreement with vanilla;
    ///  3. a locked connector of the world: unlocked, locked again, trading on and off - the same way.
    /// SO's switch ("Conveyor reach cache") is turned off and on: off, vanilla answers and SO searches nothing.
    /// The world is left as it was: the base removed, the connector locked and not trading, the switch as it was.
    /// </summary>
    public sealed class ConveyorReachScenario : TestScenario
    {
        public const string ScenarioName = "conveyor_reach";
        private const int Networks = 8, MaxInventories = 80;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        private readonly List<string> _wrong = new List<string>();
        private readonly ConfigOverride _config = new ConfigOverride();
        private int _asked;

        private static bool Patched(MyInventory from, MyInventory to, MyDefinitionId? item) =>
            item.HasValue ? ((IMyInventory)from).CanTransferItemTo(to, (MyItemType)item.Value) : ((IMyInventory)from).IsConnectedTo(to);

        private static IMyConveyorEndpointBlock Endpoint(MyCubeBlock block) => block as IMyConveyorEndpointBlock;

        private static bool Vanilla(MyCubeBlock from, MyCubeBlock to, MyDefinitionId? item) =>
            MyGridConveyorSystem.ComputeCanTransfer(Endpoint(from), Endpoint(to), item);

        private static string ItemName(MyDefinitionId? item) => item?.SubtypeName ?? "any";

        /// <summary>Asks, compares with vanilla, checks the expectation if any; returns the answer.</summary>
        private bool Ask(string when, MyCubeBlock from, MyCubeBlock to, MyDefinitionId? item, bool? expected = null)
        {
            var got = Patched(from.GetInventory(0), to.GetInventory(0), item);
            var vanilla = Vanilla(from, to, item);
            _asked++;
            if (got != vanilla)
                _wrong.Add($"{when}: {from.DisplayNameText} -> {to.DisplayNameText}, {ItemName(item)}: {got}, vanilla {vanilla}");
            else if (expected.HasValue && got != expected.Value)
                _wrong.Add($"{when}: {from.DisplayNameText} -> {to.DisplayNameText}, {ItemName(item)}: {got} (vanilla too), expected {expected.Value}");
            return got;
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var reach = Integrations.TypeOf("SentisOptimisations", "Optimizer.Optimizations.ConveyorReach");
            Check(reach != null, "SentisOptimisations' ConveyorReach not found: a build without it");
            long Counter(string name) => Convert.ToInt64(reach.GetField(name)?.GetValue(null) ?? -1L);

            // the items: no item, ore, ingot, small component, large component, tool
            var physical = MyDefinitionManager.Static.GetPhysicalItemDefinitions().Where(d => d.Public).ToList();
            MyDefinitionId? Find(Func<MyPhysicalItemDefinition, bool> what) => physical.FirstOrDefault(what)?.Id;
            var ore = MyDefinitionId.Parse("MyObjectBuilder_Ore/Iron");
            var plate = MyDefinitionId.Parse("MyObjectBuilder_Component/SteelPlate");
            var items = new List<MyDefinitionId?>
            {
                null, ore, MyDefinitionId.Parse("MyObjectBuilder_Ingot/Iron"), plate,
                Find(d => d.Id.TypeId == typeof(MyObjectBuilder_Component) && d.Size.AbsMax() > 0.25f),
                Find(d => d.Id.TypeId == typeof(MyObjectBuilder_PhysicalGunObject)),
            }.Distinct().ToList();
            Note("items: " + string.Join(", ", items.Select(ItemName)));

            // 1. the world: the biggest networks, every inventory to every inventory
            var groups = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && g.Physics != null)
                .Select(g => MyCubeGridGroups.Static.Logical.GetGroup(g)).Where(g => g != null).Distinct()
                .Select(g => g.Nodes.SelectMany(n => n.NodeData.GetFatBlocks()).Where(b => b.HasInventory && b is IMyConveyorEndpointBlock && b.IsFunctional).ToList())
                .OrderByDescending(l => l.Count).Take(Networks).ToList();
            var searches0 = Counter("Searches");
            var answered0 = Counter("Answered");
            long pairs = 0, reachable = 0;
            double patchedMs = 0, vanillaMs = 0;
            foreach (var blocks in groups)
            {
                var inventories = blocks.Take(MaxInventories).ToList();
                foreach (var item in items)
                {
                    foreach (var to in inventories)
                    {
                        var answers = new bool[inventories.Count];
                        var watch = Stopwatch.StartNew();
                        for (var i = 0; i < inventories.Count; i++) answers[i] = Patched(inventories[i].GetInventory(0), to.GetInventory(0), item);
                        patchedMs += watch.Elapsed.TotalMilliseconds;
                        watch.Restart();
                        for (var i = 0; i < inventories.Count; i++)
                        {
                            var vanilla = Vanilla(inventories[i], to, item);
                            if (vanilla != answers[i] && _wrong.Count < 40)
                                _wrong.Add($"world: {inventories[i].DisplayNameText} ({inventories[i].CubeGrid.DisplayName}) -> {to.DisplayNameText} ({to.CubeGrid.DisplayName}), {ItemName(item)}: {answers[i]}, vanilla {vanilla}");
                            if (vanilla) reachable++;
                        }
                        vanillaMs += watch.Elapsed.TotalMilliseconds;
                        pairs += inventories.Count;
                    }
                    yield return null;
                }
            }
            Note($"CONVEYOR REACH | world: {groups.Count} networks ({string.Join(", ", groups.Select(g => g.Count))} inventories), {pairs} questions, " +
                 $"reachable {reachable}, not {pairs - reachable}; differing from vanilla {_wrong.Count}; " +
                 $"{patchedMs:0} ms answered, vanilla {vanillaMs:0} ms; searches {Counter("Searches") - searches0} for {Counter("Answered") - answered0} answers");
            var worldWrong = _wrong.Count;
            Check(groups.Count > 0 && pairs > 0, "no conveyor network with inventories in the world");

            // the switch on the settings page, at once: off - vanilla answers, no search of SO's; on again - SO's, the same answers
            {
                var sample = groups[0].Take(40).ToList();
                bool[] AskSample()
                {
                    var got = new List<bool>();
                    foreach (var to in sample.Take(5))
                    foreach (var from in sample)
                        got.Add(Patched(from.GetInventory(0), to.GetInventory(0), ore));
                    return got.ToArray();
                }
                var before = AskSample();
                _config.Set("ConveyorReach", false);
                var searchesOff = Counter("Searches");
                var off = AskSample();
                var searchedOff = Counter("Searches") - searchesOff;
                _config.Set("ConveyorReach", true);
                var searchesOn = Counter("Searches");
                var on = AskSample();
                var searchedOn = Counter("Searches") - searchesOn;
                var vanilla = new List<bool>();
                foreach (var to in sample.Take(5))
                foreach (var from in sample)
                    vanilla.Add(Vanilla(from, to, ore));
                Note($"CONVEYOR REACH | switch: off - {searchedOff} searches of SO's for {off.Length} questions; on again - {searchedOn}; " +
                     $"answers the same as vanilla off {off.SequenceEqual(vanilla)}, on {on.SequenceEqual(vanilla)}, before {before.SequenceEqual(vanilla)}");
                if (searchedOff != 0) _wrong.Add($"switch: off, yet {searchedOff} searches of SO's");
                if (searchedOn == 0) _wrong.Add("switch: on again, yet no search of SO's");
                if (!off.SequenceEqual(vanilla) || !on.SequenceEqual(vanilla)) _wrong.Add("switch: answers differ from vanilla");
                worldWrong = _wrong.Count;
            }

            // 2. the test base
            var owner = WorldApi.TestIdentityId();
            var origin = TestRunner.RunOrigin ?? new Vector3D(-40000, 25000, 70000);
            var specs = new List<BlockSpec>
            {
                new BlockSpec("LargeBlockSmallContainer", new Vector3I(0, 0, 0)),          // A
                new BlockSpec("LargeBlockConveyor", new Vector3I(1, 0, 0)),
                new BlockSpec("LargeBlockConveyorSorter", new Vector3I(2, 0, 0)),       // turned below so that its ports face A and B
                new BlockSpec("LargeBlockConveyor", new Vector3I(3, 0, 0)),
                new BlockSpec("LargeBlockSmallContainer", new Vector3I(4, 0, 0)),          // B
                new BlockSpec("LargeBlockSmallContainer", new Vector3I(1, 1, 0)),          // D, A's side
                new BlockSpec("LargeBlockSmallContainer", new Vector3I(3, 1, 0)),          // C, B's side
                new BlockSpec(WorldApi.FindSubtype(MyCubeSize.Large, "batteryblock"), new Vector3I(2, -1, 0)),
            };
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + "conveyor-reach", MyCubeSize.Large, true, origin, specs);
            foreach (var block in ob.CubeBlocks) block.Owner = owner;
            // the sorter's ports (its model's conveyor dummies) along the chain: the axis they lie on in the block, turned onto X
            var sorterDefinition = MyDefinitionManager.Static.GetCubeBlockDefinition(MyDefinitionId.Parse("MyObjectBuilder_ConveyorSorter/LargeBlockConveyorSorter"));
            var ports = VRage.Game.Models.MyModels.GetModelOnlyDummies(sorterDefinition.Model).Dummies
                .Where(p => p.Key.ToLowerInvariant().Contains("conveyor")).Select(p => p.Value.Matrix.Translation).ToList();
            var axis = ports.Count == 0 ? Vector3.Right : Vector3.Normalize(ports.Aggregate(Vector3.Zero, (sum, t) => sum + Vector3.Abs(t)));
            var sorterOb = ob.CubeBlocks.First(x => x.SubtypeName == "LargeBlockConveyorSorter");
            sorterOb.BlockOrientation = axis.X >= axis.Y && axis.X >= axis.Z ? new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up)
                : axis.Z >= axis.Y ? new SerializableBlockOrientation(Base6Directions.Direction.Left, Base6Directions.Direction.Up)
                : new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Left);
            Note($"the sorter's ports: {ports.Count}, along {axis:0.0}");
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            yield return WaitForTicks(30);
            MyCubeBlock At(int x, int y) => grid.GetCubeBlock(new Vector3I(x, y, 0))?.FatBlock;
            var a = At(0, 0);
            var b = At(4, 0);
            var d = At(1, 1);
            var c = At(3, 1);
            var sorter = (MyConveyorSorter)At(2, 0);
            Check(a != null && b != null && c != null && d != null && sorter != null, "the test base is not whole");
            ((IMyTerminalBlock)a).CustomName = "A";
            ((IMyTerminalBlock)b).CustomName = "B";
            ((IMyTerminalBlock)c).CustomName = "C";
            ((IMyTerminalBlock)d).CustomName = "D";
            WorldApi.ChargeBatteries(grid);
            var battery = grid.GetFatBlocks().OfType<Sandbox.Game.Entities.MyBatteryBlock>().First();
            ((IMyConveyorSorter)sorter).SetFilter(MyConveyorSorterMode.Blacklist, new List<MyInventoryItemFilter>());
            yield return WaitForTicks(120);   // the conveyor network worked out
            // the base link by link, as vanilla sees it
            string Link(int x1, int y1, int x2, int y2) =>
                $"({x1},{y1})-({x2},{y2}) {(MyGridConveyorSystem.Reachable(Endpoint(At(x1, y1)).ConveyorEndpoint, Endpoint(At(x2, y2)).ConveyorEndpoint) ? "+" : "-")}";
            Note("CONVEYOR REACH | test base links: " + string.Join(", ", Link(0, 0, 1, 0), Link(1, 0, 2, 0), Link(2, 0, 3, 0), Link(3, 0, 4, 0), Link(1, 1, 1, 0), Link(3, 1, 3, 0), Link(2, 0, 1, 0), Link(3, 0, 2, 0), Link(0, 0, 4, 0), Link(4, 0, 0, 0)) +
                 $"; sorter working {sorter.IsWorking}, powered {sorter.ResourceSink?.IsPoweredByType(MyResourceDistributorComponent.ElectricityId)}, " +
                 $"orientation {sorter.Orientation.Forward}/{sorter.Orientation.Up}; its lines: " +
                 string.Join(", ", Enumerable.Range(0, sorter.ConveyorEndpoint.GetLineCount()).Select(n =>
                 {
                     var line = sorter.ConveyorEndpoint.GetConveyorLine(n);
                     return line == null ? "none" : $"{(line.IsWorking ? "working" : "not working")} {line.GetEndpoint(0)?.CubeBlock?.Position}-{line.GetEndpoint(1)?.CubeBlock?.Position} {line.Type} " +
                                                    $"{typeof(MyConveyorLine).GetField("m_conductivity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(line)}";
                 })));

            var all = new[] { a, b, c, d };
            // across the sorter (A, D <-> B, C), or not
            bool Across(MyCubeBlock x, MyCubeBlock y) => (x == a || x == d) != (y == a || y == d);
            // the sorter is one way (its two lines conduct FORWARD): from B's side to A's only
            bool Way(MyCubeBlock x, MyCubeBlock y) => !Across(x, y) || x == b || x == c;

            void AskAll(string when, Func<MyCubeBlock, MyCubeBlock, MyDefinitionId?, bool?> expected)
            {
                foreach (var item in new MyDefinitionId?[] { null, ore, plate })
                foreach (var x in all)
                foreach (var y in all)
                    if (x != y) Ask(when, x, y, item, expected(x, y, item));
            }

            // a change asked about in its frame (after the same questions were answered before it, in that frame) and in
            // the next frames
            IEnumerator Step(string what, Action change, Func<MyCubeBlock, MyCubeBlock, MyDefinitionId?, bool?> expected, int settle = 0)
            {
                AskAll(what + ", before", (x, y, i) => null);
                change();
                if (settle == 0) AskAll(what + ", same frame", expected);
                else AskAll(what + ", same frame", (x, y, i) => null);
                yield return WaitForTicks(1);
                if (settle == 0) AskAll(what + ", next frame", expected);
                yield return WaitForTicks(Math.Max(settle, 1));
                AskAll(what + ", settled", expected);
            }

            var wrongBefore = _wrong.Count;
            yield return Step("all through", () => { }, (x, y, i) => Way(x, y));
            yield return Step("sorter: only iron ore", () =>
                ((IMyConveyorSorter)sorter).SetFilter(MyConveyorSorterMode.Whitelist, new List<MyInventoryItemFilter> { new MyInventoryItemFilter(ore) }),
                (x, y, i) => !Across(x, y) || Way(x, y) && (i == null || i == ore));      // with no item a sorter is not asked
            yield return Step("sorter: all but steel plates", () =>
                ((IMyConveyorSorter)sorter).SetFilter(MyConveyorSorterMode.Blacklist, new List<MyInventoryItemFilter> { new MyInventoryItemFilter(plate) }),
                (x, y, i) => !Across(x, y) || Way(x, y) && i != plate);
            yield return Step("sorter off", () => ((IMyFunctionalBlock)sorter).Enabled = false,
                (x, y, i) => !Across(x, y) || Way(x, y) && i == null);      // with no item a sorter is not asked
            yield return Step("sorter on", () => ((IMyFunctionalBlock)sorter).Enabled = true, (x, y, i) => !Across(x, y) || Way(x, y) && i != plate);
            // (no power: the sorter passes nothing; whether the conveyors carry anything unpowered is vanilla's to say -
            // compared with vanilla only)
            yield return Step("battery off", () => ((IMyFunctionalBlock)battery).Enabled = false, (x, y, i) => null, settle: 120);
            yield return Step("battery on", () => ((IMyFunctionalBlock)battery).Enabled = true, (x, y, i) => !Across(x, y) || Way(x, y) && i != plate, settle: 120);
            yield return Step("sorter: all through", () =>
                ((IMyConveyorSorter)sorter).SetFilter(MyConveyorSorterMode.Blacklist, new List<MyInventoryItemFilter>()), (x, y, i) => Way(x, y));

            var stranger = MySession.Static.Players.GetAllIdentities().FirstOrDefault(i => i.DisplayName == "ST conveyor stranger")
                           ?? MySession.Static.Players.CreateNewIdentity("ST conveyor stranger");
            // B a stranger's: an enemy's block passes nobody's item, and a stranger's item passes no block of ours
            yield return Step("B a stranger's", () => b.ChangeOwner(stranger.IdentityId, MyOwnershipShareModeEnum.None),
                (x, y, i) => x != b && y != b && Way(x, y));
            yield return Step("B shared with all", () => b.ChangeOwner(stranger.IdentityId, MyOwnershipShareModeEnum.All),
                (x, y, i) => x != b && Way(x, y));      // ours into B; the stranger's B into none of ours
            yield return Step("B ours again", () => b.ChangeOwner(owner, MyOwnershipShareModeEnum.None), (x, y, i) => Way(x, y));
            yield return Step("conveyor at A ground off", () => grid.RazeBlock(new Vector3I(1, 0, 0)),
                (x, y, i) => (x == b || x == c) && (y == b || y == c));
            Note($"CONVEYOR REACH | test base: {_wrong.Count - wrongBefore} wrong answers");

            // 3. a locked connector of the world
            var wrongBase = _wrong.Count;
            var connector = MyEntities.GetEntities().OfType<MyCubeGrid>().SelectMany(g => g.GetFatBlocks().OfType<MyShipConnector>())
                .FirstOrDefault(x => x.Connected && x.Other != null && x.HasInventory &&
                                     x.CubeGrid.GetFatBlocks().Any(f => f.HasInventory && f is IMyConveyorEndpointBlock && f != x) &&
                                     x.Other.CubeGrid.GetFatBlocks().Any(f => f.HasInventory && f is IMyConveyorEndpointBlock && f != x.Other));
            if (connector == null) Note("CONVEYOR REACH | connector: no locked connector between grids with inventories in the world - not asked");
            else
            {
                var other = connector.Other;
                var here = connector.CubeGrid.GetFatBlocks().Where(f => f.HasInventory && f is IMyConveyorEndpointBlock && f.IsFunctional).Take(15).ToList();
                var there = other.CubeGrid.GetFatBlocks().Where(f => f.HasInventory && f is IMyConveyorEndpointBlock && f.IsFunctional).Take(15).ToList();
                var asked = here.SelectMany(x => there.Select(y => (x, y))).Concat(there.SelectMany(x => here.Select(y => (x, y)))).ToList();
                var linked = asked.Count(p => Vanilla(p.x, p.y, ore));
                void AskConnector(string when)
                {
                    foreach (var (x, y) in asked)
                    foreach (var item in new MyDefinitionId?[] { null, ore })
                        Ask(when, x, y, item);
                }
                IEnumerator Toggle(string what, Action change)
                {
                    AskConnector(what + ", before");
                    change();
                    AskConnector(what + ", same frame");
                    yield return WaitForTicks(1);
                    AskConnector(what + ", next frame");
                    yield return WaitForTicks(120);
                    AskConnector(what + ", settled");
                }
                var lockedLinks = linked;
                yield return Toggle("connector unlocked", () => ((IMyShipConnector)connector).Disconnect());
                var unlockedLinks = asked.Count(p => Vanilla(p.x, p.y, ore));
                yield return Toggle("connector locked again", () => ((IMyShipConnector)connector).Connect());
                yield return WaitForTicks(60);
                var relocked = connector.Connected;
                yield return Toggle("connector trading", () => connector.TradingEnabled.Value = true);
                var tradingLinks = asked.Count(p => Vanilla(p.x, p.y, ore));
                yield return Toggle("connector not trading", () => connector.TradingEnabled.Value = false);
                Note($"CONVEYOR REACH | connector '{connector.DisplayNameText}' of {connector.CubeGrid.DisplayName} to {other.CubeGrid.DisplayName}: {asked.Count} pairs; " +
                     $"reachable (iron ore) locked {lockedLinks}, unlocked {unlockedLinks}, trading {tradingLinks}; locked again {relocked}; wrong answers {_wrong.Count - wrongBase}");
                Check(relocked, "the connector did not lock again");
                if (lockedLinks > 0 && unlockedLinks >= lockedLinks) _wrong.Add("connector: unlocking it took no path away");
            }

            // by step: how many wrong, and the first of each
            var bySteps = _wrong.GroupBy(w => w.Substring(0, Math.Max(0, w.IndexOf(':')))).Select(g => $"[{g.Count()}] {g.First()}");
            Note($"CONVEYOR REACH | {_asked} questions on the test base and the connector; wrong {_wrong.Count - worldWrong}" +
                 (_wrong.Count == 0 ? "" : ": " + string.Join(" | ", bySteps.Take(40))));
            Check(_wrong.Count == 0, $"{_wrong.Count} answers wrong");
        }

        public override void Cleanup()
        {
            try { _config.Restore(); }
            finally { base.Cleanup(); }
        }
    }
}
