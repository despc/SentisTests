using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Upgrade modules on a refinery: a productivity module makes it faster, an effectiveness module gives more ingots of
    /// the ore, a power efficiency module makes it use less. The module's place on the refinery comes from the models (its
    /// upgrade points), so it is searched for first; each module then goes on a refinery of its own, and the refinery with
    /// the effectiveness module refines iron at the yield its upgraded efficiency gives - more than a bare refinery does.
    /// </summary>
    public sealed class UpgradeModulesCheck : RegressionCheck
    {
        public override string Name => Prefix + "upgrade_modules";

        private static readonly (string Module, string Upgrade)[] Modules =
        {
            ("UpgradeModule/LargeProductivityModule", "Productivity"),
            ("UpgradeModule/LargeEffectivenessModule", "Effectiveness"),
            ("UpgradeModule/LargeEnergyModule", "PowerEfficiency"),
        };

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 12, -12));
            var refineryPart = new Part("Refinery/LargeRefinery", 0, 0, 0);
            Part place = null;
            // a module is on a machine when one of its upgrade points meets one of the machine's - the test a module makes
            // every 100 frames (MyUpgradeModule.RefreshConnections), made here at once
            yield return Neighbour(refineryPart, Modules[0].Module, null, "on its upgrade points", (probe, at) =>
            {
                var machine = probe.GetCubeBlock(refineryPart.Min).FatBlock;
                var module = probe.GetCubeBlock(at.Min)?.FatBlock;
                var points = module?.GetType().GetField("m_connectionPositions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(module)
                    as Sandbox.Game.GameSystems.Conveyors.ConveyorLinePosition[];
                if (module == null) return false;
                if (points == null) throw new Core.ScenarioFailedException("MyUpgradeModule.m_connectionPositions is not in this game");
                return points.Any(p => machine.GetComponent().ConnectionPositions.Contains(p.GetConnectingPosition()));
            }, p => place = p);
            // the reactor on a cell neither the refinery nor the module takes
            var taken = new HashSet<Vector3I>(Cells(refineryPart).Concat(Cells(place)));
            var power = taken.SelectMany(c => Base6Directions.IntDirections.Select(d => c + d)).First(c => !taken.Contains(c));
            var reactorPart = new Part("Reactor/LargeBlockSmallGenerator", power.X, power.Y, power.Z);
            var alone = Build("upgrade-bare", Site.At(-20, 0, 0), true, refineryPart, reactorPart);
            var bareValues = Block<MyRefinery>(alone).UpgradeValues.ToDictionary(p => p.Key, p => p.Value);
            alone.Close();

            var refineries = new Dictionary<string, MyRefinery>();
            var notes = new List<string>();
            for (var i = 0; i < Modules.Length; i++)
            {
                var module = new Part(Modules[i].Module, place.Min.X, place.Min.Y, place.Min.Z).Facing(place.Forward, place.Up);
                var grid = Build("upgrade-" + Modules[i].Upgrade, Site.At(i * 15, 0, 0), true, new[]
                {
                    refineryPart, module, reactorPart,
                });
                var refinery = Block<MyRefinery>(grid);
                Put(Inventory(Block<MyReactor>(grid)), "Ingot/Uranium", 5);
                refineries[Modules[i].Upgrade] = refinery;
                yield return Until(() => refinery.UpgradeValues[Modules[i].Upgrade] - bareValues[Modules[i].Upgrade], v => v > 0.001,
                    Modules[i].Upgrade + " of the refinery over a bare one's " + F(bareValues[Modules[i].Upgrade]), 15);
                foreach (var other in Modules.Where(m => m.Upgrade != Modules[i].Upgrade))
                    Check(Math.Abs(refinery.UpgradeValues[other.Upgrade] - bareValues[other.Upgrade]) < 0.001,
                        Modules[i].Module + " changed the refinery's " + other.Upgrade + " to " + F(refinery.UpgradeValues[other.Upgrade]));
                notes.Add(Modules[i].Upgrade + " " + F(bareValues[Modules[i].Upgrade]) + " -> " + F(refinery.UpgradeValues[Modules[i].Upgrade]));
            }

            // the yield of the refinery with the effectiveness module
            var effective = refineries["Effectiveness"];
            const double ore = 1000;
            Put(effective.InputInventory, "Ore/Iron", ore);
            yield return Until(() => Amount(effective.OutputInventory, "Ingot/Iron"), v => v >= 50, "iron ingots out of the upgraded refinery", 120, () => State(effective));
            effective.Enabled = false;
            yield return Seconds(1);
            var used = ore - Amount(effective.InputInventory, "Ore/Iron");
            var made = Amount(effective.OutputInventory, "Ingot/Iron");
            var blueprint = Blueprints.Taking(effective, Item("Ore/Iron"));
            var perOre = Blueprints.Gives(blueprint, Item("Ingot/Iron")) / Blueprints.Takes(blueprint, Item("Ore/Iron"));
            var material = ((MyRefineryDefinition)effective.BlockDefinition).MaterialEfficiency;
            var expected = used * perOre * material * effective.UpgradeValues["Effectiveness"];
            Check(Math.Abs(made - expected) <= expected * 0.02 + 0.01,
                "the upgraded refinery made " + F(made) + " kg of iron from " + F(used) + " kg of ore; its efficiency " + F(material * effective.UpgradeValues["Effectiveness"]) + " gives " + F(expected));
            Result("modules at " + place.Min + " facing " + place.Forward + ": " + string.Join(", ", notes) + "; with the effectiveness module " + F(used) + " kg of ore -> " + F(made) +
                   " kg of ingots (bare: " + F(used * perOre * material) + ")");
        }
    }

    /// <summary>
    /// An assembler in repeat mode, asked for one steel plate, keeps making plates: three plates made, the plate is still
    /// in its queue. Repeat switched off, the queue runs out.
    /// </summary>
    public sealed class AssemblerRepeatCheck : RegressionCheck
    {
        public override string Name => Prefix + "assembler_repeat";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var grid = Build("repeat", Site.Origin, true,
                new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                new Part("Assembler/LargeAssembler", 1, 0, 0));
            Put(Inventory(Block<MyReactor>(grid)), "Ingot/Uranium", 5);
            var assembler = Block<MyAssembler>(grid);
            Put(assembler.InputInventory, "Ingot/Iron", 1000);
            Request(assembler, "RepeatEnabledCallback", false, true);
            Check(assembler.RepeatEnabled, "the assembler did not switch to repeat");
            Request(assembler, "OnAddQueueItemRequest", -1, (SerializableDefinitionId)Blueprints.Get("SteelPlate").Id, (MyFixedPoint)1);
            yield return Until(() => Amount(assembler.OutputInventory, "Component/SteelPlate"), v => v >= 3, "steel plates made in repeat", 120, () => State(assembler));
            var queued = ((Sandbox.ModAPI.Ingame.IMyProductionBlock)assembler).IsQueueEmpty ? 0 : 1;
            Check(queued > 0, "the assembler's queue is empty in repeat after " + Amount(assembler.OutputInventory, "Component/SteelPlate") + " plates");
            Request(assembler, "RepeatEnabledCallback", false, false);
            yield return Until(() => ((Sandbox.ModAPI.Ingame.IMyProductionBlock)assembler).IsQueueEmpty ? 1 : 0, v => v > 0, "the queue run out with repeat off", 30);
            Result("one plate queued in repeat made " + Amount(assembler.OutputInventory, "Component/SteelPlate") + " and stayed queued; repeat off, the queue ran out");
        }
    }

    /// <summary>
    /// Two assemblers on one conveyor line, the second in cooperative mode: plates queued on the first, the second takes a
    /// share of the queue and makes plates of its own.
    /// </summary>
    public sealed class CooperativeAssemblyCheck : RegressionCheck
    {
        public override string Name => Prefix + "assembler_cooperative";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var masterPart = new Part("Assembler/LargeAssembler", 0, 0, 0);
            Part slavePart = null;
            yield return ConveyorNeighbour(masterPart, "Assembler/LargeAssembler", null, p => slavePart = p);
            var taken = new HashSet<Vector3I>(Cells(masterPart).Concat(Cells(slavePart)));
            var free = taken.SelectMany(c => Base6Directions.IntDirections.Select(d => c + d)).First(c => !taken.Contains(c));
            var grid = Build("cooperative", Site.Origin, true, masterPart, slavePart,
                new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z).With(b => Motion.Charged(b)));
            var master = (MyAssembler)grid.GetCubeBlock(masterPart.Min).FatBlock;
            var slave = (MyAssembler)grid.GetCubeBlock(slavePart.Min).FatBlock;
            Put(master.InputInventory, "Ingot/Iron", 1000);
            Put(slave.InputInventory, "Ingot/Iron", 1000);
            ((Sandbox.ModAPI.IMyTerminalBlock)slave).SetValueBool("slaveMode", true);
            Check(slave.IsSlave, "the second assembler did not go into cooperative mode");
            Request(master, "OnAddQueueItemRequest", -1, (SerializableDefinitionId)Blueprints.Get("SteelPlate").Id, (MyFixedPoint)40);
            yield return Until(() => Amount(slave.OutputInventory, "Component/SteelPlate"), v => v >= 1, "plates made by the cooperative assembler", 60,
                () => "master made " + Amount(master.OutputInventory, "Component/SteelPlate") + "; " + State(slave));
            Result("plates queued on one assembler, the cooperative one took a share and made " + Amount(slave.OutputInventory, "Component/SteelPlate") +
                   " (the first " + Amount(master.OutputInventory, "Component/SteelPlate") + ")");
        }
    }

    /// <summary>A survival kit, on a battery, grinds stone into gravel and the little iron, nickel and silicon it holds.</summary>
    public sealed class SurvivalKitStoneCheck : RegressionCheck
    {
        public override string Name => Prefix + "survival_kit_stone";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var grid = Build("kit-stone", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("SurvivalKit/SurvivalKitLarge", 1, 0, 0));
            var kit = Block<MyAssembler>(grid);
            var blueprint = Blueprints.Taking(kit, Item("Ore/Stone"));
            const double stone = 100;
            Put(kit.InputInventory, "Ore/Stone", stone);
            Request(kit, "OnAddQueueItemRequest", -1, (SerializableDefinitionId)blueprint.Id, (MyFixedPoint)5);
            Func<double> ingots = () => kit.OutputInventory.GetItems().Where(i => i.Content is VRage.Game.MyObjectBuilder_Ingot).Sum(i => (double)i.Amount);
            yield return Until(ingots, v => v > 0, "ingots out of the survival kit's stone", 60, () => State(kit));
            Check(Amount(kit.InputInventory, "Ore/Stone") < stone, "the kit made ingots and took no stone");
            Result("blueprint " + blueprint.Id.SubtypeName + ": " + F(stone - Amount(kit.InputInventory, "Ore/Stone")) + " kg of stone -> " +
                   string.Join(", ", kit.OutputInventory.GetItems().Select(i => i.Content.SubtypeName + " " + F((double)i.Amount))));
        }
    }

    /// <summary>
    /// An O2/H2 generator on a conveyor line with a cargo container of ice, its own inventory empty: it pulls the ice in by
    /// itself and makes hydrogen into a tank on the line.
    /// </summary>
    public sealed class GeneratorPullsIceCheck : RegressionCheck
    {
        public override string Name => Prefix + "generator_pulls_ice";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var cargoPart = new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0);
            Part generatorPart = null;
            yield return ConveyorNeighbour(cargoPart, "OxygenGenerator/", null, p => generatorPart = p);
            var taken = new HashSet<Vector3I>(Cells(cargoPart).Concat(Cells(generatorPart)));
            Part tankPart = null;
            yield return ConveyorNeighbour(generatorPart, "OxygenTank/LargeHydrogenTankSmall", p => !Cells(p).Overlaps(taken), p => tankPart = p);
            taken.UnionWith(Cells(tankPart));
            var free = taken.SelectMany(c => Base6Directions.IntDirections.Select(d => c + d)).Where(c => !taken.Contains(c)).Take(2).ToList();
            var grid = Build("pull-ice", Site.Origin, true, cargoPart, generatorPart, tankPart,
                new Part("BatteryBlock/LargeBlockBatteryBlock", free[0].X, free[0].Y, free[0].Z).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", free[1].X, free[1].Y, free[1].Z).With(b => Motion.Charged(b)));
            var cargo = Inventory(Block<MyCargoContainer>(grid));
            var generator = Block<MyGasGenerator>(grid);
            var tank = (Sandbox.ModAPI.Ingame.IMyGasTank)Block<MyGasTank>(grid);
            Check(Amount(Inventory(generator), "Ore/Ice") == 0, "the generator has ice of its own");
            Put(cargo, "Ore/Ice", 1000);
            yield return Until(() => Amount(Inventory(generator), "Ore/Ice"), v => v > 0, "ice pulled into the generator, kg", 30, () => State(generator));
            var hydrogen = Sandbox.Game.Entities.Character.Components.MyCharacterOxygenComponent.HydrogenId;
            var oxygen = Sandbox.Game.Entities.Character.Components.MyCharacterOxygenComponent.OxygenId;
            var gasTank = Block<MyGasTank>(grid);
            var source = generator.Components.Get<Sandbox.Game.EntityComponents.MyResourceSourceComponent>();
            Func<string> gas = () => "ice in it " + F(Amount(Inventory(generator), "Ore/Ice")) + " kg, in the cargo " + F(Amount(cargo, "Ore/Ice")) +
                                     " kg; it gives H2 " + source?.CurrentOutputByType(hydrogen).ToString("F3") + ", O2 " + source?.CurrentOutputByType(oxygen).ToString("F3") +
                                     " L/s; the tank takes " + gasTank.ResourceSink.CurrentInputByType(hydrogen).ToString("F3") + " L/s, wants " +
                                     gasTank.ResourceSink.RequiredInputByType(hydrogen).ToString("F3") + ", at " + (tank.FilledRatio * 100).ToString("F4") + " %; " + Held(gasTank);
            var told = DateTime.UtcNow;
            yield return Until(() =>
            {
                if ((DateTime.UtcNow - told).TotalSeconds >= 2)
                {
                    told = DateTime.UtcNow;
                    Note(gas());
                }
                return tank.FilledRatio;
            }, v => v > 0.0001, "hydrogen in the tank", 60, () => State(generator) + "; " + gas() + "; on one line " +
                                                                Sandbox.Game.GameSystems.MyGridConveyorSystem.Reachable(generator.ConveyorEndpoint, gasTank.ConveyorEndpoint));
            Result("the generator pulled " + F(1000 - Amount(cargo, "Ore/Ice")) + " kg of ice off the cargo, the tank at " + (tank.FilledRatio * 100).ToString("F2") + " %");
        }

        /// <summary>
        /// What is between the tank and its gas: its updates, and the transfers SentisOptimisations holds for it
        /// (GasTankOptimisations batches them).
        /// </summary>
        private static string Held(MyGasTank tank)
        {
            var text = "updates " + tank.NeedsUpdate;
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisationsPlugin.GasTankOptimisations")).FirstOrDefault(t => t != null);
            var batches = type?.GetField("Batches", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null) as System.Collections.IDictionary;
            if (batches == null) return text + ", no SO gas batches";
            var batch = batches.Contains(tank.EntityId) ? batches[tank.EntityId] : null;
            if (batch == null) return text + ", SO holds nothing";
            var bt = batch.GetType();
            return text + ", SO holds " + bt.GetField("Amount").GetValue(batch) + " L in " + bt.GetField("Count").GetValue(batch) + " transfers since " +
                   ((DateTime)bt.GetField("Since").GetValue(batch)).ToString("HH:mm:ss.fff");
        }
    }

    /// <summary>
    /// Every hydrogen tank fed by an O2/H2 generator fills by what flows into it. 33 generator-and-tank pairs get their ice
    /// 3 frames apart, so that the tanks' 100-frame update falls at every point of the first frames of their filling: a
    /// tank that stopped its per-frame updates there while the gas kept coming took nothing more (SentisOptimisations'
    /// GasTankOptimisations held the transfers without counting them for the tank - 1000 kg of ice burnt into a tank
    /// left empty). For 15 s the gas each tank is given is added up and set against what is in it.
    /// </summary>
    public sealed class GasTanksFillCheck : RegressionCheck
    {
        public override string Name => Prefix + "gas_tanks_fill";

        private const int Pairs = 33;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(30, 10, 30));
            var generatorPart = new Part("OxygenGenerator/", 0, 0, 0);
            Part tankPart = null;
            yield return ConveyorNeighbour(generatorPart, "OxygenTank/LargeHydrogenTankSmall", null, p => tankPart = p);
            var taken = new HashSet<Vector3I>(Cells(generatorPart).Concat(Cells(tankPart)));
            var free = taken.SelectMany(c => Base6Directions.IntDirections.Select(d => c + d)).First(c => !taken.Contains(c));
            var batteryPart = new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z).With(b => Motion.Charged(b));
            var hydrogen = Sandbox.Game.Entities.Character.Components.MyCharacterOxygenComponent.HydrogenId;
            var tanks = new List<MyGasTank>();
            var given = new double[Pairs];
            void Count()
            {
                for (var t = 0; t < tanks.Count; t++)
                    given[t] += tanks[t].ResourceSink.CurrentInputByType(hydrogen) / 60.0;
            }
            var generators = new List<MyGasGenerator>();
            for (var i = 0; i < Pairs; i++)
            {
                var grid = Build("gas-fill-" + i, Site.At(i % 6 * 12, 0, i / 6 * 12), true, generatorPart, tankPart, batteryPart);
                generators.Add(Block<MyGasGenerator>(grid));
                tanks.Add(Block<MyGasTank>(grid));
            }
            // built together, the pairs settle; then the gas starts 3 frames later in each than in the one before (the ice
            // put in): the start falls at every point of the tanks' 100-frame cycle
            yield return Seconds(3);
            for (var i = 0; i < Pairs; i++)
            {
                Put(Inventory(generators[i]), "Ore/Ice", 1000);
                for (var f = 0; f < 3; f++)
                {
                    Count();
                    yield return null;
                }
            }
            for (var f = Frames(15); f > 0; f--)
            {
                Count();
                yield return null;
            }
            var wrong = new List<string>();
            var total = 0.0;
            for (var t = 0; t < tanks.Count; t++)
            {
                var held = tanks[t].FilledRatio * tanks[t].Capacity;
                total += held;
                // what SentisOptimisations may still hold for the tank: at most 30 frames of its flow
                if (given[t] <= 0 || held < given[t] * 0.9)
                    wrong.Add("tank " + t + " (ice at frame " + t * 3 + "): " + F(held) + " L of " + F(given[t]) + " L given, updates " + tanks[t].NeedsUpdate);
            }
            Check(wrong.Count == 0, wrong.Count + " of " + Pairs + " tanks took less than the gas given to them: " + string.Join("; ", wrong.Take(6)));
            Result(Pairs + " tanks fed from 3 frames apart, each holds what it was given (" + F(total) + " L in all, " + F(given.Sum()) + " L given)");
        }
    }

    /// <summary>A hydrogen tank, full, fills an empty hydrogen bottle in its inventory when the player presses "Refill".</summary>
    public sealed class TankRefillCheck : RegressionCheck
    {
        public override string Name => Prefix + "tank_refill";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -6));
            var grid = Build("tank-refill", Site.Origin, true,
                new Part("OxygenTank/LargeHydrogenTankSmall", 0, 0, 0).With(b => ((MyObjectBuilder_GasTank)b).FilledRatio = 1f),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var tank = Block<MyGasTank>(grid);
            var inventory = Inventory(tank);
            var bottle = (MyObjectBuilder_GasContainerObject)MyObjectBuilderSerializer.CreateNewObject(Item("GasContainerObject/HydrogenBottle"));
            bottle.GasLevel = 0;
            Check(inventory.AddItems(1, bottle), "the bottle does not go into the tank");
            var before = ((Sandbox.ModAPI.Ingame.IMyGasTank)tank).FilledRatio;
            Request(tank, "OnRefillCallback");
            Func<double> level = () => inventory.GetItems().Select(i => i.Content).OfType<MyObjectBuilder_GasContainerObject>().Select(b => (double)b.GasLevel).DefaultIfEmpty(0).Max();
            yield return Until(level, v => v > 0.99, "the bottle refilled", 10, () => State(tank));
            var after = ((Sandbox.ModAPI.Ingame.IMyGasTank)tank).FilledRatio;
            Check(after < before, "the tank gave a bottle of hydrogen and stayed at " + (after * 100).ToString("F3") + " %");
            Result("refilled from the tank: the bottle full, the tank " + (before * 100).ToString("F2") + " -> " + (after * 100).ToString("F2") + " %");
        }
    }
}
