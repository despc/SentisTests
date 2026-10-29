using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A closed room of armor with a door in a wall, an air vent and a full oxygen tank: the vent fills it; the door
    /// opened, the room is open to space and its air goes; the door closed again, the vent fills it again.
    /// </summary>
    public sealed class AirtightDoorCheck : RegressionCheck
    {
        public override string Name => Prefix + "airtight_door";
        public override int TimeoutSeconds => 150;

        protected override IEnumerator Check()
        {
            var settings = MySession.Static.Settings;
            SkipUnless(settings.EnableOxygen && settings.EnableOxygenPressurization, "oxygen pressurization is off in this world");
            yield return Arrive(Site.At(0, 6, -8));
            // the room as reg_air_vent's: the cell (0,1,0) in armor -1..1 x 0..2 x -1..1, the vent under it; the wall at
            // (1,1,0) a door, turned each way until the room is airtight with it closed
            var vent = new Part("AirVent/", 0, 0, 0).Facing(Direction.Up, Direction.Backward);
            Part tank = null;
            yield return ConveyorNeighbour(vent, "OxygenTank/", p => Cells(p).All(c => c.Y < 0 || Math.Abs(c.X) > 1 || Math.Abs(c.Z) > 1), p => tank = p);
            var tankCells = Cells(tank);
            var doorCell = new Vector3I(1, 1, 0);
            MyCubeGrid grid = null;
            SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent airVent = null;
            Sandbox.ModAPI.Ingame.IMyDoor door = null;
            foreach (var face in new[] { Direction.Right, Direction.Left, Direction.Forward, Direction.Backward })
            {
                var parts = new List<Part> { vent, tank.With(b => ((MyObjectBuilder_GasTank)b).FilledRatio = 1f), new Part("Door/", doorCell.X, doorCell.Y, doorCell.Z).Facing(face, Direction.Up) };
                for (var x = -1; x <= 1; x++)
                for (var y = 0; y <= 2; y++)
                for (var z = -1; z <= 1; z++)
                {
                    var cell = new Vector3I(x, y, z);
                    if (cell == Vector3I.Zero || cell == new Vector3I(0, 1, 0) || cell == doorCell || tankCells.Contains(cell)) continue;
                    parts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, z));
                }
                var battery = new Part("BatteryBlock/LargeBlockBatteryBlock", tankCells.Contains(new Vector3I(-2, 0, 0)) ? 2 : -2, 0, 0).With(b => Motion.Charged(b));
                parts.Add(battery);
                grid = Build("airtight-door", Site.Origin, true, parts);
                airVent = (SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent)Block<SpaceEngineers.Game.Entities.Blocks.MyAirVent>(grid);
                door = (Sandbox.ModAPI.Ingame.IMyDoor)Block<MyDoor>(grid);
                airVent.Depressurize = false;
                door.CloseDoor();
                for (var f = Frames(2); f > 0 && !airVent.CanPressurize; f--) yield return null;
                if (airVent.CanPressurize) break;
                grid.Close();
                yield return Seconds(0.5);
                grid = null;
            }
            Check(grid != null, "the room is not airtight with its door closed, whichever way the door is turned");
            yield return Until(() => airVent.GetOxygenLevel(), v => v > 0.9, "the room's air with the door closed", 30, () => State((MyCubeBlock)airVent));
            door.OpenDoor();
            yield return Until(() => airVent.GetOxygenLevel(), v => v < 0.1, "the air gone with the door open", 20,
                () => "door " + door.Status + ", can pressurize " + airVent.CanPressurize);
            Check(!airVent.CanPressurize, "the vent still takes the room for airtight with its door open");
            door.CloseDoor();
            yield return Until(() => airVent.GetOxygenLevel(), v => v > 0.9, "the room's air again with the door closed", 40, () => "door " + door.Status + ", " + State((MyCubeBlock)airVent));
            Result("the room held air with its door closed, lost it with the door open, filled again closed");
        }
    }

    /// <summary>
    /// A ship welder takes the components it welds with from a cargo container on its conveyors: a frame of armor
    /// welded whole, the plates gone from the cargo.
    /// </summary>
    public sealed class WelderConveyorCheck : RegressionCheck
    {
        public override string Name => Prefix + "welder_conveyor";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, 8));
            var welderPart = new Part("ShipWelder/LargeShipWelder", 0, 0, 0);
            Part cargoPart = null;
            yield return ConveyorNeighbour(welderPart, "CargoContainer/LargeBlockSmallContainer", null, p => cargoPart = p);
            var taken = new HashSet<Vector3I>(Cells(welderPart).Concat(Cells(cargoPart)));
            var free = taken.SelectMany(c => Base6Directions.IntDirections.Select(d => c + d)).First(c => !taken.Contains(c));
            var rig = Build("welder-conveyor", Site.Origin, true, welderPart, cargoPart, new Part("Reactor/LargeBlockSmallGenerator", free.X, free.Y, free.Z));
            var welder = Block<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(rig);
            Put(Inventory(Block<MyReactor>(rig)), "Ingot/Uranium", 5);
            var cargo = Inventory(Block<MyCargoContainer>(rig));
            Put(cargo, "Component/SteelPlate", 100);
            Check(Amount(Inventory(welder), "Component/SteelPlate") == 0, "the welder has plates of its own");
            // the frame where the welder reaches (its sensor's sphere, from its model)
            var sphere = WorldApi.SensorSphere(welder);
            var target = Build("welder-conveyor-target", sphere.Center, true, new[] { new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0).At(0.05f) });
            var slim = target.GetCubeBlock(Vector3I.Zero);
            Switch(welder, true);
            yield return Until(() => slim.Integrity / slim.MaxIntegrity, v => v >= 0.999, "the frame welded with plates from the cargo", 60, () => State(welder) + ", plates in the cargo " +
                                                                                                                                                  F(Amount(cargo, "Component/SteelPlate")));
            var used = 100 - Amount(cargo, "Component/SteelPlate") - Amount(Inventory(welder), "Component/SteelPlate");
            Check(used > 0, "the frame was welded and the cargo gave no plates");
            Result("the welder pulled plates off the cargo on its conveyors and welded the frame whole (" + F(used) + " plates used)");
        }
    }

    /// <summary>
    /// A cargo container holds what its definition says times the world's block inventory multiplier, and no more: ore
    /// put in beyond that is turned away.
    /// </summary>
    public sealed class CargoCapacityCheck : RegressionCheck
    {
        public override string Name => Prefix + "cargo_capacity";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -6));
            var grid = Build("cargo-capacity", Site.Origin, true, new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0));
            var cargo = Block<MyCargoContainer>(grid);
            var inventory = Inventory(cargo);
            var definition = (Sandbox.Definitions.MyCargoContainerDefinition)cargo.BlockDefinition;
            // the definition's inventory size, or, without one, the block's own volume (MyCargoContainer.Init)
            var size = definition.InventorySize.Volume > 0 ? definition.InventorySize.Volume
                : definition.Size.X * definition.Size.Y * definition.Size.Z * Math.Pow(grid.GridSize, 3);
            var expected = size * MySession.Static.Settings.BlocksInventorySizeMultiplier;
            // a mod of the world may size the inventories itself (the modded V5's: 9 times the definition's)
            if (Game.Mods.InWorld && Math.Abs((double)inventory.MaxVolume - expected) > expected * 0.01)
            {
                Note("the cargo holds " + F((double)inventory.MaxVolume * 1000) + " L, its definition times the world's multiplier says " + F(expected * 1000) + " L: a mod of the world's sizes it");
                expected = (double)inventory.MaxVolume;
            }
            Check(Math.Abs((double)inventory.MaxVolume - expected) <= expected * 0.01, "the cargo holds " + F((double)inventory.MaxVolume * 1000) + " L, its definition times the world's " +
                                                                                       MySession.Static.Settings.BlocksInventorySizeMultiplier + " says " + F(expected * 1000) + " L");
            var ore = Item("Ore/Stone");
            var fits = (double)inventory.ComputeAmountThatFits(ore);
            Check(fits > 0, "nothing fits into an empty cargo");
            var added = inventory.AddItems((MyFixedPoint)(fits * 2), VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(ore));
            var held = Amount(inventory, "Ore/Stone");
            Check(held <= fits * 1.001, "the cargo took " + F(held) + " kg of stone, " + F(fits) + " kg fit");
            Check(inventory.CurrentVolume <= inventory.MaxVolume, "the cargo holds more than its volume");
            yield return null;
            Result("cargo of " + F((double)inventory.MaxVolume * 1000) + " L (definition x " + MySession.Static.Settings.BlocksInventorySizeMultiplier + "): " + F(fits) +
                   " kg of stone fit, twice that put in " + (added ? "was cut to " + F(held) : "was turned away"));
        }
    }

    /// <summary>A player with a flat suit battery charges it at a medical room, as they ask it for help.</summary>
    public sealed class MedicalRechargeCheck : RegressionCheck
    {
        public override string Name => Prefix + "medical_recharge";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -6));
            var grid = Build("medical-recharge", Site.Origin, true,
                new Part("MedicalRoom/LargeMedicalRoom", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
            var room = Block<SpaceEngineers.Game.Entities.Blocks.MyMedicalRoom>(grid);
            yield return Until(() => room.IsWorking ? 1 : 0, v => v > 0, "the medical room working", 10, () => State(room));
            StandAt(room.PositionComp.GetPosition() + Site.Up * 0.2, room.PositionComp.GetPosition() + Site.Forward);
            Body.SuitBattery.ResourceSource.SetRemainingCapacityByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId, 1E-05f * 0.05f);
            var low = Body.SuitEnergyLevel;
            yield return Until(() =>
                {
                    Request(room, "RequestSupport", Body.EntityId);
                    return Body.SuitEnergyLevel - low;
                }, v => v > 0.1, "the suit charging at the medical room", 20, () => "energy " + F(Body.SuitEnergyLevel * 100) + " %");
            Result("at the medical room the suit charged " + F(low * 100) + " -> " + F(Body.SuitEnergyLevel * 100) + " %");
        }
    }

    /// <summary>A hurt player at a survival kit, asking it for help, is healed.</summary>
    public sealed class SurvivalKitHealCheck : RegressionCheck
    {
        public override string Name => Prefix + "survival_kit_heal";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -6));
            var grid = Build("kit-heal", Site.Origin, true,
                new Part("SurvivalKit/SurvivalKitLarge", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var kit = grid.GetCubeBlock(Vector3I.Zero).FatBlock;
            yield return Until(() => kit.IsWorking ? 1 : 0, v => v > 0, "the survival kit working", 10, () => State(kit));
            StandAt(kit.PositionComp.GetPosition() - Site.Forward * 1.5, kit.PositionComp.GetPosition());
            var health = Body.StatComp.Health;
            health.Value = health.MaxValue * 0.4f;
            var hurt = health.Value;
            yield return Until(() =>
                {
                    Request(kit, "RequestSupport", Body.EntityId);
                    return health.Value - hurt;
                }, v => v >= 5, "the survival kit healing", 15);
            Result("healed at the survival kit " + F(hurt) + " -> " + F(health.Value) + " of " + F(health.MaxValue));
        }
    }
}

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A ship with nobody in it flown by overrides alone, as a script or a timer flies it: a gyro's override turns it, a
    /// thruster's override pushes it.
    /// </summary>
    public sealed class OverridesCheck : RegressionCheck
    {
        public override string Name => Prefix + "overrides";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, 10));
            var ship = Build("overrides", Site.Origin, false, new[]
            {
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("Gyro/LargeBlockGyro", 0, 1, 0),
                new Part("Thrust/LargeBlockSmallThrust", 1, 0, 0).Facing(Direction.Backward),
            });
            var gyro = (Sandbox.ModAPI.Ingame.IMyGyro)Block<MyGyro>(ship);
            var heading = ship.WorldMatrix.Forward;
            gyro.GyroOverride = true;
            gyro.Yaw = 1f;
            for (var f = Frames(3); f > 0; f--) yield return null;
            var turned = Motion.Angle(heading, ship.WorldMatrix.Forward);
            Check(turned > 5, "3 s of the gyro's yaw override turned the ship " + F(turned) + " degrees (" + State((MyCubeBlock)gyro) + ")");
            gyro.Yaw = 0f;
            yield return Until(() => ship.Physics.AngularVelocity.Length(), v => v < 0.05, "the gyro holding the ship still with its override at 0", 10);
            var thruster = (Sandbox.ModAPI.Ingame.IMyThrust)Block<MyThrust>(ship);
            var from = ship.PositionComp.GetPosition();
            thruster.ThrustOverridePercentage = 1f;
            for (var f = Frames(3); f > 0; f--) yield return null;
            var moved = Vector3D.Distance(from, ship.PositionComp.GetPosition());
            thruster.ThrustOverridePercentage = 0f;
            gyro.GyroOverride = false;
            Check(moved > 2, "3 s of the thruster's override moved the ship " + F(moved) + " m (" + State((MyCubeBlock)thruster) + ")");
            Result("with nobody aboard: the gyro's override turned the ship " + F(turned) + " degrees, the thruster's override pushed it " + F(moved) + " m");
        }
    }
}
