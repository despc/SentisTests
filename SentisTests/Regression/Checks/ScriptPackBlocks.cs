using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisTests.Core;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The rest of Sentis_v5_ScriptPack_Fix's blocks and grid logic, on the server (<see cref="ScriptPackFixCheck"/> has the
    /// custom inhibitor's jump drive, the loot collector's loose item, the admin protectors' count and the suppressor):
    /// - speed limits: a large ship with no thrust, thrown at 140 m/s, is slowed to its limit (80 m/s), and its entry goes
    ///   once the ship is gone;
    /// - a destroyed container's loot bag is opened by a loot collector, which takes its ore, and leaves the mod's bags;
    /// - the magic container (admin 2) fills itself as its CustomData says;
    /// - the admin store puts its stock on sale;
    /// - the cargo quest's start console spawns its projected ship with the valuable cargo;
    /// - a parachute gets its canvas and auto deploy;
    /// - an admin protector's grid takes no damage;
    /// - the custom inhibitor's DAMAGE of blocks and of players;
    /// - the nanobot welds a block of its own grid;
    /// - FixShip (the cockpit's action, done by SentisGameplayImprovements) makes the ship again.
    /// Each part is skipped (noted) when its blocks are not in this game. Skipped where the mod is not loaded.
    /// </summary>
    public sealed class ScriptPackBlocksCheck : RegressionCheck
    {
        public override string Name => Prefix + "scriptpack_blocks";
        public override int TimeoutSeconds => 240;
        protected override bool NeedsLooseItems => true;
        protected override bool NeedsBags => true;
        protected override bool NeedsDamage => true;

        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<string> _failures = new List<string>();
        private readonly List<string> _passed = new List<string>();
        private Assembly _mod;

        protected override IEnumerator Check()
        {
            _mod = MyScriptManager.Static?.Scripts?.Values.FirstOrDefault(a => a.GetType("AdminProtector.AdminProtectorBlock") != null);
            SkipUnless(_mod != null, "no Sentis_v5_ScriptPack_Fix mod in this world");
            yield return Arrive(Site.At(0, 0, -30));

            // set up every part, then one wait for the loops (speed 3 s, loot 2 s, magic ~17 s, inhibitor 5 s, the rest 100 frames)
            var speed = SetUp("speed limit", () => SpeedShip(Site.At(400, 0, 0)));
            var bag = SetUp("loot bag", () => LootBag(Site.At(-400, 0, 0)));
            var magic = SetUp("magic container", () => MagicContainer(Site.At(0, 400, 0)));
            var store = SetUp("admin store", () => AdminStore(Site.At(0, -400, 0)));
            var cargo = SetUp("cargo start console", () => CargoConsole(Site.At(800, 0, 0)));
            var chute = SetUp("parachute", () => Parachute(Site.At(-800, 0, 0)));
            var inhibitor = SetUp("custom inhibitor DAMAGE", () => InhibitorDamage(Site.At(0, 0, 0)));
            var nano = SetUp("nanobot weld", () => Nanobot(Site.At(0, 800, 0)));

            yield return Seconds(22);

            Verify("speed limit", speed);
            Verify("loot bag", bag);
            Verify("magic container", magic);
            Verify("admin store", store);
            Verify("cargo start console", cargo);
            Verify("parachute", chute);
            Verify("custom inhibitor DAMAGE", inhibitor);
            Verify("nanobot weld", nano);

            // the speed limit's entry of a ship gone
            if (_speedShip != null)
            {
                var ship = _speedShip;
                yield return CloseAndWait(ship);
                yield return Seconds(7);
                var limits = SpeedLimits();
                if (limits != null)
                {
                    var left = limits.Keys.Cast<object>().Any(k => k is IMyCubeGrid g && g.EntityId == ship.EntityId);
                    Note("speed limit of the closed ship: " + (left ? "still kept" : "dropped"));
                    (left ? _failures : _passed).Add("speed limit entry of a closed ship " + (left ? "kept" : "dropped"));
                }
            }

            // admin protector: no damage
            foreach (var step in Run("admin protector damage", AdminProtectorDamage(Site.At(-400, 400, 0)))) yield return step;
            // FixShip
            foreach (var step in Run("FixShip", FixShip(Site.At(400, 400, 0)))) yield return step;

            foreach (var passed in _passed) Note("ok: " + passed);
            foreach (var failure in _failures) Note("FAILED: " + failure);
            Check(_failures.Count == 0, string.Join("; ", _failures));
            Result(string.Join("; ", _passed));
        }

        // ---------- the parts: each set up returns its verdict, read after the wait ----------

        private Func<string> SetUp(string what, Func<Func<string>> setUp)
        {
            try
            {
                return setUp();
            }
            catch (ScenarioSkippedException e)
            {
                Note("skipped " + what + ": " + e.Message);
                return null;
            }
            catch (Exception e)
            {
                _failures.Add(what + ": set up failed: " + e.Message);
                return null;
            }
        }

        /// <summary>The verdict: null passed (noted), otherwise the failure.</summary>
        private void Verify(string what, Func<string> verdict)
        {
            if (verdict == null) return;
            try
            {
                var failure = verdict();
                if (failure == null) _passed.Add(what);
                else _failures.Add(what + ": " + failure);
            }
            catch (Exception e)
            {
                _failures.Add(what + ": " + e.Message);
            }
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
                    _failures.Add(what + ": " + e.Message);
                    yield break;
                }
                yield return current;
            }
        }

        private MyCubeGrid _speedShip;

        private Func<string> SpeedShip(Vector3D at)
        {
            Need("Cockpit/LargeBlockCockpit");
            var limitsType = _mod.GetType("Sentis_v5.SpeedRestrictionsSession");
            if (limitsType == null) throw new ScenarioSkippedException("no SpeedRestrictionsSession in the mod");
            var ship = Build("speed-ship", at, false, new Part("Cockpit/LargeBlockCockpit", 0, 0, 0), Battery(0, -1, 0));
            _speedShip = ship;
            ship.Physics.LinearVelocity = (Vector3)(Site.Forward * 140);
            return () =>
            {
                var v = ship.Physics.LinearVelocity.Length();
                var limits = SpeedLimits();
                double limit = -1;
                if (limits != null)
                    foreach (DictionaryEntry e in limits)
                        if (e.Key is IMyCubeGrid g && g.EntityId == ship.EntityId) limit = (double)e.Value;
                // a ship slowed to its limit (80 m/s with no thrust) is slow and uncontrolled: the mod drops its entry then
                Note("speed ship: " + v.ToString("F1") + " m/s (thrown at 140), its limit " + (limit < 0 ? "dropped (slow, uncontrolled)" : limit.ToString("F1")));
                if (v > 81) return "the ship thrown at 140 m/s with no thrust still goes " + v.ToString("F1") + " m/s (its limit is 80)";
                if (limit >= 0 && v > limit + 1) return "the ship goes " + v.ToString("F1") + " m/s over its limit " + limit.ToString("F1");
                return null;
            };
        }

        private IDictionary SpeedLimits() => _mod.GetType("Sentis_v5.SpeedRestrictionsSession")?.GetField("MaxSpeeds", Any)?.GetValue(null) as IDictionary;

        private Func<string> LootBag(Vector3D at)
        {
            Need("ConveyorSorter/Loot_Collector");
            var collectorGrid = Build("bag-collector", at, true, new Part("ConveyorSorter/Loot_Collector", 0, 0, 0).With(Enabled), BatteryAbove("ConveyorSorter/Loot_Collector"));
            var sorter = (Sandbox.ModAPI.IMyConveyorSorter)Fat(collectorGrid, "Loot_Collector");
            sorter.SetFilter(Sandbox.ModAPI.Ingame.MyConveyorSorterMode.Blacklist, new List<Sandbox.ModAPI.Ingame.MyInventoryItemFilter>());
            var boxAt = at + Site.Forward * 30;
            var grid = Build("bag-box", boxAt, true, new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 0, -1, 0));
            var box = grid.GetCubeBlock(Vector3I.Zero);
            Put(Inventory(box.FatBlock), "Ore/Nickel", 100);
            box.DoDamage(box.MaxIntegrity * 10, MyDamageType.Explosion, true, null, 0);
            var bags = (HashSet<MyCargoContainerInventoryBagEntity>)_mod.GetType("Sentis_v5.Session")?.GetField("Bags", Any)?.GetValue(null);
            return () =>
            {
                var taken = Amount(Inventory((MyEntity)sorter), "Ore/Nickel");
                var bagsNear = MyEntities.GetEntities().OfType<MyCargoContainerInventoryBagEntity>().Count(b => !b.MarkedForClose && Vector3D.Distance(b.PositionComp.GetPosition(), boxAt) < 50);
                var closedKept = bags?.Count(b => b.Closed || b.MarkedForClose) ?? -1;
                Note("loot bag: " + taken.ToString("F1") + " nickel collected, bags left near " + bagsNear + ", closed bags still in the mod's set " + closedKept +
                     (MySession.Static.Settings.TemporaryContainers ? "" : " (the world has no temporary containers: the ore floats out)"));
                if (taken < 99.99) return "the collector took " + taken.ToString("F1") + " of the destroyed container's 100 nickel";
                if (bagsNear > 0) return "a loot bag near the collector is not opened";
                if (closedKept > 0) return closedKept + " closed bags kept in Session.Bags";
                return null;
            };
        }

        private Func<string> MagicContainer(Vector3D at)
        {
            Need("CargoContainer/LargeBlockSmallContainer_admin2");
            var grid = Build("magic-box", at, true, new Part("CargoContainer/LargeBlockSmallContainer_admin2", 0, 0, 0));
            var box = Fat(grid, "LargeBlockSmallContainer_admin2");
            ((Sandbox.ModAPI.IMyTerminalBlock)box).CustomData = "item=Ore_Cobalt;max=50;speed=10;boost=1;startBoost=00:00;endBoost=00:01";
            return () =>
            {
                var have = Amount(Inventory(box), "Ore/Cobalt");
                Note("magic container: " + have.ToString("F0") + " cobalt");
                return have >= 10 ? null : "the magic container made no cobalt in 22 s (" + have.ToString("F0") + ")";
            };
        }

        private Func<string> AdminStore(Vector3D at)
        {
            Need("StoreBlock/Admin_Store");
            var storeType = _mod.GetType("Sentis_v5.AdminStore") ?? _mod.GetTypes().FirstOrDefault(t => t.Name == "AdminStore");
            if (storeType == null) throw new ScenarioSkippedException("no AdminStore in the mod");
            var storeSize = Definition("StoreBlock/Admin_Store").Size;
            var grid = Build("admin-store", at, true, new Part("StoreBlock/Admin_Store", 0, 0, 0).With(Enabled),
                new Part("CargoContainer/LargeBlockSmallContainer", storeSize.X, 0, 0), Battery(storeSize.X, 1, 0));
            var store = (Sandbox.ModAPI.IMyStoreBlock)Fat(grid, "Admin_Store");
            store.CustomData = "type=STATION_SELL;item=Ore_Iron;minCount=10;maxCount=100;minPrice=10;maxPrice=100;production=0";
            Put(Inventory(grid.GetCubeBlock(new Vector3I(storeSize.X, 0, 0)).FatBlock), "Ore/Iron", 50);
            // the 3 min loop: the stores' recount at once (the admins' /reset_stores does the same)
            var recount = storeType.GetMethod("RecalculateStores", Any);
            var instance = Activator.CreateInstance(storeType);
            MyAPIGateway.Parallel.StartBackground(() => recount.Invoke(instance, null));
            return () =>
            {
                var items = new List<Sandbox.ModAPI.Ingame.MyStoreQueryItem>();
                store.GetPlayerStoreItems(items);
                foreach (var item in items) Note("admin store: " + item.ItemId + " x" + item.Amount + " at " + item.PricePerUnit);
                return items.Any(i => i.Amount > 0) ? null : "the admin store put nothing on sale (" + items.Count + " items)";
            };
        }

        private MyCubeGrid _cargoConsole;

        private Func<string> CargoConsole(Vector3D at)
        {
            Need("Projector/Cargo_Start_Console");
            Need("Beacon/Valuable_Cargo");
            // the ship to hand out: the valuable cargo on a bed of armour (made, taken as a blueprint, closed)
            var cargoSize = Definition("Beacon/Valuable_Cargo").Size;
            var bed = Enumerable.Range(0, cargoSize.X).SelectMany(x => Enumerable.Range(0, cargoSize.Z).Select(z => new Part("CubeBlock/LargeBlockArmorBlock", x, -1, z)));
            var model = Build("cargo-model", at + Site.Up * 200, false, bed.Concat(new[] { new Part("Beacon/Valuable_Cargo", 0, 0, 0) }));
            var blueprint = (MyObjectBuilder_CubeGrid)model.GetObjectBuilder(true);
            model.Close();
            var console = Build("cargo-console", at, true, new Part("Projector/Cargo_Start_Console", 0, 0, 0).With(Enabled),
                BatteryAbove("Projector/Cargo_Start_Console"));
            _cargoConsole = console;
            var projector = (Sandbox.ModAPI.IMyProjector)Fat(console, "Cargo_Start_Console");
            projector.ProjectionOffset = new Vector3I(0, 20, 0);
            projector.SetProjectedGrid(blueprint);
            return () =>
            {
                var cargoes = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.IsPreview && !g.MarkedForClose && g != console &&
                        Vector3D.Distance(g.PositionComp.GetPosition(), at) < 500 && g.GetFatBlocks().Any(b => b.BlockDefinition.Id.SubtypeName == "Valuable_Cargo")).ToList();
                Note("cargo start console: projecting " + (projector.ProjectedGrid != null) + ", " + cargoes.Count + " cargo ships spawned" +
                     string.Concat(cargoes.Select(c => ", " + c.DisplayName + (c.IsStatic ? " static" : " ship"))));
                foreach (var c in cargoes) _spawned.Add(c);
                if (projector.ProjectedGrid == null) return "the console projects nothing";
                if (cargoes.Count == 0) return "no ship with the valuable cargo spawned";
                if (cargoes.Count > 1) return cargoes.Count + " cargo ships spawned (one while it is within 5 km)";
                return null;
            };
        }

        private readonly List<MyCubeGrid> _spawned = new List<MyCubeGrid>();

        private Func<string> Parachute(Vector3D at)
        {
            Need("Parachute/LgParachute");
            var grid = Build("chute-ship", at, true, new Part("Parachute/LgParachute", 0, 0, 0), Battery(0, -1, 0));
            var chute = (SpaceEngineers.Game.ModAPI.IMyParachute)Fat(grid, "LgParachute");
            return () =>
            {
                var canvas = Amount(Inventory((MyEntity)chute), "Component/Canvas");
                Note("parachute: " + canvas.ToString("F0") + " canvas, auto deploy " + chute.AutoDeploy + " at " + chute.AutoDeployHeight.ToString("F0") + " m");
                if (canvas < 3) return "the parachute got " + canvas.ToString("F0") + " canvas";
                if (!chute.AutoDeploy) return "the parachute's auto deploy is off";
                return null;
            };
        }

        private Func<string> InhibitorDamage(Vector3D at)
        {
            Need("Beacon/Custom_Inhibitor");
            // the player stands 30 m off the site's origin (Arrive): in the radius, the victim battery too
            var grid = Build("damage-inhibitor", at, true, Beacon("Custom_Inhibitor", 60));
            var beacon = (Sandbox.ModAPI.IMyBeacon)Fat(grid, "Custom_Inhibitor");
            const string config = "LargeBlockSmallGenerator:DAMAGE\nPlayers:DAMAGE";
            beacon.CustomData = config;
            beacon.Radius = 60;
            var victim = Build("damage-victim", at + Site.Right * 20, true, new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0));
            var reactor = Fat(victim, "LargeBlockSmallGenerator");
            var health = Body?.StatComp?.Health?.Value ?? -1;
            return () =>
            {
                if (beacon.CustomData != config) return "the inhibitor's CustomData became its default (it was read empty)";
                var hurt = reactor.Closed || reactor.SlimBlock.Integrity < reactor.SlimBlock.MaxIntegrity;
                var now = Body?.StatComp?.Health?.Value ?? -1;
                Note("custom inhibitor: reactor " + (reactor.Closed ? "gone" : reactor.SlimBlock.Integrity.ToString("F0") + " of " + reactor.SlimBlock.MaxIntegrity.ToString("F0")) +
                     ", player health " + health.ToString("F0") + " -> " + now.ToString("F0"));
                if (!hurt) return "the reactor in the radius took no damage";
                if (health > 0 && now >= health) return "the player in the radius took no damage";
                return null;
            };
        }

        private Func<string> Nanobot(Vector3D at)
        {
            const string nanobot = "ShipWelder/SELtdLargeNanobotBuildAndRepairSystem";
            Need(nanobot);
            var size = Definition(nanobot).Size;
            var grid = Build("nanobot-own", at, true, new[]
            {
                new Part(nanobot, 0, 0, 0).With(Enabled),
                Battery(0, size.Y, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", size.X + 2, 0, 0).At(0.3f),
                new Part("CubeBlock/LargeBlockArmorBlock", size.X, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", size.X + 1, 0, 0),
            });
            var welder = Fat(grid, "SELtdLargeNanobotBuildAndRepairSystem");
            Put(Inventory(welder), "Component/SteelPlate", 200);
            var target = grid.GetCubeBlock(new Vector3I(size.X + 2, 0, 0));
            var before = target.Integrity;
            return () =>
            {
                Note("nanobot: the armour block's integrity " + before.ToString("F0") + " -> " + target.Integrity.ToString("F0") + " of " + target.MaxIntegrity.ToString("F0"));
                return target.Integrity > before + 1 ? null : "the nanobot did not weld the damaged armour block of its own grid";
            };
        }

        private IEnumerator AdminProtectorDamage(Vector3D at)
        {
            Need("Beacon/AdminGridProtectorLarge");
            var size = Definition("Beacon/AdminGridProtectorLarge").Size;
            var grid = Build("admin-immortal", at, true, new Part("Beacon/AdminGridProtectorLarge", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 0, -1, 0));
            yield return Seconds(1);
            var armor = grid.GetCubeBlock(new Vector3I(0, -1, 0));
            var before = armor.Integrity;
            armor.DoDamage(before * 0.5f, MyDamageType.Bullet, true, null, 0);
            yield return Seconds(1);
            Note("admin protector: armour " + before.ToString("F0") + " -> " + armor.Integrity.ToString("F0") + " after damage of half of it");
            (armor.Integrity < before - 0.01 ? _failures : _passed).Add(armor.Integrity < before - 0.01 ? "the admin protector's grid took damage" : "admin protector: no damage");
        }

        private IEnumerator FixShip(Vector3D at)
        {
            var logic = AppDomain.CurrentDomain.GetAssemblies().Select(a => SafeType(a, "SentisGameplayImprovements.FixShipLogic")).FirstOrDefault(t => t != null);
            if (logic == null) throw new ScenarioSkippedException("no SentisGameplayImprovements' FixShipLogic");
            var ship = Build("fixship-ship", at, false, new Part("Cockpit/LargeBlockCockpit", 0, 0, 0), Battery(0, -1, 0));
            yield return Seconds(2); // BigOwners: the game's next update
            var id = ship.EntityId;
            logic.GetMethod("DoFixShip", Any).Invoke(null, new object[] { id, Identity });
            yield return Seconds(5); // made again in parallel, then added
            var again = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !g.MarkedForClose && g.EntityId != id && g.DisplayName == ship.DisplayName &&
                                                                                            Vector3D.Distance(g.PositionComp.GetPosition(), at) < 50);
            Note("FixShip: the old ship " + (ship.Closed || ship.MarkedForClose ? "gone" : "still there") + ", made again " + (again != null));
            if (again != null) _spawned.Add(again);
            (again != null && (ship.Closed || ship.MarkedForClose) ? _passed : _failures).Add(again != null && (ship.Closed || ship.MarkedForClose)
                ? "FixShip made the ship again" : "FixShip did not make the ship again");
        }

        public override void Cleanup()
        {
            try
            {
                if (_cargoConsole != null && !_cargoConsole.MarkedForClose) _cargoConsole.Close();
                foreach (var grid in _spawned) if (grid != null && !grid.MarkedForClose) grid.Close();
                // a cargo ship the console spawned after the verdict
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && !g.IsPreview &&
                             g.GetFatBlocks().Any(b => b.BlockDefinition.Id.SubtypeName == "Valuable_Cargo") && Site != null &&
                             Vector3D.Distance(g.PositionComp.GetPosition(), Site.Origin) < 3000).ToList())
                    grid.Close();
            }
            catch (Exception e)
            {
                Log.Error(e, "scriptpack_blocks: cleanup");
            }
            base.Cleanup();
        }

        // ---------- helpers ----------

        private static Type SafeType(Assembly a, string name)
        {
            try
            {
                return a.GetType(name);
            }
            catch
            {
                return null;
            }
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
