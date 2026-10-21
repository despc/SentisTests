using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;
using IngameInventory = VRage.Game.ModAPI.Ingame.IMyInventory;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A small factory on conveyors (the production scenario's layout): a cargo container of ore, a refinery, an
    /// assembler and a reactor on a line of conveyor junctions. The refinery pulls the ore through the conveyors itself
    /// and pushes its ingots on; the assembler, given 5 steel plates to make, pulls the iron it needs - nothing is put
    /// into any block but the cargo container.
    /// </summary>
    public sealed class ConveyorCheck : RegressionCheck
    {
        public override string Name => Prefix + "conveyor";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 12, -10));
            var grid = Build("conveyor", Site.Origin, true,
                new Part("Refinery/LargeRefinery", 0, 0, 0),
                new Part("Assembler/LargeAssembler", -3, 1, 1),
                new Part("CargoContainer/LargeBlockLargeContainer", -7, -1, 0),
                new Part("Reactor/LargeBlockSmallGenerator", -2, -1, 1),
                new Part("Conveyor/LargeBlockConveyor", -1, 0, 1),
                new Part("Conveyor/LargeBlockConveyor", -2, 0, 1),
                new Part("Conveyor/LargeBlockConveyor", -3, 0, 1),
                new Part("Conveyor/LargeBlockConveyor", -4, 0, 1));
            var cargo = Inventory(Block<MyCargoContainer>(grid));
            var refinery = Block<MyRefinery>(grid);
            var assembler = Block<MyAssembler>(grid);
            var reactor = Block<MyReactor>(grid);
            Put(cargo, "Ore/Iron", 2000);
            // the reactor started with a little uranium of its own (the conveyors take power), the rest in the cargo
            Put(Inventory(reactor), "Ingot/Uranium", 0.5);
            yield return Until(() => ((IngameInventory)cargo).IsConnectedTo(refinery.InputInventory) && ((IngameInventory)cargo).IsConnectedTo(assembler.InputInventory) &&
                                     ((IngameInventory)cargo).IsConnectedTo(Inventory(reactor)) ? 1 : 0, v => v > 0,
                "the cargo on the conveyors with the refinery, the assembler and the reactor", 10,
                () => "refinery " + ((IngameInventory)cargo).IsConnectedTo(refinery.InputInventory) + ", assembler " + ((IngameInventory)cargo).IsConnectedTo(assembler.InputInventory) +
                      ", reactor " + ((IngameInventory)cargo).IsConnectedTo(Inventory(reactor)));
            Put(cargo, "Ingot/Uranium", 10);

            yield return Until(() => Amount(Inventory(reactor), "Ingot/Uranium"), v => v > 0.6, "uranium pulled into the reactor", 30);
            yield return Until(() => 2000 - Amount(cargo, "Ore/Iron"), v => v > 0, "ore pulled out of the cargo", 30, () => State(refinery));
            var blueprint = Blueprints.Get("SteelPlate");
            Request(assembler, "OnAddQueueItemRequest", -1, (SerializableDefinitionId)blueprint.Id, (MyFixedPoint)5);
            yield return Until(() => Total(grid, "Component/SteelPlate"), v => v >= 5, "steel plates made of iron the assembler pulled", 120, () => State(assembler));
            Result("ore pulled from the cargo by the refinery, uranium by the reactor, iron by the assembler: " + Total(grid, "Component/SteelPlate") +
                   " plates; ingots in the cargo " + F(Amount(cargo, "Ingot/Iron")));
        }
    }

    /// <summary>
    /// A conveyor sorter set to drain moves everything from the container on one side of it to the one on the other;
    /// with a whitelist of ore, only ore.
    /// </summary>
    public sealed class SorterCheck : RegressionCheck
    {
        public override string Name => Prefix + "sorter";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -6));
            // the sorter's two ports on its front and back: tried facing one container, then the other
            foreach (var facing in new[] { Base6Directions.Direction.Right, Base6Directions.Direction.Left })
            {
                var grid = Build("sorter", Site.Origin, true,
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 1, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f),
                    new Part("CargoContainer/LargeBlockSmallContainer", -1, 0, 0).Named("A"),
                    new Part("ConveyorSorter/LargeBlockConveyorSorter", 0, 0, 0).Facing(facing),
                    new Part("CargoContainer/LargeBlockSmallContainer", 1, 0, 0).Named("B"));
                var a = Inventory(grid.GetCubeBlock(new Vector3I(-1, 0, 0)).FatBlock);
                var b = Inventory(grid.GetCubeBlock(new Vector3I(1, 0, 0)).FatBlock);
                var sorter = Block<MyConveyorSorter>(grid);
                Put(a, "Ore/Iron", 100);
                Put(a, "Component/SteelPlate", 10);
                // only ore through
                ((Sandbox.ModAPI.Ingame.IMyConveyorSorter)sorter).SetFilter(Sandbox.ModAPI.Ingame.MyConveyorSorterMode.Whitelist,
                    new System.Collections.Generic.List<Sandbox.ModAPI.Ingame.MyInventoryItemFilter>
                    {
                        new Sandbox.ModAPI.Ingame.MyInventoryItemFilter(new MyDefinitionId(typeof(MyObjectBuilder_Ore)), true),
                    });
                ((Sandbox.ModAPI.IMyTerminalBlock)sorter).SetValueBool("DrainAll", true);
                for (var f = Frames(20); f > 0 && Amount(b, "Ore/Iron") < 100; f--)
                    yield return null;
                if (Amount(b, "Ore/Iron") >= 100)
                {
                    yield return Seconds(3);
                    Check(Amount(b, "Component/SteelPlate") == 0, "plates went through a sorter that lets only ore through");
                    Result("sorter facing " + facing + " drained 100 kg of ore from A to B, the plates stayed in A (" + Amount(a, "Component/SteelPlate") + ")");
                    yield break;
                }
                Note("facing " + facing + ": in B ore " + F(Amount(b, "Ore/Iron")) + ", left in A " + F(Amount(a, "Ore/Iron")) + ", in the sorter "
                     + F(sorter.InventoryCount > 0 ? Amount(Inventory(sorter), "Ore/Iron") : 0) + "; " + State(sorter));
                grid.Close();
                yield return null;
            }
            Check(false, "the sorter moved nothing either way it faced");
        }
    }

    /// <summary>
    /// A ship with a connector comes to a station's connector: connected as the player connects them, the ship's cargo is
    /// on the station's conveyors; disconnected, it is not, and the two grids are apart again.
    /// </summary>
    public sealed class ConnectorCheck : RegressionCheck
    {
        public override string Name => Prefix + "connector";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(6, 6, 0));
            var station = Build("connector-station", Site.Origin, true,
                new Part("ShipConnector/Connector", 0, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 1),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 1).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f));
            var dock = (MyShipConnector)station.GetCubeBlock(Vector3I.Zero).FatBlock;
            // the ship's connector face to face with the station's: its grid turned about, a block's length on
            var shipAt = station.GridIntegerToWorld(Vector3I.Zero) + Site.Forward * 2.5;
            var ship = Build("connector-ship", shipAt, false, new[]
            {
                new Part("ShipConnector/Connector", 0, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 1),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 1).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f),
            }, forward: -Site.Forward, up: Site.Up);
            var shipConnector = (MyShipConnector)ship.GetCubeBlock(Vector3I.Zero).FatBlock;
            var stationCargo = Inventory(station.GetCubeBlock(new Vector3I(0, 0, 1)).FatBlock);
            var shipCargo = Inventory(ship.GetCubeBlock(new Vector3I(0, 0, 1)).FatBlock);
            Check(!((IngameInventory)shipCargo).IsConnectedTo(stationCargo), "the ship's cargo is on the station's conveyors before docking");

            yield return Until(() => ((Sandbox.ModAPI.Ingame.IMyShipConnector)shipConnector).Status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connectable ? 1 : 0,
                v => v > 0, "the connectors in reach of each other", 20, () => "status " + ((Sandbox.ModAPI.Ingame.IMyShipConnector)shipConnector).Status + ", " +
                                                                                    F(Vector3D.Distance(dock.PositionComp.GetPosition(), shipConnector.PositionComp.GetPosition())) + " m apart");
            Request(shipConnector, "TryConnect");
            yield return Until(() => shipConnector.Connected ? 1 : 0, v => v > 0, "the connectors connected", 10);
            yield return Until(() => ((IngameInventory)shipCargo).IsConnectedTo(stationCargo) ? 1 : 0, v => v > 0, "the ship's cargo on the station's conveyors", 10);
            Put(stationCargo, "Component/SteelPlate", 20);
            Request(stationCargo, "InventoryTransferItem_Implementation", (MyFixedPoint)20, stationCargo.GetItems()[0].ItemId,
                ship.GetCubeBlock(new Vector3I(0, 0, 1)).FatBlock.EntityId, (byte)0, -1);
            Check(Amount(shipCargo, "Component/SteelPlate") == 20, "the plates did not go over the connection");

            Request(shipConnector, "TryDisconnect");
            yield return Until(() => shipConnector.Connected ? 0 : 1, v => v > 0, "the connectors disconnected", 10);
            yield return Until(() => ((IngameInventory)shipCargo).IsConnectedTo(stationCargo) ? 0 : 1, v => v > 0, "the ship's cargo off the station's conveyors", 10);
            Result("ship docked and undocked by connector; its cargo was on the station's conveyors only while docked");
        }
    }

    /// <summary>
    /// Two grids touching only by their merge blocks, face to face, both on: they become one grid. A merge block switched
    /// off, it parts in two again; the part that was the ship, static now as the station it joined, turned into a ship
    /// again, moves.
    /// </summary>
    public sealed class MergeCheck : RegressionCheck
    {
        public override string Name => Prefix + "merge";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(6, 6, 0));
            // which face of the merge block merges: read off one built out of the way
            var probe = Build("merge-probe", Site.Origin + Site.Up * 40, true, new Part("MergeBlock/LargeShipMergeBlock", 0, 0, 0));
            var probeMerge = probe.GetCubeBlock(Vector3I.Zero).FatBlock;
            var forwardField = probeMerge.GetType().GetField("m_forward", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Check(forwardField != null, "MyShipMergeBlock.m_forward is not in this game");
            var faceInGrid = Base6Directions.GetIntVector(probeMerge.Orientation.TransformDirection((Base6Directions.Direction)forwardField.GetValue(probeMerge)));
            probe.Close();
            // each grid's armor behind its merge block: the two touch by the merge blocks alone
            Part[] Parts() => new[]
            {
                new Part("MergeBlock/LargeShipMergeBlock", 0, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", -faceInGrid.X, -faceInGrid.Y, -faceInGrid.Z),
            };
            var station = Build("merge-station", Site.Origin, true, Parts());
            var merge = station.GetCubeBlock(Vector3I.Zero).FatBlock;
            var face = merge.WorldMatrix.GetDirectionVector((Base6Directions.Direction)forwardField.GetValue(merge));
            // the ship face to face: its grid the station's turned half round about an axis across the face
            var across = Math.Abs(Vector3D.Dot(face, Site.Up)) > 0.9 ? Site.Right : Site.Up;
            var turn = MatrixD.CreateFromAxisAngle(across, Math.PI);
            var shipAt = merge.PositionComp.GetPosition() + face * 2.5;
            var ship = Build("merge-ship", shipAt, false, Parts(),
                forward: Vector3D.TransformNormal(Site.Forward, turn), up: Vector3D.TransformNormal(Site.Up, turn));
            var blocks = station.BlocksCount;
            yield return Until(() => station.BlocksCount, v => v >= blocks + 2, "the ship's blocks in the station's grid", 30,
                () => "ship " + (ship.MarkedForClose ? "closed" : "apart, " + F(Vector3D.Distance(ship.PositionComp.GetPosition(), merge.PositionComp.GetPosition())) + " m"));
            var merged = station.BlocksCount;

            // the station's merge block off: the grid parts where the merge blocks met
            Switch((MyCubeBlock)merge, false);
            yield return Until(() => station.BlocksCount, v => v <= merged - 2, "the grid parted with the merge block off", 15, () => station.BlocksCount + " blocks in the station's grid");
            var part = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => g != station && !g.MarkedForClose &&
                                                                                          Vector3D.Distance(g.PositionComp.WorldAABB.Center, shipAt) < 6 &&
                                                                                          g.GetFatBlocks().Any(b => b.BlockDefinition.Id.SubtypeName == "LargeShipMergeBlock"));
            Check(part != null, "no grid of the ship's merge block came off the station");
            Track(part);
            var wasStatic = part.IsStatic;
            if (wasStatic)
            {
                var reason = typeof(MyCubeGrid).GetNestedType("MyTestDynamicReason", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                Request(part, "OnConvertedToShipRequest", Enum.Parse(reason, "ConvertToShip"));
                yield return Until(() => part.IsStatic ? 0 : 1, v => v > 0, "the part that came off a ship again", 5);
            }
            yield return null;
            part.Physics.LinearVelocity = (Vector3)(face * 2);
            var from = part.PositionComp.GetPosition();
            yield return Seconds(1);
            var moved = Vector3D.Distance(from, part.PositionComp.GetPosition());
            Check(moved > 1, "the part that came off moved " + F(moved) + " m in 1 s pushed at 2 m/s");
            Result("merged into " + merged + " blocks; the merge block off, parted in two" + (wasStatic ? ", the part off static and turned into a ship again" : "") +
                   ", it moved " + F(moved) + " m");
        }
    }
}
