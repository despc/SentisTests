using System;
using System.Collections;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SpaceEngineers.Game.EntityComponents.Blocks;
using SpaceEngineers.Game.SessionComponents;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The grid storage of a Services Terminal (<c>MyGridsStorageEntityComponent</c>): a ship of the player's with 10
    /// plates in its container is stored (<c>StoreGridTrusted</c>, what the terminal's "Store" sends) - it leaves the
    /// world and a record of the player's has it; fetched back at once (<c>RetrieveGridTrusted</c>, not delayed), the
    /// ship is in the world again by the terminal, the record gone, the plates in it (the world lets grids keep their
    /// inventory).
    /// </summary>
    public sealed class GridStorageCheck : RegressionCheck
    {
        public override string Name => Prefix + "grid_storage";
        public override int TimeoutSeconds => 240;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.GetComponent<MyGridsStorageSessionComponent>() != null, "no grid storage in this world");
            yield return Arrive(Site.At(0, 6, -8));
            var station = Build("storage-terminal", Site.Origin, true,
                new Part("FunctionalBlock/ServicesTerminal", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 4, 0, 0).With(b => Motion.Charged(b)));
            var terminal = station.GetFatBlocks().FirstOrDefault(b => b.BlockDefinition.Id.SubtypeName == "ServicesTerminal");
            Check(terminal != null, "no Services Terminal built");
            var storage = terminal.Components.Get<MyGridsStorageEntityComponent>();
            Check(storage != null, "the Services Terminal has no grid storage");
            yield return Until(() => terminal.IsWorking ? 1 : 0, v => v > 0, "the Services Terminal working", 10, () => State(terminal));

            var ship = Build("storage-ship", Site.At(0, 0, 30), false,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 1, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 2, 0, 0));
            var shipId = ship.EntityId;
            var shipName = ship.DisplayName;
            var blocks = ship.BlocksCount;
            Put(Inventory(Block<MyCubeBlock>(ship, b => b.HasInventory)), "Component/SteelPlate", 10);
            if (ship.Physics != null) ship.Physics.LinearVelocity = Vector3.Zero;
            yield return Seconds(1);

            var session = MySession.Static.GetComponent<MyGridsStorageSessionComponent>();
            var before = session.GetStoredGridsData().Keys.ToList();
            Request(storage, "StoreGridTrusted", shipId);
            yield return Until(() => MyEntities.EntityExists(shipId) ? 0 : 1, v => v > 0, "the ship stored (gone from the world)", 60);
            Guid id = Guid.Empty;
            yield return Until(() =>
            {
                var record = session.GetStoredGridsData().FirstOrDefault(p => !before.Contains(p.Key) && p.Value.OwnerId == Identity);
                id = record.Key;
                return id == Guid.Empty ? 0 : 1;
            }, v => v > 0, "a record of the player's in the storage", 30);
            var stored = session.GetStoredGridsData()[id];
            Note("stored as " + id + " ('" + stored.SavedGridDetails?.DisplayName + "')");

            var grids = new System.Collections.Generic.HashSet<long>(MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.EntityId));
            Request(storage, "RetrieveGridTrusted", id, false);
            MyCubeGrid back = null;
            yield return Until(() =>
            {
                back = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !grids.Contains(g.EntityId) && g.DisplayName == shipName);
                return back == null ? 0 : 1;
            }, v => v > 0, "the ship back in the world", 60);
            Track(back);
            // the record goes once the spawn is done, a few frames after the grid is in the world
            yield return Until(() => session.GetStoredGridsData().ContainsKey(id) ? 0 : 1, v => v > 0, "the record gone from the storage", 30);
            Check(back.BlocksCount == blocks, "the ship came back with " + back.BlocksCount + " blocks, not " + blocks);
            Check(back.BigOwners.Contains(Identity), "the ship came back not the player's");
            var plates = Total(back, "Component/SteelPlate");
            Check(Math.Abs(plates - 10) < 0.01, "the ship came back with " + F(plates) + " plates, not 10");
            var away = Vector3D.Distance(back.PositionComp.GetPosition(), terminal.PositionComp.GetPosition());
            Result("stored and fetched back: " + blocks + " blocks, the player's, 10 plates, " + F(away) + " m from the terminal; the record gone");
        }

        private static T Block<T>(MyCubeGrid grid, Func<T, bool> which) where T : class =>
            grid.GetFatBlocks().OfType<T>().FirstOrDefault(which);
    }

    /// <summary>
    /// SentisOptimisations' save window of the grid storage (<c>GridStorageSaveWindow</c>): while the world is being
    /// saved a grid is not stored (a grid stored then lost its files after a restart, 15-16.10.2026). The world saved
    /// (Torch's save, as an autosave), a store asked for while the save runs leaves the ship in the world and makes no
    /// record; once the save is done the same ship is stored. Not in the suite: a save of the whole world in the middle
    /// of it slows the checks beside it.
    /// </summary>
    public sealed class GridStorageSaveCheck : RegressionCheck
    {
        public const string ScenarioName = Prefix + "grid_storage_save";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        protected override IEnumerator Check()
        {
            var window = SentisTests.Core.Integrations.TypeOf("SentisOptimisations", "SentisOptimisationsPlugin.GridStorageSaveWindow");
            SkipUnless(window != null, "no SentisOptimisations GridStorageSaveWindow here");
            var saving = window.GetProperty("Saving");
            Func<bool> Saving = () => (bool)saving.GetValue(null);
            SkipUnless(MySession.Static.GetComponent<MyGridsStorageSessionComponent>() != null, "no grid storage in this world");
            yield return Arrive(Site.At(0, 6, -8));
            var station = Build("storage-terminal", Site.Origin, true,
                new Part("FunctionalBlock/ServicesTerminal", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 4, 0, 0).With(b => Motion.Charged(b)));
            var terminal = station.GetFatBlocks().First(b => b.BlockDefinition.Id.SubtypeName == "ServicesTerminal");
            var storage = terminal.Components.Get<MyGridsStorageEntityComponent>();
            yield return Until(() => terminal.IsWorking ? 1 : 0, v => v > 0, "the Services Terminal working", 10, () => State(terminal));
            var ship = Build("storage-ship", Site.At(0, 0, 30), false,
                // a block with an owner: armor alone has none, and a grid of nobody's is not the player's to store
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CargoContainer/LargeBlockSmallContainer", 1, 0, 0));
            var shipId = ship.EntityId;
            yield return Seconds(1);
            var session = MySession.Static.GetComponent<MyGridsStorageSessionComponent>();
            Func<int> Records = () => session.GetStoredGridsData().Values.Count(r => r.OwnerId == Identity);
            var records = Records();

            var save = SentisTestsPlugin.TorchInstance.Save();
            yield return Until(() => Saving() ? 1 : 0, v => v > 0, "the world being saved (the save window open)", 60);
            Request(storage, "StoreGridTrusted", shipId);
            var stillSaving = Saving();
            yield return Seconds(1);
            Check(MyEntities.EntityExists(shipId), "the ship was stored while the world was being saved");
            Check(Records() == records, "a record was made while the world was being saved");
            yield return Until(() => save.IsCompleted && !Saving() ? 1 : 0, v => v > 0, "the save done", 180);
            Note("the save " + (save.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? save.Result.ToString() : save.Status.ToString()));

            Request(storage, "StoreGridTrusted", shipId);
            yield return Until(() => MyEntities.EntityExists(shipId) ? 0 : 1, v => v > 0, "the ship stored after the save", 60);
            yield return Until(() => Records() > records ? 1 : 0, v => v > 0, "its record after the save", 30);
            // the record taken out again: the stand's storage is not left with it
            var id = session.GetStoredGridsData().First(p => p.Value.OwnerId == Identity).Key;
            Request(storage, "DeleteGridEndpoint", id);
            Result("during the save the ship stayed and no record was made" + (stillSaving ? "" : " (the save ended in the same frame)") +
                   "; after it the ship was stored");
        }
    }
}
