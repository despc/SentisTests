using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
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
    /// A ship through the VirtualGarage plugin and back: put into the garage and taken out where it
    /// was (!g loadbase), it must come back whole - the same grids and blocks, the rotor head on its
    /// rotor, the program in its programmable block, the ore in its container, the owner - and
    /// every fake client must be sent every grid of it. Six seconds later it must still be the same
    /// grids (the plugin used to close and re-create them after a load).
    /// </summary>
    internal sealed class GarageRoundtripScenario : TestScenario
    {
        public const string ScenarioName = "garage_roundtrip";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        private const string Prefix = "garage-";
        private const string Program = "// VirtualGarage roundtrip\npublic void Main(string argument) { Echo(argument); }";
        private const int Ore = 1234;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private Vector3D _up, _side;
        private string _file;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var save = PluginType("VirtualGarage.VirtualGarageSave");
            var load = PluginType("VirtualGarage.VirtualGarageLoad");
            Check(save != null && load != null, "the VirtualGarage plugin is not loaded");

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            _up = Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            _side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(_up));
            Vector3D centre = default;
            for (var d = 100000.0; d <= 1000000; d += 50000)
            {
                centre = anchor + _up * d + _side * 3000;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(centre).Length() < 0.001f) break;
            }

            FakeClients.RemoveAll();
            FakeClients.Add(2, Network, p => (centre + _up * 60 + _side * (20 * p), 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;
            var owner = FakeClients.Character(0)?.GetPlayerIdentityId() ?? 0;
            Check(owner != 0, "the fake player has no identity");

            // ------------------------------------------------------------- the ship
            var ship = Spawn(centre, owner, "ship");
            var settle = WaitForSeconds(2, "the ship settles");
            while (settle.MoveNext()) yield return settle.Current;
            var stator = WorldApi.FindFunctional<MyMotorStator>(ship);
            var recreate = typeof(MyMechanicalConnectionBlockBase).GetMethod("DoRecreateTop", BindingFlags.Instance | BindingFlags.NonPublic);
            recreate.Invoke(stator, new object[] { owner, Enum.Parse(recreate.GetParameters()[1].ParameterType, "Normal"), true });
            var topped = Wait(() => stator.TopGrid != null, "the rotor head is built", 10);
            while (topped.MoveNext()) yield return topped.Current;
            ((Sandbox.ModAPI.Ingame.IMyMotorStator)stator).RotorLock = true;
            var head = stator.TopGrid;
            foreach (var y in new[] { 1, 2 })
            {
                var armor = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                armor.Min = new SerializableVector3I(0, y, 0);
                armor.Owner = owner;
                ((IMyCubeGrid)head).AddBlock(armor, false);
            }
            var cargo = WorldApi.FindFunctional<MyCargoContainer>(ship);
            ((VRage.Game.ModAPI.IMyInventory)cargo.GetInventory()).AddItems((MyFixedPoint)Ore, new MyObjectBuilder_Ore { SubtypeName = "Iron" });
            var built = WaitForSeconds(2, "the ship is finished");
            while (built.MoveNext()) yield return built.Current;

            var group = (List<MyCubeGrid>)save.GetMethod("Group").Invoke(null, new object[] { ship });
            var before = Describe(group);
            var position = ship.PositionComp.GetPosition();
            Note("before: " + before);
            Check(group.Count == 2, "the ship is " + group.Count + " grids, not a ship and its rotor head");

            // ------------------------------------------------------------- into the garage
            _file = (string)save.GetMethod("Store").Invoke(null, new object[] { owner, group });
            Check(_file != null, "the garage refused the ship (owner without a Steam id?)");
            var gone = Wait(() => group.All(g => g.MarkedForClose || g.Closed), "the ship leaves the world", 10);
            while (gone.MoveNext()) yield return gone.Current;
            var written = Wait(() => File.Exists(_file), "the garage file is written", 20);
            while (written.MoveNext()) yield return written.Current;
            Note("in the garage: " + Path.GetFileName(_file));

            // ------------------------------------------------------------- and out again, where it was
            List<MyCubeGrid> back = null;
            string failure = null;
            var placementType = load.GetNestedType("Placement");
            var placement = Activator.CreateInstance(placementType);
            placementType.GetField("Original").SetValue(placement, true);
            load.GetMethod("Load").Invoke(null, new object[]
            {
                _file, owner, placement,
                new Action<List<MyCubeGrid>>(grids => back = grids),
                new Action<string>(why => failure = why),
            });
            var loaded = Wait(() => back != null || failure != null, "the ship comes out of the garage", 30);
            while (loaded.MoveNext()) yield return loaded.Current;
            Check(failure == null, "the garage did not give the ship back: " + failure);
            foreach (var grid in back) Track(grid);
            var outAt = DateTime.UtcNow;

            // every fake player gets every grid
            var sent = Wait(() => FakeClients.ArrivedCount(back) == 0, "the players get the ship", 30);
            while (sent.MoveNext()) yield return sent.Current;
            var sentSeconds = (DateTime.UtcNow - outAt).TotalSeconds;
            Note("sent to the players in " + sentSeconds.ToString("F1") + " s: " + FakeClients.Arrivals(back));

            var after = Describe(back);
            Note("after: " + after);
            var main = back.OrderByDescending(g => g.BlocksCount).First();
            var moved = Vector3D.Distance(main.PositionComp.GetPosition(), position);
            Check(after == before, "the ship came back different:\n  before " + before + "\n  after  " + after);
            Check(moved < 0.5, "the ship came back " + moved.ToString("F1") + " m from where it was");

            // nothing re-creates it afterwards
            var ids = back.Select(g => g.EntityId).ToList();
            var later = WaitForSeconds(6, "the ship stays");
            while (later.MoveNext()) yield return later.Current;
            Check(back.All(g => !g.MarkedForClose && !g.Closed), "the ship was closed after it came out (a fix-ship?)");
            Check(back.Select(g => g.EntityId).SequenceEqual(ids), "the ship's grids changed after it came out");

            // ------------------------------------------------------------- loadbase onto a taken place
            var file2 = (string)save.GetMethod("Store").Invoke(null, new object[] { owner, back });
            var written2 = Wait(() => File.Exists(file2), "the ship is in the garage again", 20);
            while (written2.MoveNext()) yield return written2.Current;
            var blocker = Spawn(centre, owner, "blocker");
            var blockerIn = WaitForSeconds(1, "somebody else's ship takes the place");
            while (blockerIn.MoveNext()) yield return blockerIn.Current;
            back = null;
            failure = null;
            load.GetMethod("Load").Invoke(null, new object[]
            {
                file2, owner, placement,
                new Action<List<MyCubeGrid>>(grids => back = grids),
                new Action<string>(why => failure = why),
            });
            var refused = Wait(() => back != null || failure != null, "loadbase onto the taken place answers", 30);
            while (refused.MoveNext()) yield return refused.Current;
            if (back != null) foreach (var grid in back) Track(grid);
            Check(back == null && failure != null, "loadbase put the ship into another ship");
            var stillThere = WaitForSeconds(1, "the file stays");
            while (stillThere.MoveNext()) yield return stillThere.Current;
            Check(File.Exists(file2), "the garage lost the ship it refused to put down");
            Note("loadbase onto a taken place: refused (" + failure + "), the ship stays in the garage");
            blocker.Close();

            // ------------------------------------------------------------- !g load: a free spot by the player
            var player = FakeClients.Character(1).PositionComp.GetPosition();
            var near = Activator.CreateInstance(placementType);
            placementType.GetField("Around").SetValue(near, player);
            back = null;
            failure = null;
            load.GetMethod("Load").Invoke(null, new object[]
            {
                file2, owner, near,
                new Action<List<MyCubeGrid>>(grids => back = grids),
                new Action<string>(why => failure = why),
            });
            var nearLoaded = Wait(() => back != null || failure != null, "the ship comes out by the player", 30);
            while (nearLoaded.MoveNext()) yield return nearLoaded.Current;
            Check(failure == null, "!g load found no place by the player: " + failure);
            foreach (var grid in back) Track(grid);
            var nearSent = Wait(() => FakeClients.ArrivedCount(back) == 0, "the players get the ship", 30);
            while (nearSent.MoveNext()) yield return nearSent.Current;
            var afterNear = Describe(back);
            var distance = Vector3D.Distance(back.OrderByDescending(g => g.BlocksCount).First().PositionComp.GetPosition(), player);
            Check(afterNear == before, "the ship came out by the player different:\n  before " + before + "\n  after  " + afterNear);
            Check(distance < MaxSpawnRadiusM + 50, "the ship came out " + distance.ToString("F0") + " m from the player");
            Note("!g load: " + distance.ToString("F0") + " m from the player, " + afterNear);

            // ------------------------------------------------------------- loadbase of a station over another station
            // A station comes back whatever static grids (or rock) stand in its place now.
            var stationAt = centre + _side * 200;
            var station = Spawn(stationAt, owner, "station", true);
            var stationSettle = WaitForSeconds(1, "the station stands");
            while (stationSettle.MoveNext()) yield return stationSettle.Current;
            var stationGroup = (List<MyCubeGrid>)save.GetMethod("Group").Invoke(null, new object[] { station });
            var stationBefore = Describe(stationGroup);
            var file3 = (string)save.GetMethod("Store").Invoke(null, new object[] { owner, stationGroup });
            var written3 = Wait(() => File.Exists(file3), "the station is in the garage", 20);
            while (written3.MoveNext()) yield return written3.Current;
            var otherStation = Spawn(stationAt, owner, "other-station", true);
            var otherIn = WaitForSeconds(1, "another station takes the place");
            while (otherIn.MoveNext()) yield return otherIn.Current;
            back = null;
            failure = null;
            load.GetMethod("Load").Invoke(null, new object[]
            {
                file3, owner, placement,
                new Action<List<MyCubeGrid>>(grids => back = grids),
                new Action<string>(why => failure = why),
            });
            var stationLoaded = Wait(() => back != null || failure != null, "loadbase of the station answers", 30);
            while (stationLoaded.MoveNext()) yield return stationLoaded.Current;
            Check(failure == null, "loadbase refused a station because of another station: " + failure);
            foreach (var grid in back) Track(grid);
            var stationAfter = Describe(back);
            Check(stationAfter == stationBefore, "the station came back different:\n  before " + stationBefore + "\n  after  " + stationAfter);
            Check(back.All(g => g.IsStatic), "the station came back dynamic");
            Check(!otherStation.MarkedForClose && !otherStation.Closed, "the other station was removed");
            Note("loadbase of a station over another station: " + stationAfter);

            // ------------------------------------------------------------- the files follow the world save
            // Saved only once a world save that saw the operation has been written.
            var savedShip = Spawn(centre + _side * 400, owner, "saved");
            var savedSettle = WaitForSeconds(1, "another ship stands");
            while (savedSettle.MoveNext()) yield return savedSettle.Current;
            var file4 = (string)save.GetMethod("Store").Invoke(null, new object[] { owner, (List<MyCubeGrid>)save.GetMethod("Group").Invoke(null, new object[] { savedShip }) });
            var written4 = Wait(() => File.Exists(file4), "it is in the garage", 20);
            while (written4.MoveNext()) yield return written4.Current;
            Check(file4.EndsWith("_unsaved.sbc"), "a grid put into the garage counts before the world is saved: " + Path.GetFileName(file4));
            var saved = file4.Substring(0, file4.Length - "_unsaved.sbc".Length) + ".sbc";
            var worldSaved = SaveWorld();
            while (worldSaved.MoveNext()) yield return worldSaved.Current;
            Check(File.Exists(saved) && !File.Exists(file4), "the world was saved but the garage file is still unsaved: " + Path.GetFileName(file4));
            back = null;
            failure = null;
            load.GetMethod("Load").Invoke(null, new object[]
            {
                saved, owner, placement,
                new Action<List<MyCubeGrid>>(grids => back = grids),
                new Action<string>(why => failure = why),
            });
            var savedOut = Wait(() => back != null || failure != null, "it comes out again", 30);
            while (savedOut.MoveNext()) yield return savedOut.Current;
            Check(failure == null, "the saved ship did not come out: " + failure);
            foreach (var grid in back) Track(grid);
            var takenOutUnsaved = saved + "_spawned_unsaved";
            var marked = Wait(() => File.Exists(takenOutUnsaved), "it is marked taken out", 10);
            while (marked.MoveNext()) yield return marked.Current;
            var worldSaved2 = SaveWorld();
            while (worldSaved2.MoveNext()) yield return worldSaved2.Current;
            Check(File.Exists(saved + "_spawned") && !File.Exists(takenOutUnsaved), "the world was saved but the taken-out file is still unsaved");
            Note("the world save: put in " + Path.GetFileName(file4) + " -> .sbc, taken out -> .sbc_spawned, each only after the world was written");

            // ------------------------------------------------------------- the admin's "everything into the garages"
            var oldType = PluginType("VirtualGarage.VirtualGarageOldGridProcessor");
            var allOwned = Spawn(centre + _side * 500, owner, "all-owned");
            var allBare = SpawnBare(centre + _side * 550, owner, "all-bare");
            var allSettle = WaitForSeconds(1, "an owned ship and an ownerless plate stand");
            while (allSettle.MoveNext()) yield return allSettle.Current;
            var ownedAt = allOwned.PositionComp.GetPosition();
            var bareAt = allBare.PositionComp.GetPosition();
            Check(allBare.BigOwners.Count == 0, "the plate has an owner");
            var mine = new Func<List<MyCubeGrid>, bool>(g => g.Any(x => x.DisplayName.StartsWith(WorldApi.EntityPrefix + Prefix + "all-")));
            var groups = (List<List<MyCubeGrid>>)oldType.GetMethod("PlayerGroups").Invoke(null, new object[] { mine });
            Check(groups.Count == 2, "\"everything\" found " + groups.Count + " of the two test grids (the ownerless one goes to its builder)");
            var processor = oldType.GetField("OldGridProcessor").GetValue(null);
            oldType.GetMethod("Enqueue").Invoke(processor, new object[] { groups });
            var allGone = Wait(() => (allOwned.MarkedForClose || allOwned.Closed) && (allBare.MarkedForClose || allBare.Closed), "both go into the garage", 20);
            while (allGone.MoveNext()) yield return allGone.Current;
            var folder = Path.GetDirectoryName(_file);
            var allFiles = Wait(() => Directory.GetFiles(folder, WorldApi.EntityPrefix + Prefix + "all-*.sbc").Length == 2, "both files are written, in the builder's garage too", 20);
            while (allFiles.MoveNext()) yield return allFiles.Current;
            Note("everything into the garages: the owned ship and the ownerless plate, both in " + Path.GetFileName(folder));

            // ------------------------------------------------------------- the admin's "the last minutes back"
            string report = null;
            load.GetMethod("LoadRecent").Invoke(null, new object[]
            {
                10, new Func<string, bool>(f => Path.GetFileName(f).StartsWith(WorldApi.EntityPrefix + Prefix + "all-")),
                new Action<string>(text => report = text),
            });
            var recent = Wait(() => report != null && report.StartsWith("Taken out"), "the last minutes come back", 30);
            while (recent.MoveNext()) yield return recent.Current;
            var returned = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && g.DisplayName.StartsWith(WorldApi.EntityPrefix + Prefix + "all-")).ToList();
            foreach (var grid in returned) Track(grid);
            var ownedBack = returned.FirstOrDefault(g => g.DisplayName.EndsWith("all-owned"));
            var bareBack = returned.FirstOrDefault(g => g.DisplayName.EndsWith("all-bare"));
            Check(report == "Taken out 2 of 2", "the last minutes did not all come back: " + report);
            Check(ownedBack != null && Vector3D.Distance(ownedBack.PositionComp.GetPosition(), ownedAt) < 0.5 && ownedBack.BigOwners.Contains(owner),
                "the owned ship did not come back where it was, to its owner");
            Check(bareBack != null && Vector3D.Distance(bareBack.PositionComp.GetPosition(), bareAt) < 0.5 && bareBack.BigOwners.Count == 0,
                "the ownerless plate did not come back where it was, ownerless");
            Note("the last minutes back: " + report);

            Note("GARAGE ROUNDTRIP RESULT: " + after + "; sent to " + FakeClients.Count + " players in " + sentSeconds.ToString("F1") +
                 " s; loadbase onto a ship refused; !g load " + distance.ToString("F0") + " m from the player; a station over another station put back; " +
                 "files counted only after the world was written; everything in and the last minutes out");
        }

        /// <summary>A world save by the admin's !save: the snapshot, then written in the background.</summary>
        private IEnumerator SaveWorld()
        {
            var answers = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            Check(commands != null && commands.HandleCommandFromServer("!save", message => answers.Enqueue(message.Message)), "!save did not run");
            var wait = Wait(() => answers.Any(a => a.StartsWith("Saved game") || a.StartsWith("Save failed")), "!save answers", 300);
            while (wait.MoveNext()) yield return wait.Current;
            Check(answers.Any(a => a.StartsWith("Saved game")), "!save failed: " + string.Join(" / ", answers));
            // the rename after the save is written happens on the save's own thread: give it a moment
            var settle = WaitForSeconds(1, "the garage catches up with the save");
            while (settle.MoveNext()) yield return settle.Current;
        }

        /// <summary>A plate of armor nobody owns, built by <paramref name="builder"/>.</summary>
        private MyCubeGrid SpawnBare(Vector3D at, long builder, string name)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 3; x++)
            {
                var block = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                block.Min = new SerializableVector3I(x, 0, 0);
                block.Owner = 0;
                block.BuiltBy = builder;
                blocks.Add(block);
            }
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = false,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, _side, _up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            return grid;
        }

        private const double MaxSpawnRadiusM = 500;   // the plugin's default MaxSpawnRadius

        /// <summary>What must survive the garage: grids and blocks, the rotor, the program, the ore, the owner.</summary>
        private static string Describe(List<MyCubeGrid> grids)
        {
            var main = grids.OrderByDescending(g => g.BlocksCount).First();
            var stator = main.GetFatBlocks().OfType<MyMotorStator>().FirstOrDefault();
            var program = main.GetFatBlocks().OfType<MyProgrammableBlock>().FirstOrDefault();
            var cargo = main.GetFatBlocks().OfType<MyCargoContainer>().FirstOrDefault();
            var headIn = stator?.TopGrid != null && grids.Contains(stator.TopGrid);
            var grouped = grids.Select(g => MyCubeGridGroups.Static.Mechanical.GetGroup(g)).Distinct().Count() == 1;
            var programText = program == null ? "none" : ((Sandbox.ModAPI.IMyProgrammableBlock)program).ProgramData == Program ? "kept" : "LOST";
            var ore = cargo == null ? 0 : (double)((VRage.Game.ModAPI.IMyInventory)cargo.GetInventory()).GetItemAmount(new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Iron"));
            var owners = string.Join(",", grids.SelectMany(g => g.BigOwners).Distinct());
            return grids.Count + " grids (" + string.Join("+", grids.Select(g => g.BlocksCount).OrderByDescending(n => n)) + " blocks), rotor head " +
                   (headIn ? "on" : "OFF") + ", one group " + grouped + ", program " + programText + ", ore " + ore + ", owners " + owners + ", " +
                   (main.IsStatic ? "static" : "dynamic");
        }

        private MyCubeGrid Spawn(Vector3D at, long owner, string name, bool isStatic = false)
        {
            MyObjectBuilder_CubeBlock Block(string subtype, int x, int y, int z)
            {
                var block = WorldApi.MakeBlockOb(subtype);
                block.Min = new SerializableVector3I(x, y, z);
                block.Owner = owner;
                block.BuiltBy = owner;
                return block;
            }

            var battery = (MyObjectBuilder_BatteryBlock)Block("LargeBlockBatteryBlock", -1, 1, 0);
            battery.CurrentStoredPower = 3f;
            var programmable = (MyObjectBuilder_MyProgrammableBlock)Block("LargeProgrammableBlock", 0, 1, 1);
            programmable.Program = Program;
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = isStatic,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, _side, _up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>
                {
                    Block("LargeBlockArmorBlock", -1, 0, 0), Block("LargeBlockArmorBlock", 0, 0, 0), Block("LargeBlockArmorBlock", 1, 0, 0),
                    Block("LargeBlockArmorBlock", -1, 0, 1), Block("LargeBlockArmorBlock", 0, 0, 1), Block("LargeBlockArmorBlock", 1, 0, 1),
                    battery, programmable, Block("LargeBlockSmallContainer", -1, 1, 1), Block("LargeStator", 1, 1, 0),
                },
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        private static Type PluginType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                // the test's garage files, whatever they became
                // every file the test's ship left in the garage, whatever it became (the game also
                // leaves a .sbcB5 cache next to a blueprint it reads)
                if (_file != null)
                    foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_file), WorldApi.EntityPrefix + Prefix + "*"))
                        File.Delete(file);
            }
            catch (Exception e)
            {
                Log.Warn("cleaning the garage after the test failed: " + e.Message);
            }
            finally { base.Cleanup(); }
        }
    }
}
