using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Every ore the refinery takes: a refinery for each of its blueprints (its own reactor), 500 kg of the ore; what came
    /// out is what the ore it took gives by the blueprint and the refinery's efficiency (within 2 %), each result of it.
    /// </summary>
    public sealed class RefineryAllOresCheck : RegressionCheck
    {
        public override string Name => Prefix + "refinery_all_ores";
        public override int TimeoutSeconds => 300;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var definition = (MyRefineryDefinition)Definition("Refinery/LargeRefinery");
            var blueprints = definition.BlueprintClasses.SelectMany(c => c)
                .Where(b => b.Prerequisites.Length == 1 && b.Prerequisites[0].Id.TypeId == typeof(MyObjectBuilder_Ore) && b.Results.Length > 0)
                .GroupBy(b => b.Prerequisites[0].Id).Select(g => g.First()).ToList();
            Check(blueprints.Count > 0, "the refinery has no ore blueprints");
            var lines = new List<(MyBlueprintDefinitionBase Blueprint, MyRefinery Refinery)>();
            for (var i = 0; i < blueprints.Count; i++)
            {
                var grid = Build("refinery-ore-" + i, Site.Origin + Site.Right * (i % 6 * 15) + Site.Forward * (i / 6 * 15), true,
                    new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                    new Part("Refinery/LargeRefinery", 2, 0, 0));
                Put(Inventory(Block<MyReactor>(grid)), "Ingot/Uranium", 5);
                var refinery = Block<MyRefinery>(grid);
                refinery.InputInventory.AddItems(500, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(blueprints[i].Prerequisites[0].Id));
                lines.Add((blueprints[i], refinery));
            }
            // until each has taken some 100 kg of its ore
            Func<(MyBlueprintDefinitionBase Blueprint, MyRefinery Refinery), double> taken = l => 500 - (double)l.Refinery.InputInventory.GetItemAmount(l.Blueprint.Prerequisites[0].Id);
            // 100 kg, or what the blueprint's time lets the refinery take in a minute (a mod's concentrates go slow)
            Func<(MyBlueprintDefinitionBase Blueprint, MyRefinery Refinery), double> goal = l =>
            {
                var perSecond = (double)l.Blueprint.Prerequisites[0].Amount / Math.Max(0.001, l.Blueprint.BaseProductionTimeInSeconds) *
                                definition.RefineSpeed * MySession.Static.RefinerySpeedMultiplier * l.Refinery.UpgradeValues["Productivity"];
                return Math.Min(100, perSecond * 60);
            };
            yield return Until(() => lines.Min(l => taken(l) - goal(l)), v => v >= 0, "every refinery through 100 kg of its ore or a minute of it, the furthest off, kg", 150,
                () => string.Join(", ", lines.Select(l => l.Blueprint.Prerequisites[0].Id.SubtypeName + " " + F(taken(l)))));
            foreach (var l in lines) l.Refinery.Enabled = false;
            yield return Seconds(1);
            var wrong = new List<string>();
            var done = new List<string>();
            foreach (var (blueprint, refinery) in lines)
            {
                var ore = blueprint.Prerequisites[0].Id;
                var used = 500 - (double)refinery.InputInventory.GetItemAmount(ore);
                var efficiency = definition.MaterialEfficiency * refinery.UpgradeValues["Effectiveness"];
                foreach (var result in blueprint.Results)
                {
                    var made = (double)refinery.OutputInventory.GetItemAmount(result.Id);
                    var expected = used * (double)result.Amount / (double)blueprint.Prerequisites[0].Amount * efficiency;
                    if (Math.Abs(made - expected) > expected * 0.02 + 0.05)
                        wrong.Add(ore.SubtypeName + " -> " + result.Id.SubtypeName + ": " + F(made) + " kg of " + F(expected) + " from " + F(used) + " kg");
                }
                done.Add(ore.SubtypeName + " " + F(used) + " -> " + string.Join("+", blueprint.Results.Select(r => r.Id.SubtypeName + " " + F((double)refinery.OutputInventory.GetItemAmount(r.Id)))));
            }
            Check(wrong.Count == 0, wrong.Count + " ores refined off their blueprint: " + string.Join("; ", wrong));
            Result(lines.Count + " ores refined by their blueprints: " + string.Join("; ", done));
        }
    }

    /// <summary>
    /// Every component the assembler makes: an assembler for each component blueprint (its own reactor), the ingots for it;
    /// one run queued gives what the blueprint gives (some give several: plates come 140 at a time), taking what it takes
    /// with the world's assembler efficiency.
    /// </summary>
    public sealed class AssemblerAllComponentsCheck : RegressionCheck
    {
        public override string Name => Prefix + "assembler_all_components";
        public override int TimeoutSeconds => 320;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var definition = (MyAssemblerDefinition)Definition("Assembler/LargeAssembler");
            var blueprints = definition.BlueprintClasses.SelectMany(c => c)
                .Where(b => b.Results.Length == 1 && b.Results[0].Id.TypeId == typeof(MyObjectBuilder_Component) && b.Prerequisites.All(p => p.Id.TypeId == typeof(MyObjectBuilder_Ingot)))
                .GroupBy(b => b.Results[0].Id).Select(g => g.First()).ToList();
            Check(blueprints.Count > 0, "the assembler has no component blueprints");
            var efficiency = MySession.Static.AssemblerEfficiencyMultiplier;
            var lines = new List<(MyBlueprintDefinitionBase Blueprint, MyAssembler Assembler)>();
            for (var i = 0; i < blueprints.Count; i++)
            {
                var grid = Build("assembler-component-" + i, Site.Origin + Site.Right * (i % 8 * 12) + Site.Forward * (i / 8 * 12), true,
                    new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                    new Part("Assembler/LargeAssembler", 1, 0, 0));
                Put(Inventory(Block<MyReactor>(grid)), "Ingot/Uranium", 5);
                var assembler = Block<MyAssembler>(grid);
                // twice what one takes
                foreach (var p in blueprints[i].Prerequisites)
                    assembler.InputInventory.AddItems((MyFixedPoint)((double)p.Amount * 2 / efficiency + 1), (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(p.Id));
                lines.Add((blueprints[i], assembler));
            }
            var stocked = lines.Select(l => l.Blueprint.Prerequisites.ToDictionary(p => p.Id, p => (double)l.Assembler.InputInventory.GetItemAmount(p.Id))).ToList();
            foreach (var l in lines) Request(l.Assembler, "OnAddQueueItemRequest", -1, (SerializableDefinitionId)l.Blueprint.Id, (MyFixedPoint)1);
            Func<(MyBlueprintDefinitionBase Blueprint, MyAssembler Assembler), double> made = l => (double)l.Assembler.OutputInventory.GetItemAmount(l.Blueprint.Results[0].Id);
            Func<(MyBlueprintDefinitionBase Blueprint, MyAssembler Assembler), double> gives = l => (double)l.Blueprint.Results[0].Amount;
            yield return Until(() => lines.Count(l => made(l) >= gives(l)), v => v >= lines.Count, "every component assembled", 200,
                () => "missing: " + string.Join(", ", lines.Where(l => made(l) < gives(l)).Select(l => l.Blueprint.Results[0].Id.SubtypeName + " (" + State(l.Assembler) + ")")));
            yield return Seconds(1);
            var wrong = new List<string>();
            for (var i = 0; i < lines.Count; i++)
            {
                var (blueprint, assembler) = lines[i];
                if (made(lines[i]) != gives(lines[i]))
                    wrong.Add(blueprint.Results[0].Id.SubtypeName + ": " + made(lines[i]) + " made of " + gives(lines[i]) + " (input " + string.Join(", ", blueprint.Prerequisites.Select(p => p.Id.SubtypeName + " " + F(stocked[i][p.Id]) + " -> " + F((double)assembler.InputInventory.GetItemAmount(p.Id))))
                              + "; output " + string.Join(", ", assembler.OutputInventory.GetItems().Select(it => it.Content.SubtypeName + " " + it.Amount)) + "; queue " + assembler.Queue.Count() + ", repeat " + assembler.RepeatEnabled
                              + ", grid " + assembler.CubeGrid.DisplayName + " blocks " + assembler.CubeGrid.BlocksCount + ")");
                foreach (var p in blueprint.Prerequisites)
                {
                    var used = stocked[i][p.Id] - (double)assembler.InputInventory.GetItemAmount(p.Id);
                    var expected = (double)p.Amount / efficiency;
                    if (Math.Abs(used - expected) > expected * 0.01 + 0.01)
                        wrong.Add(blueprint.Results[0].Id.SubtypeName + ": took " + F(used) + " kg of " + p.Id.SubtypeName + ", the blueprint " + F(expected));
                }
            }
            Check(wrong.Count == 0, wrong.Count + " components off their blueprint: " + string.Join("; ", wrong));
            Result(lines.Count + " components assembled each by its blueprint (materials x" + F(1 / efficiency) + " by the world): " + string.Join(", ", lines.Select(l => l.Blueprint.Results[0].Id.SubtypeName)));
        }
    }
}
