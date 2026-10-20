using System;
using System.Collections;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;

namespace SentisTests.Regression.Checks
{
    internal static class Blueprints
    {
        public static MyBlueprintDefinitionBase Get(string subtype) =>
            MyDefinitionManager.Static.GetBlueprintDefinition(new MyDefinitionId(typeof(MyObjectBuilder_BlueprintDefinition), subtype))
            ?? throw new Core.ScenarioSkippedException("no blueprint " + subtype + " in this game");

        /// <summary>How much of an item one run of the blueprint takes.</summary>
        public static double Takes(MyBlueprintDefinitionBase blueprint, MyDefinitionId item) =>
            blueprint.Prerequisites.Where(p => p.Id == item).Sum(p => (double)p.Amount);

        /// <summary>How much of an item one run of the blueprint gives.</summary>
        public static double Gives(MyBlueprintDefinitionBase blueprint, MyDefinitionId item) =>
            blueprint.Results.Where(r => r.Id == item).Sum(r => (double)r.Amount);
    }

    /// <summary>
    /// A refinery, powered, turns iron ore into ingots at the yield of its blueprint and its own efficiency: what came out
    /// is what the ore it took gives, no more, no less (within 2 %).
    /// </summary>
    public sealed class RefineryCheck : RegressionCheck
    {
        public override string Name => Prefix + "refinery";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var grid = Build("refinery", Site.Origin, true,
                new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                new Part("Refinery/LargeRefinery", 2, 0, 0));
            Put(Inventory(Block<MyReactor>(grid)), "Ingot/Uranium", 5);
            var refinery = Block<MyRefinery>(grid);
            const double ore = 1000;
            Put(refinery.InputInventory, "Ore/Iron", ore);

            yield return Until(() => Amount(refinery.OutputInventory, "Ingot/Iron"), v => v >= 50, "iron ingots out of the refinery", 120);
            refinery.Enabled = false;
            yield return Seconds(1);
            var used = ore - Amount(refinery.InputInventory, "Ore/Iron");
            var made = Amount(refinery.OutputInventory, "Ingot/Iron");
            var blueprint = Blueprints.Get("IronOreToIngot");
            var perOre = Blueprints.Gives(blueprint, RegressionCheck.Item("Ingot/Iron")) / Blueprints.Takes(blueprint, RegressionCheck.Item("Ore/Iron"));
            var efficiency = ((MyRefineryDefinition)refinery.BlockDefinition).MaterialEfficiency * refinery.UpgradeValues["Effectiveness"];
            var expected = used * perOre * efficiency;
            Check(Math.Abs(made - expected) <= expected * 0.02 + 0.01,
                "the refinery made " + F(made) + " kg of iron from " + F(used) + " kg of ore; its blueprint and efficiency give " + F(expected));
            Result(F(used) + " kg of iron ore -> " + F(made) + " kg of ingots (" + F(perOre) + " x " + F(efficiency) + ", as it should)");
        }
    }

    /// <summary>
    /// An assembler, powered, builds 10 steel plates from iron ingots queued as a player queues them, taking what the
    /// blueprint takes (with the world's assembler efficiency); switched to disassembly, it takes 5 of them apart and
    /// gives the iron back.
    /// </summary>
    public sealed class AssemblerCheck : RegressionCheck
    {
        public override string Name => Prefix + "assembler";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var grid = Build("assembler", Site.Origin, true,
                new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                new Part("Assembler/LargeAssembler", 1, 0, 0));
            Put(Inventory(Block<MyReactor>(grid)), "Ingot/Uranium", 5);
            var assembler = Block<MyAssembler>(grid);
            const double iron = 1000;
            Put(assembler.InputInventory, "Ingot/Iron", iron);
            var blueprint = Blueprints.Get("SteelPlate");

            Request(assembler, "OnAddQueueItemRequest", -1, (SerializableDefinitionId)blueprint.Id, (MyFixedPoint)10);
            yield return Until(() => Amount(assembler.OutputInventory, "Component/SteelPlate"), v => v >= 10, "steel plates assembled", 120);
            yield return Seconds(1);
            Check(Amount(assembler.OutputInventory, "Component/SteelPlate") == 10, "the assembler made " + Amount(assembler.OutputInventory, "Component/SteelPlate") + " plates of 10");
            var used = iron - Amount(assembler.InputInventory, "Ingot/Iron");
            var expected = 10 * Blueprints.Takes(blueprint, RegressionCheck.Item("Ingot/Iron")) / MySession.Static.AssemblerEfficiencyMultiplier;
            Check(Math.Abs(used - expected) <= expected * 0.01 + 0.01, "10 plates took " + F(used) + " kg of iron, the blueprint says " + F(expected));

            Request(assembler, "ModeSwitchCallback", true);
            Check(assembler.DisassembleEnabled, "the assembler did not switch to disassembly");
            Request(assembler, "OnAddQueueItemRequest", -1, (SerializableDefinitionId)blueprint.Id, (MyFixedPoint)5);
            var left = Amount(assembler.InputInventory, "Ingot/Iron");
            yield return Until(() => Amount(assembler.OutputInventory, "Component/SteelPlate"), v => v <= 5, "steel plates taken apart", 120);
            yield return Seconds(1);
            var back = Amount(assembler.InputInventory, "Ingot/Iron") - left;
            Check(back > 0, "disassembly gave no iron back");
            Result("10 plates from " + F(used) + " kg of iron; 5 taken apart gave " + F(back) + " kg back");
        }
    }

    /// <summary>A survival kit, on a battery, makes steel plates from iron ingots as a player's first base does.</summary>
    public sealed class SurvivalKitCheck : RegressionCheck
    {
        public override string Name => Prefix + "survival_kit";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var grid = Build("survival-kit", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0),
                new Part("SurvivalKit/SurvivalKitLarge", 1, 0, 0));
            var kit = Block<MyAssembler>(grid);
            Put(kit.InputInventory, "Ingot/Iron", 200);
            var blueprint = Blueprints.Get("SteelPlate");
            Check(((MyProductionBlockDefinition)kit.BlockDefinition).BlueprintClasses.Any(c => c.ContainsBlueprint(blueprint)),
                "the survival kit cannot make steel plates");
            Request(kit, "OnAddQueueItemRequest", -1, (SerializableDefinitionId)blueprint.Id, (MyFixedPoint)3);
            yield return Until(() => Amount(kit.OutputInventory, "Component/SteelPlate"), v => v >= 3, "steel plates from the survival kit", 120);
            Result("survival kit made 3 steel plates");
        }
    }
}
