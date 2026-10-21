using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using VRageMath;
using PowerProducer = Sandbox.ModAPI.Ingame.IMyPowerProducer;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A reactor with uranium runs a refinery full of ore: the reactor gives power and burns uranium, the refinery is
    /// powered and makes ingots. Turned off from the terminal, the reactor gives nothing and the refinery stops.
    /// </summary>
    public sealed class ReactorCheck : RegressionCheck
    {
        public override string Name => Prefix + "reactor";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var grid = Build("reactor", Site.Origin, true,
                new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                new Part("Refinery/LargeRefinery", 2, 0, 0));
            var reactor = Block<MyReactor>(grid);
            var refinery = Block<MyRefinery>(grid);
            Put(Inventory(reactor), "Ingot/Uranium", 5);
            Put(refinery.InputInventory, "Ore/Iron", 2000);

            yield return Until(() => ((PowerProducer)reactor).CurrentOutput, v => v > 0.01, "the reactor's output, MW", 30);
            yield return Until(() => refinery.IsProducing ? 1 : 0, v => v > 0, "the refinery producing", 30);
            var uranium = Amount(Inventory(reactor), "Ingot/Uranium");
            yield return Until(() => Amount(refinery.OutputInventory, "Ingot/Iron"), v => v > 1, "iron ingots out of the refinery", 60);
            yield return Until(() => uranium - Amount(Inventory(reactor), "Ingot/Uranium"), v => v > 0, "uranium burnt", 30);
            var output = ((PowerProducer)reactor).CurrentOutput;

            reactor.Enabled = false;
            yield return Until(() => ((PowerProducer)reactor).CurrentOutput, v => v <= 0, "the reactor's output once off, MW", 10);
            yield return Until(() => refinery.IsProducing ? 1 : 0, v => v <= 0, "the refinery producing with no power", 10);
            Result("reactor " + F(output) + " MW, " + (uranium - Amount(Inventory(reactor), "Ingot/Uranium")).ToString("F6") + " kg of uranium burnt; off - the refinery stopped");
        }
    }

    /// <summary>
    /// A battery charges from a reactor's surplus (recharge mode), then, the reactor off, runs a refinery on what it
    /// holds (auto mode), and its charge goes down.
    /// </summary>
    public sealed class BatteryCheck : RegressionCheck
    {
        public override string Name => Prefix + "battery";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var grid = Build("battery", Site.Origin, true,
                new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0)
                    .With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 0.5f),
                new Part("Refinery/LargeRefinery", 3, 0, 0));
            var reactor = Block<MyReactor>(grid);
            var battery = Block<MyBatteryBlock>(grid);
            var refinery = Block<MyRefinery>(grid);
            Put(Inventory(reactor), "Ingot/Uranium", 5);

            battery.ChargeMode = Sandbox.ModAPI.Ingame.ChargeMode.Recharge;
            var empty = battery.CurrentStoredPower;
            yield return Until(() => battery.CurrentStoredPower - empty, v => v > 0.001, "the battery charging from the reactor, MWh", 60);
            var charged = battery.CurrentStoredPower;

            reactor.Enabled = false;
            battery.ChargeMode = Sandbox.ModAPI.Ingame.ChargeMode.Auto;
            Put(refinery.InputInventory, "Ore/Iron", 2000);
            yield return Until(() => refinery.IsProducing ? 1 : 0, v => v > 0, "the refinery running on the battery", 30);
            var before = battery.CurrentStoredPower;
            yield return Until(() => before - battery.CurrentStoredPower, v => v > 0.0005, "the battery discharging, MWh", 60);
            Result("charged " + empty.ToString("F4") + " -> " + charged.ToString("F4") + " MWh, then discharged to " + battery.CurrentStoredPower.ToString("F4") +
                   " MWh running the refinery");
        }
    }

    /// <summary>A solar panel in open space, turned to the sun, gives power.</summary>
    public sealed class SolarCheck : RegressionCheck
    {
        public override string Name => Prefix + "solar";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var sun = (Vector3D)MySector.DirectionToSunNormalized;
            Check(sun.LengthSquared() > 0.5, "the world has no sun");
            // the panel's face is its block's back or front, whichever: both are tried
            var best = 0.0;
            foreach (var facing in new[] { sun, -sun })
            {
                var up = Vector3D.CalculatePerpendicularVector(facing);
                var grid = Build("solar", Site.Origin, true, new[] { new Part("SolarPanel/LargeBlockSolarPanel", 0, 0, 0) },
                    forward: facing, up: up);
                var panel = Block<SpaceEngineers.Game.Entities.Blocks.MySolarPanel>(grid);
                var within = Within(15);
                while (within())
                {
                    best = Math.Max(best, ((PowerProducer)panel).MaxOutput);
                    if (best > 0.01) break;
                    yield return null;
                }
                yield return CloseAndWait(grid);
                if (best > 0.01) break;
            }
            Check(best > 0.01, "no power from a solar panel turned to the sun either way");
            Result("solar panel " + F(best * 1000) + " kW in the sun");
        }
    }

    /// <summary>A wind turbine on a planet with an atmosphere, standing clear on a floor, gives power.</summary>
    public sealed class WindCheck : RegressionCheck
    {
        public override string Name => Prefix + "wind";
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            Check(Site.Planet.HasAtmosphere, "the planet of the planet sites has no atmosphere");
            yield return Arrive(Site.At(-12, 3, -12));
            var pad = Pad("wind-pad", Site.Origin, 2);
            var top = pad.GridIntegerToWorld(new Vector3I(0, 1, 0));
            // the turbine high on a pole: wind turbines want clear air around them
            var parts = Enumerable.Range(0, 6).Select(y => new Part("CubeBlock/LargeBlockArmorBlock", 0, y, 0)).ToList();
            parts.Add(new Part("WindTurbine/LargeBlockWindTurbine", -1, 6, -1));
            var tower = Build("wind", top, true, parts, forward: pad.WorldMatrix.Forward, up: pad.WorldMatrix.Up);
            var turbine = Block<SpaceEngineers.Game.Entities.Blocks.MyWindTurbine>(tower);
            yield return Until(() => ((PowerProducer)turbine).MaxOutput, v => v > 0.001, "the wind turbine's output, MW", 60);
            Result("wind turbine " + F(((PowerProducer)turbine).MaxOutput * 1000) + " kW");
        }
    }

    /// <summary>
    /// Ice in an O2/H2 generator (powered by a reactor) fills a hydrogen tank; a hydrogen engine then burns it and gives
    /// power.
    /// </summary>
    public sealed class HydrogenCheck : RegressionCheck
    {
        public override string Name => Prefix + "hydrogen";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            // the tank and the engine where their conveyor ports meet the generator's
            Part tankPart = null, enginePart = null;
            yield return ConveyorNeighbour(new Part("OxygenGenerator/", 0, 0, 0), "OxygenTank/LargeHydrogenTankSmall", null, p => tankPart = p);
            var tankCells = Cells(tankPart);
            yield return ConveyorNeighbour(new Part("OxygenGenerator/", 0, 0, 0), "HydrogenEngine/LargeHydrogenEngine", p => !Cells(p).Overlaps(tankCells), p => enginePart = p);
            var parts = new[] { new Part("OxygenGenerator/", 0, 0, 0), tankPart, enginePart, new Part("Reactor/LargeBlockSmallGenerator", 6, 0, 0) };
            var grid = Build("hydrogen", Site.Origin, true, parts);
            var reactor = Block<MyReactor>(grid);
            var generator = Block<Sandbox.Game.Entities.Blocks.MyGasGenerator>(grid);
            var tank = Block<Sandbox.Game.Entities.Blocks.MyGasTank>(grid);
            var engine = Block<SpaceEngineers.Game.Entities.Blocks.MyHydrogenEngine>(grid);
            Put(Inventory(reactor), "Ingot/Uranium", 5);
            Put(Inventory(generator), "Ore/Ice", 1000);
            Note("the tank at " + tankPart.Min + " facing " + tankPart.Forward + ", the engine at " + enginePart.Min + " facing " + enginePart.Forward);

            engine.Enabled = false;
            var ice = Amount(Inventory(generator), "Ore/Ice");
            yield return Until(() => ((Sandbox.ModAPI.Ingame.IMyGasTank)tank).FilledRatio, v => v > 0.001, "the hydrogen tank filling from the generator", 60);
            yield return Until(() => ice - Amount(Inventory(generator), "Ore/Ice"), v => v > 0, "ice used by the generator, kg", 20);
            var filled = ((Sandbox.ModAPI.Ingame.IMyGasTank)tank).FilledRatio;

            engine.Enabled = true;
            yield return Until(() => ((PowerProducer)engine).MaxOutput, v => v > 0.001, "the hydrogen engine's output, MW", 60);
            Result("tank " + (filled * 100).ToString("F2") + " % from " + F(ice - Amount(Inventory(generator), "Ore/Ice")) + " kg of ice; engine " +
                   F(((PowerProducer)engine).MaxOutput) + " MW");
        }
    }
}
