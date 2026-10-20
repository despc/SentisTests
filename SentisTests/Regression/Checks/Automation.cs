using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage.Game;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;

namespace SentisTests.Regression.Checks
{
    internal static class Scripts
    {
        private static readonly System.Reflection.FieldInfo Instance =
            typeof(MyProgrammableBlock).GetField("m_instance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private static readonly System.Reflection.MethodInfo Compress =
            typeof(MyProgrammableBlock).Assembly.GetType("Sandbox.Game.Entities.Blocks.StringCompressor")
                ?.GetMethod("CompressString", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        /// <summary>The block's script compiled and its program made.</summary>
        public static bool Ready(MyProgrammableBlock pb) => pb.IsWorking && (Instance == null || Instance.GetValue(pb) != null);

        /// <summary>The argument as "Run" in a programmable block's terminal sends it (<c>RunProgramRequest</c> takes it compressed).</summary>
        public static object Argument(string argument) =>
            (Compress ?? throw new Core.ScenarioFailedException("StringCompressor.CompressString is not in this game")).Invoke(null, new object[] { argument });
    }

    /// <summary>A camera with raycasting on scans 100 m ahead of it and finds the block standing 50 m in front.</summary>
    public sealed class CameraRaycastCheck : RegressionCheck
    {
        public override string Name => Prefix + "camera_raycast";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -6));
            var grid = Build("camera", Site.Origin, true,
                new Part("CameraBlock/LargeCameraBlock", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 1).With(b => Motion.Charged(b)));
            var camera = (Sandbox.ModAPI.Ingame.IMyCameraBlock)Block<MyCameraBlock>(grid);
            var ahead = ((MyCameraBlock)camera).WorldMatrix.Forward;
            var target = Build("camera-target", ((MyCameraBlock)camera).PositionComp.GetPosition() + ahead * 50, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            camera.EnableRaycast = true;
            yield return Until(() => camera.AvailableScanRange, v => v >= 100, "the camera's scan range charging, m", 10, () => State((MyCameraBlock)camera));
            var hit = camera.Raycast(100, 0, 0);
            Check(!hit.IsEmpty(), "the camera's 100 m scan found nothing; the block is 50 m ahead");
            Check(hit.EntityId == target.EntityId, "the camera's scan found " + hit.Name + " (" + hit.Type + "), not the block ahead");
            var at = Vector3D.Distance(hit.HitPosition ?? Vector3D.Zero, ((MyCameraBlock)camera).PositionComp.GetPosition());
            Check(at > 45 && at < 52, "the camera's scan hit " + F(at) + " m away; the block's face is 48.75 m");
            Result("the camera's scan found the block ahead, hit " + F(at) + " m away (" + hit.Relationship + ")");
        }
    }

    /// <summary>
    /// Two stations 300 m apart, each with an antenna and a programmable block: a message one script broadcasts (IGC)
    /// reaches the other's script through the antennas; with the sender's antenna turned down to 100 m, the next one
    /// does not.
    /// </summary>
    public sealed class AntennaMessageCheck : RegressionCheck
    {
        public override string Name => Prefix + "antenna_message";

        private const string Sender = "public void Main(string argument) { IGC.SendBroadcastMessage(\"regression\", argument, TransmissionDistance.AntennaRelay); }";

        private const string Receiver = @"
IMyBroadcastListener listener;
public Program() { listener = IGC.RegisterBroadcastListener(""regression""); listener.SetMessageCallback(""got""); }
public void Main(string argument, UpdateType update)
{
    while (listener.HasPendingMessage) Me.CustomData = listener.AcceptMessage().Data.ToString();
}";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.EnableIngameScripts, "in-game scripts are off in this world");
            yield return Arrive(Site.At(0, 10, -10));
            MyCubeGrid Station(string name, Vector3D at, string script) => Build(name, at, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 1).With(b => Motion.Charged(b)),
                new Part("RadioAntenna/LargeBlockRadioAntenna", 1, 0, 0),
                new Part("MyProgrammableBlock/LargeProgrammableBlock", -1, 0, 0).With(b => ((MyObjectBuilder_MyProgrammableBlock)b).Program = script));
            var from = Station("igc-sender", Site.Origin, Sender);
            var to = Station("igc-receiver", Site.At(300, 0, 0), Receiver);
            var send = Block<MyProgrammableBlock>(from);
            var receive = Block<MyProgrammableBlock>(to);
            var antennas = new[] { Block<MyRadioAntenna>(from), Block<MyRadioAntenna>(to) };
            foreach (var antenna in antennas)
            {
                ((Sandbox.ModAPI.IMyTerminalBlock)antenna).SetValueFloat("Radius", 500f);
                ((Sandbox.ModAPI.IMyTerminalBlock)antenna).SetValueBool("EnableBroadCast", true);
            }
            yield return Until(() => Scripts.Ready(send) && Scripts.Ready(receive) && antennas.All(a => a.IsWorking) ? 1 : 0, v => v > 0, "both scripts compiled, both antennas on", 30,
                () => State(send) + "; " + State(receive) + "; " + State(antennas[0]) + "; " + State(antennas[1]));
            // the antennas find who is in their reach every 100 frames or so: sent again every 2 s until it comes
            var sent = 0;
            var last = DateTime.MinValue;
            yield return Until(() =>
                {
                    if (receive.CustomData == "near") return 1;
                    if ((DateTime.UtcNow - last).TotalSeconds >= 2)
                    {
                        last = DateTime.UtcNow;
                        sent++;
                        Request(send, "RunProgramRequest", Scripts.Argument("near"), Sandbox.ModAPI.Ingame.UpdateType.Terminal);
                    }
                    return 0;
                }, v => v > 0, "the message through the antennas 300 m off", 20,
                () => "sent " + sent + " times, the receiver has '" + receive.CustomData + "'; " + send.DetailedInfo.ToString().Trim() + " | " + receive.DetailedInfo.ToString().Trim());
            ((Sandbox.ModAPI.IMyTerminalBlock)antennas[0]).SetValueFloat("Radius", 100f);
            yield return Seconds(4);
            Request(send, "RunProgramRequest", Scripts.Argument("far"), Sandbox.ModAPI.Ingame.UpdateType.Terminal);
            yield return Seconds(4);
            Check(receive.CustomData == "near", "a message from an antenna reaching 100 m came to a station 300 m off ('" + receive.CustomData + "')");
            Result("a script's broadcast reached the other station's script 300 m off through the antennas (sent " + sent + " times); with the antenna at 100 m it did not");
        }
    }

    /// <summary>
    /// A ship with a remote control, thrusters every way and a gyro, given a waypoint 150 m off with the autopilot on, flies
    /// there by itself and stops there.
    /// </summary>
    public sealed class RemoteAutopilotCheck : RegressionCheck
    {
        public override string Name => Prefix + "remote_autopilot";
        public override int TimeoutSeconds => 150;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, 10));
            var parts = new List<Part>
            {
                new Part("RemoteControl/LargeBlockRemoteControl", 0, 0, 0),
                new Part("Gyro/LargeBlockGyro", -1, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)),
            };
            // a thruster each way, against the core (a thruster is two cells along its forward)
            foreach (var (face, x, y, z) in new[]
                     {
                         (Direction.Right, 2, 0, 0), (Direction.Left, -3, 0, 0), (Direction.Up, 0, 1, 0),
                         (Direction.Down, 0, -2, 0), (Direction.Backward, 0, 0, 1), (Direction.Forward, 0, 0, -2),
                     })
                parts.Add(new Part("Thrust/LargeBlockSmallThrust", x, y, z).Facing(face, Base6Directions.GetPerpendicular(face)));
            var ship = Build("autopilot", Site.Origin, false, parts);
            var remote = (Sandbox.ModAPI.Ingame.IMyRemoteControl)Block<MyRemoteControl>(ship);
            yield return Until(() => ((MyRemoteControl)remote).IsWorking ? 1 : 0, v => v > 0, "the remote control working", 10, () => State((MyRemoteControl)remote));
            var waypoint = Site.At(150, 0, 0);
            remote.ClearWaypoints();
            remote.AddWaypoint(waypoint, "regression");
            remote.FlightMode = Sandbox.ModAPI.Ingame.FlightMode.OneWay;
            remote.SpeedLimit = 20;
            remote.SetAutoPilotEnabled(true);
            Check(remote.IsAutoPilotEnabled, "the autopilot did not come on");
            Func<double> off = () => Vector3D.Distance(ship.PositionComp.WorldAABB.Center, waypoint);
            yield return Until(off, v => v < 10, "the ship flying to the waypoint, m off it", 90, () => F(Motion.Speed(ship)) + " m/s; " + State((MyRemoteControl)remote));
            yield return Until(() => Motion.Speed(ship), v => v < 1, "the ship stopping at the waypoint, m/s", 30);
            Result("flew to the waypoint 150 m off by the autopilot, stopped " + F(off()) + " m from it");
        }
    }

    /// <summary>A player's suit low on energy charges while they sit in a seat of a powered ship.</summary>
    public sealed class SeatRechargeCheck : RegressionCheck
    {
        public override string Name => Prefix + "seat_recharge";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 2, 4));
            var grid = Build("seat-recharge", Site.Origin, true,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var seat = Block<MyCockpit>(grid);
            var source = Body.SuitBattery.ResourceSource;
            // a tenth of a full suit (MyCharacter.SuitEnergyLevel: the capacity over 1E-05 MWh)
            source.SetRemainingCapacityByType(MyResourceDistributorComponent.ElectricityId, 1E-05f * 0.1f);
            var low = Body.SuitEnergyLevel;
            yield return Sit(seat);
            yield return Until(() => Body.SuitEnergyLevel - low, v => v > 0.05, "the suit charging in the seat", 30, () => "energy " + F(Body.SuitEnergyLevel * 100) + " %");
            Result("seated, the suit charged " + F(low * 100) + " -> " + F(Body.SuitEnergyLevel * 100) + " %");
        }
    }

    /// <summary>
    /// A player in a powered cryo chamber full of oxygen, in open space, breathes the chamber's air and not the suit's:
    /// the suit's oxygen, going down outside, stays.
    /// </summary>
    public sealed class CryoChamberCheck : RegressionCheck
    {
        public override string Name => Prefix + "cryo_chamber";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableOxygen, "oxygen is off in this world");
            yield return Arrive(Site.At(0, 2, 4));
            var grid = Build("cryo", Site.Origin, true,
                // full of oxygen, as a tank on its conveyors fills it: the one inside breathes it, not the suit's
                new Part("CryoChamber/LargeBlockCryoChamber", 0, 0, 0).With(b => ((MyObjectBuilder_Cockpit)b).OxygenLevel = 1f),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var cryo = Block<MyCryoChamber>(grid);
            yield return Until(() => cryo.IsWorking ? 1 : 0, v => v > 0, "the cryo chamber working", 30, () => State(cryo));
            // breathing outside first: the suit's oxygen goes down
            var suit = Body.OxygenComponent;
            var start = suit.SuitOxygenLevel;
            yield return Seconds(5);
            var outside = start - suit.SuitOxygenLevel;
            yield return Sit(cryo);
            var seated = suit.SuitOxygenLevel;
            yield return Seconds(5);
            var inside = seated - suit.SuitOxygenLevel;
            Check(inside <= 0.000001, "in the cryo chamber the suit's oxygen went down by " + (inside * 100).ToString("F4") + " % in 5 s (outside " + (outside * 100).ToString("F4") +
                                      " %; the chamber's oxygen " + F(cryo.OxygenFillLevel * 100) + " %)");
            Result("the suit's oxygen in 5 s: outside -" + (outside * 100).ToString("F4") + " %, in the cryo chamber -" + (Math.Max(0, inside) * 100).ToString("F4") + " %");
        }
    }
}
