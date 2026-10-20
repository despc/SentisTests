using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>A programmable block whose script sets its own update frequency runs again and again with nobody pressing Run.</summary>
    public sealed class ProgrammableSelfRunCheck : RegressionCheck
    {
        public override string Name => Prefix + "programmable_self_run";

        private const string Script = @"
int runs;
public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update10; }
public void Main(string argument, UpdateType update) { if ((update & UpdateType.Update10) != 0) Me.CustomData = (++runs).ToString(); }";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.EnableIngameScripts, "in-game scripts are off in this world");
            yield return Arrive(Site.At(0, 4, -6));
            var grid = Build("programmable-self", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("MyProgrammableBlock/LargeProgrammableBlock", 1, 0, 0).With(b => ((MyObjectBuilder_MyProgrammableBlock)b).Program = Script));
            var pb = Block<MyProgrammableBlock>(grid);
            Func<double> runs = () => int.TryParse(pb.CustomData, out var n) ? n : 0;
            yield return Until(runs, v => v >= 10, "the script running by itself, times", 30, () => State(pb) + "; " + pb.DetailedInfo.ToString().Trim().Replace("\n", " "));
            Result("the script ran " + runs() + " times by itself, every 10 frames");
        }
    }

    /// <summary>A jump drive with its store empty charges from the grid's batteries.</summary>
    public sealed class JumpDriveChargeCheck : RegressionCheck
    {
        public override string Name => Prefix + "jump_drive_charge";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, 8));
            var ship = Build("jump-charge", Site.Origin, true,
                new Part("JumpDrive/LargeJumpDrive", 0, 0, 0).With(b => ((MyObjectBuilder_JumpDrive)b).StoredPower = 0f),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 3, 0, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 3, 1, 0).With(b => Motion.Charged(b)));
            var drive = (Sandbox.ModAPI.Ingame.IMyJumpDrive)Block<MyJumpDrive>(ship);
            yield return Until(() => drive.CurrentStoredPower, v => v > 0.01, "the jump drive charging, MWh", 30, () => State((MyCubeBlock)drive));
            var first = drive.CurrentStoredPower;
            yield return Seconds(2);
            Check(drive.CurrentStoredPower > first, "the jump drive's charge stopped at " + first.ToString("F3") + " MWh");
            Result("the jump drive charged to " + drive.CurrentStoredPower.ToString("F3") + " of " + drive.MaxStoredPower.ToString("F3") + " MWh from the batteries");
        }
    }

    /// <summary>
    /// A hydrogen tank set to stockpile gives nothing out: the hydrogen thrusters on it get no gas and the ship does not
    /// move; stockpile off, it goes.
    /// </summary>
    public sealed class TankStockpileCheck : RegressionCheck
    {
        public override string Name => Prefix + "tank_stockpile";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, 10));
            var tank = new Part("OxygenTank/LargeHydrogenTankSmall", 0, 0, 0).With(b => ((MyObjectBuilder_GasTank)b).FilledRatio = 1f);
            Part back = null, front = null;
            yield return ConveyorNeighbour(tank, "Thrust/LargeBlockSmallHydrogenThrust", null, p => back = p);
            var backCells = Cells(back);
            var opposite = Base6Directions.GetFlippedDirection(back.Forward);
            yield return ConveyorNeighbour(tank, "Thrust/LargeBlockSmallHydrogenThrust", p => p.Forward == opposite && !Cells(p).Overlaps(backCells), p => front = p);
            var taken = new HashSet<Vector3I>(Cells(tank).Concat(backCells).Concat(Cells(front)));
            var free = Enumerable.Range(1, 6).Select(x => new Vector3I(-x, 0, 0)).Where(c => !taken.Contains(c)).Take(2).ToList();
            var ship = Build("tank-stockpile", Site.Origin, false, new[]
            {
                tank, back, front,
                new Part("Cockpit/LargeBlockCockpitSeat", free[0].X, free[0].Y, free[0].Z),
                new Part("BatteryBlock/LargeBlockBatteryBlock", free[1].X, free[1].Y, free[1].Z).With(b => Motion.Charged(b)),
            });
            var seat = Block<MyCockpit>(ship);
            var gas = (Sandbox.ModAPI.Ingame.IMyGasTank)Block<MyGasTank>(ship);
            gas.Stockpile = true;
            yield return Sit(seat);
            var keys = -(Vector3)Base6Directions.GetVector(back.Forward);
            var from = ship.PositionComp.GetPosition();
            yield return HoldShip(3, seat, keys);
            var still = Vector3D.Distance(ship.PositionComp.GetPosition(), from);
            Check(still < 0.5, "with the tank stockpiling 3 s on the thrusters moved the ship " + F(still) + " m (" +
                               string.Join("; ", Blocks<MyThrust>(ship).Select(t => State(t) + ", thrust " + F(t.CurrentStrength))) + ")");
            gas.Stockpile = false;
            from = ship.PositionComp.GetPosition();
            yield return HoldShip(3, seat, keys);
            var gone = Vector3D.Distance(ship.PositionComp.GetPosition(), from);
            Check(gone > 2, "stockpile off, 3 s on the thrusters moved the ship " + F(gone) + " m");
            Result("the tank stockpiling, the hydrogen thrusters moved the ship " + F(still) + " m in 3 s; stockpile off, " + F(gone) + " m");
        }
    }

    /// <summary>
    /// A player with no components welds a frame of armor: the welder takes it only as far as the components already in
    /// the frame go, and there it stops, far from whole.
    /// </summary>
    public sealed class WeldWithoutComponentsCheck : HandCheck
    {
        public override string Name => Prefix + "weld_without_components";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -4));
            var grid = Build("weld-empty", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0).At(0.05f));
            var block = grid.GetCubeBlock(Vector3I.Zero);
            Inventory(Body).Clear();
            yield return Equip("WelderItem");
            yield return AimAt(block);
            var frame = block.Integrity;
            yield return Pull();
            for (var f = Frames(4); f > 0; f--) yield return null;
            var reached = block.Integrity;
            for (var f = Frames(3); f > 0; f--) yield return null;
            Release();
            Check(block.Integrity <= reached + 0.01, "with no components the welder kept welding: " + F(reached) + " -> " + F(block.Integrity) + " in 3 s more");
            Check(block.Integrity < block.MaxIntegrity * 0.2, "with no components the frame was welded to " + F(block.Integrity) + " of " + F(block.MaxIntegrity));
            Result("with no components the frame went from " + F(frame) + " to " + F(block.Integrity) + " (what its own components give) and stopped, of " + F(block.MaxIntegrity));
        }
    }
}
