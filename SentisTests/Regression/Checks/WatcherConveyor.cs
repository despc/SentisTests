using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisWatcher's conveyor and connector events. A ship docks to a station by connector (the player locks it): an
    /// event "connect" at each connector, the player who did it named. Over the connection the player moves plates by
    /// hand (the transfer event, not a conveyor one), the conveyors move some (conveyor_out at the ship's cargo,
    /// conveyor_in at the station's, "by conveyor") and a script on the station takes the rest (named with its
    /// programmable block and its owner as the one behind it). Undocked: "disconnect", by the player.
    /// </summary>
    public sealed class WatcherConveyorCheck : RegressionCheck
    {
        public override string Name => Prefix + "watcher_conveyor";

        private const string Script = @"
public void Main(string argument)
{
    var from = GridTerminalSystem.GetBlockWithName(""conveyor-ship-cargo"") as IMyCargoContainer;
    var to = GridTerminalSystem.GetBlockWithName(""conveyor-station-cargo"") as IMyCargoContainer;
    if (from == null || to == null) { Me.CustomData = ""no cargo""; return; }
    var inventory = from.GetInventory(0);
    Me.CustomData = inventory.ItemCount > 0 && inventory.TransferItemTo(to.GetInventory(0), 0, null, true, null) ? ""moved"" : ""nothing moved"";
}";

        private static Type Watcher(string type) => Core.Integrations.TypeOf("SentisWatcher", "SentisWatcher." + type);

        protected override IEnumerator Check()
        {
            SkipUnless(Watcher("Recording.ConveyorLog") != null, "SentisWatcher has no ConveyorLog");
            SkipUnless(MySession.Static.EnableIngameScripts, "in-game scripts are off in this world");
            var recorder = Watcher("Recording.Recorder").GetProperty("Current").GetValue(null);
            SkipUnless(recorder != null, "SentisWatcher records nothing");
            var plugin = Watcher("SentisWatcherPlugin").GetProperty("Instance").GetValue(null);
            var store = plugin.GetType().GetProperty("Store").GetValue(plugin);
            var web = Activator.CreateInstance(Watcher("Storage.WebData"), store);
            long Now() => (long)Watcher("Storage.Clock").GetProperty("Now").GetValue(null);
            var from = Now() - 1000;

            yield return Arrive(Site.At(6, 6, 0));
            MyObjectBuilder_CubeBlock Named(MyObjectBuilder_CubeBlock b, string name)
            {
                ((MyObjectBuilder_TerminalBlock)b).CustomName = name;
                return b;
            }
            var station = Build("conveyor-station", Site.Origin, true,
                new Part("ShipConnector/Connector", 0, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 1).With(b => Named(b, "conveyor-station-cargo")),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 1).With(b => Motion.Charged(b)),
                new Part("MyProgrammableBlock/LargeProgrammableBlock", -1, 0, 1).With(b => ((MyObjectBuilder_MyProgrammableBlock)b).Program = Script));
            var shipAt = station.GridIntegerToWorld(Vector3I.Zero) + Site.Forward * 2.5;
            var ship = Build("conveyor-ship", shipAt, false, new[]
            {
                new Part("ShipConnector/Connector", 0, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 1).With(b => Named(b, "conveyor-ship-cargo")),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 1).With(b => Motion.Charged(b)),
            }, forward: -Site.Forward, up: Site.Up);
            var shipConnector = (MyShipConnector)ship.GetCubeBlock(Vector3I.Zero).FatBlock;
            var stationConnector = (MyShipConnector)station.GetCubeBlock(Vector3I.Zero).FatBlock;
            var shipBox = ship.GetCubeBlock(new Vector3I(0, 0, 1)).FatBlock;
            var stationBox = station.GetCubeBlock(new Vector3I(0, 0, 1)).FatBlock;
            var shipCargo = Inventory(shipBox);
            var stationCargo = Inventory(stationBox);
            var pb = Block<MyProgrammableBlock>(station);

            yield return Until(() => ((Sandbox.ModAPI.Ingame.IMyShipConnector)shipConnector).Status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connectable ? 1 : 0,
                v => v > 0, "the connectors in reach of each other", 20, () => "status " + ((Sandbox.ModAPI.Ingame.IMyShipConnector)shipConnector).Status);
            Request(shipConnector, "TryConnect");
            yield return Until(() => shipConnector.Connected ? 1 : 0, v => v > 0, "the connectors connected", 10);
            yield return Until(() => Scripts.Ready(pb) ? 1 : 0, v => v > 0, "the station's script compiled", 30, () => State(pb));

            // by hand: the player's transfer, not the conveyors'
            Put(stationCargo, "Component/SteelPlate", 20);
            Request(stationCargo, "InventoryTransferItem_Implementation", (MyFixedPoint)20, stationCargo.GetItems()[0].ItemId, shipBox.EntityId, (byte)0, -1);
            Check(Amount(shipCargo, "Component/SteelPlate") == 20, "the player's 20 plates did not go over the connection");
            // the conveyors (the mod API's move, through the conveyor network): 5 plates back
            Check(((VRage.Game.ModAPI.IMyInventory)shipCargo).TransferItemTo(stationCargo, 0, null, true, (MyFixedPoint)5, true), "the conveyors moved nothing");
            // the station's script: the other 15
            pb.Run("", Sandbox.ModAPI.Ingame.UpdateType.Terminal);
            Check(pb.CustomData == "moved", "the script says '" + pb.CustomData + "'");
            Check(Amount(shipCargo, "Component/SteelPlate") == 0 && Amount(stationCargo, "Component/SteelPlate") == 20,
                "plates left on the ship " + F(Amount(shipCargo, "Component/SteelPlate")) + ", at the station " + F(Amount(stationCargo, "Component/SteelPlate")));

            Request(shipConnector, "TryDisconnect");
            yield return Until(() => shipConnector.Connected ? 0 : 1, v => v > 0, "the connectors disconnected", 10);

            recorder.GetType().GetMethod("FlushAggregates").Invoke(recorder, new object[] { true });
            yield return Seconds(4);
            var to = Now();
            List<IDictionary<string, object>> Events(long id) =>
                ((IEnumerable)((IDictionary<string, object>)web.GetType().GetMethod("Events").Invoke(web, new object[] { id, from, to }))["events"])
                .Cast<IDictionary<string, object>>().ToList();
            string Text(IEnumerable<IDictionary<string, object>> list) => string.Join("; ", list.Select(e => e["kind"] + " x" + e["amount"] + " " + e["detail"]));

            var me = Body.GetPlayerIdentityId();
            var outs = Events(shipBox.EntityId).Where(e => (string)e["kind"] == "conveyor_out").ToList();
            var ins = Events(stationBox.EntityId).Where(e => (string)e["kind"] == "conveyor_in").ToList();
            Note("ship's cargo: " + Text(outs));
            Note("station's cargo: " + Text(ins));
            double Sum(IEnumerable<IDictionary<string, object>> list) => list.Sum(e => Convert.ToDouble(e["amount"]));
            Check(Math.Abs(Sum(outs) - 20) < 0.01 && Math.Abs(Sum(ins) - 20) < 0.01, "conveyor moves out " + F(Sum(outs)) + ", in " + F(Sum(ins)) + "; 5 + 15 were moved");
            var byConveyor = outs.FirstOrDefault(e => ((string)e["detail"]).EndsWith("by conveyor"));
            var byScript = outs.FirstOrDefault(e => ((string)e["detail"]).Contains("by script \""));
            Check(byConveyor != null && Math.Abs(Convert.ToDouble(byConveyor["amount"]) - 5) < 0.01, "no move of 5 by the conveyors: " + Text(outs));
            Check(byScript != null && Math.Abs(Convert.ToDouble(byScript["amount"]) - 15) < 0.01, "no move of 15 by the script: " + Text(outs));
            Check(byScript != null && (string)byScript["actor"] == pb.OwnerId.ToString() && ((string)byScript["detail"]).Contains("by script \"Programmable Block\" on " + station.DisplayName),
                "the script's move: actor " + byScript?["actor"] + " (the block's owner " + pb.OwnerId + "), " + byScript?["detail"]);
            Check(((string)outs[0]["detail"]).Contains("from=" + shipBox.EntityId) && ((string)outs[0]["detail"]).Contains("to=" + stationBox.EntityId),
                "the move names the blocks wrong: " + outs[0]["detail"]);
            var hand = Events(shipBox.EntityId).Count(e => (string)e["kind"] == "transfer");
            Check(hand == 1, "the player's own move written " + hand + " times as a transfer");

            var connector = Events(shipConnector.EntityId).Where(e => ((string)e["kind"]).EndsWith("connect")).ToList();
            var other = Events(stationConnector.EntityId).Where(e => ((string)e["kind"]).EndsWith("connect")).ToList();
            Note("ship's connector: " + Text(connector));
            Check(connector.Select(e => (string)e["kind"]).SequenceEqual(new[] { "connect", "disconnect" }), "the ship's connector: " + Text(connector));
            Check(other.Select(e => (string)e["kind"]).SequenceEqual(new[] { "connect", "disconnect" }), "the station's connector: " + Text(other));
            Check(connector.All(e => (string)e["actor"] == me.ToString() && ((string)e["detail"]).Contains("by player")),
                "the connector's events do not name the player " + me + ": " + string.Join("; ", connector.Select(e => e["actor"] + " " + e["detail"])));
            Result("docked by the player, 5 plates by the conveyors and 15 by the station's script written as conveyor moves; the hand's move not; connect and disconnect named the player");
        }
    }
}
