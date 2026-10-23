using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using VRage;
using VRage.Game;
using VRageMath;
using LaserStatus = Sandbox.ModAPI.Ingame.MyLaserAntennaStatus;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Two stations 2 km apart in open space, each with a powered laser antenna given the other's coordinates (as a player
    /// pastes a GPS and presses Connect): the heads turn, find each other and connect; one switched to idle, the link is
    /// gone; the other antenna picked from the terminal's list (<c>MyLaserAntenna.OnConnectToRecRequest</c>) and the link
    /// made permanent (<c>ChangePermRequest</c>), they connect again.
    /// </summary>
    public sealed class LaserAntennaCheck : RegressionCheck
    {
        public override string Name => Prefix + "laser_antenna";
        public override int TimeoutSeconds => 150;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, -10));
            MyCubeGrid Station(string name, Vector3D at) => Build(name, at, true,
                new Part("LaserAntenna/LargeBlockLaserAntenna", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, -1, 0).With(b => Motion.Charged(b)));
            var one = Block<MyLaserAntenna>(Station("laser-one", Site.Origin));
            var two = Block<MyLaserAntenna>(Station("laser-two", Site.At(0, 0, 2000)));
            yield return Until(() => one.IsWorking && two.IsWorking ? 1 : 0, v => v > 0, "both laser antennas working", 10, () => State(one) + "; " + State(two));
            string Gps(MyLaserAntenna to)
            {
                var p = to.PositionComp.GetPosition();
                return "GPS:regression:" + p.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" +
                       p.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" + p.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":";
            }
            var a = (Sandbox.ModAPI.Ingame.IMyLaserAntenna)one;
            var b = (Sandbox.ModAPI.Ingame.IMyLaserAntenna)two;
            a.SetTargetCoords(Gps(two));
            a.Connect();
            b.SetTargetCoords(Gps(one));
            b.Connect();
            var started = DateTime.UtcNow;
            yield return Until(() => a.Status == LaserStatus.Connected && b.Status == LaserStatus.Connected ? 1 : 0, v => v > 0, "the laser antennas connected", 90,
                () => "one " + a.Status + ", two " + b.Status);
            var took = (DateTime.UtcNow - started).TotalSeconds;
            ((Sandbox.ModAPI.Ingame.IMyTerminalBlock)one).GetActionWithName("Idle").Apply(one);
            yield return Until(() => b.Status != LaserStatus.Connected ? 1 : 0, v => v > 0, "the link gone with one antenna idle", 15, () => "one " + a.Status + ", two " + b.Status);
            var lost = b.Status;
            // linked again the way the terminal does it: the other antenna picked from the list, the link made permanent
            Request(one, "OnConnectToRecRequest", two.EntityId);
            yield return Until(() => a.Status == LaserStatus.Connected && b.Status == LaserStatus.Connected ? 1 : 0, v => v > 0, "the laser antennas connected again", 60,
                () => "one " + a.Status + ", two " + b.Status);
            // permanent only once connected
            Request(one, "ChangePermRequest", true);
            Check(a.IsPermanent && b.IsPermanent, "the link was not made permanent: one " + a.IsPermanent + ", two " + b.IsPermanent);
            Result("2 km apart, the laser antennas turned to each other's coordinates and connected in " + F(took) + " s; one idle, the other " + lost +
                   "; picked from the list, connected again, permanent");
        }
    }

    /// <summary>
    /// An event controller set to "Door opened" on a door, its first slot switching a light off: the player opens the door
    /// and the light goes off.
    /// </summary>
    public sealed class EventControllerCheck : RegressionCheck
    {
        public override string Name => Prefix + "event_controller";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            // the event's number is given to it by its definition: read off a controller built out of the way
            var probe = Build("event-probe", Site.At(0, 60, 0), true, new Part("EventControllerBlock/EventControllerLarge", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            object doorEvent = null;
            foreach (var component in ((VRage.Game.Entity.MyEntity)probe.GetCubeBlock(Vector3I.Zero).FatBlock).Components)
                if (component.GetType().Name == "MyEventDoorOpened") doorEvent = component;
            Check(doorEvent != null, "the event controller has no \"Door opened\" event");
            var eventId = (long)doorEvent.GetType().GetProperty("UniqueSelectionId").GetValue(doorEvent);
            probe.Close();

            var doorId = MyEntityIdentifier.AllocateId();
            var lightId = MyEntityIdentifier.AllocateId();
            void Set(MyObjectBuilder_CubeBlock block, string field, object value) =>
                (block.GetType().GetField(field) ?? throw new Core.ScenarioFailedException(block.GetType().Name + "." + field + " is not in this game")).SetValue(block, value);
            var grid = Build("event", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("Door/", 1, 0, 0).With(b => b.EntityId = doorId),
                new Part("InteriorLight/SmallLight", -1, 0, 0).With(b => b.EntityId = lightId),
                new Part("EventControllerBlock/EventControllerLarge", 0, 1, 0).With(b =>
                {
                    Set(b, "SelectedEvent", eventId);
                    // the builder's own list type (a serializable list of entity ids)
                    var blocks = Activator.CreateInstance(b.GetType().GetField("SelectedBlocks").FieldType);
                    ((System.Collections.Generic.ICollection<long>)blocks).Add(doorId);
                    Set(b, "SelectedBlocks", blocks);
                    Set(b, "Toolbar", Toolbars.One(lightId, "OnOff_Off"));
                }));
            var door = Block<MyDoor>(grid);
            var light = Block<MyLightingBlock>(grid);
            var controller = grid.GetCubeBlock(new Vector3I(0, 1, 0)).FatBlock;
            yield return Until(() => controller.IsWorking && light.IsWorking ? 1 : 0, v => v > 0, "the event controller and the light working", 10, () => State(controller) + "; " + State(light));
            yield return Seconds(1);
            Check(light.Enabled, "the light went off before the door was opened");
            ((Sandbox.ModAPI.IMyTerminalBlock)door).SetValueBool("Open", true);
            yield return Until(() => light.Enabled ? 0 : 1, v => v > 0, "the light switched off by the event of the door opening", 10,
                () => "door " + ((Sandbox.ModAPI.Ingame.IMyDoor)door).Status + "; " + ((Sandbox.ModAPI.IMyTerminalBlock)controller).DetailedInfo.Trim().Replace("\n", " "));
            Result("the door opened, the event controller ran its slot and switched the light off");
        }
    }

    /// <summary>
    /// A ship with an AI flight block (AI Move), thrusters every way and a gyro: given a waypoint 150 m off (as the AI
    /// recorder and the basic task hand it over, <c>MyFlightMovementBlock.SetWaypoints</c>) and its AI behavior switched on in the
    /// terminal, it flies there by itself and stops there.
    /// </summary>
    public sealed class AiFlightCheck : RegressionCheck
    {
        public override string Name => Prefix + "ai_flight";
        public override int TimeoutSeconds => 150;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, 10));
            var parts = new System.Collections.Generic.List<Part>
            {
                new Part("FlightMovementBlock/LargeFlightMovement", 0, 0, 0),
                new Part("Gyro/LargeBlockGyro", -1, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)),
            };
            foreach (var (face, x, y, z) in new[]
                     {
                         (Base6Directions.Direction.Right, 2, 0, 0), (Base6Directions.Direction.Left, -3, 0, 0), (Base6Directions.Direction.Up, 0, 1, 0),
                         (Base6Directions.Direction.Down, 0, -2, 0), (Base6Directions.Direction.Backward, 0, 0, 1), (Base6Directions.Direction.Forward, 0, 0, -2),
                     })
                parts.Add(new Part("Thrust/LargeBlockSmallThrust", x, y, z).Facing(face, Base6Directions.GetPerpendicular(face)));
            var ship = Build("ai-flight", Site.Origin, false, parts);
            var block = ship.GetCubeBlock(Vector3I.Zero).FatBlock;
            var flight = (Sandbox.ModAPI.IMyFlightMovementBlock)block;
            yield return Until(() => block.IsWorking ? 1 : 0, v => v > 0, "the AI flight block working", 10, () => State(block));
            var waypoint = Site.At(150, 0, 0);
            // the waypoints as the AI recorder and the basic task hand them over; the mod API's AddWaypoint throws on a
            // server ("Event 'AddWaypointBroadcast' not found": it raises the component's event on the block)
            ((SpaceEngineers.Game.Entities.Blocks.MyFlightMovementBlock)block).SetWaypoints(
                new System.Collections.Generic.List<Sandbox.Game.Entities.MyAutopilotWaypoint> { new Sandbox.Game.Entities.MyAutopilotWaypoint(waypoint, "regression") }, false, false);
            flight.FlightMode = Sandbox.ModAPI.Ingame.FlightMode.OneWay;
            ((Sandbox.ModAPI.IMyTerminalBlock)block).SetValueFloat("SpeedLimit", 20f);
            ((Sandbox.ModAPI.IMyTerminalBlock)block).SetValueBool("ActivateBehavior", true);
            yield return Until(() => flight.IsAutoPilotEnabled ? 1 : 0, v => v > 0, "the AI flight's autopilot on", 10,
                () => ((Sandbox.ModAPI.IMyTerminalBlock)block).DetailedInfo.Trim().Replace("\n", " "));
            Func<double> off = () => Vector3D.Distance(ship.PositionComp.WorldAABB.Center, waypoint);
            yield return Until(off, v => v < 10, "the ship flying to the waypoint by AI, m off it", 90,
                () => F(Motion.Speed(ship)) + " m/s; " + ((Sandbox.ModAPI.IMyTerminalBlock)block).DetailedInfo.Trim().Replace("\n", " "));
            yield return Until(() => Motion.Speed(ship), v => v < 1, "the ship stopping at the waypoint, m/s", 30);
            Result("the AI flight block flew the ship to the waypoint 150 m off and stopped " + F(off()) + " m from it");
        }
    }
}
