using System;
using System.Collections;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisTests.Core;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The modded V5's blink drive (Sentis_v5_ScriptPack_Fix, Blink/BlinkDrive.cs) on a jump drive, with the check's fake
    /// player seated in the ship's cockpit and the drive's "BlinkDrive_Jump" action applied (as a seated player's toolbar
    /// does; the action asks for the grid's controlling player, then the server moves the ship in 20 steps):
    /// - the ship goes 500 m along its cockpit's forward, the pilot still in the seat, and the drive pays 85% of its charge;
    /// - again at once (charged again): refused, the 20 s cooldown;
    /// - a ship with nobody in its cockpit: refused;
    /// - a ship with a wall where it would land: refused;
    /// - after the cooldown, with less than 85% charge: refused; charged: it goes again.
    /// Skipped where the mod is not loaded.
    /// </summary>
    public sealed class BlinkDriveCheck : RegressionCheck
    {
        public override string Name => Prefix + "blink_drive";
        public override int TimeoutSeconds => 150;

        private const string Drive = "JumpDrive/LargeJumpDrive";
        private const string Seat = "Cockpit/LargeBlockCockpit";
        private const double Distance = 500;

        protected override IEnumerator Check()
        {
            var mod = MyScriptManager.Static?.Scripts?.Values.FirstOrDefault(a => a.GetType("Sentis_v5.BlinkDrive") != null);
            SkipUnless(mod != null, "no blink drive mod in this world");
            SkipUnless(Has(Drive) && Has(Seat), "no " + Drive + " or " + Seat + " in this game");

            yield return Arrive(Site.At(0, 0, -40));
            var failures = new List<string>();

            var ship = Ship("blink-ship", Site.At(0, 0, 0));
            // the wall's ship and the pilotless one, well apart (the blink is along each cockpit's forward)
            var lonely = Ship("blink-nobody", Site.At(600, 0, 0));
            var blocked = Ship("blink-blocked", Site.At(-600, 0, 0));
            var blockedSeat = SeatOf(blocked);
            var wallAt = blocked.PositionComp.GetPosition() + blockedSeat.WorldMatrix.Forward * Distance;
            var wall = Build("blink-wall", wallAt, true, Bodies.Wall("CubeBlock/LargeBlockArmorBlock", 4, 2).ToArray());
            yield return Seconds(2); // the drive's action is made on its first frames; BigOwners on the next update

            var seat = SeatOf(ship);
            var drive = DriveOf(ship);
            yield return Sit(seat);
            yield return Seconds(PilotSettles);

            // 1. the blink
            var from = ship.PositionComp.GetPosition();
            var forward = seat.WorldMatrix.Forward;
            var charge = drive.CurrentStoredPower;
            var max = drive.MaxStoredPower;
            if (!Blink(drive)) failures.Add("the jump drive has no BlinkDrive_Jump action");
            yield return Seconds(2);
            var moved = ship.PositionComp.GetPosition() - from;
            var along = Vector3D.Dot(moved, forward);
            var aside = (moved - forward * along).Length();
            var paid = (charge - drive.CurrentStoredPower) / max;
            Note("blink: " + along.ToString("F1") + " m along the cockpit's forward, " + aside.ToString("F1") + " m aside, paid " +
                 (paid * 100).ToString("F0") + "% of the charge, pilot " + (seat.Pilot != null ? "seated" : "gone"));
            if (Math.Abs(along - Distance) > 30) failures.Add("the ship went " + along.ToString("F0") + " m along its cockpit's forward, not " + Distance);
            if (aside > 10) failures.Add("the ship went " + aside.ToString("F0") + " m aside");
            if (Math.Abs(paid - 0.85) > 0.02) failures.Add("the drive paid " + (paid * 100).ToString("F0") + "% of its charge, not 85%");
            if (seat.Pilot == null) failures.Add("the pilot left the seat with the blink");

            // 2. again at once, charged: the cooldown
            drive.CurrentStoredPower = max;
            yield return Refused(ship, drive, "a blink again at once (the 20 s cooldown)", "Wait to jump again", failures);

            // 3. nobody in the cockpit
            yield return Refused(lonely, DriveOf(lonely), "a blink with nobody in the cockpit", "No pilot found", failures);

            // 4. a wall where it would land: the pilot over to that ship
            seat.RemovePilot();
            yield return Seconds(1);
            yield return Sit(blockedSeat);
            yield return Seconds(PilotSettles);
            yield return Refused(blocked, DriveOf(blocked), "a blink into a wall", "obstacle ahead", failures);
            blockedSeat.RemovePilot();
            yield return Seconds(1);

            // 5. after the cooldown: too little charge, then enough
            yield return Sit(seat);
            yield return Seconds(PilotSettles);
            yield return Seconds(19);
            drive.CurrentStoredPower = max * 0.5f;
            yield return Refused(ship, drive, "a blink with 50% charge (85% needed)", "Charging", failures);
            drive.CurrentStoredPower = max;
            from = ship.PositionComp.GetPosition();
            forward = seat.WorldMatrix.Forward;
            Blink(drive);
            yield return Seconds(2);
            var second = Vector3D.Dot(ship.PositionComp.GetPosition() - from, forward);
            Note("blink after the cooldown: " + second.ToString("F1") + " m");
            if (Math.Abs(second - Distance) > 30) failures.Add("after the cooldown, charged, the ship went " + second.ToString("F0") + " m, not " + Distance);
            seat.RemovePilot();

            foreach (var failure in failures) Note("FAILED: " + failure);
            Check(failures.Count == 0, string.Join("; ", failures));
            Result("the seated pilot's blink took the ship 500 m along the cockpit for 85% of the charge; refused in the cooldown, " +
                   "with nobody seated, into a wall and with too little charge; again after the cooldown");
        }

        /// <summary>The action applied, then the ship still where it was 2 s later.</summary>
        private IEnumerator Refused(MyCubeGrid ship, IMyJumpDrive drive, string what, string reason, List<string> failures)
        {
            var log = KeenLog();
            var start = new FileInfo(log).Length;
            var from = ship.PositionComp.GetPosition();
            var charge = drive.CurrentStoredPower;
            Blink(drive);
            yield return Seconds(2);
            var moved = Vector3D.Distance(ship.PositionComp.GetPosition(), from);
            Note(what + ": moved " + moved.ToString("F1") + " m, charge " + (charge / drive.MaxStoredPower * 100).ToString("F0") + "% -> " +
                 (drive.CurrentStoredPower / drive.MaxStoredPower * 100).ToString("F0") + "%");
            if (moved > 5) failures.Add(what + " went " + moved.ToString("F0") + " m");
            // the reason the mod gave (on a dedicated server its notification goes to the log)
            var said = Tail(log, start).Split('\n').Where(l => l.Contains("[BlinkDrive] " + drive.EntityId + " msg:"))
                .Select(l => l.Substring(l.IndexOf("msg:") + 4).Trim()).ToList();
            Note(what + ": the mod said " + (said.Count == 0 ? "nothing" : string.Join(" | ", said)));
            if (!said.Any(s => s.IndexOf(reason, StringComparison.OrdinalIgnoreCase) >= 0))
                failures.Add(what + " was not refused for its reason ('" + reason + "'): " + (said.Count == 0 ? "nothing said" : string.Join(" | ", said)));
        }

        /// <summary>Seconds the game takes to tie a seated player to the seat's grid (GetPlayerControllingEntity of the grid).</summary>
        private const int PilotSettles = 4;

        private static string KeenLog() =>
            Directory.GetFiles(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs"), "Keen-*.log").OrderByDescending(File.GetLastWriteTimeUtc).First();

        private static string Tail(string path, long from)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(Math.Min(from, stream.Length), SeekOrigin.Begin);
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }

        private static bool Blink(IMyJumpDrive drive)
        {
            var action = ((Sandbox.ModAPI.IMyTerminalBlock)drive).GetActionWithName("BlinkDrive_Jump");
            if (action == null) return false;
            action.Apply(drive);
            return true;
        }

        private MyCubeGrid Ship(string name, Vector3D at)
        {
            var driveSize = Definition(Drive).Size;
            var stored = ((MyJumpDriveDefinition)Definition(Drive)).PowerNeededForJump;
            return Build(name, at, false, new[]
            {
                new Part(Seat, 0, 0, 0),
                new Part(Drive, 0, -driveSize.Y, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_JumpDrive)b).StoredPower = stored),
                new Part("BatteryBlock/LargeBlockBatteryBlock", driveSize.X, -1, 0).With(b => Motion.Charged(b)),
            }, ownedBy: Identity);
        }

        private static MyCockpit SeatOf(MyCubeGrid grid) => grid.GetFatBlocks().OfType<MyCockpit>().First();

        private static IMyJumpDrive DriveOf(MyCubeGrid grid) => grid.GetFatBlocks().OfType<IMyJumpDrive>().First();

        private static bool Has(string id)
        {
            var parts = id.Split('/');
            return MyDefinitionManager.Static.TryGetCubeBlockDefinition(new MyDefinitionId(MyObjectBuilderType.Parse("MyObjectBuilder_" + parts[0]), parts[1]), out _);
        }
    }
}
