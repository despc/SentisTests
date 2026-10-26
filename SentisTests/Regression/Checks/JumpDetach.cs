using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using SpaceEngineers.Game.Entities.Blocks;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A jump drive between clusters detached by hand (SentisClusters' <c>!clusters detach</c>) and attached ones, both
    /// ways: a station of 12 blocks at the site, a buoy (a free container) 7 km up, a ship of reg_jump_across_clusters'.
    /// - out of a detached cluster into an attached one: the site's cluster detached (kept by the station, the biggest
    ///   grid there), the ship jumps up to the buoy's cluster, attached;
    /// - back, attached into detached;
    /// - into a detached cluster from a detached one: the buoy's cluster detached too, the ship jumps up again;
    /// - and back.
    /// After every jump the ship is in the cluster it jumped to and moves on thrust there; the server stays up (a jump out
    /// of a detached cluster crashed Havok in the ship's first step under thrust in the new cluster, 26.10.2026). Not part
    /// of the regression: it detaches clusters.
    /// </summary>
    public sealed class JumpDetachCheck : RegressionCheck
    {
        public const string ScenarioName = "jump_detach";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        protected override IEnumerator Check()
        {
            SkipUnless(Integrations.IsLoaded("SentisClusters"), "SentisClusters is not loaded");
            yield return Arrive(Site.At(0, 0, 8));
            var definition = (MyJumpDriveDefinition)Definition("JumpDrive/LargeJumpDrive");
            // the station the biggest grid of the site: the site's cluster detached by hand stays with it, not with the ship
            var stationParts = new List<Part> { new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)) };
            for (var i = 1; i < 12; i++) stationParts.Add(new Part("CubeBlock/LargeBlockArmorBlock", i, 0, 0));
            var station = Build("jump-detach-station", Site.At(30, 0, 0), true, stationParts);
            // the buoy up there: a free grid, so that a cluster is there before the ship comes
            var up = Site.Up * 7000;
            var buoyParts = new List<Part>();
            for (var i = 0; i < 10; i++) buoyParts.Add(new Part("CubeBlock/LargeBlockArmorBlock", i, 0, 0));
            var buoy = Build("jump-detach-buoy", Site.At(30, 0, 0) + up, false, buoyParts);
            var ship = Build("jump-detach-ship", Site.Origin, false,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, -1, 0).With(b => Motion.Charged(b)),
                new Part("Gyro/LargeBlockGyro", 0, 1, 0),
                new Part("Thrust/LargeBlockSmallThrust", 1, 0, 0).Facing(Direction.Backward),
                new Part("Thrust/LargeBlockSmallThrust", -1, 0, 0).Facing(Direction.Forward),
                new Part("JumpDrive/LargeJumpDrive", 2, 0, 0).With(b => ((MyObjectBuilder_JumpDrive)b).StoredPower = definition.PowerNeededForJump));
            var drive = Block<MyJumpDrive>(ship);
            yield return Until(() => drive.IsWorking ? 1 : 0, v => v > 0, "the jump drive working", 10, () => State(drive));
            var seat = Block<MyCockpit>(ship);
            yield return Sit(seat);
            yield return Seconds(1);

            var site = station.PositionComp.GetPosition();
            Detach(site, "the site's");
            var report = new List<string>();

            // detached -> attached
            yield return Jump(ship, seat, drive, definition, buoy.PositionComp.GetPosition() + Site.Forward * 40, "up, out of the detached site's cluster", report);
            Check(!Detached(buoy), "the buoy's cluster is detached before it was asked to be: " + Here(buoy));
            // attached -> detached
            yield return Jump(ship, seat, drive, definition, site + Site.Forward * 40, "back down, into the detached site's cluster", report);
            Check(Detached(ship), "the ship back at the site is not in a detached cluster: " + Here(ship));
            // detached -> detached
            Detach(buoy.PositionComp.GetPosition(), "the buoy's");
            yield return Jump(ship, seat, drive, definition, buoy.PositionComp.GetPosition() + Site.Forward * 40, "up again, into the detached buoy's cluster", report);
            Check(Detached(ship), "the ship up at the buoy is not in a detached cluster: " + Here(ship));
            // and attached -> detached the other way: the site's attached, the buoy's still detached
            Command("!clusters attach " + V(site));
            yield return Jump(ship, seat, drive, definition, site + Site.Forward * 40, "down, out of the detached buoy's cluster into the attached site's", report);
            Check(!Detached(ship), "the ship at the attached site is in a detached cluster: " + Here(ship));
            Result("jumps with thrust after each: " + string.Join("; ", report));
        }

        private IEnumerator Jump(MyCubeGrid ship, MyCockpit seat, MyJumpDrive drive, MyJumpDriveDefinition definition, Vector3D target, string what, List<string> report)
        {
            Check(Recharge(drive, definition), "MyJumpDrive.CurrentStoredPower is not in this game: the drive cannot be charged");
            yield return Until(() => drive.CanJumpAndHasAccess(Identity) ? 1 : 0, v => v > 0, "the drive ready to jump " + what, 10, () => State(drive));
            var from = ship.PositionComp.GetPosition();
            var before = ClusterId(ship);
            Request(ship, "OnJumpRequested", target, Identity, 10f, (long?)null);
            yield return Until(() => ship.GridSystems.JumpSystem.IsJumping ? 1 : 0, v => v > 0, "the jump " + what + " counting down", 5);
            // the game ends a jump short of another grid in the way
            yield return Until(() => Vector3D.Distance(ship.PositionComp.GetPosition(), from), v => v > 5000, "the ship jumped " + what + ", m", 30);
            // thrust at once, in the ship's first frames in the new cluster (the crash was there)
            var at = ship.PositionComp.GetPosition();
            yield return HoldShip(3, seat, new Vector3(0, 0, -1));
            var speed = Motion.Speed(ship);
            var moved = Vector3D.Distance(ship.PositionComp.GetPosition(), at);
            Check(speed > 0.5 && moved > 0.5, "the ship sits dead after jumping " + what + ": " + F(speed) + " m/s, " + F(moved) + " m after 3 s of thrust (" + PhysicsState(ship) + ")");
            var within = Within(20);
            while (Motion.Speed(ship) > 0.3 && within())
            {
                ShipKeys(seat, Vector3.Zero);
                yield return null;
            }
            Check(Motion.Speed(ship) <= 0.3, "the ship not stopped by its dampeners after jumping " + what + ": " + F(Motion.Speed(ship)) + " m/s after 20 s (" + PhysicsState(ship) + ")");
            report.Add(what + ": cluster " + before + " -> " + ClusterId(ship) + (Detached(ship) ? " (detached)" : "") + ", " + F(speed) + " m/s");
        }

        private void Detach(Vector3D at, string whose)
        {
            var answer = string.Join(" ", Command("!clusters detach " + V(at)));
            Note("detach " + whose + ": " + answer);
            Check(answer.Contains("detached"), whose + " cluster was not detached: " + answer);
        }

        private static bool Detached(MyCubeGrid grid) => Here(grid).Contains("detached");

        private static string Here(MyCubeGrid grid) => string.Join(" ", Command("!clusters here " + V(grid.PositionComp.GetPosition())));

        private static string V(Vector3D v) => $"{v.X:F0} {v.Y:F0} {v.Z:F0}";

        private static bool Recharge(MyJumpDrive drive, MyJumpDriveDefinition definition)
        {
            var power = typeof(MyJumpDrive).GetProperty("CurrentStoredPower", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (power == null || !power.CanWrite) return false;
            power.SetValue(drive, definition.PowerNeededForJump);
            return true;
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
            base.Cleanup();
        }
    }
}
