using System;
using System.Collections;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Ingame;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI.Ingame;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// End-to-end vanilla production test. The fixture owns all initial state: ore is serialized in
    /// cargo, uranium fuel is serialized in the reactor, and the assembler queue is serialized in
    /// the grid blueprint. Runtime code only observes vanilla refinery/conveyor/assembler behavior.
    /// </summary>
    public sealed class ProductionScenario : TestScenario
    {
        public const string ScenarioName = "production";
        private const string ResourceName = "SentisTests.Resources.ProductionGrid.xml";

        private static readonly Dictionary<string, int> Wanted =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "SteelPlate", 5 },
                { "Motor", 5 },
                { "Computer", 5 },
            };

        public override string Name { get { return ScenarioName; } }
        public override int TimeoutSeconds { get { return 240; } }

        public override IEnumerator Run()
        {
            Note("loading production fixture");
            var ob = WorldApi.LoadGridTemplate(ResourceName, WorldApi.EntityPrefix + "production",
                new Vector3D(450, 0, 0), Vector3.Forward, Vector3.Up);
            ob.IsStatic = true;
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            WorldApi.EnsureDistributor(grid);

            yield return WaitForTicks(30);

            MySlimBlock cargo = null;
            MySlimBlock refinery = null;
            MySlimBlock assembler = null;
            MySlimBlock reactor = null;
            foreach (var slim in grid.GetBlocks())
            {
                var subtype = slim.BlockDefinition.Id.SubtypeName;
                if (subtype == "LargeBlockLargeContainer") cargo = slim;
                else if (subtype == "LargeRefinery") refinery = slim;
                else if (subtype == "LargeAssembler") assembler = slim;
                else if (subtype == "LargeBlockSmallGenerator") reactor = slim;
            }

            Check(cargo != null, "production fixture has no large cargo container");
            Check(refinery != null, "production fixture has no refinery");
            Check(assembler != null, "production fixture has no assembler");
            Check(reactor != null, "production fixture has no reactor");

            var cargoInv = Inventory(cargo, 0);
            var refineryIn = Inventory(refinery, 0);
            var refineryOut = Inventory(refinery, 1);
            var assemblerIn = Inventory(assembler, 0);
            var assemblerOut = Inventory(assembler, 1);
            var reactorInv = Inventory(reactor, 0);

            Check(cargoInv.IsConnectedTo(refineryIn), "cargo is not conveyor-connected to refinery input");
            Check(cargoInv.IsConnectedTo(assemblerIn), "cargo is not conveyor-connected to assembler input");
            Check(cargoInv.IsConnectedTo(reactorInv), "cargo is not conveyor-connected to reactor");

            var queue = new List<MyProductionItem>();
            var production = assembler.FatBlock as IMyProductionBlock;
            Check(production != null, "assembler does not expose IMyProductionBlock");
            production.GetQueue(queue);
            Check(queue.Count == Wanted.Count, "assembler queue must be serialized with exactly three entries; found " + queue.Count);
            var queuedComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in queue)
            {
                var blueprint = item.BlueprintId.SubtypeName;
                string component = blueprint == "MotorComponent" ? "Motor" :
                    blueprint == "ComputerComponent" ? "Computer" : blueprint;
                int wanted;
                Check(Wanted.TryGetValue(component, out wanted), "unexpected assembler queue blueprint " + blueprint);
                Check(queuedComponents.Add(component), "duplicate assembler queue blueprint " + blueprint);
                Check((int)item.Amount == wanted, blueprint + " queue amount is " + item.Amount + ", expected " + wanted);
            }
            Check(queuedComponents.Count == Wanted.Count, "assembler queue does not contain every required component");

            var initialIronOre = Count(cargoInv, "MyObjectBuilder_Ore", "Iron");
            var initialNickelOre = Count(cargoInv, "MyObjectBuilder_Ore", "Nickel");
            var initialSiliconOre = Count(cargoInv, "MyObjectBuilder_Ore", "Silicon");
            Check(initialIronOre >= 500, "cargo Iron ore is not serialized or too small: " + initialIronOre);
            Check(initialNickelOre >= 100, "cargo Nickel ore is not serialized or too small: " + initialNickelOre);
            Check(initialSiliconOre >= 50, "cargo Silicon ore is not serialized or too small: " + initialSiliconOre);
            Check(Count(reactorInv, "MyObjectBuilder_Ingot", "Uranium") > 0,
                "reactor has no serialized uranium fuel");
            Check(Count(grid, "MyObjectBuilder_Ingot", "Iron") == 0 &&
                  Count(grid, "MyObjectBuilder_Ingot", "Nickel") == 0 &&
                  Count(grid, "MyObjectBuilder_Ingot", "Silicon") == 0,
                "production fixture must start without processed Iron/Nickel/Silicon ingots");
            foreach (var wanted in Wanted)
                Check(Count(grid, "MyObjectBuilder_Component", wanted.Key) == 0,
                    wanted.Key + " must not be preloaded in the production fixture");

            Note("production started: ore Fe=" + initialIronOre + ", Ni=" + initialNickelOre +
                 ", Si=" + initialSiliconOre + "; queue SteelPlate=5, Motor=5, Computer=5");

            var started = DateTime.UtcNow;
            var lastLog = DateTime.MinValue;
            while (true)
            {
                var steel = Count(grid, "MyObjectBuilder_Component", "SteelPlate");
                var motors = Count(grid, "MyObjectBuilder_Component", "Motor");
                var computers = Count(grid, "MyObjectBuilder_Component", "Computer");
                var ironOre = Count(grid, "MyObjectBuilder_Ore", "Iron");
                var nickelOre = Count(grid, "MyObjectBuilder_Ore", "Nickel");
                var siliconOre = Count(grid, "MyObjectBuilder_Ore", "Silicon");

                if (steel >= 5 && motors >= 5 && computers >= 5)
                {
                    Check(ironOre < initialIronOre && nickelOre < initialNickelOre && siliconOre < initialSiliconOre,
                        "not all required ores were consumed by the refinery");
                    production.GetQueue(queue);
                    Check(queue.Count == 0, "assembler produced targets but queue is not empty: " + queue.Count);
                    Note("production verified: ore consumed Fe=" + (initialIronOre - ironOre) +
                         ", Ni=" + (initialNickelOre - nickelOre) + ", Si=" + (initialSiliconOre - siliconOre) +
                         "; components SteelPlate=" + steel + ", Motor=" + motors + ", Computer=" + computers);
                    yield break;
                }

                if ((DateTime.UtcNow - started).TotalSeconds > 180)
                    throw new ScenarioFailedException("production timed out: components SteelPlate=" + steel +
                        ", Motor=" + motors + ", Computer=" + computers + "; ore Fe=" + ironOre +
                        ", Ni=" + nickelOre + ", Si=" + siliconOre);

                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 10)
                {
                    Note("production progress: SteelPlate=" + steel + "/5, Motor=" + motors +
                         "/5, Computer=" + computers + "/5");
                    lastLog = DateTime.UtcNow;
                }
                yield return null;
            }
        }

        private static IMyInventory Inventory(MySlimBlock slim, int index)
        {
            var owner = slim.FatBlock as IMyInventoryOwner;
            Check(owner != null && owner.HasInventory, slim.BlockDefinition.Id.SubtypeName + " has no inventory");
            return owner.GetInventory(index);
        }

        private static int Count(MyCubeGrid grid, string typeId, string subtype)
        {
            var total = 0;
            foreach (var slim in grid.GetBlocks())
            {
                var owner = slim.FatBlock as IMyInventoryOwner;
                if (owner == null || !owner.HasInventory) continue;
                for (var i = 0; i < owner.InventoryCount; i++)
                    total += Count(owner.GetInventory(i), typeId, subtype);
            }
            return total;
        }

        private static int Count(IMyInventory inventory, string typeId, string subtype)
        {
            decimal total = 0;
            var concrete = (MyInventoryBase)(object)inventory;
            foreach (dynamic item in concrete.GetItems())
            {
                if (string.Equals(item.Content.TypeId.ToString(), typeId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)item.Content.SubtypeName, subtype, StringComparison.OrdinalIgnoreCase))
                    total += (decimal)(double)item.Amount;
            }
            return (int)total;
        }

        public override void CleanupLeftovers()
        {
            var entities = new HashSet<VRage.ModAPI.IMyEntity>();
            Sandbox.ModAPI.MyAPIGateway.Entities.GetEntities(entities, e =>
                e is MyCubeGrid && (e.Name ?? "").StartsWith(WorldApi.EntityPrefix + "production",
                    StringComparison.OrdinalIgnoreCase));
            foreach (var entity in entities)
            {
                var keep = false;
                foreach (var tracked in Tracked)
                    if (tracked == entity) { keep = true; break; }
                if (keep) continue;
                Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(entity);
                entity.Close();
            }
        }
    }
}
