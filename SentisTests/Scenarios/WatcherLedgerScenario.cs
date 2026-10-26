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
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisWatcher's inventory ledger books where every item came from, and tells the honest from the rest.
    /// A player owns two containers, two assemblers and a refinery. Honest: ore moved between the containers,
    /// a steel plate assembled from its ingots, ingots refined from ore, the bags a dead character and a destroyed
    /// container leave, a suit refilled from a bottle (an item changed in place), algae out of an algae farm (the
    /// source "farm"), and SentisOptimisations taking apart for a frozen assembler no more plates than it holds
    /// (it gave the ingots of six for three), a prototech gyroscope ground down to prototech scrap, a ship fixed by
    /// ALE ShipFixer (its blocks made again under new ids with what they held) - no alert. Not honest:
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
        public override int TimeoutSeconds => 240;

        private static readonly MyDefinitionId IronOre = new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Iron");
        private static readonly MyDefinitionId IronIngot = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Iron");
        private static readonly MyDefinitionId SteelPlate = new MyDefinitionId(typeof(MyObjectBuilder_Component), "SteelPlate");

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisWatcher");
            SkipUnless(assembly != null, "SentisWatcher is not loaded");
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

            var anchorM = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + Prefix + "anchor");
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
            var farm = Spawn("farm", centre + side * 160, up, side, identity, "LargeBlockAlgaeFarm").GetFatBlocks().First();
            var third = (MyAssembler)Spawn("assembler-3", centre + side * 190, up, side, identity, "LargeAssembler").GetFatBlocks().First();
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

            // an algae farm makes algae out of light: the game's own source, "farm" (it was "unknown:MyItemProducerComponent.Produce")
            var producerType = typeof(SpaceEngineers.Game.Entities.Blocks.MyGravityGenerator).Assembly.GetType("SpaceEngineers.Game.EntityComponents.Blocks.MyItemProducerComponent");
            Check(producerType != null, "the game has no MyItemProducerComponent");
            Check(farm.Components.TryGet(producerType, out var producer) && producer != null, "the algae farm has no item producer component");
            var algae = new MyDefinitionId(typeof(MyObjectBuilder_PhysicalObject), "Algae");
            producer.GetType().GetMethod("Produce").Invoke(producer, new object[] { algae, (MyFixedPoint)2 });
            Check(farm.GetInventory(0).GetItemAmount(algae) == 2, "the algae farm made no algae");

            // SentisOptimisations makes up for the time an assembler was frozen with batches of its own: six plates
            // queued to take apart and three held are three taken apart (it gave the ingots of six)
            var freezer = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == "SentisOptimisations")
                ?.GetType("SentisOptimisationsPlugin.Freezer.FreezerPatches");
            SkipUnless(freezer != null, "SentisOptimisations is not loaded");
            ((MyInventory)third.OutputInventory).AddItems(3, new MyObjectBuilder_Component { SubtypeName = "SteelPlate" });
            var perPlate = (double)(plate.Prerequisites[0].Amount * (MyFixedPoint)(1f / third.GetEfficiencyMultiplierForBlueprint(plate)));
            freezer.GetMethod("FinishDisassembling", Any).Invoke(null, new object[] { plate, 6, third, false });
            Note($"frozen assembler: plates left {third.OutputInventory.GetItemAmount(SteelPlate)}, ingots {third.InputInventory.GetItemAmount(IronIngot)} ({perPlate} a plate)");
            Check(third.OutputInventory.GetItemAmount(SteelPlate) == 0, "the three plates were not taken apart");
            Check(Math.Abs((double)third.InputInventory.GetItemAmount(IronIngot) - 3 * perPlate) < 0.001, "three plates did not give the ingots of three");

            // a prototech block ground down: its prototech components come out as Ingot/PrototechScrap, one for one
            // (DeconstructId in the block's definition) - not more than it is built of (it was "grind_excess")
            var proto = Spawn("prototech", centre + side * 220, up, side, identity, "LargeBlockPrototechGyro", "LargeBlockLargeContainer");
            var gyro = proto.GetCubeBlock(Vector3I.Zero);
            var scrapBin = (MyInventory)proto.GetFatBlocks().First(x => x.BlockDefinition.Id.SubtypeName == "LargeBlockLargeContainer").GetInventory(0);
            // as a grinder does: a cut into the block's stockpile, then the stockpile into the grinder's inventory
            for (var cut = 0; cut < 10000 && !gyro.IsFullyDismounted; cut++)
            {
                gyro.DecreaseMountLevel(gyro.MaxIntegrity * 0.0005f, scrapBin, false, identity);
                gyro.MoveItemsFromConstructionStockpile(scrapBin);
            }
            gyro.SpawnConstructionStockpile();
            proto.RazeBlock(gyro.Min);
            var prototechScrap = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "PrototechScrap");
            Note($"prototech gyroscope ground: {scrapBin.GetItemAmount(prototechScrap)} prototech scrap; the container holds {string.Join(", ", scrapBin.GetItems().Select(x => x.Content.SubtypeName + " " + x.Amount))}");
            Check(scrapBin.GetItemAmount(prototechScrap) > 0, "the prototech gyroscope gave no prototech scrap");

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

            // ------------------------------------------------------------- honest: a ship fixed
            // ALE ShipFixer, the server's fixship, closes the grid and makes it again from its builders on worker
            // threads, no plugin on the stack: the blocks come back under new ids, their inventories loaded from what
            // they held. It refuses a grid with a cockpit under control, but a pilot who controls a searchlight from
            // the cockpit leaves it not under control: the pilot in the cockpit's builder is loaded again too. On the
            // server that was "unknown_source" ("load:unknown:MyComponentBase.SetContainer gave ...", 18.10 22:56,
            // "USS Footameida", its pilot on a searchlight); no more than they held before - no alert. The pilot is
            // a second player, the ship its own (what the fixer does to it is noted), the first stays near (a grid nobody is near is
            // frozen, and the fixer finds no grid without a body)
            var seatName = WorldApi.EntityPrefix + Prefix + "seat";
            FakeClients.Add(1, Network, p => (centre + side * 20 + up * 5, 0, 0), withCharacters: true);
            var second = WaitForSeconds(5, "the pilot arrives");
            while (second.MoveNext()) yield return second.Current;
            var pilot = FakeClients.Character(1);
            Check(pilot != null, "the pilot has no character");
            var pilotIdentity = pilot.GetPlayerIdentityId();
            // the pilot's own ship (a cockpit takes no pilot it does not let in)
            var seatGrid = Spawn("seat", centre + side * 20, up, side, pilotIdentity, "LargeBlockCockpitSeat", "LargeSearchlight");
            var seat = seatGrid.GetFatBlocks().OfType<MyCockpit>().First();
            var searchlight = seatGrid.GetFatBlocks().OfType<SpaceEngineers.Game.Entities.Blocks.MySearchlight>().First();
            var stone = new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Stone");
            ((MyInventory)seat.GetInventory(0)).AddItems(5, new MyObjectBuilder_Ore { SubtypeName = "Stone" });    // from this plugin
            ((MyInventory)pilot.GetInventory(0)).AddItems(3, new MyObjectBuilder_Ore { SubtypeName = "Stone" });   // from this plugin
            ((Sandbox.ModAPI.IMyCockpit)seat).AttachPilot(pilot);
            for (var tick = 0; tick < 300 && seat.Pilot == null; tick++) yield return null;
            Note($"fixship: the pilot {pilot.EntityId} (dead {pilot.IsDead}, using {pilot.UsingEntity?.GetType().Name}, at {Vector3D.Distance(pilot.PositionComp.GetPosition(), seat.PositionComp.GetPosition()):0.#} m), the cockpit's pilot {seat.Pilot?.EntityId}, the client's character {FakeClients.Character(1)?.EntityId}");
            Check(seat.Pilot != null, "the pilot did not sit in the cockpit");
            pilot = seat.Pilot;
            var pilotPlayer = MySession.Static.Players.GetControllingPlayer(seat);
            Check(pilotPlayer != null, "no player controls the cockpit");
            pilotPlayer.Controller.TakeControl(searchlight);        // the pilot takes the searchlight over from the cockpit
            pass = Pass(sweep);
            while (pass.MoveNext()) yield return pass.Current;
            var fixerCore = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("ALE_ShipFixer.ShipFixerCore")).FirstOrDefault(x => x != null);
            SkipUnless(fixerCore != null, "ALE ShipFixer is not loaded");
            var fixer = fixerCore.GetField("Instance", Any)?.GetValue(null) ?? fixerCore.GetProperty("Instance", Any)?.GetValue(null);
            Check(fixer != null, "ALE ShipFixer has no instance");
            Note($"fixship: before, the cockpit under control {((Sandbox.ModAPI.IMyShipController)seat).IsUnderControl}, its pilot {seat.Pilot?.EntityId} with {pilot.GetInventory(0).GetItemAmount(stone)} stone");
            var fixResult = fixerCore.GetMethods().First(m => m.Name == "FixShip" && m.GetParameters().Length == 3)
                .Invoke(fixer, new object[] { pilotIdentity, seat.CubeGrid.EntityId, "nogrid" });             // the owner's own request
            Note($"fixship: {fixResult}");
            Check(fixResult.ToString() == "SHIP_FIXED", "ALE ShipFixer did not fix the ship");
            MyCockpit remade = null;
            for (var tick = 0; tick < 600 && remade == null; tick++)
            {
                remade = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName == seatName && !g.MarkedForClose && g.EntityId != seatGrid.EntityId)
                    .SelectMany(g => g.GetFatBlocks()).OfType<MyCockpit>().FirstOrDefault();
                if (remade == null) yield return null;
            }
            Check(remade != null, "the fixed ship did not come back");
            Track(remade.CubeGrid);
            var settled = WaitForSeconds(3, "the fixed ship settles");
            while (settled.MoveNext()) yield return settled.Current;
            var pilotNow = MyEntities.GetEntityById(pilot.EntityId) as Sandbox.Game.Entities.Character.MyCharacter;
            Note($"fixship: the cockpit {seat.EntityId} made again as {remade.EntityId}, {remade.GetInventory(0).GetItemAmount(stone)} stone in it; " +
                 $"its pilot {remade.Pilot?.EntityId}; the old pilot {pilotNow?.EntityId} (the same object {ReferenceEquals(pilotNow, pilot)}, dead {pilotNow?.IsDead}, stone {pilotNow?.GetInventory(0)?.GetItemAmount(stone)})");
            Check(remade.GetInventory(0).GetItemAmount(stone) == 5, "the fixed ship's cockpit did not keep its stone");
            pass = Pass(sweep);
            while (pass.MoveNext()) yield return pass.Current;

            // ------------------------------------------------------------- honest: bags
            // What comes into a bag came from what left it, past the ledger's hooks: no alert. Two kinds:
            //  - a dead character's backpack (MyInventoryBagEntity): a copy of the body's inventory;
            //  - a destroyed block's cargo bag (MyCargoContainerInventoryBagEntity, "Cargo Bag (N)", with
            //    TemporaryContainers on): the block's own inventory moved into it.
            var pocket = (MyInventory)character.GetInventory(0);
            pocket.AddItems(20, new MyObjectBuilder_Ingot { SubtypeName = "Iron" });                 // from this plugin

            // ------------------------------------------------------------- honest: the suit refills from a bottle
            // The game changes an item in place (a bottle's gas, a datapad's text) by taking it out and putting it
            // back (MyInventory.ModifyContent): nothing is made, no alert. The game itself must do it - a call
            // from here would be booked to this plugin - so the suit is left low on hydrogen with a full bottle.
            pocket.AddItems(1, new Sandbox.Common.ObjectBuilders.Definitions.MyObjectBuilder_GasContainerObject { SubtypeName = "HydrogenBottle", GasLevel = 1f });
            var hydrogen = Sandbox.Game.Entities.Character.Components.MyCharacterOxygenComponent.HydrogenId;
            character.OxygenComponent.UpdateStoredGasLevel(ref hydrogen, 0.05f);
            float BottleLevel() => pocket.GetItems().Select(x => x.Content).OfType<Sandbox.Common.ObjectBuilders.Definitions.MyObjectBuilder_GasContainerObject>()
                .Where(x => x.SubtypeName == "HydrogenBottle").Select(x => x.GasLevel).DefaultIfEmpty(-1f).First();
            for (var tick = 0; tick < 1200 && BottleLevel() >= 1f; tick++) yield return null;
            Note($"the bottle after the suit refilled: {BottleLevel():0.###}, the suit {character.OxygenComponent.GetGasFillLevel(hydrogen):0.###}");
            Check(BottleLevel() >= 0 && BottleLevel() < 1f, "the suit did not refill from the bottle");
            var loot = Spawn("loot", centre + side * 130, up, side, identity, "LargeBlockSmallContainer").GetFatBlocks().First();
            ((MyInventory)loot.GetInventory(0)).AddItems(30, new MyObjectBuilder_Ingot { SubtypeName = "Iron" });
            pass = Pass(sweep);
            while (pass.MoveNext()) yield return pass.Current;
            var before = new HashSet<long>(MyEntities.GetEntities().Where(IsBag).Select(x => x.EntityId));
            var beforeAll = new HashSet<long>(MyEntities.GetEntities().Select(x => x.EntityId));
            var diedAt = character.PositionComp.GetPosition();
            var suitStore = character.Components.Get<MyInventoryBase>();
            Note($"the character before its death: inventory {suitStore?.GetType().Name} with {(suitStore as MyInventory)?.GetItemsCount()} kinds, {suitStore?.GetItemAmount(IronIngot)} ingots; " +
                 $"its cluster {ClusterIdOf(character)}");
            MyEntity NewBag(int ingots) => MyEntities.GetEntities()
                .FirstOrDefault(x => IsBag(x) && !before.Contains(x.EntityId) && x.GetInventory(0)?.GetItemAmount(IronIngot) == ingots);

            var watched = new System.Collections.Concurrent.ConcurrentQueue<string>();
            Action<MyEntity> created = x =>
            {
                if (x is MyInventoryBagEntity)
                    watched.Enqueue($"bag created {x.EntityId} on thread '{System.Threading.Thread.CurrentThread.Name}' at {Vector3D.Distance(x.PositionComp.GetPosition(), diedAt):F0} m");
            };
            Action<MyEntity> removed = x =>
            {
                if (x is MyInventoryBagEntity)
                    watched.Enqueue($"bag removed {x.EntityId} at {Vector3D.Distance(x.PositionComp.GetPosition(), diedAt):F0} m from the death\n{Environment.StackTrace}");
            };
            MyEntities.OnEntityCreate += created;
            MyEntities.OnEntityRemove += removed;
            character.Kill(true, new VRage.Game.ModAPI.MyDamageInformation(false, 1000f, MyDamageType.Suicide, character.EntityId));
            var temporary = MySession.Static.Settings.TemporaryContainers;
            MySession.Static.Settings.TemporaryContainers = true;
            try { loot.ReleaseInventory((MyInventory)loot.GetInventory(0)); }
            finally { MySession.Static.Settings.TemporaryContainers = temporary; }
            MyEntity backpack = null, cargoBag = null;
            for (var tick = 0; tick < 600 && (backpack == null || cargoBag == null); tick++)
            {
                backpack = backpack ?? NewBag(20);
                cargoBag = cargoBag ?? NewBag(30);
                if (backpack == null || cargoBag == null) yield return null;
            }
            foreach (var x in MyEntities.GetEntities().Where(x => IsBag(x) && !before.Contains(x.EntityId)))
                Note($"new bag: {x.DisplayName} ({x.GetType().Name}), {x.GetInventory(0)?.GetItemAmount(IronIngot)} ingots");
            MyEntities.OnEntityCreate -= created;
            MyEntities.OnEntityRemove -= removed;
            while (watched.TryDequeue(out var line)) Note(line);
            if (backpack == null)
            {
                foreach (var x in MyEntities.GetEntities().Where(x => !beforeAll.Contains(x.EntityId) && (Vector3D.Distance(x.PositionComp.GetPosition(), diedAt) < 50 || IsBag(x))))
                    Note($"new near the death or a bag: {x.GetType().Name} '{x.DisplayName}' {x.EntityId} at {Vector3D.Distance(x.PositionComp.GetPosition(), diedAt):F0} m, closed {x.Closed}, inventory {x.GetInventory(0)?.GetItemAmount(IronIngot)}");
                var left = character.Components.Get<MyInventoryBase>();
                Note($"the dead character: dead {character.IsDead}, closed {character.Closed}, marked {character.MarkedForClose}, in scene {character.InScene}, " +
                     $"inventory {left?.GetType().Name} with {left?.GetItemAmount(IronIngot)} ingots, its cluster now {ClusterIdOf(character)}");
            }
            Check(character.IsDead, "the character did not die");
            Check(backpack != null, "the dead character left no bag with its ingots");
            Check(cargoBag is MyCargoContainerInventoryBagEntity, "the destroyed container left no cargo bag with its ingots");
            Track(backpack);
            Track(cargoBag);

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
            var pilotAlerts = ((List<Dictionary<string, object>>)anomalies["alerts"]).Where(x => x["actor"] as string == pilotIdentity.ToString()).ToList();
            foreach (var alert in pilotAlerts) Note($"pilot's alert {alert["kind"]}: {alert["detail"]}");
            Check(!pilotAlerts.Any(x => (string)x["kind"] == "unknown_source"), "the fixed ship's pilot got items from a path the ledger does not know");
            Check(!alerts.Any(x => (string)x["kind"] == "grind_excess"), "the prototech gyroscope ground down gave more than it is built of");
            Check(alerts.Count(x => (string)x["kind"] == "production_without_input") == 3, "honest production raised an alert");
            // a bag has no owner of its own: its alerts are found by the entity
            var bags = new[] { backpack.EntityId.ToString(), cargoBag.EntityId.ToString() };
            var bagAlerts = ((List<Dictionary<string, object>>)anomalies["alerts"]).Where(x => bags.Contains(x["entity"] as string)).ToList();
            foreach (var alert in bagAlerts) Note($"bag alert {alert["kind"]}: {alert["detail"]}");
            Check(bagAlerts.Count == 0, "a bag raised an alert");

            var ledger = (Dictionary<string, object>)data.GetType().GetMethod("Ledger").Invoke(data, new object[] { "player", identity, from, to });
            var sources = (Dictionary<string, Dictionary<string, double>>)ledger["sources"];
            foreach (var item in sources) Note(item.Key + ": " + string.Join(", ", item.Value.Select(p => p.Key + " " + p.Value)));
            double Source(string item, string kind) => sources.TryGetValue(item, out var byKind) && byKind.TryGetValue(kind, out var v) ? v : 0;
            Check(Source("Ore/Iron", "plugin:SentisTests") >= 1100, "the ore put in by this plugin is not booked to it");
            Check(Source("Ore/Iron", "unexplained") == 500, "the edited stack is not booked as unexplained");
            Check(Source("Component/SteelPlate", "assemble") == 2, "the two steel plates made are not booked to assembly");
            Check(Source("Component/SteelPlate", "disassemble") == -4, "the plates taken apart (one, and the frozen assembler's three) are not booked to disassembly");
            Check(Source("Ingot/Iron", "assemble") < 0, "the ingots the plate took are not booked to assembly");
            Check(Source("Ingot/Iron", "refine") > 0, "the refined ingots are not booked to the refinery");
            Check(Source("PhysicalObject/Algae", "farm") == 2, "the algae are not booked to the farm");
            Check(Source("Ore/Iron", "refine") == -10, "the refinery's ore is not booked to it");
            Check(Source("Ore/Iron", "move") == 0, "a move within the grid does not cancel out");
        }

        private static int ClusterIdOf(MyEntity entity)
        {
            var world = (entity?.Physics as Sandbox.Engine.Physics.MyPhysicsBody)?.HavokWorld;
            if (world == null) return -1;
            foreach (var c in Sandbox.Engine.Physics.MyPhysics.Clusters.GetClusters())
                if (ReferenceEquals(c.UserData, world)) return c.ClusterId;
            return -1;
        }

        private static bool IsBag(MyEntity entity) => entity is MyCargoContainerInventoryBagEntity || entity is MyInventoryBagEntity;

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
