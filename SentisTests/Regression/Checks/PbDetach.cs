using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Core;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Programmable blocks and SentisClusters' detached clusters. Two ships 8 km apart along the world's X - two clusters
    /// of the physics - each with a programmable block whose script, every tick, works its own grid: lists every block,
    /// switches a light, turns a gyro and thrusters (each against the other: the ship stays), triggers a timer, takes a
    /// rotor's head off and puts it back, moves an ingot between two containers; sends a message (IGC, antennas of 10 km)
    /// and counts the other ship's; scans the other ship with a camera (Raycast, 9 km) whenever the camera has charged.
    /// The scripts run on the game thread always, a detached cluster's entities on its own thread meanwhile.
    ///
    /// Four phases of <see cref="PhaseSeconds"/> s: nothing detached, ship A's cluster detached (SentisClusters'
    /// <c>!clusters detach</c>, kept so), B's, both. In every phase both scripts must run on, throw nothing, move items,
    /// get the other's messages, and the clusters' threads throw nothing; the camera's hits are told phase by phase (a
    /// world that steps is hidden from the ray casts of other threads). Not part of the regression: it switches detaching.
    /// </summary>
    public sealed class PbDetachCheck : RegressionCheck
    {
        public const string ScenarioName = "pb_detach";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 600;
        private const int PhaseSeconds = 30;
        private const double Apart = 8000;

        private const string Script = @"
IMyInteriorLight light; IMyGyro gyro; List<IMyThrust> thr = new List<IMyThrust>(); IMyTimerBlock timer; IMyMotorStator rotor;
IMyCameraBlock cam; List<IMyCargoContainer> cargo = new List<IMyCargoContainer>(); IMyBroadcastListener l;
long n, moved, sent, got, scans, hits, errs, blocks, detaches; string tag; string last = """";
T One<T>() where T : class { var list = new List<T>(); GridTerminalSystem.GetBlocksOfType(list, b => ((IMyTerminalBlock)b).IsSameConstructAs(Me)); return list.Count > 0 ? list[0] : null; }
public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update1;
    tag = Me.CustomName;
    light = One<IMyInteriorLight>(); gyro = One<IMyGyro>(); timer = One<IMyTimerBlock>(); rotor = One<IMyMotorStator>(); cam = One<IMyCameraBlock>();
    GridTerminalSystem.GetBlocksOfType(thr); GridTerminalSystem.GetBlocksOfType(cargo);
    cam.EnableRaycast = true;
    l = IGC.RegisterBroadcastListener(""pbd"");
}
public void Main(string argument, UpdateType update)
{
    try
    {
        n++;
        var all = new List<IMyTerminalBlock>(); GridTerminalSystem.GetBlocks(all); blocks = all.Count;
        // some 20 000 of the 50 000 instructions a run may take: two scripts counted in one counter at once trip the limit
        double work = 0; for (int i = 0; i < 4000; i++) work += Math.Sqrt(i);
        light.Enabled = !light.Enabled;
        gyro.GyroOverride = n % 2 == 0; gyro.Yaw = 0; gyro.Pitch = 0; gyro.Roll = 0;
        foreach (var t in thr) { t.ThrustOverridePercentage = 0f; t.Enabled = n % 120 < 60; }
        if (n % 30 == 0) timer.Trigger();
        if (rotor != null) { if (n % 240 == 120) { rotor.Detach(); detaches++; } else if (n % 240 == 0) rotor.Attach(); }
        if (cargo.Count >= 2)
        {
            var a = cargo[0].GetInventory(); var b = cargo[1].GetInventory();
            var from = n / 100 % 2 == 0 ? a : b; var to = from == a ? b : a;
            if (from.ItemCount > 0 && from.TransferItemTo(to, 0, null, true, 1)) moved++;
        }
        IGC.SendBroadcastMessage(""pbd"", tag + "":"" + n, TransmissionDistance.AntennaRelay); sent++;
        while (l.HasPendingMessage) { var m = l.AcceptMessage(); var s = m.Data as string; if (s != null && !s.StartsWith(tag + "":"")) got++; }
        if (cam.CanScan(9000)) { scans++; if (!cam.Raycast(9000, 0, 0).IsEmpty()) hits++; }
    }
    catch (Exception e) { errs++; last = e.GetType().Name + "": "" + e.Message; }
    if (n % 30 == 0)
        Me.CustomData = ""n="" + n + "" blocks="" + blocks + "" moved="" + moved + "" sent="" + sent + "" got="" + got + "" scans="" + scans + "" hits="" + hits +
                        "" detaches="" + detaches + "" errs="" + errs + "" last="" + last;
}";

        private readonly Scenarios.ConfigOverride _config = new Scenarios.ConfigOverride();

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.EnableIngameScripts, "in-game scripts are off in this world");
            Check(Integrations.IsLoaded("SentisClusters") || Command("!clusters status").Any(), "SentisClusters is not loaded");
            _config.Set("FreezerEnabled", false);
            yield return Arrive(Site.At(0, 10, -15));
            var a = Ship("pbd-a", Site.Origin, "A", Direction.Left, 8);
            var b = Ship("pbd-b", Site.At(Apart, 0, 0), "B", Direction.Right, -1);
            yield return Seconds(1);
            foreach (var ship in new[] { a, b })
                foreach (var stator in Blocks<Sandbox.Game.Entities.Cube.MyMotorStator>(ship).Where(s => s.TopGrid == null))
                    typeof(MyMechanicalConnectionBlockBase).GetMethod("DoRecreateTop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Invoke(stator, new object[] { Identity, MyMechanicalConnectionBlockBase.MyTopBlockSize.Normal, true });
            var pbA = Block<MyProgrammableBlock>(a);
            var pbB = Block<MyProgrammableBlock>(b);
            yield return Until(() => Scripts.Ready(pbA) && Scripts.Ready(pbB) && Count(pbA, "n") > 60 && Count(pbB, "n") > 60 ? 1 : 0, v => v > 0,
                "both scripts compiled and running", 60, () => "A: " + pbA.CustomData + " | B: " + pbB.CustomData + " | " + pbA.DetailedInfo + " | " + pbB.DetailedInfo);
            foreach (var stator in new[] { a, b }.SelectMany(Blocks<Sandbox.Game.Entities.Cube.MyMotorStator>)) if (stator.TopGrid != null) Track(stator.TopGrid);
            Check(Command("!clusters set Detach on").Any(), "!clusters set did not answer");

            var report = new List<string>();
            var failures = new List<string>();
            var phases = new[] { ("nothing detached", new Vector3D[0]), ("A detached", new[] { a.PositionComp.GetPosition() }),
                ("B detached", new[] { b.PositionComp.GetPosition() }), ("both detached", new[] { a.PositionComp.GetPosition(), b.PositionComp.GetPosition() }) };
            foreach (var (name, detach) in phases)
            {
                Command("!clusters attach all");
                foreach (var at in detach)
                {
                    var answer = string.Join(" ", Command($"!clusters detach {at.X:F0} {at.Y:F0} {at.Z:F0}"));
                    Check(answer.Contains("detached"), name + ": " + answer);
                }
                yield return Seconds(2);
                var before = (Read(pbA), Read(pbB), ClusterExceptions());
                yield return Seconds(PhaseSeconds);
                var after = (Read(pbA), Read(pbB), ClusterExceptions());
                var line = name + ": " + Delta("A", before.Item1, after.Item1) + "; " + Delta("B", before.Item2, after.Item2) + "; " + Diag(a, b) +
                           "; clusters' threads threw " + (after.Item3 - before.Item3);
                report.Add(line);
                Note(line);
                foreach (var (who, x, y) in new[] { ("A", before.Item1, after.Item1), ("B", before.Item2, after.Item2) })
                {
                    if (y["n"] - x["n"] < 300) failures.Add(name + ": " + who + "'s script ran " + (y["n"] - x["n"]) + " times in " + PhaseSeconds + " s");
                    if (y["errs"] > x["errs"]) failures.Add(name + ": " + who + "'s script threw " + (y["errs"] - x["errs"]) + " times, the last: " + Last(who == "A" ? pbA : pbB));
                    if (y["moved"] == x["moved"]) failures.Add(name + ": " + who + "'s script moved no ingot");
                    if (y["got"] == x["got"]) failures.Add(name + ": " + who + " got none of the other's messages");
                    if (y["scans"] > x["scans"] && y["hits"] == x["hits"]) failures.Add(name + ": " + who + "'s camera hit the other ship in none of " + (y["scans"] - x["scans"]) + " scans");
                }
                if (after.Item3 > before.Item3) failures.Add(name + ": the clusters' threads threw " + (after.Item3 - before.Item3) + " times");
            }
            Command("!clusters attach all");
            Check(failures.Count == 0, string.Join(" | ", failures) + " || " + string.Join(" | ", report));
            Result(string.Join(" | ", report));
        }

        /// <summary>A ship: the programmable block and what its script works, the camera at the end facing the other ship.</summary>
        private MyCubeGrid Ship(string name, Vector3D at, string tag, Direction camera, int cameraX)
        {
            var ship = Build(name, at, false,
                new Part("MyProgrammableBlock/LargeProgrammableBlock", 0, 0, 0).With(b =>
                {
                    // one setup: Named and With each set it, the later one wins (both blocks were named alike, and each
                    // script took the other's messages for its own)
                    var pb = (MyObjectBuilder_MyProgrammableBlock)b;
                    pb.Program = Script;
                    pb.CustomName = tag;
                }),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)),
                new Part("InteriorLight/SmallLight", 3, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 4, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 5, 0, 0),
                new Part("Gyro/LargeBlockGyro", 6, 0, 0),
                new Part("TimerBlock/TimerBlockLarge", 7, 0, 0),
                new Part("CameraBlock/LargeCameraBlock", cameraX, 0, 0).Facing(camera),
                new Part("Thrust/LargeBlockSmallThrust", 3, -1, 0).Facing(Direction.Backward),
                new Part("Thrust/LargeBlockSmallThrust", 5, -1, 0).Facing(Direction.Forward),
                new Part("MotorStator/LargeStator", 6, 1, 0),
                new Part("RadioAntenna/LargeBlockRadioAntenna", 2, 1, 0).With(b =>
                {
                    var antenna = (MyObjectBuilder_RadioAntenna)b;
                    antenna.BroadcastRadius = 10000;
                    antenna.EnableBroadcasting = true;
                }));
            // the antenna's reach as a player sets it, through the terminal (reg_antenna_message): set in the builder
            // alone it read 10 km and carried no message
            foreach (var antenna in Blocks<Sandbox.ModAPI.IMyTerminalBlock>(ship).Where(t => t is Sandbox.ModAPI.Ingame.IMyRadioAntenna))
            {
                antenna.SetValueFloat("Radius", 10000f);
                antenna.SetValueBool("EnableBroadCast", true);
            }
            var cargo = Blocks<MyCargoContainer>(ship).First();
            var ingot = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Ingot>("Iron");
            cargo.GetInventory().AddItems((MyFixedPoint)100, ingot);
            return ship;
        }

        /// <summary>Why messages or ray casts may not get through: the antennas, the ships' distance, where the cameras look.</summary>
        private static string Diag(MyCubeGrid a, MyCubeGrid b)
        {
            string Antenna(MyCubeGrid g)
            {
                var antenna = Blocks<Sandbox.ModAPI.Ingame.IMyRadioAntenna>(g).FirstOrDefault();
                return antenna == null ? "no antenna" : "antenna working " + antenna.IsWorking + ", radius " + antenna.Radius.ToString("F0") +
                                                        ", broadcasting " + antenna.EnableBroadcasting + ", owner " + antenna.OwnerId;
            }
            string Camera(MyCubeGrid g, MyCubeGrid other)
            {
                var camera = Blocks<Sandbox.ModAPI.IMyCameraBlock>(g).FirstOrDefault();
                if (camera == null) return "no camera";
                return "camera raycast " + camera.EnableRaycast + ", range " + camera.AvailableScanRange.ToString("F0");
            }
            string Hears(MyCubeGrid g, MyCubeGrid other)
            {
                object Prop(object o, string name) => o?.GetType().GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(o);
                var mine = Blocks<Sandbox.Game.Entities.Cube.MyRadioAntenna>(g).FirstOrDefault();
                var theirs = Blocks<Sandbox.Game.Entities.Cube.MyRadioAntenna>(other).FirstOrDefault();
                var heard = Prop(Prop(mine, "RadioReceiver"), "BroadcastersInRange") as System.Collections.IEnumerable;
                var theirBroadcaster = Prop(theirs, "RadioBroadcaster");
                var count = 0;
                var hearsThem = false;
                if (heard != null) foreach (var x in heard) { count++; hearsThem |= ReferenceEquals(x, theirBroadcaster); }
                var pbOther = Blocks<MyProgrammableBlock>(other).FirstOrDefault();
                var pbMine = Blocks<MyProgrammableBlock>(g).FirstOrDefault();
                var linked = theirBroadcaster is Sandbox.Game.Entities.MyDataBroadcaster tb && pbMine != null && pbOther != null &&
                             Sandbox.Game.GameSystems.MyAntennaSystem.Static.CheckConnection(pbMine, tb, pbOther.OwnerId, false);
                return "hears " + count + " broadcasters" + (hearsThem ? ", the other's among them" : ", not the other's") + ", IGC link from the other " + linked +
                       " (the other's broadcaster " + (theirBroadcaster == null ? "none" : "radius " + Prop(theirBroadcaster, "BroadcastRadius") + ", enabled " + Prop(theirBroadcaster, "Enabled")) + ")";
            }
            return "ships " + Vector3D.Distance(a.PositionComp.GetPosition(), b.PositionComp.GetPosition()).ToString("F0") + " m apart; A " + Antenna(a) + ", " + Hears(a, b) + ", " + Camera(a, b) +
                   "; B " + Antenna(b) + ", " + Hears(b, a) + ", " + Camera(b, a);
        }

        private static Dictionary<string, long> Read(MyProgrammableBlock pb)
        {
            var values = new Dictionary<string, long> { ["n"] = 0, ["moved"] = 0, ["got"] = 0, ["scans"] = 0, ["hits"] = 0, ["errs"] = 0, ["detaches"] = 0, ["blocks"] = 0 };
            foreach (Match m in Regex.Matches(pb.CustomData ?? "", @"(\w+)=(\d+)")) values[m.Groups[1].Value] = long.Parse(m.Groups[2].Value);
            return values;
        }

        private static long Count(MyProgrammableBlock pb, string what) => Read(pb)[what];

        private static string Last(MyProgrammableBlock pb)
        {
            var at = (pb.CustomData ?? "").IndexOf("last=", StringComparison.Ordinal);
            return at < 0 ? "?" : pb.CustomData.Substring(at + 5);
        }

        private static string Delta(string who, Dictionary<string, long> x, Dictionary<string, long> y) =>
            who + " ran " + (y["n"] - x["n"]) + ", moved " + (y["moved"] - x["moved"]) + ", got " + (y["got"] - x["got"]) + ", camera " +
            (y["hits"] - x["hits"]) + " hits of " + (y["scans"] - x["scans"]) + " scans, rotor detached " + (y["detaches"] - x["detaches"]) + ", errors " + (y["errs"] - x["errs"]) +
            ", blocks " + y["blocks"];

        /// <summary>"exceptions in the clusters' threads: N" of !clusters status.</summary>
        private static long ClusterExceptions()
        {
            var status = string.Join("\n", Command("!clusters status"));
            var m = Regex.Match(status, @"exceptions in the clusters' threads: (\d+)");
            return m.Success ? long.Parse(m.Groups[1].Value) : 0;
        }

        private static List<string> Command(string text)
        {
            var answers = new List<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            commands?.HandleCommandFromServer(text, message => answers.Add(message.Message));
            return answers;
        }

        public override void Cleanup()
        {
            try { Command("!clusters attach all"); }
            catch (Exception) { }
            try { _config.Restore(); }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
