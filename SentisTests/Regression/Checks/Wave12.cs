using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using VRage.Game;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;

namespace SentisTests.Regression.Checks
{
    /// <summary>A room of armor round the cell (0,1,0) with an air vent in its floor and a full oxygen tank under it, filled with air.</summary>
    public abstract class VentRoomCheck : RegressionCheck
    {
        protected MyCubeGrid Room;
        protected SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent Vent;
        protected IMyGasTank Tank;

        protected IEnumerator FilledRoom()
        {
            var settings = MySession.Static.Settings;
            SkipUnless(settings.EnableOxygen && settings.EnableOxygenPressurization, "oxygen pressurization is off in this world");
            var vent = new Part("AirVent/", 0, 0, 0).Facing(Direction.Up, Direction.Backward);
            Part tank = null;
            yield return ConveyorNeighbour(vent, "OxygenTank/", p => Cells(p).All(c => c.Y < 0 || Math.Abs(c.X) > 1 || Math.Abs(c.Z) > 1), p => tank = p);
            var tankCells = Cells(tank);
            var parts = new List<Part> { vent, tank.With(b => ((MyObjectBuilder_GasTank)b).FilledRatio = 1f) };
            for (var x = -1; x <= 1; x++)
            for (var y = 0; y <= 2; y++)
            for (var z = -1; z <= 1; z++)
            {
                var cell = new Vector3I(x, y, z);
                if (cell == Vector3I.Zero || cell == new Vector3I(0, 1, 0) || tankCells.Contains(cell)) continue;
                parts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, z));
            }
            var battery = new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b));
            parts.Add(!tankCells.Contains(battery.Min) ? battery : new Part("BatteryBlock/LargeBlockBatteryBlock", -2, 0, 0).With(b => Motion.Charged(b)));
            Room = Build("vent-room", Site.Origin, true, parts);
            Vent = (SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent)Block<SpaceEngineers.Game.Entities.Blocks.MyAirVent>(Room);
            Tank = (IMyGasTank)Block<MyGasTank>(Room);
            Vent.Depressurize = false;
            yield return Until(() => Vent.CanPressurize ? 1 : 0, v => v > 0, "the room airtight for the vent", 10);
            yield return Until(() => Vent.GetOxygenLevel(), v => v > 0.9, "the room's air", 30, () => "tank " + Tank.FilledRatio.ToString("F4") + ", " + State((MyCubeBlock)Vent));
        }
    }

    /// <summary>The air vent switched to depressurize pulls the room's air back into the tank: the room goes empty, the tank fills.</summary>
    public sealed class AirVentDepressurizeCheck : VentRoomCheck
    {
        public override string Name => Prefix + "air_vent_depressurize";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -8));
            yield return FilledRoom();
            var tank = Tank.FilledRatio;
            Vent.Depressurize = true;
            yield return Until(() => Vent.GetOxygenLevel(), v => v < 0.05, "the room emptied by the vent", 30, () => "tank " + Tank.FilledRatio.ToString("F4"));
            Check(Tank.FilledRatio > tank, "the room's air went and the tank took none of it (" + F(tank * 100) + " -> " + F(Tank.FilledRatio * 100) + " %)");
            Result("depressurized: the room's air " + F(Vent.GetOxygenLevel() * 100) + " %, the tank " + F(tank * 100) + " -> " + F(Tank.FilledRatio * 100) + " %");
        }
    }

    /// <summary>A hole in the wall of a room full of air: the air goes out at once, the room is not airtight any more.</summary>
    public sealed class HullBreachCheck : VentRoomCheck
    {
        public override string Name => Prefix + "hull_breach";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -8));
            yield return FilledRoom();
            var wall = Room.GetCubeBlock(new Vector3I(1, 1, 0));
            Check(wall != null, "no wall block beside the room's cell");
            wall.DoDamage(wall.MaxIntegrity * 10, MyDamageType.Bullet, true, null, 0);
            yield return Until(() => Vent.GetOxygenLevel(), v => v < 0.05, "the air out of the holed room", 15, () => "wall " + (Room.GetCubeBlock(new Vector3I(1, 1, 0)) == null ? "gone" : "there"));
            Check(!Vent.CanPressurize, "the vent says the holed room is airtight");
            Result("the wall shot through, the room's air " + F(Vent.GetOxygenLevel() * 100) + " % at once, not airtight");
        }
    }

    /// <summary>A gatling turret on the conveyors of a container with ammunition takes its magazines by itself.</summary>
    public sealed class TurretAmmoConveyorCheck : RegressionCheck
    {
        public override string Name => Prefix + "turret_ammo_conveyor";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var turret = new Part("LargeGatlingTurret/", 0, 0, 0);
            Part cargo = null;
            yield return ConveyorNeighbour(turret, "CargoContainer/LargeBlockSmallContainer", null, p => cargo = p);
            var used = Cells(cargo);
            used.UnionWith(Cells(turret));
            var free = used.SelectMany(c => Base6Directions.IntDirections.Select(d => c + (Vector3I)d)).First(c => !used.Contains(c));
            var grid = Build("turret-ammo", Site.Origin, true, turret, cargo, new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z).With(b => Motion.Charged(b)));
            var gun = (MyCubeBlock)grid.GetCubeBlock(Vector3I.Zero).FatBlock;
            var definition = (MyWeaponBlockDefinition)gun.BlockDefinition;
            var magazine = MyDefinitionManager.Static.GetWeaponDefinition(definition.WeaponDefinitionId).AmmoMagazinesId[0];
            var box = Inventory((MyCubeBlock)grid.GetCubeBlock(cargo.Min).FatBlock);
            box.AddItems(10, (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(magazine));
            var own = Inventory(gun);
            yield return Until(() => (double)own.GetItemAmount(magazine), v => v > 0, "the turret's magazines from the container", 30, () => State(gun));
            Result("the turret took " + own.GetItemAmount(magazine) + " " + magazine.SubtypeName + " from the container on its conveyors (" + box.GetItemAmount(magazine) + " left there)");
        }
    }

    /// <summary>A ship grinder grinding a container away takes what was in it along with the container's components.</summary>
    public sealed class GrindContainerCheck : RegressionCheck
    {
        public override string Name => Prefix + "grind_container";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, 8));
            var target = Build("grind-box", Site.Origin, true, new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0));
            Put(Inventory((MyCubeBlock)target.GetCubeBlock(Vector3I.Zero).FatBlock), "Ore/Iron", 50);
            var rig = ToolRig("box-grinder", "ShipGrinder/LargeShipGrinder", Site.Origin - Site.Forward * 1.0, Site.Forward);
            var grinder = Block<MyShipGrinder>(rig);
            Switch(grinder, true);
            yield return Until(() => target.MarkedForClose || target.GetCubeBlock(Vector3I.Zero) == null ? 1 : 0, v => v > 0, "the container ground away", 120,
                () => State(grinder) + "; integrity " + F(target.GetCubeBlock(Vector3I.Zero)?.Integrity ?? 0));
            yield return Seconds(1);
            var ore = Amount(Inventory(grinder), "Ore/Iron");
            var floating = MyEntities.GetEntities().OfType<MyFloatingObject>()
                .Where(f => Vector3D.Distance(f.PositionComp.GetPosition(), Site.Origin) < 20 && f.Item.Content.GetObjectId() == Item("Ore/Iron")).Sum(f => (double)f.Item.Amount);
            Check(ore + floating >= 49.99, "of the 50 kg of ore in the container the grinder took " + F(ore) + " kg, " + F(floating) + " kg lies about");
            Result("the container ground away: its 50 kg of ore " + F(ore) + " kg in the grinder" + (floating > 0 ? ", " + F(floating) + " kg spilt" : ""));
        }
    }

    /// <summary>A reactor with "Use conveyor system" off takes no uranium from the container on its conveyors; on, it does.</summary>
    public sealed class UseConveyorOffCheck : RegressionCheck
    {
        public override string Name => Prefix + "use_conveyor_off";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var reactorPart = new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0).With(b => ((MyObjectBuilder_FunctionalBlock)b).Enabled = true);
            Part cargo = null;
            yield return ConveyorNeighbour(reactorPart, "CargoContainer/LargeBlockSmallContainer", null, p => cargo = p);
            var used = Cells(cargo);
            used.Add(Vector3I.Zero);
            var free = used.SelectMany(c => Base6Directions.IntDirections.Select(d => c + (Vector3I)d)).First(c => !used.Contains(c));
            var grid = Build("conveyor-off", Site.Origin, true, reactorPart, cargo, new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z).With(b => Motion.Charged(b)));
            var reactor = Block<MyReactor>(grid);
            ((Sandbox.ModAPI.IMyTerminalBlock)reactor).SetValueBool("UseConveyor", false);
            Put(Inventory((MyCubeBlock)grid.GetCubeBlock(cargo.Min).FatBlock), "Ingot/Uranium", 10);
            yield return Until(() => ConveyorsReady(reactor) ? 1 : 0, v => v > 0, "the reactor's conveyors worked out", 30);
            yield return Seconds(8);
            var off = Amount(Inventory(reactor), "Ingot/Uranium");
            Check(off == 0, "with \"Use conveyor system\" off the reactor took " + F(off) + " kg of uranium");
            ((Sandbox.ModAPI.IMyTerminalBlock)reactor).SetValueBool("UseConveyor", true);
            yield return Until(() => Amount(Inventory(reactor), "Ingot/Uranium"), v => v > 0, "the reactor pulling uranium with the conveyor system on", 30, () => State(reactor));
            Result("conveyor system off: no uranium taken in 8 s; on: " + F(Amount(Inventory(reactor), "Ingot/Uranium")) + " kg pulled");
        }
    }

    /// <summary>
    /// Battery modes: on recharge the battery only takes power (a reactor charges it, it gives nothing); on discharge it
    /// takes none and powers the grid alone with the reactor off.
    /// </summary>
    public sealed class BatteryModesCheck : RegressionCheck
    {
        public override string Name => Prefix + "battery_modes";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var grid = Build("battery-modes", Site.Origin, true,
                new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => ((MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f),
                new Part("InteriorLight/SmallLight", 0, 1, 0));
            var reactor = Block<MyReactor>(grid);
            Put(Inventory(reactor), "Ingot/Uranium", 5);
            var battery = (IMyBatteryBlock)Block<Sandbox.Game.Entities.MyBatteryBlock>(grid);
            var light = Block<Sandbox.Game.Entities.Blocks.MyLightingBlock>(grid);
            battery.ChargeMode = ChargeMode.Recharge;
            yield return Until(() => battery.CurrentInput, v => v > 0, "the battery on recharge taking power", 15, () => "output " + F(battery.CurrentOutput) + " MW, " + State(reactor));
            Check(battery.CurrentOutput == 0, "the battery on recharge gives " + F(battery.CurrentOutput) + " MW");
            var input = battery.CurrentInput;
            battery.ChargeMode = ChargeMode.Discharge;
            Switch(reactor, false);
            yield return Until(() => battery.CurrentOutput, v => v > 0, "the battery on discharge powering the light", 15, () => "input " + F(battery.CurrentInput) + " MW, light working " + light.IsWorking);
            Check(battery.CurrentInput == 0, "the battery on discharge takes " + F(battery.CurrentInput) + " MW");
            Check(light.IsWorking, "the light is out with the battery discharging");
            Result("recharge: took " + F(input) + " MW, gave 0; discharge: took 0, gave " + F(battery.CurrentOutput) + " MW to the light with the reactor off");
        }
    }

    /// <summary>An airtight hangar door opens and closes all the way.</summary>
    public sealed class HangarDoorCheck : RegressionCheck
    {
        public override string Name => Prefix + "hangar_door";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -12));
            var grid = Build("hangar", Site.Origin, true,
                new Part("AirtightHangarDoor/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var door = (IMyDoor)Block<Sandbox.Game.Entities.MyAirtightHangarDoor>(grid);
            door.CloseDoor();
            yield return Until(() => door.Status == DoorStatus.Closed ? 1 : 0, v => v > 0, "the hangar door closed", 30, () => door.Status + ", " + State((MyCubeBlock)door));
            door.OpenDoor();
            yield return Until(() => door.Status == DoorStatus.Open ? 1 : 0, v => v > 0, "the hangar door open", 30, () => door.Status + " " + F(door.OpenRatio * 100) + " %");
            door.CloseDoor();
            yield return Until(() => door.Status == DoorStatus.Closed ? 1 : 0, v => v > 0, "the hangar door closed again", 30, () => door.Status + " " + F(door.OpenRatio * 100) + " %");
            Result("hangar door opened and closed");
        }
    }

    /// <summary>
    /// Artificial mass: a free ship with an artificial mass block in a gravity generator's field falls along it; with the
    /// block off it stays.
    /// </summary>
    public sealed class ArtificialMassCheck : RegressionCheck
    {
        public override string Name => Prefix + "artificial_mass";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -30));
            var station = Build("mass-field", Site.Origin, true,
                new Part("GravityGenerator/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var generator = Block<SpaceEngineers.Game.Entities.Blocks.MyGravityGenerator>(station);
            yield return Until(() => generator.IsWorking ? 1 : 0, v => v > 0, "the gravity generator working", 10, () => State(generator));
            var ship = Build("mass-ship", station.GridIntegerToWorld(Vector3I.Zero) + Site.Up * 15, false,
                new Part("VirtualMass/VirtualMassLarge", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var block = (MyCubeBlock)ship.GetCubeBlock(Vector3I.Zero).FatBlock;
            Switch(block, false);
            yield return Seconds(1);
            ship.Physics.LinearVelocity = Vector3.Zero;
            yield return Seconds(2);
            var still = Motion.Speed(ship);
            Check(still < 0.3, "the ship with its artificial mass off moves at " + F(still) + " m/s in the field");
            Switch(block, true);
            yield return Until(() => Motion.Speed(ship), v => v > 1, "the ship with artificial mass falling in the field", 10, () => State(block) + ", gravity " + F(generator.GetGravityMultiplier(ship.PositionComp.GetPosition())));
            Result("artificial mass off: " + F(still) + " m/s; on: the ship falls in the field (" + F(Motion.Speed(ship)) + " m/s)");
        }
    }

    /// <summary>A spherical gravity generator pulls a player in its field towards its centre.</summary>
    public sealed class SphericalGravityCheck : RegressionCheck
    {
        public override string Name => Prefix + "spherical_gravity";

        protected override IEnumerator Check()
        {
            var station = Build("sphere-field", Site.Origin, true,
                new Part("GravityGeneratorSphere/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var generator = Block<SpaceEngineers.Game.Entities.Blocks.MyGravityGeneratorSphere>(station);
            yield return Until(() => generator.IsWorking ? 1 : 0, v => v > 0, "the spherical gravity generator working", 10, () => State(generator));
            var centre = station.GridIntegerToWorld(Vector3I.Zero);
            yield return Arrive(centre + Site.Right * 20);
            Body.JetpackComp?.TurnOnJetpack(false);
            yield return Seconds(2);
            var gravity = Body.Gravity;
            var towards = Vector3D.Normalize(centre - Body.PositionComp.GetPosition());
            Check(gravity.Length() > 1, "the player in the field feels " + F(gravity.Length()) + " m/s2");
            var dot = Vector3D.Dot(Vector3D.Normalize(gravity), towards);
            Check(dot > 0.95, "the field pulls the player off the centre (cos " + F(dot) + ")");
            Result("in the spherical field " + F(gravity.Length()) + " m/s2 towards the centre (cos " + F(dot) + ")");
        }
    }

    /// <summary>A warhead's countdown: armed and started at 3 s, it blows up after it, not before.</summary>
    public sealed class WarheadCountdownCheck : RegressionCheck
    {
        public override string Name => Prefix + "warhead_countdown";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 40, -40));
            var grid = Build("countdown", Site.Origin, true, new Part("Warhead/LargeWarhead", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            var warhead = Block<MyWarhead>(grid);
            var ingame = (IMyWarhead)warhead;
            ((Sandbox.ModAPI.IMyTerminalBlock)warhead).SetValueBool("Safety", true);
            ingame.DetonationTime = 3;
            ingame.StartCountdown();
            yield return Seconds(2);
            Check(!warhead.MarkedForClose && !warhead.Closed, "the warhead blew up before its 3 s countdown ended");
            Check(ingame.IsCountingDown, "the warhead is not counting down");
            yield return Until(() => warhead.MarkedForClose || warhead.Closed ? 1 : 0, v => v > 0, "the warhead blown up after the countdown", 5, () => "time left " + F(ingame.DetonationTime));
            Result("counted down from 3 s and blew up after it");
        }
    }

    /// <summary>
    /// A container blown apart lets out what it held: in a loot bag where the world has temporary containers, else as
    /// floating objects. Ore comes out whole (components only by their drop chance).
    /// </summary>
    public sealed class DestroyedCargoSpillsCheck : RegressionCheck
    {
        public override string Name => Prefix + "destroyed_cargo_spills";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -12));
            var grid = Build("spill-box", Site.Origin, true, new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 0, -1, 0));
            var box = grid.GetCubeBlock(Vector3I.Zero);
            Put(Inventory(box.FatBlock), "Ore/Iron", 100);
            var ore = Item("Ore/Iron");
            box.DoDamage(box.MaxIntegrity * 10, MyDamageType.Explosion, true, null, 0);
            Func<double> floating = () => MyEntities.GetEntities().OfType<MyFloatingObject>()
                .Where(f => !f.MarkedForClose && Vector3D.Distance(f.PositionComp.GetPosition(), Site.Origin) < 30 && f.Item.Content.GetObjectId() == ore).Sum(f => (double)f.Item.Amount);
            Func<double> bagged = () => MyEntities.GetEntities().OfType<Sandbox.Game.Entities.MyCargoContainerInventoryBagEntity>()
                .Where(b => !b.MarkedForClose && Vector3D.Distance(b.PositionComp.GetPosition(), Site.Origin) < 30).Sum(b => (double)(b.GetInventory()?.GetItemAmount(ore) ?? 0));
            yield return Until(() => floating() + bagged(), v => v >= 99.99, "the destroyed container's 100 kg of ore let out", 10, () => "block " + (grid.GetCubeBlock(Vector3I.Zero) == null ? "gone" : "there") + ", floating " + F(floating()) + ", in bags " + F(bagged()));
            var inBag = bagged();
            foreach (var bag in MyEntities.GetEntities().OfType<Sandbox.Game.Entities.MyCargoContainerInventoryBagEntity>().Where(b => Vector3D.Distance(b.PositionComp.GetPosition(), Site.Origin) < 30).ToList()) bag.Close();
            Result("the container destroyed, its 100 kg of ore " + (inBag > 0 ? "in a loot bag (" + F(inBag) + " kg)" : "floating about") + (MySession.Static.Settings.TemporaryContainers ? ", the world has temporary containers" : ""));
        }
    }

    /// <summary>A ship running into a player standing in its way hurts them.</summary>
    public sealed class RunOverCheck : RegressionCheck
    {
        public override string Name => Prefix + "run_over";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.Origin);
            Body.JetpackComp?.TurnOnJetpack(true);
            Body.StatComp.Health.Value = Body.StatComp.Health.MaxValue;
            var health = Body.StatComp.Health.Value;
            var ship = Build("run-over", Site.Origin - Site.Forward * 30, false, Bodies.Plate(MyCubeSize.Large), MyCubeSize.Large);
            yield return null;
            ship.Physics.LinearVelocity = (Vector3)(Site.Forward * 25);
            var launched = ship.PositionComp.WorldAABB.Center;
            yield return Until(() => health - (Body.IsDead ? 0 : Body.StatComp.Health.Value), v => v > 0, "the player hurt by the ship", 5,
                () => "ship " + F(Vector3D.Distance(ship.PositionComp.WorldAABB.Center, Body.PositionComp.GetPosition())) + " m off at " + F(Motion.Speed(ship)) + " m/s, " +
                      F(Vector3D.Distance(launched, ship.PositionComp.WorldAABB.Center)) + " m gone, physics active " + ship.Physics?.IsActive + ", in world " + ship.Physics?.IsInWorld +
                      "; ahead of it " + Ahead(ship) + "; the player at " + F(Vector3D.Distance(Body.PositionComp.GetPosition(), Site.Origin)) + " m from the site, physics " + (Body.Physics?.Enabled == true));
            var lost = health - (Body.IsDead ? 0 : Body.StatComp.Health.Value);
            ship.Close();
            Result("run over at 25 m/s: the player lost " + F(lost) + " health" + (Body.IsDead ? " (dead)" : ""));
        }

        /// <summary>The entities in the ship's way to the site, nearest first.</summary>
        private string Ahead(MyCubeGrid ship)
        {
            var line = new LineD(ship.PositionComp.WorldAABB.Center, Site.Origin + Site.Forward * 5);
            var found = new List<MyLineSegmentOverlapResult<VRage.Game.Entity.MyEntity>>();
            MyGamePruningStructure.GetTopmostEntitiesOverlappingRay(ref line, found);
            return string.Join(", ", found.Where(r => r.Element != ship).OrderBy(r => r.Distance).Take(5)
                .Select(r => (r.Element.DisplayName ?? r.Element.GetType().Name) + " " + F(r.Distance) + " m" + (r.Element.MarkedForClose ? " (closed)" : "")));
        }
    }

    /// <summary>The sun goes round: its direction turns at the world's rotation rate (a full turn in the interval set).</summary>
    public sealed class SunRotationCheck : RegressionCheck
    {
        public override string Name => Prefix + "sun_rotation";

        protected override IEnumerator Check()
        {
            var settings = MySession.Static.Settings;
            SkipUnless(settings.EnableSunRotation && settings.SunRotationIntervalMinutes > 0, "the sun does not turn in this world");
            yield return Arrive(Site.At(0, 0, -3));
            var from = (Vector3D)MySector.DirectionToSunNormalized;
            var time = MySession.Static.ElapsedGameTime;
            yield return Seconds(10);
            var to = (Vector3D)MySector.DirectionToSunNormalized;
            var passed = (MySession.Static.ElapsedGameTime - time).TotalSeconds;
            var angle = Math.Acos(Math.Min(1, Vector3D.Dot(from, to)));
            var expected = 2 * Math.PI * passed / (settings.SunRotationIntervalMinutes * 60);
            // the sun goes round an axis tilted to its direction: the angle it turns is no more than the full rate's
            Check(angle > expected * 0.2 && angle < expected * 1.5, "the sun turned " + angle.ToString("F5") + " rad in " + F(passed) + " s, at the world's rate " + expected.ToString("F5"));
            Result("the sun turned " + angle.ToString("F5") + " rad in " + F(passed) + " s of game time (a full turn in " + settings.SunRotationIntervalMinutes + " min: " + expected.ToString("F5") + ")");
        }
    }
}
