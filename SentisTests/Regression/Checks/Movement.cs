using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;

namespace SentisTests.Regression.Checks
{
    internal static class Motion
    {
        public static double Speed(VRage.ModAPI.IMyEntity entity) => entity.Physics?.LinearVelocity.Length() ?? 0;

        public static double Hydrogen(Sandbox.Game.Entities.Character.MyCharacter character) =>
            character.OxygenComponent?.GetGasFillLevel(MyCharacterOxygenComponent.HydrogenId) ?? 0;

        /// <summary>The angle in degrees between two directions.</summary>
        public static double Angle(Vector3D a, Vector3D b) => MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(Vector3D.Normalize(a), Vector3D.Normalize(b)), -1, 1)));

        public static Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock Charged(VRage.Game.MyObjectBuilder_CubeBlock block)
        {
            var battery = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)block;
            battery.CurrentStoredPower = 3f;
            return battery;
        }
    }

    /// <summary>
    /// A player flies on the jetpack in open space: holding forward, the astronaut speeds up and goes forward, burning the
    /// suit's hydrogen; let go, the dampeners stop them.
    /// </summary>
    public sealed class JetpackCheck : RegressionCheck
    {
        public override string Name => Prefix + "jetpack";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.Origin);
            FakeClients.SetFree(Me, true);
            yield return Hold(1, Vector3.Zero, jetpack: true, dampeners: true, face: Site.Forward);
            var body = Body;
            Check(body.JetpackComp?.TurnedOn == true, "the jetpack did not come on");
            var from = body.PositionComp.GetPosition();
            var hydrogen = Motion.Hydrogen(body);
            yield return Hold(3, new Vector3(0, 0, -1), jetpack: true, dampeners: true, face: Site.Forward);
            var speed = Motion.Speed(body);
            var gone = Vector3D.Dot(body.PositionComp.GetPosition() - from, Site.Forward);
            Check(gone > 3, "3 s on the jetpack forward took the astronaut " + F(gone) + " m forward (speed " + F(speed) + " m/s)");
            yield return Hold(1, Vector3.Zero, jetpack: true, dampeners: true, face: Site.Forward);
            var within = Within(10);
            while (Motion.Speed(body) > 0.3 && within())
            {
                Keys(Vector3.Zero, jetpack: true, dampeners: true, face: Site.Forward);
                yield return null;
            }
            Check(Motion.Speed(body) <= 0.3, "the dampeners did not stop the astronaut: " + F(Motion.Speed(body)) + " m/s after 10 s");
            // the suit's gas is counted now and then, not every frame: looked at once the flight is over
            yield return Hold(2, Vector3.Zero, jetpack: true, dampeners: true, face: Site.Forward);
            Check(Motion.Hydrogen(body) < hydrogen, "the jetpack flew on no hydrogen (" + hydrogen.ToString("F6") + " in the suit before and after)");
            Result("jetpack: " + F(gone) + " m forward in 3 s at " + F(speed) + " m/s, hydrogen " + hydrogen.ToString("F4") + " -> " +
                   Motion.Hydrogen(body).ToString("F4") + ", stopped by the dampeners");
        }
    }

    /// <summary>A player walks on a floor on a planet: holding forward, they go forward on their feet and stay on the floor.</summary>
    public sealed class WalkCheck : RegressionCheck
    {
        public override string Name => Prefix + "walk";
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            var pad = Pad("walk-pad", Site.Origin, 5);
            var floor = pad.PositionComp.GetPosition() + Site.Up * 1.25;
            yield return Arrive(floor + Site.Up * 1.5 - Site.Forward * 5);
            FakeClients.SetFree(Me, true);
            var body = Body;
            yield return Hold(3, Vector3.Zero, jetpack: false, face: Site.Forward);
            var height = Vector3D.Dot(body.PositionComp.GetPosition() - floor, Site.Up);
            Check(Math.Abs(height) < 0.6, "the astronaut is not standing on the floor: " + F(height) + " m over it, " + body.GetCurrentMovementState());
            var from = body.PositionComp.GetPosition();
            yield return Hold(3, new Vector3(0, 0, -1), jetpack: false, face: Site.Forward);
            var state = body.GetCurrentMovementState();
            var gone = Vector3D.Dot(body.PositionComp.GetPosition() - from, Site.Forward);
            var now = Vector3D.Dot(body.PositionComp.GetPosition() - floor, Site.Up);
            yield return Hold(1, Vector3.Zero, jetpack: false, face: Site.Forward);
            Check(gone > 3, "3 s of walking forward took the astronaut " + F(gone) + " m (" + state + ")");
            Check(Math.Abs(now) < 0.6, "the astronaut left the floor walking: " + F(now) + " m over it");
            Result("walked " + F(gone) + " m forward in 3 s (" + state + "), on the floor");
        }
    }

    /// <summary>
    /// A small ship in space - a seat, a battery, a gyro, thrusters both ways - flown by its pilot: forward on the keys,
    /// it goes forward; the keys let go, the dampeners stop it; the mouse turns it on its gyro.
    /// </summary>
    public sealed class ShipFlightCheck : RegressionCheck
    {
        public override string Name => Prefix + "ship_flight";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, 4));
            var ship = Build("flight", Site.Origin, false,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                new Part("Gyro/LargeBlockGyro", 0, 1, 0),
                new Part("Thrust/LargeBlockSmallThrust", 1, 0, 0).Facing(Direction.Backward),
                new Part("Thrust/LargeBlockSmallThrust", -1, 0, 0).Facing(Direction.Forward));
            var seat = Block<MyCockpit>(ship);
            yield return Sit(seat);
            var from = ship.PositionComp.GetPosition();
            var battery = Block<MyBatteryBlock>(ship);
            var charge = battery.CurrentStoredPower;

            yield return HoldShip(3, seat, new Vector3(0, 0, -1));
            var speed = Motion.Speed(ship);
            var gone = Vector3D.Dot(ship.PositionComp.GetPosition() - from, seat.WorldMatrix.Forward);
            Check(gone > 2, "3 s of forward thrust took the ship " + F(gone) + " m forward (" + F(speed) + " m/s; " +
                            string.Join("; ", Blocks<MyThrust>(ship).Select(t => State(t) + ", thrust " + F(t.CurrentStrength))) + ")");
            for (var f = Frames(15); f > 0 && Motion.Speed(ship) > 0.3; f--)
            {
                ShipKeys(seat, Vector3.Zero);
                yield return null;
            }
            Check(Motion.Speed(ship) <= 0.3, "the dampeners did not stop the ship: " + F(Motion.Speed(ship)) + " m/s after 15 s");

            var heading = seat.WorldMatrix.Forward;
            yield return HoldShip(3, seat, Vector3.Zero, new Vector2(0, 10));
            var turned = Motion.Angle(heading, seat.WorldMatrix.Forward);
            Check(turned > 5, "3 s of turning the mouse turned the ship " + F(turned) + "° (" + State(Block<MyGyro>(ship)) + ")");
            Check(battery.CurrentStoredPower < charge, "the thrusters and the gyro took no power from the battery");
            Result("flew " + F(gone) + " m forward at " + F(speed) + " m/s, stopped on the dampeners, turned " + F(turned) + "° on the gyro, battery " +
                   charge.ToString("F4") + " -> " + battery.CurrentStoredPower.ToString("F4") + " MWh");
        }
    }

    /// <summary>
    /// A ship on atmospheric thrusters over a planet, its pilot holding nothing: the dampeners hold it where it is against
    /// gravity; up on the keys, it climbs.
    /// </summary>
    public sealed class HoverCheck : RegressionCheck
    {
        public override string Name => Prefix + "hover";
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            Check(Site.Planet.HasAtmosphere, "the planet of the planet sites has no atmosphere");
            var at = Highest(Site.Origin, 10) + Site.Up * 40;
            yield return Arrive(at + Site.Forward * 4);
            var ship = Build("hover", at, false,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 1, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 1, 1).With(b => Motion.Charged(b)),
                new Part("Gyro/LargeBlockGyro", 0, 2, 0),
                new Part("Thrust/LargeBlockSmallAtmosphericThrust", 1, -1, 0).Facing(Direction.Down, Direction.Forward),
                new Part("Thrust/LargeBlockSmallAtmosphericThrust", -1, -1, 0).Facing(Direction.Down, Direction.Forward));
            var seat = Block<MyCockpit>(ship);
            yield return Sit(seat);
            yield return HoldShip(4, seat, Vector3.Zero);
            var height = Vector3D.Dot(ship.PositionComp.GetPosition() - at, Site.Up);
            yield return HoldShip(4, seat, Vector3.Zero);
            var drift = Vector3D.Dot(ship.PositionComp.GetPosition() - at, Site.Up) - height;
            Check(Math.Abs(drift) < 1.5 && Motion.Speed(ship) < 1, "the dampeners did not hold the ship up: it went " + F(drift) + " m in 4 s, at " + F(Motion.Speed(ship)) +
                                                                 " m/s (" + string.Join("; ", Blocks<MyThrust>(ship).Select(t => State(t) + ", thrust " + F(t.CurrentStrength))) + ")");
            var before = Vector3D.Dot(ship.PositionComp.GetPosition() - at, Site.Up);
            yield return HoldShip(3, seat, new Vector3(0, 1, 0));
            var climbed = Vector3D.Dot(ship.PositionComp.GetPosition() - at, Site.Up) - before;
            Check(climbed > 1, "3 s of up on the keys climbed " + F(climbed) + " m");
            Result("held in the air (" + F(drift) + " m in 4 s), climbed " + F(climbed) + " m in 3 s on atmospheric thrusters");
        }
    }

    /// <summary>A rotor on a station gets its head as a player adds it; set turning, the head turns; reversed, it turns back.</summary>
    public sealed class RotorCheck : RegressionCheck
    {
        public override string Name => Prefix + "rotor";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(6, 4, 0));
            var grid = Build("rotor", Site.Origin, true,
                new Part("MotorStator/LargeStator", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var stator = Block<MyMotorStator>(grid);
            yield return AddHead(stator);
            var head = stator.TopGrid;
            Track(head);
            ((Sandbox.ModAPI.IMyTerminalBlock)stator).SetValueFloat("Velocity", 10f);
            var heading = head.WorldMatrix.Forward;
            yield return Until(() => Motion.Angle(heading, head.WorldMatrix.Forward), v => v > 30, "the rotor's head turning, degrees", 15, () => State(stator));
            var angle = ((Sandbox.ModAPI.Ingame.IMyMotorStator)stator).Angle;
            ((Sandbox.ModAPI.IMyTerminalBlock)stator).SetValueFloat("Velocity", -10f);
            yield return Until(() => MathHelper.ToDegrees(angle - ((Sandbox.ModAPI.Ingame.IMyMotorStator)stator).Angle), v => Math.Abs(v) > 20, "the rotor turning back, degrees", 15);
            // detached, as a player detaches it, the head is a grid of its own; attached again, it is on the rotor
            var rotor = (Sandbox.ModAPI.Ingame.IMyMotorStator)stator;
            ((Sandbox.ModAPI.IMyTerminalBlock)stator).SetValueFloat("Velocity", 0f);
            rotor.Detach();
            yield return Until(() => rotor.IsAttached ? 0 : 1, v => v > 0, "the head detached", 5);
            Check(!head.MarkedForClose, "the head was gone when it was detached");
            rotor.Attach();
            yield return Until(() => rotor.IsAttached ? 1 : 0, v => v > 0, "the head attached again", 10,
                () => "the head " + F(Vector3D.Distance(head.PositionComp.GetPosition(), stator.PositionComp.GetPosition())) + " m off");
            Result("rotor head added, turned both ways, detached and attached again");
        }
    }
}

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A ladder of six sections standing on a floor on a planet: the player gets on its bottom (as with F on it) and holds
    /// forward - they climb up it, on the ladder all the way.
    /// </summary>
    public sealed class LadderCheck : RegressionCheck
    {
        public override string Name => Prefix + "ladder";
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            var pad = Pad("ladder-pad", Site.Origin, 2);
            var floor = pad.PositionComp.GetPosition() + Site.Up * 1.25;
            yield return Arrive(floor + Site.Up * 1.5 - Site.Forward * 3);
            var parts = new System.Collections.Generic.List<Part>();
            for (var y = 0; y < 6; y++) parts.Add(new Part("Ladder2/LadderShaft", 0, y, 0));
            var ladder = Build("ladder", floor + Site.Up * 1.25, true, parts, forward: pad.WorldMatrix.Forward, up: pad.WorldMatrix.Up);
            var bottom = ladder.GetCubeBlock(Vector3I.Zero).FatBlock;
            FakeClients.SetFree(Me, true);
            var body = Body;
            var component = body.Components.Get<Sandbox.Game.Entities.Character.Components.MyCharacterLadderComponent>()
                            ?? throw new Core.ScenarioFailedException("the character has no ladder component");
            Request(component, "GetOnLadder_Request", bottom.EntityId);
            yield return Until(() => body.IsOnLadder ? 1 : 0, v => v > 0, "the player on the ladder", 5, () => body.GetCurrentMovementState().ToString());
            var from = Vector3D.Dot(body.PositionComp.GetPosition() - floor, Site.Up);
            var top = from;
            var climbing = false;
            for (var f = Frames(6); f > 0; f--)
            {
                Keys(new Vector3(0, 0, -1));
                top = Math.Max(top, Vector3D.Dot(body.PositionComp.GetPosition() - floor, Site.Up));
                climbing |= body.GetCurrentMovementState() == VRage.Game.MyCharacterMovementEnum.LadderUp;
                yield return null;
            }
            Check(climbing, "forward on the ladder never climbed (" + body.GetCurrentMovementState() + ", on the ladder " + body.IsOnLadder + ")");
            Check(top - from > 3, "6 s of climbing took the player " + F(top - from) + " m up the ladder");
            Result("on the ladder and up it " + F(top - from) + " m in 6 s");
        }
    }
}
