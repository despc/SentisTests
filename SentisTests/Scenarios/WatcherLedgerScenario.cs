using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisWatcher's inventory ledger books where every item came from, and tells the honest from the rest.
    /// A player owns two containers, two assemblers and a refinery. Honest: ore moved between the containers,
    /// a steel plate assembled from its ingots, ingots refined from ore - no alert. Not honest:
    ///
    ///  - an assembler finishing a steel plate with no ingots in it (production_without_input);
    ///  - an assembler taking apart a steel plate it does not have (production_without_input, disassemble);
    ///  - a refinery making ingots with no ore (production_without_input, refine);
    ///  - a container's stack edited in place, past the game's methods (bypass);
    ///  - items put in by this plugin (the ledger names it as the source: plugin:SentisTests).
    ///
    /// The ledger of the player then explains each change by its source.
    /// </summary>
    public sealed class WatcherLedgerScenario : TestScenario
    {
        public const string ScenarioName = "watcher_ledger";
        private const string Prefix = "ledger-";
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 150;

        private static readonly MyDefinitionId IronOre = new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Iron");
        private static readonly MyDefinitionId IronIngot = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Iron");
        private static readonly MyDefinitionId SteelPlate = new MyDefinitionId(typeof(MyObjectBuilder_Component), "SteelPlate");

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisWatcher");
            Check(assembly != null, "SentisWatcher is not loaded");
            var pluginType = assembly.GetType("SentisWatcher.SentisWatcherPlugin");
            var plugin = pluginType.GetProperty("Instance").GetValue(null);
            var store = pluginType.GetProperty("Store").GetValue(plugin);
            var sweep = pluginType.GetProperty("Sweep").GetValue(plugin);
            Check(store != null, "SentisWatcher records nothing (disabled?)");
            var ledgerType = assembly.GetType("SentisWatcher.Ledger.InventoryLedger");
            Check(ledgerType.GetField("Current").GetValue(null) != null, "the inventory ledger is off (its hooks are missing?)");
            var data = Activator.CreateInstance(assembly.GetType("SentisWatcher.Storage.WebData"), store);
            var clock = assembly.GetType("SentisWatcher.Storage.Clock");
            long Now() => (long)clock.GetProperty("Now").GetValue(null);
            var from = Now() - 1000;

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var centre = anchorM.Translation + up * 1000 - side * 3500;

            FakeClients.RemoveAll();
            FakeClients.Add(1, Network, p => (centre + up * 10, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            var character = FakeClients.Character(0);
            Check(character != null, "the player has no character");
            var identity = character.GetPlayerIdentityId();

            // ------------------------------------------------------------- the player's blocks
            var store1 = Spawn("store", centre, up, side, identity, "LargeBlockSmallContainer", "LargeBlockArmorBlock", "LargeBlockSmallContainer");
            var containers = store1.GetFatBlocks().Where(b => b.BlockDefinition.Id.SubtypeName == "LargeBlockSmallContainer").OrderBy(b => b.Position.X).ToList();
            var a = (MyInventory)containers[0].GetInventory(0);
            var b = (MyInventory)containers[1].GetInventory(0);
            var honest = (MyAssembler)Spawn("assembler-1", centre + side * 30, up, side, identity, "LargeAssembler").GetFatBlocks().First();
            var dishonest = (MyAssembler)Spawn("assembler-2", centre + side * 60, up, side, identity, "LargeAssembler").GetFatBlocks().First();
            var refinery = (MyRefinery)Spawn("refinery", centre + side * 100, up, side, identity, "LargeRefinery").GetFatBlocks().First();
            var settle = WaitForSeconds(2, "the grids settle");
            while (settle.MoveNext()) yield return settle.Current;

            // let the sweep see every inventory once, empty: from here on each change must be explained
            var pass = Pass(sweep);
            while (pass.MoveNext()) yield return pass.Current;

            // ------------------------------------------------------------- honest
            a.AddItems(1000, new MyObjectBuilder_Ore { SubtypeName = "Iron" });                     // from this plugin
            MyInventory.Transfer(a, b, IronOre, MyItemFlags.None, 100);
            Check(b.GetItemAmount(IronOre) == 100, "the transfer did not move 100 ore");

            var plate = MyDefinitionManager.Static.GetBlueprintDefinition(new MyDefinitionId(typeof(MyObjectBuilder_BlueprintDefinition), "SteelPlate"));
            Check(plate != null, "no steel plate blueprint");
            ((MyInventory)honest.InputInventory).AddItems(1000, new MyObjectBuilder_Ingot { SubtypeName = "Iron" });
            Invoke(honest, "FinishAssembling", plate);
            Check(honest.OutputInventory.GetItemAmount(SteelPlate) == 1, "the honest assembler made no steel plate");

            var ironBlueprint = MyDefinitionManager.Static.GetBlueprintDefinitions()
                .FirstOrDefault(d => d.Prerequisites.Length == 1 && d.Prerequisites[0].Id == IronOre && d.Results.Any(r => r.Id == IronIngot));
            Check(ironBlueprint != null, "no iron ore blueprint");
            ((MyInventory)refinery.InputInventory).AddItems(100, new MyObjectBuilder_Ore { SubtypeName = "Iron" });
            Invoke(refinery, "ChangeRequirementsToResults", ironBlueprint, (MyFixedPoint)10);
            Note($"honest refinery: ore left {refinery.InputInventory.GetItemAmount(IronOre)}, ingots {refinery.OutputInventory.GetItemAmount(IronIngot)}");
            Check(refinery.InputInventory.GetItemAmount(IronOre) == 90, "the honest refinery did not take 10 ore");

            // ------------------------------------------------------------- not honest
            Invoke(dishonest, "FinishAssembling", plate);                // no ingots in it
            Check(dishonest.OutputInventory.GetItemAmount(SteelPlate) == 1, "the game did not make the plate from nothing (did it change?)");
            Invoke(dishonest, "FinishDisassembling", plate);             // takes the plate back apart: honest
            Invoke(dishonest, "FinishDisassembling", plate);             // no plate left to take apart
            Note($"dishonest assembler: ingots {dishonest.InputInventory.GetItemAmount(IronIngot)}, plates {dishonest.OutputInventory.GetItemAmount(SteelPlate)}");

            ((MyInventory)refinery.InputInventory).RemoveItemsOfType(refinery.InputInventory.GetItemAmount(IronOre), IronOre);
            Invoke(refinery, "ChangeRequirementsToResults", ironBlueprint, (MyFixedPoint)10);      // no ore

            // a stack edited in place: what a broken mod or plugin could do with reflection
            var items = b.GetItems();
            var stack = items[0];
            stack.Amount += 500;
            items[0] = stack;
            Note("container b holds " + b.GetItemAmount(IronOre) + " ore after the edit");

            pass = Pass(sweep);
            while (pass.MoveNext()) yield return pass.Current;
            var flush = WaitForSeconds(3, "the records are written");
            while (flush.MoveNext()) yield return flush.Current;

            // ------------------------------------------------------------- read back
            var to = Now() + 1000;
            var anomalies = (Dictionary<string, object>)data.GetType().GetMethod("Anomalies").Invoke(data, new object[] { from, to });
            var alerts = ((List<Dictionary<string, object>>)anomalies["alerts"]).Where(x => x["actor"] as string == identity.ToString()).ToList();
            foreach (var alert in alerts) Note($"alert {alert["kind"]}: {alert["detail"]}");
            bool Has(string kind, string text) => alerts.Any(x => (string)x["kind"] == kind && ((string)x["detail"] ?? "").Contains(text));

            Check(Has("production_without_input", "assemble SteelPlate"), "the plate assembled from nothing raised no alert");
            Check(Has("production_without_input", "disassemble SteelPlate"), "the plate taken apart from nothing raised no alert");
            Check(Has("production_without_input", "refine"), "the ingots refined from nothing raised no alert");
            Check(Has("bypass", containers[1].DisplayNameText), "the stack edited in place raised no bypass alert");
            Check(!alerts.Any(x => (string)x["kind"] == "bypass" && !((string)x["detail"]).Contains(containers[1].DisplayNameText)),
                "an honest inventory raised a bypass alert");
            Check(!alerts.Any(x => (string)x["kind"] == "dupe_transfer"), "an honest transfer raised a dupe alert");
            Check(!alerts.Any(x => (string)x["kind"] == "unknown_source"), "an item came from a path the ledger does not know");
            Check(alerts.Count(x => (string)x["kind"] == "production_without_input") == 3, "honest production raised an alert");

            var ledger = (Dictionary<string, object>)data.GetType().GetMethod("Ledger").Invoke(data, new object[] { "player", identity, from, to });
            var sources = (Dictionary<string, Dictionary<string, double>>)ledger["sources"];
            foreach (var item in sources) Note(item.Key + ": " + string.Join(", ", item.Value.Select(p => p.Key + " " + p.Value)));
            double Source(string item, string kind) => sources.TryGetValue(item, out var byKind) && byKind.TryGetValue(kind, out var v) ? v : 0;
            Check(Source("Ore/Iron", "plugin:SentisTests") >= 1100, "the ore put in by this plugin is not booked to it");
            Check(Source("Ore/Iron", "unexplained") == 500, "the edited stack is not booked as unexplained");
            Check(Source("Component/SteelPlate", "assemble") == 2, "the two steel plates made are not booked to assembly");
            Check(Source("Component/SteelPlate", "disassemble") == -1, "the plate taken apart is not booked to disassembly");
            Check(Source("Ingot/Iron", "assemble") < 0, "the ingots the plate took are not booked to assembly");
            Check(Source("Ingot/Iron", "refine") > 0, "the refined ingots are not booked to the refinery");
            Check(Source("Ore/Iron", "refine") == -10, "the refinery's ore is not booked to it");
            Check(Source("Ore/Iron", "move") == 0, "a move within the grid does not cancel out");
        }

        private static void Invoke(object target, string method, params object[] args)
        {
            var info = target.GetType().GetMethods(Any).Concat(target.GetType().BaseType.GetMethods(Any)).First(m => m.Name == method);
            info.Invoke(target, args);
        }

        private IEnumerator Pass(object sweep)
        {
            sweep.GetType().GetMethod("StartNow").Invoke(sweep, null);
            yield return null;
            yield return null;
            var busy = sweep.GetType().GetProperty("Busy");
            var swept = Wait(() => !(bool)busy.GetValue(sweep), "the inventory pass ends", 30);
            while (swept.MoveNext()) yield return swept.Current;
        }

        private MyCubeGrid Spawn(string name, Vector3D at, Vector3D up, Vector3D side, long owner, params string[] subtypes)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < subtypes.Length; x++)
            {
                var block = WorldApi.MakeBlockOb(subtypes[x]);
                block.Min = new SerializableVector3I(x, 0, 0);
                block.Owner = owner;
                block.BuiltBy = owner;
                blocks.Add(block);
            }
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, side, up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
