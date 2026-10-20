using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage.Game;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;
using GearMode = SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// In open space a small ship's landing gear locks onto a big ship that drifts: locked, the small ship goes along with
    /// the big one, keeping its place on it; unlocked, the big one drifts away from it.
    /// </summary>
    public sealed class LandingGearShipCheck : RegressionCheck
    {
        public override string Name => Prefix + "landing_gear_ship";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -12));
            // the big ship: a plate of 3 x 3 heavy armor blocks, its top face 1.25 m over the site
            var plate = new List<Part>();
            for (var x = -1; x <= 1; x++)
            for (var z = -1; z <= 1; z++)
                plate.Add(new Part("CubeBlock/LargeHeavyBlockArmorBlock", x, 0, z));
            foreach (var face in new[] { Direction.Down, Direction.Forward, Direction.Backward, Direction.Up, Direction.Left, Direction.Right })
            {
                var big = Build("gear-carrier", Site.Origin - Site.Up * 1.25, false, plate);
                var up = Base6Directions.GetPerpendicular(face);
                var small = Build("gear-ship", Site.Origin + Site.Up * (1.25 + 0.3), false, new[] { new Part("LandingGear/LargeBlockLandingGear", 0, 0, 0).Facing(face, up) });
                var gear = (SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear)Block<SpaceEngineers.Game.Entities.Blocks.MyLandingGear>(small);
                for (var f = Frames(3); f > 0 && gear.LockMode == GearMode.Unlocked; f--)
                    yield return null;
                if (gear.LockMode == GearMode.Unlocked)
                {
                    small.Close();
                    big.Close();
                    yield return null;
                    continue;
                }
                gear.Lock();
                yield return Until(() => gear.IsLocked ? 1 : 0, v => v > 0, "the gear locked on the ship", 5);
                // where the small ship is on the big one, in the big one's own frame (the two may turn together)
                Func<Vector3D> onBig = () => Vector3D.Transform(small.PositionComp.GetPosition(), big.PositionComp.WorldMatrixNormalizedInv);
                var offset = onBig();
                var start = small.PositionComp.GetPosition();
                big.Physics.LinearVelocity = (Vector3)Site.Right * 3;
                yield return Seconds(2);
                var drift = Vector3D.Distance(onBig(), offset);
                var went = Vector3D.Distance(small.PositionComp.GetPosition(), start);
                Check(drift < 0.2, "the locked ship slid " + F(drift) + " m on the one it is locked to");
                Check(went > 1, "the locked ship went " + F(went) + " m in 2 s with the big one pushed at 3 m/s (now " + F(Motion.Speed(big)) + " m/s)");
                gear.Unlock();
                yield return Until(() => gear.IsLocked ? 0 : 1, v => v > 0, "the gear unlocked", 5);
                small.Physics.LinearVelocity = Vector3.Zero;
                big.Physics.LinearVelocity = (Vector3)Site.Right * 5;
                yield return Seconds(1);
                var apart = Vector3D.Distance(onBig(), offset);
                Check(apart > 2, "unlocked, the ships are still together (" + F(apart) + " m apart from where they were locked)");
                Result("gear facing " + face + " locked on a drifting ship: carried " + F(went) + " m along in 2 s, slid " + F(drift) + " m; unlocked, " + F(apart) + " m apart in 1 s");
                yield break;
            }
            Check(false, "the landing gear came ready to lock on the ship no way it faced");
        }
    }

    /// <summary>
    /// A connector with "Collect all" pulls everything off the cargo container on its conveyors; with "Throw out" it throws
    /// it into space in front of it.
    /// </summary>
    public sealed class ConnectorThrowOutCheck : RegressionCheck
    {
        public override string Name => Prefix + "connector_throw_out";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -10));
            var cargoPart = new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0);
            Part connectorPart = null;
            yield return ConveyorNeighbour(cargoPart, "ShipConnector/Connector", null, p => connectorPart = p);
            var taken = new HashSet<Vector3I>(Cells(cargoPart).Concat(Cells(connectorPart)));
            var free = taken.SelectMany(c => Base6Directions.IntDirections.Select(d => c + d)).First(c => !taken.Contains(c));
            var grid = Build("throw-out", Site.Origin, true, cargoPart, connectorPart,
                new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z).With(b => Motion.Charged(b)));
            var cargo = Inventory(Block<MyCargoContainer>(grid));
            var connector = Block<MyShipConnector>(grid);
            Put(cargo, "Ore/Stone", 200);
            ((Sandbox.ModAPI.IMyTerminalBlock)connector).SetValueBool("CollectAll", true);
            yield return Until(() => Amount(Inventory(connector), "Ore/Stone"), v => v > 0, "stone pulled into the connector, kg", 30, () => State(connector));
            ((Sandbox.ModAPI.IMyTerminalBlock)connector).SetValueBool("ThrowOut", true);
            var near = connector.PositionComp.GetPosition();
            Func<double> thrown = () => MyEntities.GetEntities().OfType<MyFloatingObject>()
                .Where(o => !o.MarkedForClose && Vector3D.Distance(o.PositionComp.GetPosition(), near) < 30 && o.Item.Content.SubtypeName == "Stone")
                .Sum(o => (double)o.Item.Amount);
            yield return Until(thrown, v => v > 0, "stone thrown out of the connector, kg", 30, () => State(connector));
            foreach (var floating in MyEntities.GetEntities().OfType<MyFloatingObject>().Where(o => Vector3D.Distance(o.PositionComp.GetPosition(), near) < 30).ToList())
                Track(floating);
            yield return Until(() => Amount(cargo, "Ore/Stone"), v => v < 200, "the cargo emptying through the connector, kg", 30);
            Result("the connector collected " + F(200 - Amount(cargo, "Ore/Stone")) + " kg of stone off the cargo and threw " + F(thrown()) + " kg out");
        }
    }

    /// <summary>
    /// A small grid's small connector (what was the ejector) throws what is in it out into space, as a player's ship dumps
    /// stone.
    /// </summary>
    public sealed class SmallConnectorThrowOutCheck : RegressionCheck
    {
        public override string Name => Prefix + "small_connector_throw_out";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 3, -5));
            var grid = Build("small-connector", Site.Origin, true, new[]
            {
                new Part("ShipConnector/ConnectorSmall", 0, 0, 0),
                new Part("BatteryBlock/SmallBlockBatteryBlock", 0, 0, 1).With(b => Motion.Charged(b)),
            }, MyCubeSize.Small);
            var connector = Block<MyShipConnector>(grid);
            Put(Inventory(connector), "Ore/Stone", 20);
            ((Sandbox.ModAPI.IMyTerminalBlock)connector).SetValueBool("ThrowOut", true);
            var near = connector.PositionComp.GetPosition();
            Func<double> thrown = () => MyEntities.GetEntities().OfType<MyFloatingObject>()
                .Where(o => !o.MarkedForClose && Vector3D.Distance(o.PositionComp.GetPosition(), near) < 20 && o.Item.Content.SubtypeName == "Stone")
                .Sum(o => (double)o.Item.Amount);
            yield return Until(thrown, v => v > 0, "stone thrown out of the small connector, kg", 30, () => State(connector));
            foreach (var floating in MyEntities.GetEntities().OfType<MyFloatingObject>().Where(o => Vector3D.Distance(o.PositionComp.GetPosition(), near) < 20).ToList())
                Track(floating);
            var mode = typeof(MyShipConnector).GetField("m_connectorMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(connector);
            Result("the small connector (" + mode + ") threw " + F(thrown()) + " kg of stone out, " + F(Amount(Inventory(connector), "Ore/Stone")) + " kg left in it");
        }
    }

    /// <summary>
    /// A player turns a station into a ship in its terminal ("Convert to ship"): it is free and moves when pushed; turned
    /// back ("Convert to station"), it stands still.
    /// </summary>
    public sealed class ConvertGridCheck : RegressionCheck
    {
        public override string Name => Prefix + "convert_grid";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -10));
            var grid = Build("convert", Site.Origin, true,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
            var reason = typeof(MyCubeGrid).GetNestedType("MyTestDynamicReason", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                         ?? throw new Core.ScenarioFailedException("MyCubeGrid.MyTestDynamicReason is not in this game");
            Request(grid, "OnConvertedToShipRequest", Enum.Parse(reason, "ConvertToShip"));
            yield return Until(() => grid.IsStatic ? 0 : 1, v => v > 0, "the station a ship", 5);
            yield return null;
            grid.Physics.LinearVelocity = (Vector3)Site.Right * 3;
            var from = grid.PositionComp.GetPosition();
            yield return Seconds(1);
            var moved = Vector3D.Distance(from, grid.PositionComp.GetPosition());
            Check(moved > 1, "the ship moved " + F(moved) + " m in 1 s pushed at 3 m/s");
            grid.Physics.LinearVelocity = Vector3.Zero;
            Request(grid, "OnConvertedToStationRequest");
            yield return Until(() => grid.IsStatic ? 1 : 0, v => v > 0, "the ship a station again", 5);
            var held = grid.PositionComp.GetPosition();
            yield return Seconds(1);
            var still = Vector3D.Distance(held, grid.PositionComp.GetPosition());
            Check(still < 0.01 && Motion.Speed(grid) < 0.01, "the station moves: " + F(still) + " m in 1 s");
            Result("station -> ship (moved " + F(moved) + " m when pushed) -> station (still)");
        }
    }
}
