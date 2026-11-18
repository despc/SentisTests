using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Network;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The modded V5's special admin blocks, what reg_scriptpack_blocks and reg_scriptpack_fix do not check, on the server:
    /// - the border beacon (ContrabandBeacon, the SentisAdventures plugin): an empty CustomData gets the example config, a
    ///   CustomData that is no config is refused; a beacon a player builds is blown up; a ship in its range with a player
    ///   near is scanned after MinutesBeforeScan (the beacon's pass run with the clock moved on): one with the contraband is
    ///   slowed and its power cut, one without it, or with it in a container of the blacklist, is not punished;
    /// - the cargo quest: the valuable cargo makes its station a ship, holds its range at 15 km and stays on, has power of
    ///   its own and wears away slowly; a start console with a cargo within 5 km, or with nothing projected, spawns nothing;
    ///   the finish console's empty CustomData gets the example, a cargo brought within 30 m is taken (its grid gone), the
    ///   reward (the projected ship) comes with the items of the CustomData and a player within 50 m gets the reputation
    ///   (the finish console's pass run at once; the reputation is put back);
    /// - the admin store: the offer's and the order's price from the stock (the middle of the range is the middle price),
    ///   the production adds to the stock and the negative one takes from it, a store switched off sells nothing, an empty
    ///   CustomData gets the example; /reset_stores in the chat recounts the stores for an admin and not for a player;
    /// - the magic container: fills up to its max and no more, the boost multiplies the speed in its hours, a config with
    ///   Windows line ends is read, a broken line makes nothing (its loop run at once);
    /// - the custom inhibitor: Players:DRAIN_HYDRO takes the hydrogen of a player in the radius; DISABLE works with the
    ///   beacon off and not past its radius; an empty CustomData gets the example and the radius of 1 m.
    /// The admin LCD's HUD is the client's and is not checked. Each part is skipped (noted) when its blocks or its plugin
    /// are not in this game. Skipped where the mod is not loaded.
    /// </summary>
    public sealed class AdminBlocksCheck : FactionCheck
    {
        public override string Name => Prefix + "admin_blocks";
        public override int TimeoutSeconds => 300;
        protected override bool NeedsDamage => true;

        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<string> _failures = new List<string>();
        private readonly List<string> _passed = new List<string>();
        private readonly List<Action> _restore = new List<Action>();
        private Assembly _mod;
        private Assembly _adventures;
        private int _mate = -1;

        protected override IEnumerator Check()
        {
            _mod = MyScriptManager.Static?.Scripts?.Values.FirstOrDefault(a => a.GetType("AdminProtector.AdminProtectorBlock") != null);
            SkipUnless(_mod != null, "no Sentis_v5_ScriptPack_Fix mod in this world");
            _adventures = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => SafeType(a, "SentisAdventures.Adventures.Contraband.ContrabandGridPositionBehavior") != null);

            yield return Arrive(Site.At(0, 0, -30));
            yield return Join(Site.At(6, 0, -30), i => _mate = i);

            try
            {
                foreach (var step in Run("custom inhibitor", Inhibitor())) yield return step;
                foreach (var step in Run("magic container", MagicContainer(Site.At(0, 400, 0)))) yield return step;
                foreach (var step in Run("admin store", AdminStore(Site.At(0, -400, 0)))) yield return step;
                foreach (var step in Run("valuable cargo", ValuableCargo(Site.At(0, 1500, 0)))) yield return step;
                foreach (var step in Run("cargo finish console", FinishConsole())) yield return step;
                foreach (var step in Run("border beacon", BorderBeacon(Site.At(2000, 0, 0)))) yield return step;
            }
            finally
            {
                foreach (var restore in _restore)
                    try { restore(); }
                    catch (Exception e) { Note("restoring failed: " + e.Message); }
            }

            foreach (var passed in _passed) Note("ok: " + passed);
            foreach (var failure in _failures) Note("FAILED: " + failure);
            Check(_failures.Count == 0, string.Join("; ", _failures));
            Result(string.Join("; ", _passed));
        }

        private void Expect(bool ok, string passed, string failed)
        {
            if (ok) _passed.Add(passed);
            else _failures.Add(failed);
        }

        private IEnumerable<object> Run(string what, IEnumerator part)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!part.MoveNext()) yield break;
                    current = part.Current;
                }
                catch (ScenarioSkippedException e)
                {
                    Note("skipped " + what + ": " + e.Message);
                    yield break;
                }
                catch (Exception e)
                {
                    _failures.Add(what + ": " + e.GetType().Name + ": " + e.Message);
                    yield break;
                }
                yield return current;
            }
        }

        // ------------------------------------------------------------------ the custom inhibitor

        private IEnumerator Inhibitor()
        {
            Need("Beacon/Custom_Inhibitor");
            Need("Reactor/LargeBlockSmallGenerator");

            // 1. the players' hydrogen: the player stands 30 m off the site's origin
            var drain = Build("inhib-drain", Site.At(0, 0, 0), true, Beacon("Custom_Inhibitor", 60));
            var drainBeacon = (IMyBeacon)Fat(drain, "Custom_Inhibitor");
            drainBeacon.CustomData = "Players:DRAIN_HYDRO";
            drainBeacon.Radius = 60;
            MyVisualScriptLogicProvider.SetPlayersHydrogenLevel(Identity, 1f);

            // 2. DISABLE with the beacon off: a reactor in the radius and one past it
            var at = Site.At(-400, 0, 0);
            var off = Build("inhib-off", at, true, Beacon("Custom_Inhibitor", 60));
            var offBeacon = (IMyBeacon)Fat(off, "Custom_Inhibitor");
            offBeacon.CustomData = "LargeBlockSmallGenerator:DISABLE";
            offBeacon.Radius = 60;
            offBeacon.Enabled = false;
            var near = (IMyFunctionalBlock)Fat(Build("inhib-near", at + Site.Right * 20, true, new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0).With(Enabled)), "LargeBlockSmallGenerator");
            var far = (IMyFunctionalBlock)Fat(Build("inhib-far", at + Site.Right * 150, true, new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0).With(Enabled)), "LargeBlockSmallGenerator");

            // 3. an empty CustomData
            var empty = Build("inhib-empty", Site.At(-400, 400, 0), true, Beacon("Custom_Inhibitor", 500));
            var emptyBeacon = (IMyBeacon)Fat(empty, "Custom_Inhibitor");
            emptyBeacon.CustomData = "";
            emptyBeacon.Radius = 500;

            yield return Seconds(12); // two passes of the 5 s loop

            var hydrogen = MyVisualScriptLogicProvider.GetPlayersHydrogenLevel(Identity);
            Note("custom inhibitor: the player's hydrogen 1 -> " + hydrogen.ToString("F2") + "; with the beacon off the reactor in the radius " +
                 (near.Enabled ? "on" : "off") + ", past it " + (far.Enabled ? "on" : "off") + "; the empty one's CustomData «" +
                 emptyBeacon.CustomData.Replace("\n", " | ") + "», radius " + emptyBeacon.Radius);
            Expect(hydrogen < 0.95f, "inhibitor DRAIN_HYDRO took the player's hydrogen", "inhibitor DRAIN_HYDRO: the player's hydrogen stayed " + hydrogen.ToString("F2"));
            Expect(!near.Enabled, "inhibitor DISABLE works with the beacon off", "inhibitor DISABLE: with the beacon off the reactor in the radius stayed on");
            Expect(far.Enabled, "inhibitor DISABLE not past its radius", "inhibitor DISABLE: the reactor 150 m off a 60 m radius was switched off");
            Expect(emptyBeacon.CustomData.Contains("LargeJumpDrive:DISABLE") && emptyBeacon.Radius <= 1.01f,
                "inhibitor's empty CustomData got the example and the radius of 1 m",
                "inhibitor's empty CustomData: «" + emptyBeacon.CustomData + "», radius " + emptyBeacon.Radius);
            drain.Close();
            MyVisualScriptLogicProvider.SetPlayersHydrogenLevel(Identity, 1f);
        }

        // ------------------------------------------------------------------ the magic container (admin 2)

        private IEnumerator MagicContainer(Vector3D at)
        {
            Need("CargoContainer/LargeBlockSmallContainer_admin2");
            var type = ModType("MagicContainer");
            var box = Fat(Build("magic-cap", at, true, new Part("CargoContainer/LargeBlockSmallContainer_admin2", 0, 0, 0)), "LargeBlockSmallContainer_admin2");
            var logic = Logic(box, type) ?? throw new ScenarioFailedException("no MagicContainer logic on the container");
            var update = type.GetMethod("UpdateBeforeSimulation100", Any);
            // the container adds on every 10th call (its 1000 frames): the cycles at once, on the game thread
            void Cycles(int n)
            {
                for (var i = 0; i < n * 10; i++) update.Invoke(logic, null);
            }
            var terminal = (IMyTerminalBlock)box;
            var now = DateTime.Now;

            // 1. up to max and no more: speed 10, max 25 -> 30 at most
            terminal.CustomData = "item=Ore_Cobalt;max=25;speed=10;boost=1;startBoost=00:00;endBoost=00:01";
            Cycles(6);
            var cobalt = Amount(Inventory(box), "Ore/Cobalt");

            // 2. the boost in its hours (a window round now), with Windows line ends and a blank line
            var start = now.AddMinutes(-30).ToString("HH:mm");
            var end = now.AddMinutes(30).ToString("HH:mm");
            var windowOk = now.AddMinutes(-30).Date == now.Date && now.AddMinutes(30).Date == now.Date;
            terminal.CustomData = "item=Ore_Nickel;max=1000;speed=10;boost=3;startBoost=" + start + ";endBoost=" + end + "\r\n" +
                                  "item=Ore_Silver;max=1000;speed=4;boost=1;startBoost=00:00;endBoost=00:01\r\n\r\n";
            Cycles(1);
            var nickel = Amount(Inventory(box), "Ore/Nickel");
            var silver = Amount(Inventory(box), "Ore/Silver");

            // 3. a broken line: nothing
            terminal.CustomData = "item=Ore_Gold;max=1000;speed=10\nitem=Ore_Uranium;max=1000;speed=10;boost=1;startBoost=00:00;endBoost=00:01";
            Cycles(1);
            var gold = Amount(Inventory(box), "Ore/Gold");
            var uranium = Amount(Inventory(box), "Ore/Uranium");
            yield return Seconds(0.1);

            Note("magic container: cobalt " + cobalt + " (max 25, speed 10); boosted nickel " + nickel + " (speed 10 x3" + (windowOk ? "" : ", the window crosses midnight: not checked") +
                 "), silver " + silver + " (speed 4, the lines end in \\r\\n); with a broken line gold " + gold + ", uranium " + uranium);
            Expect(cobalt >= 25 && cobalt <= 30, "magic container filled up to its max", "magic container: " + cobalt + " cobalt for max 25 at speed 10");
            if (windowOk) Expect(nickel == 30, "magic container's boost x3 in its hours", "magic container's boost: " + nickel + " nickel in a cycle, not 30");
            Expect(silver == 4, "magic container read the config with Windows line ends", "magic container: the config with \\r\\n made " + silver + " silver, not 4");
            Expect(gold == 0 && uranium == 0, "magic container: a broken line makes nothing", "magic container: with a broken line it made gold " + gold + ", uranium " + uranium);
        }

        // ------------------------------------------------------------------ the admin store

        private IEnumerator AdminStore(Vector3D at)
        {
            Need("StoreBlock/Admin_Store");
            var storeType = ModType("AdminStore");
            var size = Definition("StoreBlock/Admin_Store").Size;
            MyCubeGrid StoreGrid(string name, Vector3D where, bool on) =>
                Build(name, where, true, new Part("StoreBlock/Admin_Store", 0, 0, 0).With(b => ((MyObjectBuilder_FunctionalBlock)b).Enabled = on),
                    new Part("CargoContainer/LargeBlockSmallContainer", size.X, 0, 0), Battery(size.X, 1, 0));

            var grid = StoreGrid("store-on", at, true);
            var store = (IMyStoreBlock)Fat(grid, "Admin_Store");
            var box = Inventory(grid.GetCubeBlock(new Vector3I(size.X, 0, 0)).FatBlock);
            var offGrid = StoreGrid("store-off", at + Site.Right * 60, false);
            var offStore = (IMyStoreBlock)Fat(offGrid, "Admin_Store");
            yield return Seconds(1);
            var example = offStore.CustomData;

            store.CustomData = "type=STATION_SELL;item=Ore_Iron;minCount=10;maxCount=100;minPrice=10;maxPrice=100;production=10\n" +
                               "type=STATION_BUY;item=Ingot_Iron;minCount=10;maxCount=100;minPrice=10;maxPrice=100;production=-10";
            Put(box, "Ore/Iron", 50);
            Put(box, "Ingot/Iron", 30);
            Put(Inventory(offGrid.GetCubeBlock(new Vector3I(size.X, 0, 0)).FatBlock), "Ore/Iron", 50);

            // the 3 min loop: one recount at once, as /reset_stores does
            var recount = storeType.GetMethod("RecalculateStores", Any);
            var instance = Activator.CreateInstance(storeType);
            MyAPIGateway.Parallel.StartBackground(() => recount.Invoke(instance, null));
            yield return Seconds(3);

            var items = Items(store);
            var offer = items.FirstOrDefault(i => i.ItemId.SubtypeName == "Iron" && i.ItemId.TypeId.ToString().EndsWith("Ore"));
            var order = items.FirstOrDefault(i => i.ItemId.SubtypeName == "Iron" && i.ItemId.TypeId.ToString().EndsWith("Ingot"));
            // the production goes into the first inventory, the store's own; the stock is the grid's
            var ore = Total(grid, "Ore/Iron");
            var ingot = Total(grid, "Ingot/Iron");
            var offItems = Items(offStore);
            Note("admin store: " + string.Join(", ", items.Select(i => i.ItemId + " x" + i.Amount + " at " + i.PricePerUnit)) + "; the stock: ore " + ore + " (was 50, +6), ingots " + ingot +
                 " (was 30, -6); the store switched off: " + offItems.Count + " items; the empty CustomData became «" + example.Replace("\n", " | ") + "»");
            // the price of 50 of 10..100 (the middle 55 at 55): 60; of 30 ingots: 80 (the float rounding may take one off)
            Expect(offer.Amount == 50 && offer.PricePerUnit >= 59 && offer.PricePerUnit <= 60, "admin store's offer: 50 iron at 60",
                "admin store's offer: " + offer.Amount + " iron ore at " + offer.PricePerUnit + ", not 50 at 60");
            Expect(order.Amount == 70 && order.PricePerUnit >= 79 && order.PricePerUnit <= 80, "admin store's order: 70 ingots at 80",
                "admin store's order: " + order.Amount + " iron ingots at " + order.PricePerUnit + ", not 70 at 80");
            Expect(ore == 56, "admin store's production added 6 (10 in 5 min, a recount of 3 min)", "admin store's production: the iron ore 50 -> " + ore + ", not 56");
            Expect(ingot == 24, "admin store's negative production took 6", "admin store's negative production: the ingots 30 -> " + ingot + ", not 24");
            Expect(offItems.Count == 0, "admin store switched off sells nothing", "admin store switched off has " + offItems.Count + " items");
            Expect(example.Contains("type=STATION_SELL;item=Ore_Iron") && example.Contains("type=STATION_BUY;item=Ingot_Iron"),
                "admin store's empty CustomData got the example", "admin store's empty CustomData became «" + example + "»");

            // /reset_stores in the chat: a player's does nothing, an admin's recounts
            Clear(store);
            Chat(Me, "/reset_stores");
            yield return Seconds(2);
            var byPlayer = Items(store).Count;
            var level = MySession.Static.GetUserPromoteLevel(Steam(Me));
            MySession.Static.SetUserPromoteLevel(Steam(Me), VRage.Game.ModAPI.MyPromoteLevel.Admin);
            try
            {
                Chat(Me, "/reset_stores");
                yield return Seconds(3);
            }
            finally
            {
                MySession.Static.SetUserPromoteLevel(Steam(Me), level);
            }
            var byAdmin = Items(store).Count;
            Note("admin store: /reset_stores of a player: " + byPlayer + " items, of an admin: " + byAdmin);
            Expect(byPlayer == 0, "admin store: a player's /reset_stores does nothing", "admin store: a player's /reset_stores recounted the stores");
            Expect(byAdmin > 0, "admin store: an admin's /reset_stores recounts", "admin store: an admin's /reset_stores put nothing on sale");
        }

        private static List<Sandbox.ModAPI.Ingame.MyStoreQueryItem> Items(IMyStoreBlock store)
        {
            var items = new List<Sandbox.ModAPI.Ingame.MyStoreQueryItem>();
            store.GetPlayerStoreItems(items);
            return items;
        }

        private static void Clear(IMyStoreBlock store)
        {
            var items = new List<VRage.Game.ModAPI.IMyStoreItem>();
            store.GetStoreItems(items);
            foreach (var item in items) store.RemoveStoreItem(item);
        }

        private void Chat(int client, string text)
        {
            var chat = typeof(Sandbox.Engine.Multiplayer.MyMultiplayerBase).GetMethod("OnChatMessageReceived_Server", BindingFlags.Static | BindingFlags.NonPublic)
                       ?? throw new ScenarioFailedException("MyMultiplayerBase.OnChatMessageReceived_Server is not in this game");
            // the game's anti-spam keeps the time of the last message per sender
            var spam = (IDictionary)typeof(Sandbox.Engine.Multiplayer.MyMultiplayerBase).GetField("m_chatAntiSpamDatas", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(Sandbox.Engine.Multiplayer.MyMultiplayer.Static);
            spam?.Remove(IdentityOf(client));
            var msg = new ChatMsg { Text = text, Author = Steam(client), Channel = (byte)Sandbox.Game.Gui.ChatChannel.Global, TargetId = 0, CrossChatEnabled = true };
            FakeClients.AsClient(client, () => chat.Invoke(null, new object[] { msg }));
        }

        // ------------------------------------------------------------------ the valuable cargo and the start console

        private IEnumerator ValuableCargo(Vector3D at)
        {
            Need("Beacon/Valuable_Cargo");
            Need("Projector/Cargo_Start_Console");
            var cargoSize = Definition("Beacon/Valuable_Cargo").Size;
            Part[] CargoParts() => Enumerable.Range(0, cargoSize.X).SelectMany(x => Enumerable.Range(0, cargoSize.Z).Select(z => new Part("CubeBlock/LargeBlockArmorBlock", x, -1, z)))
                .Concat(new[] { new Part("Beacon/Valuable_Cargo", 0, 0, 0).With(b => ((MyObjectBuilder_FunctionalBlock)b).Enabled = false) }).ToArray();

            // 1. a cargo on a station, switched off, no power on the grid
            var station = Build("cargo-station", at, true, CargoParts());
            var cargo = (IMyBeacon)Fat(station, "Valuable_Cargo");
            yield return Seconds(2);
            cargo.Radius = 100;
            cargo.Enabled = false;

            // 2. a start console projecting a cargo ship, within 5 km of this cargo; 3. one projecting nothing, far off
            var model = Build("cargo-model", at + Site.Up * 300, false, CargoParts());
            var blueprint = (MyObjectBuilder_CubeGrid)model.GetObjectBuilder(true);
            model.Close();
            var nearConsole = Build("cargo-start-near", at + Site.Right * 300, true, new Part("Projector/Cargo_Start_Console", 0, 0, 0).With(Enabled), BatteryAbove("Projector/Cargo_Start_Console"));
            var nearProjector = (IMyProjector)Fat(nearConsole, "Cargo_Start_Console");
            nearProjector.ProjectionOffset = new Vector3I(0, 20, 0);
            nearProjector.SetProjectedGrid(blueprint);
            var emptyAt = Site.At(0, -9000, 0);
            Build("cargo-start-empty", emptyAt, true, new Part("Projector/Cargo_Start_Console", 0, 0, 0).With(Enabled), BatteryAbove("Projector/Cargo_Start_Console"));
            var integrity = cargo.SlimBlock.Integrity;
            yield return Seconds(8); // the logics' 100 frames, several times

            var sink = ((MyCubeBlock)cargo).Components.Get<Sandbox.Game.EntityComponents.MyResourceSinkComponent>();
            var powered = sink?.IsPoweredByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId) ?? false;
            var worn = integrity - cargo.SlimBlock.Integrity;
            var spawnedNear = CargoShipsNear(at + Site.Right * 300, 1000).Count(g => g != station);
            var spawnedEmpty = CargoShipsNear(emptyAt, 1000).Count;
            Note("valuable cargo: the grid " + (station.IsStatic ? "static" : "a ship") + ", radius " + cargo.Radius + ", " + (cargo.Enabled ? "on" : "off") + ", powered " + powered +
                 ", integrity " + integrity.ToString("F2") + " -> " + cargo.SlimBlock.Integrity.ToString("F2") + " of " + cargo.SlimBlock.MaxIntegrity.ToString("F0") +
                 "; the start console with a cargo within 5 km spawned " + spawnedNear + ", the one projecting nothing " + spawnedEmpty);
            Expect(!station.IsStatic, "valuable cargo made its station a ship", "valuable cargo's grid stayed a station");
            Expect(cargo.Radius >= 14999 && cargo.Enabled, "valuable cargo holds its range of 15 km and stays on", "valuable cargo: radius " + cargo.Radius + ", " + (cargo.Enabled ? "on" : "off"));
            Expect(powered, "valuable cargo has power of its own", "valuable cargo on a grid with no power is not powered");
            Expect(worn > 0 && worn < cargo.SlimBlock.MaxIntegrity * 0.01, "valuable cargo wears away slowly",
                "valuable cargo's integrity went down by " + worn.ToString("F3") + " of " + cargo.SlimBlock.MaxIntegrity.ToString("F0") + " in 8 s");
            Expect(spawnedNear == 0, "cargo start console spawns nothing with a cargo within 5 km", "cargo start console spawned " + spawnedNear + " with a cargo within 5 km");
            Expect(spawnedEmpty == 0, "cargo start console projecting nothing spawns nothing", "cargo start console projecting nothing spawned " + spawnedEmpty);
            foreach (var grid in CargoShipsNear(at, 2000).Concat(CargoShipsNear(emptyAt, 1000))) Track(grid);
        }

        private static List<MyCubeGrid> CargoShipsNear(Vector3D at, double radius) =>
            MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.IsPreview && !g.MarkedForClose && Vector3D.Distance(g.PositionComp.GetPosition(), at) < radius &&
                                                                     g.GetFatBlocks().Any(b => b.BlockDefinition.Id.SubtypeName == "Valuable_Cargo")).ToList();

        // ------------------------------------------------------------------ the finish console (SentisAdventures)

        private IEnumerator FinishConsole()
        {
            Need("Projector/Cargo_Finish_Console");
            Need("Beacon/Valuable_Cargo");
            var processorType = SafeType(_adventures, "SentisAdventures.Adventures.CargoQuest.CargoFinishConsoleProcessor") ?? throw new ScenarioSkippedException("no SentisAdventures plugin");
            var faction = Found(Me);
            yield return Seconds(0.5);

            // the console by the players (the mate stands 6 m off the player): the reward a container on armour
            var at = Site.At(0, 0, -60);
            var console = Build("cargo-finish", at, true, new Part("Projector/Cargo_Finish_Console", 0, 0, 0).With(Enabled), BatteryAbove("Projector/Cargo_Finish_Console"));
            var projector = (IMyProjector)Fat(console, "Cargo_Finish_Console");
            yield return Seconds(1);
            var example = projector.CustomData;
            var reward = Build("cargo-reward", Site.At(0, 300, -60), false, new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 0, -1, 0));
            var rewardName = reward.DisplayName;
            var blueprint = (MyObjectBuilder_CubeGrid)reward.GetObjectBuilder(true);
            reward.Close();
            projector.ProjectionOffset = new Vector3I(0, 30, 0);
            projector.SetProjectedGrid(blueprint);
            projector.CustomData = "Ore_Gold=7;Reward_rep=3";

            var cargoSize = Definition("Beacon/Valuable_Cargo").Size;
            var cargoGrid = Build("cargo-delivered", at + Site.Right * 15, false,
                Enumerable.Range(0, cargoSize.X).SelectMany(x => Enumerable.Range(0, cargoSize.Z).Select(z => new Part("CubeBlock/LargeBlockArmorBlock", x, -1, z)))
                    .Concat(new[] { new Part("Beacon/Valuable_Cargo", 0, 0, 0) }));
            yield return Seconds(2); // the plugin takes the cargo into its list on the next frame

            var core = SafeType(_adventures, "SentisAdventures.SentisAdventuresPlugin").GetProperty("Instance", Any).GetValue(null);
            core = core.GetType().GetField("VirtualRepCore", Any).GetValue(core);
            int Rep(int client) => (int)core.GetType().GetMethod("GetRepOfPlayerAndFaction").Invoke(core, new object[] { IdentityOf(client), faction.FactionId });
            void SetRep(int client, int rep) => core.GetType().GetMethod("UpdatePlayerFactionRep").Invoke(core, new object[] { IdentityOf(client), faction.FactionId, rep, "regression check" });
            var mateRep = Rep(_mate);
            var myRep = Rep(Me);
            _restore.Add(() => { SetRep(_mate, mateRep); SetRep(Me, myRep); });

            // the plugin's pass of 30 s at once
            processorType.GetMethod("Update").Invoke(Activator.CreateInstance(processorType), null);
            yield return Seconds(4);
            var spawned = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !g.MarkedForClose && !g.IsPreview && g.DisplayName == rewardName);
            if (spawned != null) Track(spawned);
            var gold = spawned == null ? -1 : Total(spawned, "Ore/Gold");
            Note("cargo finish console: the empty CustomData became «" + example + "»; the cargo " + (Gone(cargoGrid) ? "taken" : "still there") + "; the reward " +
                 (spawned == null ? "not spawned" : "spawned with " + gold + " gold") + "; the mate's reputation " + mateRep + " -> " + Rep(_mate));
            Expect(example.Contains("Reward_rep"), "cargo finish console's empty CustomData got the example", "cargo finish console's empty CustomData became «" + example + "»");
            Expect(Gone(cargoGrid), "cargo finish console took the cargo", "cargo finish console: the cargo 15 m off stayed");
            Expect(spawned != null && gold == 7, "cargo finish console's reward with the items of the CustomData", "cargo finish console's reward: " + (spawned == null ? "none" : gold + " gold, not 7"));
            Expect(Rep(_mate) == mateRep + 3, "cargo finish console gave the reputation to a player within 50 m", "cargo finish console: the mate's reputation " + mateRep + " -> " + Rep(_mate) + ", not +3");
        }

        // ------------------------------------------------------------------ the border beacon (SentisAdventures)

        private IEnumerator BorderBeacon(Vector3D at)
        {
            Need("Beacon/ContrabandBeacon");
            var behaviorType = SafeType(_adventures, "SentisAdventures.Adventures.Contraband.ContrabandGridPositionBehavior") ?? throw new ScenarioSkippedException("no SentisAdventures plugin");
            var behavior = behaviorType.GetField("Instance", Any).GetValue(null);

            // 1. an empty CustomData; a CustomData that is no config
            var beaconGrid = Build("border-beacon", at, true, new[] { Beacon("ContrabandBeacon", 1000) }, ownedBy: 0);
            var beacon = (IMyBeacon)Fat(beaconGrid, "ContrabandBeacon");
            yield return Seconds(1);
            var example = beacon.CustomData;
            beacon.CustomData = "not a config";
            yield return Seconds(0.5);
            var refused = beacon.CustomData == example;
            Expect(example.Contains("ContrabandItems") && example.Contains("MinutesBeforeScan"), "border beacon's empty CustomData got the example config",
                "border beacon's empty CustomData became «" + example + "»");
            Expect(refused, "border beacon refused a CustomData that is no config", "border beacon took «" + beacon.CustomData + "»");

            // 2. a beacon a player builds is blown up (far off: the explosions go on for a minute)
            var playersAt = Site.At(-5000, 0, 0);
            var players = Build("border-beacon-player", playersAt, true, Beacon("ContrabandBeacon", 100), new Part("CubeBlock/LargeBlockArmorBlock", 0, -1, 0));
            var playersBeacon = Fat(players, "ContrabandBeacon");

            // 3. the scan: a ship with the contraband, one without it, one with it in a container of the blacklist
            beacon.CustomData = "{\"ContrabandItems\":[{\"Type\":\"Ingot\",\"Sybtype\":\"Platinum\"}],\"NpcFactionTag\":\"QQ9\",\"MinutesBeforeScan\":1,\"MinBlockCount\":2," +
                                "\"BeaconLifetimeInMinutes\":-1,\"AdvancedActions\":{\"SlowdownScannedShip\":true,\"IfContrabandFoundTurnOffEnergy\":true}," +
                                "\"BlacklistedContainerSubtypes\":[\"LargeBlockLargeContainer\"],\"Blueprints\":[]}";
            beacon.Radius = 1000;
            MyCubeGrid Ship(string name, Vector3D where, string container, string item)
            {
                var ship = Build(name, where, false, new Part("CargoContainer/" + container, 0, 0, 0), Battery(0, -1, 0));
                Put(Inventory(Fat(ship, container)), item, 10);
                return ship;
            }
            var smuggler = Ship("border-smuggler", at + Site.Right * 100, "LargeBlockSmallContainer", "Ingot/Platinum");
            var honest = Ship("border-honest", at + Site.Right * 200, "LargeBlockSmallContainer", "Ingot/Iron");
            var hidden = Ship("border-hidden", at + Site.Right * 300, "LargeBlockLargeContainer", "Ingot/Platinum");
            yield return Seconds(2);
            smuggler.Physics.LinearVelocity = (Vector3)(Site.Forward * 5);

            var process = behaviorType.GetMethod("ProcessBeacons");
            var now = DateTime.Now;
            process.Invoke(behavior, new object[] { now });                     // they come into the range
            process.Invoke(behavior, new object[] { now.AddMinutes(2) });       // MinutesBeforeScan later: the scan
            yield return Seconds(17); // the scan's 15 s

            bool Powered(MyCubeGrid g) => g.GetFatBlocks().OfType<IMyFunctionalBlock>().Any(b => b is IMyBatteryBlock && b.Enabled);
            var speed = smuggler.Physics?.LinearVelocity.Length() ?? -1;
            Note("border beacon: the player's beacon " + (Gone(playersBeacon) || playersBeacon.SlimBlock.IsDestroyed ? "blown up" : "still there") +
                 "; the smuggler " + (Powered(smuggler) ? "powered" : "power cut") + " at " + speed.ToString("F1") + " m/s (was 5); the honest one " +
                 (Powered(honest) ? "powered" : "power cut") + "; the one with it in a blacklisted container " + (Powered(hidden) ? "powered" : "power cut"));
            Expect(Gone(playersBeacon) || playersBeacon.SlimBlock.IsDestroyed, "border beacon a player builds is blown up", "border beacon a player built is still there");
            Expect(!Powered(smuggler), "border beacon found the contraband and cut the power", "border beacon: the ship with the platinum still has its power");
            Expect(speed >= 0 && speed < 1, "border beacon slowed the scanned ship", "border beacon: the scanned ship goes " + speed.ToString("F1") + " m/s");
            Expect(Powered(honest), "border beacon left a ship without contraband", "border beacon cut the power of a ship without contraband");
            Expect(Powered(hidden), "border beacon does not look into a blacklisted container", "border beacon found the contraband in a blacklisted container");
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>A type of the mod by its name (the game may compile it under another namespace).</summary>
        private Type ModType(string name) =>
            _mod.GetType("Sentis_v5." + name) ?? _mod.GetTypes().FirstOrDefault(t => t.Name == name) ?? throw new ScenarioSkippedException("no " + name + " in the mod");

        private ulong Steam(int client) => FakeClients.PlayerOf(client).Id.SteamId;

        private static bool Gone(MyEntity entity) => entity == null || entity.Closed || entity.MarkedForClose;

        private static object Logic(MyEntity entity, Type type) =>
            entity.GameLogic?.GetType().GetMethod("GetAs")?.MakeGenericMethod(type).Invoke(entity.GameLogic, null);

        private static Type SafeType(Assembly a, string name)
        {
            if (a == null) return null;
            try { return a.GetType(name); }
            catch { return null; }
        }

        private static void Need(string id)
        {
            var parts = id.Split('/');
            if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(new MyDefinitionId(MyObjectBuilderType.Parse("MyObjectBuilder_" + parts[0]), parts[1]), out _))
                throw new ScenarioSkippedException("no " + id + " in this game");
        }

        private static void Enabled(MyObjectBuilder_CubeBlock b) => ((MyObjectBuilder_FunctionalBlock)b).Enabled = true;

        private static Part Beacon(string subtype, float radius) =>
            new Part("Beacon/" + subtype, 0, 0, 0).With(b =>
            {
                var beacon = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_Beacon)b;
                beacon.BroadcastRadius = radius;
                beacon.Enabled = true;
            });

        private static Part Battery(int x, int y, int z) => new Part("BatteryBlock/LargeBlockBatteryBlock", x, y, z).With(b => Motion.Charged(b));

        private static Part BatteryAbove(string id) => Battery(0, Definition(id).Size.Y, 0);

        private static MyCubeBlock Fat(MyCubeGrid grid, string subtype) => grid.GetFatBlocks().First(b => b.BlockDefinition.Id.SubtypeName == subtype);
    }
}
