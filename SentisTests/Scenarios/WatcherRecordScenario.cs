using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisWatcher records what happens, and its queries read it back: a player stands by a grid with two
    /// containers, moves ore from one to the other (the game's own transfer request), hits a block; then
    ///
    ///  - the player's timeline has their position, the transfer and the damage;
    ///  - the grid's history has its position and the damage by that player;
    ///  - the second container's inventory is recorded with the ore moved into it;
    ///  - "who was near" finds the player and the grid;
    ///  - the transfer raised no dupe alert.
    /// </summary>
    public sealed class WatcherRecordScenario : TestScenario
    {
        public const string ScenarioName = "watcher_record";
        private const string Prefix = "watcher-";
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisWatcher");
            Check(assembly != null, "SentisWatcher is not loaded");
            var pluginType = assembly.GetType("SentisWatcher.SentisWatcherPlugin");
            var plugin = pluginType.GetProperty("Instance").GetValue(null);
            var store = pluginType.GetProperty("Store").GetValue(plugin);
            var sweep = pluginType.GetProperty("Sweep").GetValue(plugin);
            Check(store != null, "SentisWatcher records nothing (disabled?)");
            var query = Activator.CreateInstance(assembly.GetType("SentisWatcher.Storage.WatcherQuery"), store);
            var clock = assembly.GetType("SentisWatcher.Storage.Clock");
            long Now() => (long)clock.GetProperty("Now").GetValue(null);
            var from = Now() - 60_000;

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var centre = anchorM.Translation + up * 1000 + side * 3500;

            FakeClients.RemoveAll();
            FakeClients.Add(1, Network, p => (centre + up * 10 + side * 10, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            var character = FakeClients.Character(0);
            Check(character != null, "the player has no character");
            var identity = character.GetPlayerIdentityId();
            var playerName = MySession.Static.Players.TryGetIdentity(identity)?.DisplayName ?? identity.ToString();

            // ------------------------------------------------------------- a grid with two containers
            var grid = SpawnGrid(centre, up, side, identity);
            var containers = grid.GetFatBlocks().Where(b => b.BlockDefinition.Id.SubtypeName == "LargeBlockSmallContainer").OrderBy(b => b.Position.X).ToList();
            Check(containers.Count == 2, "the grid has " + containers.Count + " containers, not 2");
            var source = (MyInventory)containers[0].GetInventory(0);
            var target = (MyInventory)containers[1].GetInventory(0);
            source.AddItems((MyFixedPoint)100, new MyObjectBuilder_Ore { SubtypeName = "Iron" });
            var settle = WaitForSeconds(2, "the grid settles");
            while (settle.MoveNext()) yield return settle.Current;

            // ------------------------------------------------------------- the player moves ore
            var item = source.GetItems()[0];
            var transfer = typeof(MyInventory).GetMethod("InventoryTransferItem_Implementation", BindingFlags.Instance | BindingFlags.NonPublic);
            // as the request of the player's client: the game checks the sender's rights, the recorder names them
            var steam = MySession.Static.Players.TryGetSteamId(identity);
            var setContext = typeof(VRage.Network.MyEventContext).GetMethod("Set", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var context = setContext.Invoke(null, new object[] { new VRage.Network.EndpointId(steam), FakeClients.StateOf(0), false });
            try
            {
                transfer.Invoke(source, new object[] { (MyFixedPoint)40, item.ItemId, containers[1].EntityId, (byte)0, -1 });
            }
            finally
            {
                (context as IDisposable)?.Dispose();
            }
            var ore = new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Iron");
            Note("transfer: source " + source.GetItemAmount(ore) + ", target " + target.GetItemAmount(ore));
            Check(target.GetItemAmount(ore) == 40, "the transfer did not move 40 ore");

            // ------------------------------------------------------------- the player hits a block
            var armor = grid.CubeBlocks.First(b => b.FatBlock == null);
            ((IMySlimBlock)armor).DoDamage(50, MyDamageType.Bullet, true, null, character.EntityId);

            // ------------------------------------------------------------- the inventories are looked at
            sweep.GetType().GetMethod("StartNow").Invoke(sweep, null);
            yield return null;
            yield return null;
            var busy = sweep.GetType().GetProperty("Busy");
            var swept = Wait(() => !(bool)busy.GetValue(sweep), "the inventory pass ends", 30);
            while (swept.MoveNext()) yield return swept.Current;
            var flush = WaitForSeconds(12, "the grids are sampled and the records written");
            while (flush.MoveNext()) yield return flush.Current;

            // ------------------------------------------------------------- read back
            var to = Now() + 60_000;
            string Ask(string method, params object[] args) =>
                (string)query.GetType().GetMethods().First(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(query, args);

            var player = Ask("Player", identity, playerName, from, to, 40);
            Note("player:\n" + player);
            Check(!player.Contains("positions: 0"), "no position of the player was recorded");
            Check(player.Contains(" transfer "), "the transfer is not in the player's timeline");
            Check(player.Contains(" damage "), "the damage is not in the player's timeline");

            var history = Ask("Grid", grid.EntityId, grid.DisplayName, from, to, 40);
            Note("grid:\n" + history);
            Check(!history.Contains("positions: 0"), "no position of the grid was recorded");
            Check(history.Contains("damage " + identity), "the damage by the player is not in the grid's history");

            var inventory = Ask("Inventory", containers[1].EntityId, from, to, 10);
            Note("inventory:\n" + inventory);
            Check(inventory.Contains("Ore/Iron:40"), "the second container's inventory was not recorded with the ore");

            var near = Ask("Near", centre.X, centre.Y, centre.Z, 50.0, from, to);
            Note("near:\n" + near);
            Check(near.Contains(playerName), "the player is not found near the grid");
            Check(near.Contains(grid.DisplayName), "the grid is not found near its own place");

            var alerts = Ask("Alerts", from, to, 20);
            Check(!alerts.Contains("dupe_transfer"), "an honest transfer raised a dupe alert:\n" + alerts);
        }

        private MyCubeGrid SpawnGrid(Vector3D at, Vector3D up, Vector3D side, long owner)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 3; x++)
            {
                var block = WorldApi.MakeBlockOb(x == 1 ? "LargeBlockArmorBlock" : "LargeBlockSmallContainer");
                block.Min = new SerializableVector3I(x, 0, 0);
                block.Owner = owner;
                block.BuiltBy = owner;
                blocks.Add(block);
            }
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "grid",
                DisplayName = WorldApi.EntityPrefix + Prefix + "grid",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, side, up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
