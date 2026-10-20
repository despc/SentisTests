using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage;
using VRage.Game;
using Sandbox.ModAPI.Interfaces;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A six-wheeled rover (wheel_perf's) on a floor on a planet, driven by its pilot: forward on the keys, it rolls on
    /// its wheels along the floor; forward and right, its wheels steer and it turns; on the handbrake the pilot puts on,
    /// forward moves it nowhere.
    /// </summary>
    public sealed class WheelsCheck : RegressionCheck
    {
        public override string Name => Prefix + "wheels";
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            var pad = Pad("wheels-pad", Site.Origin, 14);
            var floor = pad.PositionComp.GetPosition() + Site.Up * 1.25;
            yield return Arrive(floor + Site.Up * 3 + Site.Right * 20);
            var group = WorldApi.LoadAuthoredGroup(Scenarios.WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + "reg-rover");
            var chassis0 = group[0].PositionAndOrientation.Value.GetMatrix();
            var toChassis = MatrixD.Invert(chassis0);
            // the lowest point of the wheels along the chassis' up (the blueprint stands upright): the rover put down
            // with them half a metre over the floor
            var low = group.Skip(1).Min(w =>
            {
                var definition = Sandbox.Definitions.MyDefinitionManager.Static.GetCubeBlockDefinition(w.CubeBlocks[0].GetId());
                var radius = Math.Max(definition.Size.X, Math.Max(definition.Size.Y, definition.Size.Z)) * (w.GridSizeEnum == MyCubeSize.Large ? 2.5 : 0.5) / 2;
                return Vector3D.Transform((Vector3D)w.PositionAndOrientation.Value.Position, toChassis).Y - radius;
            });
            var chassis = MatrixD.CreateWorld(floor + Site.Up * (0.5 - low), Site.Forward, Site.Up);
            var move = toChassis * chassis;
            foreach (var ob in group)
            {
                ob.PositionAndOrientation = new MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
                ob.IsStatic = false;
                ob.Handbrake = false;
                foreach (var block in ob.CubeBlocks)
                {
                    block.Owner = Identity;
                    block.BuiltBy = Identity;
                }
            }
            var grids = group.Select(WorldApi.SpawnGrid).ToList();
            foreach (var grid in grids) Track(grid);
            var rover = grids[0];
            WorldApi.ChargeBatteries(rover);
            var seat = Block<MyCockpit>(rover);
            // down on its wheels and still before the drive
            yield return Seconds(1);
            yield return Until(() => Motion.Speed(rover), v => v < 1, "the rover settling on the floor, m/s", 30);
            // the wheels held to 20 km/h in their terminals: the drive, the stop and the turn all on the floor
            foreach (var wheel in Blocks<MyMotorSuspension>(rover))
                ((Sandbox.ModAPI.IMyTerminalBlock)wheel).SetValueFloat("Speed Limit", 20f);
            yield return Sit(seat);
            var from = rover.PositionComp.WorldAABB.Center;
            var standing = Vector3D.Dot(from - floor, Site.Up);
            yield return HoldShip(3, seat, new Vector3(0, 0, -1));
            var gone = Vector3D.Reject(rover.PositionComp.WorldAABB.Center - from, Site.Up).Length();
            var height = Vector3D.Dot(rover.PositionComp.WorldAABB.Center - floor, Site.Up);
            Check(gone > 2, "3 s of forward on the keys rolled the rover " + F(gone) + " m (" + F(Motion.Speed(rover)) + " m/s; " +
                            string.Join("; ", Blocks<MyMotorSuspension>(rover).Select(w => State(w) + ", wheel " + (((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)w).IsAttached ? "on" : "off"))) +
                            "; wheels in the seat " + ((Sandbox.ModAPI.Ingame.IMyShipController)seat).ControlWheels + ", handbrake " + ((Sandbox.ModAPI.Ingame.IMyShipController)seat).HandBrake + ")");
            Check(height > 0 && height > standing - 2, "the rover went through the floor: its middle " + F(height) + " m over it, from " + F(standing));
            // braked to a stop: space puts the brakes on in the pilot's game (MyShipController.TryEnableBrakes), and each
            // wheel's brake comes to the server in its own sync
            Func<double> speed = () => { ShipKeys(seat, Vector3.Zero); seat.TryEnableBrakes(true); return Motion.Speed(rover); };
            yield return Until(speed, v => v < 0.5, "the rover braking to a stop, m/s", 20, () => "its middle " + F(Vector3D.Dot(rover.PositionComp.WorldAABB.Center - floor, Site.Up)) + " m over the floor, brakes " + seat.AreBreaksOn);
            seat.TryEnableBrakes(false);
            // forward and right on the keys: the front wheels steer, the rover turns
            var heading = Vector3D.Reject(rover.WorldMatrix.Forward, Site.Up);
            var steer = 0.0;
            for (var f = Frames(3); f > 0; f--)
            {
                ShipKeys(seat, new Vector3(1, 0, -1));
                steer = Math.Max(steer, Blocks<MyMotorSuspension>(rover).Max(w => Math.Abs((double)((Sandbox.ModAPI.Ingame.IMyMotorSuspension)w).SteerAngle)));
                yield return null;
            }
            var turned = Motion.Angle(heading, Vector3D.Reject(rover.WorldMatrix.Forward, Site.Up));
            Check(steer > 0.05, "right on the keys steered no wheel (largest angle " + F(MathHelper.ToDegrees(steer)) + " degrees)");
            Check(turned > 5, "3 s of forward and right turned the rover " + F(turned) + " degrees (wheels steered up to " + F(MathHelper.ToDegrees(steer)) + ")");
            // stopped again, the handbrake on as the pilot puts it on: forward on the keys moves it nowhere
            yield return Until(speed, v => v < 0.5, "the rover braking to a stop after the turn, m/s", 20, () => "its middle " + F(Vector3D.Dot(rover.PositionComp.WorldAABB.Center - floor, Site.Up)) + " m over the floor, brakes " + seat.AreBreaksOn);
            seat.TryEnableBrakes(false);
            Request(rover, "ToggleHandbrakeRequest");
            Check(((Sandbox.ModAPI.Ingame.IMyShipController)seat).HandBrake, "the handbrake did not go on");
            var parked = rover.PositionComp.WorldAABB.Center;
            yield return HoldShip(3, seat, new Vector3(0, 0, -1));
            var crept = Vector3D.Reject(rover.PositionComp.WorldAABB.Center - parked, Site.Up).Length();
            Check(crept < 1, "with the handbrake on 3 s of forward rolled the rover " + F(crept) + " m");
            Request(rover, "ToggleHandbrakeRequest");
            Check(!((Sandbox.ModAPI.Ingame.IMyShipController)seat).HandBrake, "the handbrake did not come off");
            Result("rover rolled " + F(gone) + " m on its wheels in 3 s, on the floor; steered " + F(turned) + " degrees (wheels up to " + F(MathHelper.ToDegrees(steer)) +
                   "); on the handbrake it rolled " + F(crept) + " m");
        }
    }

    /// <summary>A ship with a charged jump drive in open space jumps 20 km on its pilot's request after the countdown.</summary>
    public sealed class JumpDriveCheck : RegressionCheck
    {
        public override string Name => Prefix + "jump_drive";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, 8));
            var definition = (Sandbox.Definitions.MyJumpDriveDefinition)Definition("JumpDrive/LargeJumpDrive");
            var ship = Build("jump", Site.Origin, false,
                new Part("JumpDrive/LargeJumpDrive", 0, 0, 0).With(b => ((MyObjectBuilder_JumpDrive)b).StoredPower = definition.PowerNeededForJump),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 3, 0, 0).With(b => Motion.Charged(b)),
                new Part("Cockpit/LargeBlockCockpitSeat", 3, 1, 0));
            var drive = Block<MyJumpDrive>(ship);
            yield return Until(() => drive.IsWorking ? 1 : 0, v => v > 0, "the jump drive working", 10, () => State(drive));
            var from = ship.PositionComp.GetPosition();
            var target = from + Site.Up * 20000;
            Request(ship, "OnJumpRequested", target, Identity, 10f, (long?)null);
            yield return Until(() => ship.GridSystems.JumpSystem.IsJumping ? 1 : 0, v => v > 0, "the jump counting down", 5);
            yield return Until(() => Vector3D.Distance(ship.PositionComp.GetPosition(), from), v => v > 15000, "the ship jumped, m", 30);
            Result("jumped " + F(Vector3D.Distance(ship.PositionComp.GetPosition(), from) / 1000) + " km");
        }
    }

    /// <summary>
    /// A player dies and asks to respawn: at their medical room or their survival kit a new astronaut of theirs stands at
    /// it; in a respawn ship (in space, or a rover on the planet), a new astronaut sits in a ship of their own.
    /// </summary>
    public sealed class RespawnCheck : RegressionCheck
    {
        private readonly string _at;

        /// <param name="at">the block respawned at, or a respawn ship's id ("RespawnSpace", "RespawnRover" - on a planet)</param>
        public RespawnCheck(string at = "MedicalRoom/LargeMedicalRoom") { _at = at; }

        private bool Ship => !_at.Contains("/");

        public override string Name => Prefix + "respawn" + (_at.StartsWith("MedicalRoom") ? "" : _at.StartsWith("SurvivalKit") ? "_survival_kit" : Rover ? "_rover" : "_ship");

        private bool Rover => _at == "RespawnRover";

        public override SiteKind Kind => Rover ? SiteKind.Planet : SiteKind.Space;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            if (Ship)
            {
                SkipUnless(MySession.Static.Settings.EnableRespawnShips, "respawn ships are off in this world");
                SkipUnless(Sandbox.Definitions.MyDefinitionManager.Static.GetRespawnShipDefinition(_at) != null, "no respawn ship " + _at + " in this game");
            }
            yield return Arrive(Site.At(0, 6, -10));
            MyCubeBlock at = null;
            if (!Ship)
            {
                var grid = Build("respawn", Site.Origin, true,
                    new Part(_at, 0, 0, 0),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
                at = grid.GetCubeBlock(Vector3I.Zero).FatBlock;
                yield return Until(() => at.IsWorking ? 1 : 0, v => v > 0, "the " + _at + " working", 10, () => State(at));
            }
            var body = Body;
            body.DoDamage(1000f, MyDamageType.Suicide, true, attackerId: 0);
            yield return Until(() => body.IsDead ? 1 : 0, v => v > 0, "the player dead", 10);
            var message = new Sandbox.Game.Multiplayer.MyPlayerCollection.RespawnMsg
            {
                JoinGame = false,
                NewIdentity = false,
                // a rover is asked for on the planet chosen in the respawn screen
                RespawnEntityId = at?.EntityId ?? (Rover ? Site.Planet.EntityId : 0),
                RespawnShipId = Ship ? _at : null,
                PlayerSerialId = 0,
                Color = Color.Red,
            };
            Request(typeof(Sandbox.Game.Multiplayer.MyPlayerCollection), "OnRespawnRequest", message);
            var pods = new HashSet<long>(MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.EntityId));
            yield return Until(() => Body != null && Body != body && !Body.IsDead ? 1 : 0, v => v > 0, "a new astronaut", Ship ? 60 : 20,
                () => "the player's character " + (Player.Character == null ? "none" : Player.Character == body ? "the dead one" : Player.Character.EntityId + (Player.Character.IsDead ? " dead" : "")) +
                      ", identity dead " + Player.Identity.IsDead + ", grids of the player's " +
                      MyEntities.GetEntities().OfType<MyCubeGrid>().Count(g => g.BigOwners.Contains(Identity)) + ", new grids " +
                      string.Join(", ", MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !pods.Contains(g.EntityId)).Select(g => g.DisplayName).Take(5)));
            Track(Body);
            if (Ship)
            {
                yield return Until(() => Body.Parent is MyCockpit ? 1 : 0, v => v > 0, "the new astronaut in the ship's seat", 10);
                var pod = ((MyCockpit)Body.Parent).CubeGrid;
                Track(pod);
                Check(pod.BigOwners.Contains(Identity), "the respawn ship is not the player's");
                Result("died and respawned in " + _at + " (" + pod.DisplayName + ", " + pod.BlocksCount + " blocks)");
                yield break;
            }
            var off = Vector3D.Distance(Body.PositionComp.GetPosition(), at.PositionComp.GetPosition());
            Check(off < 10, "the new astronaut is " + F(off) + " m from the " + _at);
            Result("died and respawned " + F(off) + " m from the " + _at);
        }
    }

    /// <summary>A player drops plates from their inventory into space and picks them up again: none lost, none made.</summary>
    public sealed class DropPickupCheck : RegressionCheck
    {
        public override string Name => Prefix + "drop_pickup";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.Origin);
            var pocket = Inventory(Body);
            Put(pocket, "Component/SteelPlate", 20);
            var before = new HashSet<long>(MyEntities.GetEntities().OfType<MyFloatingObject>().Select(f => f.EntityId));
            Request(pocket, "DropItem_Implementation", (MyFixedPoint)8, pocket.GetItems().First(i => i.Content.SubtypeName == "SteelPlate").ItemId);
            MyFloatingObject dropped = null;
            yield return Until(() =>
            {
                dropped = MyEntities.GetEntities().OfType<MyFloatingObject>().FirstOrDefault(f => !before.Contains(f.EntityId) &&
                                                                                                    Vector3D.Distance(f.PositionComp.GetPosition(), Site.Origin) < 20);
                return dropped != null ? 1 : 0;
            }, v => v > 0, "the dropped plates floating", 10);
            Track(dropped);
            Check(Amount(pocket, "Component/SteelPlate") == 12, "the player has " + Amount(pocket, "Component/SteelPlate") + " plates after dropping 8 of 20");
            Check((double)dropped.Item.Amount == 8, "8 plates dropped, " + dropped.Item.Amount + " floating");
            Request(pocket, "PickupItem_Implementation", dropped.EntityId, (MyFixedPoint)8);
            yield return Until(() => Amount(pocket, "Component/SteelPlate"), v => v >= 20, "the plates picked up", 10);
            yield return Until(() => dropped.MarkedForClose ? 1 : 0, v => v > 0, "the floating plates gone", 5);
            Check(Amount(pocket, "Component/SteelPlate") == 20, "the player has " + Amount(pocket, "Component/SteelPlate") + " plates after picking 8 up again");
            Result("8 plates dropped and picked up, 20 in the pocket as before");
        }
    }

    /// <summary>A line of three blocks loses its middle one: the two ends become grids of their own.</summary>
    public sealed class GridSplitCheck : RegressionCheck
    {
        public override string Name => Prefix + "grid_split";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -6));
            var grid = Build("split", Site.Origin, false,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 2, 0, 0));
            var far = grid.GridIntegerToWorld(new Vector3I(2, 0, 0));
            var before = new HashSet<long>(MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.EntityId));
            grid.RazeBlock(new Vector3I(1, 0, 0));
            MyCubeGrid piece = null;
            yield return Until(() =>
            {
                piece = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !before.Contains(g.EntityId) && !g.MarkedForClose &&
                                                                                       Vector3D.Distance(g.PositionComp.WorldAABB.Center, far) < 3);
                return piece != null ? 1 : 0;
            }, v => v > 0, "the far end a grid of its own", 10, () => "the grid has " + grid.BlocksCount + " blocks");
            Track(piece);
            Check(grid.BlocksCount == 1 && piece.BlocksCount == 1, "after the split: " + grid.BlocksCount + " and " + piece.BlocksCount + " blocks");
            Result("the middle block gone, the line split into two grids of one block each");
        }
    }

    /// <summary>
    /// A ship on hydrogen thrusters fed from a full tank on its conveyors, flown by its pilot: forward on the keys, it
    /// goes, and the tank empties a little.
    /// </summary>
    public sealed class HydrogenThrustCheck : RegressionCheck
    {
        public override string Name => Prefix + "hydrogen_thrust";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, 10));
            var tank = new Part("OxygenTank/LargeHydrogenTankSmall", 0, 0, 0)
                .With(b => ((MyObjectBuilder_GasTank)b).FilledRatio = 1f);
            // two thrusters on the tank's conveyor ports, facing opposite ways along one axis
            Part back = null, front = null;
            yield return ConveyorNeighbour(tank, "Thrust/LargeBlockSmallHydrogenThrust", null, p => back = p);
            var backCells = Cells(back);
            var opposite = Base6Directions.GetFlippedDirection(back.Forward);
            yield return ConveyorNeighbour(tank, "Thrust/LargeBlockSmallHydrogenThrust", p => p.Forward == opposite && !Cells(p).Overlaps(backCells), p => front = p);
            var taken = new HashSet<Vector3I>(Cells(tank).Concat(backCells).Concat(Cells(front)));
            var free = Enumerable.Range(1, 6).Select(x => new Vector3I(-x, 0, 0)).Where(c => !taken.Contains(c)).Take(2).ToList();
            var ship = Build("hydrogen-thrust", Site.Origin, false, new[]
            {
                tank, back, front,
                new Part("Cockpit/LargeBlockCockpitSeat", free[0].X, free[0].Y, free[0].Z),
                new Part("BatteryBlock/LargeBlockBatteryBlock", free[1].X, free[1].Y, free[1].Z).With(b => Motion.Charged(b)),
            });
            var seat = Block<MyCockpit>(ship);
            var gas = (Sandbox.ModAPI.Ingame.IMyGasTank)Block<Sandbox.Game.Entities.Blocks.MyGasTank>(ship);
            yield return Sit(seat);
            var full = gas.FilledRatio;
            var from = ship.PositionComp.GetPosition();
            // the keys along the thrusters' axis (the seat faces the grid's forward)
            yield return HoldShip(3, seat, -(Vector3)Base6Directions.GetVector(back.Forward));
            var gone = Vector3D.Distance(ship.PositionComp.GetPosition(), from);
            Check(gone > 2, "3 s on hydrogen thrusters moved the ship " + F(gone) + " m (" +
                            string.Join("; ", Blocks<MyThrust>(ship).Select(t => State(t) + ", thrust " + F(t.CurrentStrength))) + ")");
            Check(gas.FilledRatio < full, "the thrusters burnt no hydrogen from the tank (" + full.ToString("F5") + ")");
            Result("hydrogen thrusters moved the ship " + F(gone) + " m in 3 s, the tank " + (full * 100).ToString("F2") + " -> " + (gas.FilledRatio * 100).ToString("F2") + " %");
        }
    }
}
