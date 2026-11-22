using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The NPC sector of the live SentisAdventures config, every wave of it. A player's ship (dynamic, more than
    /// <c>MinBlockCount</c> blocks, powered, a main cockpit and two large containers; it takes no damage) at the
    /// sector's centre. The plugin's pass (<c>NpcSectorBehavior.CheckPlayerGrids</c>) is called by the scenario, its
    /// own 30-second pass held off for the run. For each wave: the bots that came (the faction's new grids with a
    /// working beacon) against <c>NpcWave</c> and its files, how far from the ship (<c>SectorSpawnDistance</c>), how
    /// many the plugin counts alive (a working beacon and remote control); on the first wave, whether a pass while it
    /// goes on spawns it again; whether the wave is over when its first bot dies (its beacon broken) or only with the
    /// last; then the reward on the next pass (<c>RewardWave</c>: credits on the player's account, items in the ship).
    /// After the last wave the ship must be taken to <c>GarbageCenter</c>. Every wave is gone through even when one
    /// fails; the problems are listed at the end.
    /// </summary>
    public sealed class AdventuresSectorScenario : TestScenario
    {
        public const string ScenarioName = "adv_sector";
        public override string Name => ScenarioName;
        public override string[] Requires => new[] { "SentisAdventures" };
        public override int TimeoutSeconds => 1500;

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private static readonly HashSet<long> Invulnerable = new HashSet<long>();
        private static bool _handler;

        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private readonly HashSet<long> _bots = new HashSet<long>();
        private MyCubeGrid _ship;
        private object _behavior;
        private Type _behaviorType;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var pluginType = Integrations.TypeOf("SentisAdventures", "SentisAdventures.SentisAdventuresPlugin");
            SkipUnless(pluginType != null, "SentisAdventures is not loaded");
            var plugin = pluginType.GetProperty("Instance", Any).GetValue(null);
            var config = pluginType.GetProperty("Config", Any).GetValue(null);
            var processor = plugin.GetType().GetField("AllGridsProcessor", Any).GetValue(plugin);
            _behavior = processor.GetType().GetProperty("SectorBehavior", Any).GetValue(processor);
            _behaviorType = _behavior.GetType();
            SkipUnless((bool)_behaviorType.GetField("_isInit", Any).GetValue(_behavior), "the NPC sector is off (no waves, or bad centres)");

            var waves = ((IEnumerable)P(config, "ConfigWaves")).Cast<object>().ToList();
            var path = (string)P(config, "SectorNpcBlueprintPath");
            var centre = (Vector3D)_behaviorType.GetField("_sectorCenter", Any).GetValue(null);
            var garbage = (Vector3D)_behaviorType.GetField("_garbageCenter", Any).GetValue(null);
            var radius = (int)P(config, "SectorRadius");
            var spawnDistance = (int)P(config, "SectorSpawnDistance");
            var minBlocks = (int)P(config, "MinBlockCount");
            Note("sector " + Fmt(centre) + " r " + radius + " m, " + waves.Count + " waves, bots of " + P(config, "FactionOwnerTag") + " " + spawnDistance + " m off, ships over " + minBlocks + " blocks");
            // leftovers of a run before: the faction's grids in the sector go first
            var leftovers = SectorBots().ToList();
            foreach (var g in leftovers) g.Close();
            if (leftovers.Count > 0) Note(leftovers.Count + " grids of " + P(config, "FactionOwnerTag") + " left in the sector before: closed");

            _gameplay.Set("DisableAnyDamageAfterStartTime", 0);
            if (!_handler)
            {
                MyDamageSystem.Static.RegisterBeforeDamageHandler(int.MaxValue - 1, OnDamage);
                _handler = true;
            }
            _behaviorType.GetMethod("UpdateConfig").Invoke(_behavior, null);
            // the plugin's own 30-second pass would go on with the waves between the scenario's steps: off for the run
            // (it returns while not initialised; CheckPlayerGrids, called here, does not look)
            _behaviorType.GetField("_isInit", Any).SetValue(_behavior, false);

            FakeClients.RemoveAll();
            FakeClients.Add(1, Network, i => (centre + Vector3D.Up * 200, 0, 0), withCharacters: true);
            yield return Wait(() => FakeClients.HasLiveCharacter(0), "the fake player's character", 60);
            var player = FakeClients.PlayerOf(0);
            var me = player.Identity.IdentityId;
            lock (Invulnerable) Invulnerable.Add(FakeClients.Character(0).EntityId);

            _ship = BuildShip(centre, me, minBlocks);
            lock (Invulnerable) Invulnerable.Add(_ship.EntityId);
            yield return WaitForSeconds(2, "the ship settles");
            Note("ship «" + _ship.DisplayName + "» " + _ship.BlocksCount + " blocks, power " + _ship.GridSystems.ResourceDistributor.ResourceState);

            var problems = new List<string>();
            var destroyedFlag = _behaviorType.GetField("WaveDestroyed", Any);
            Pass();
            for (var w = 0; w < waves.Count; w++)
            {
                var wave = waves[w];
                var expected = Parse((string)P(wave, "NpcWave"));
                var missing = expected.Where(e => !File.Exists(Path.Combine(path, e.Key))).Select(e => e.Key).ToList();
                var want = expected.Sum(e => e.Value);

                // the wave comes: the faction's new grids with a beacon, until they stop coming
                var bots = new List<MyCubeGrid>();
                var quiet = 0;
                for (var t = 0; t < 40 && quiet < 5; t++)
                {
                    yield return WaitForSeconds(1, "wave " + (w + 1) + " spawning");
                    var now = NewBots();
                    quiet = now.Count > 0 && now.Count == bots.Count ? quiet + 1 : 0;
                    bots = now;
                    if (now.Count == 0 && t >= 20) break;
                }
                foreach (var b in bots) _bots.Add(b.EntityId);
                var alive = AliveBots().Count;
                var shipAt = _ship.PositionComp.GetPosition();
                var distances = bots.Select(b => Vector3D.Distance(b.PositionComp.GetPosition(), shipAt)).ToList();
                var line = "wave " + (w + 1) + ": " + bots.Count + " bots of " + want + " (" + P(wave, "NpcWave") + ")" +
                           (missing.Count > 0 ? ", NO FILE " + string.Join(", ", missing) : "") +
                           (distances.Count > 0 ? ", " + distances.Min().ToString("F0") + "-" + distances.Max().ToString("F0") + " m from the ship" : "") +
                           ", alive to the plugin " + alive;
                if (missing.Count > 0) problems.Add("wave " + (w + 1) + ": no blueprint " + string.Join(", ", missing) + " - " + bots.Count + " of " + want + " bots came");
                else if (bots.Count != want) problems.Add("wave " + (w + 1) + ": " + bots.Count + " bots, not " + want);
                if (bots.Count > 0 && alive == 0 && w == 0) problems.Add("the plugin counts no bot alive (it wants a working beacon and remote control): " + Controls(bots[0]));
                if (distances.Any(d => Math.Abs(d - spawnDistance) > 1000)) problems.Add("wave " + (w + 1) + ": bots " + distances.Min().ToString("F0") + "-" + distances.Max().ToString("F0") + " m off, not " + spawnDistance);

                // a wave going on must not come again on the next pass
                if (w == 0 && bots.Count > 0)
                {
                    Pass();
                    yield return WaitForSeconds(12, "a pass with the wave going on");
                    var again = NewBots();
                    var extra = again.Count - bots.Count;
                    line += "; a pass with it going on: " + (extra > 0 ? "+" + extra + " BOTS (spawned again)" : "nothing new");
                    if (extra > 0) problems.Add("wave 1 spawned again on a pass while going on: " + bots.Count + " -> " + again.Count + " bots");
                    foreach (var b in again) _bots.Add(b.EntityId);
                    bots = again;
                }

                // the first bot dies: the wave is over only with the last one
                if (bots.Count > 1)
                {
                    Kill(bots[0]);
                    yield return WaitForSeconds(2, "the first bot dies");
                    if ((bool)destroyedFlag.GetValue(null))
                    {
                        line += "; wave over after 1 of " + bots.Count + " bots";
                        problems.Add("wave " + (w + 1) + " counted destroyed after the first of " + bots.Count + " bots");
                    }
                }
                foreach (var bot in bots) Kill(bot);
                yield return WaitForSeconds(3, "the bots die");
                var destroyed = (bool)destroyedFlag.GetValue(null);
                line += "; all killed, wave destroyed " + destroyed;
                if (!destroyed)
                {
                    problems.Add("wave " + (w + 1) + ": not counted destroyed" + (bots.Count == 0 ? " (no bot came: the sector waits forever)" : ""));
                    destroyedFlag.SetValue(null, true);
                }

                // the reward, on the next pass (which spawns the next wave too)
                var reward = Parse((string)P(wave, "RewardWave"));
                ((IMyPlayer)player).TryGetBalanceInfo(out var balanceBefore);
                var itemsBefore = Items(_ship);
                Pass();
                yield return WaitForSeconds(2, "the reward");
                ((IMyPlayer)player).TryGetBalanceInfo(out var balanceAfter);
                var itemsAfter = Items(_ship);
                var got = new List<string>();
                foreach (var r in reward)
                {
                    long delta;
                    if (r.Key.Contains("Credit")) delta = balanceAfter - balanceBefore;
                    else delta = (long)((itemsAfter.TryGetValue(r.Key, out var a) ? a : 0) - (itemsBefore.TryGetValue(r.Key, out var b) ? b : 0));
                    got.Add(r.Key + " " + delta + "/" + r.Value);
                    if (delta != r.Value) problems.Add("wave " + (w + 1) + " reward " + r.Key + ": " + delta + ", not " + r.Value);
                }
                Note(line + "; reward " + string.Join(", ", got));

                // the dead go before the next wave is counted
                yield return WaitForSeconds(6, "the explosions");
                foreach (var id in _bots)
                    if (MyEntities.GetEntityById(id) is MyCubeGrid dead && !dead.MarkedForClose) dead.Close();
                _seen.UnionWith(_bots);
            }

            yield return WaitForSeconds(2, "the ship goes");
            var fromGarbage = Vector3D.Distance(_ship.PositionComp.GetPosition(), garbage);
            Note("after the last wave the ship is " + (fromGarbage / 1000).ToString("F1") + " km from GarbageCenter, " +
                 (Vector3D.Distance(_ship.PositionComp.GetPosition(), centre) / 1000).ToString("F1") + " km from the sector");
            if (fromGarbage > 52000) problems.Add("the ship was not taken to GarbageCenter after the last wave: " + (fromGarbage / 1000).ToString("F0") + " km off it");

            Note("ADV SECTOR RESULT | " + waves.Count + " waves; " + (problems.Count == 0 ? "all fine" : problems.Count + " problems: " + string.Join(" | ", problems)));
            Check(problems.Count == 0, problems.Count + " problems: " + string.Join(" | ", problems));
        }

        private readonly HashSet<long> _seen = new HashSet<long>();

        /// <summary>The faction's dynamic grids in the sector (the plugin's own list).</summary>
        private IEnumerable<MyCubeGrid> SectorBots() =>
            ((IEnumerable)_behaviorType.GetMethod("GetNpcGridsInSector", Any).Invoke(null, null)).Cast<MyCubeGrid>().Where(g => !g.MarkedForClose);

        /// <summary>The faction's grids of this wave: in the sector, not seen in a wave before, with a working beacon.</summary>
        private List<MyCubeGrid> NewBots() =>
            SectorBots().Where(g => !_seen.Contains(g.EntityId) && g.GetFatBlocks().Any(b => b is Sandbox.Game.Entities.Cube.MyBeacon && b.IsWorking)).ToList();

        private static string Controls(MyCubeGrid bot) =>
            "«" + bot.DisplayName + "»: beacons " + bot.GetFatBlocks().Count(b => b is Sandbox.Game.Entities.Cube.MyBeacon) +
            ", remote controls " + bot.GetFatBlocks().Count(b => b is Sandbox.Game.Entities.MyRemoteControl) +
            ", AI blocks " + string.Join("/", bot.GetFatBlocks().Select(b => b.BlockDefinition.Id.TypeId.ToString().Replace("MyObjectBuilder_", ""))
                .Where(n => n.Contains("Combat") || n.Contains("FlightMovement")).Distinct());

        private void Pass() => _behaviorType.GetMethod("CheckPlayerGrids").Invoke(_behavior, null);

        private List<MyCubeGrid> AliveBots()
        {
            var all = (IEnumerable)_behaviorType.GetMethod("GetNpcGridsInSector", Any).Invoke(null, null);
            var alive = _behaviorType.GetMethod("IsNpcGridAlive", Any);
            return all.Cast<MyCubeGrid>().Where(g => !g.MarkedForClose && (bool)alive.Invoke(null, new object[] { g })).ToList();
        }

        private static void Kill(MyCubeGrid bot)
        {
            foreach (var block in bot.GetFatBlocks().Where(b => b is Sandbox.Game.Entities.Cube.MyBeacon).ToList())
                ((IMySlimBlock)block.SlimBlock).DoDamage(block.SlimBlock.MaxIntegrity * 5, MyDamageType.Explosion, true);
        }

        /// <summary>"a.sbc=1;b=2" or "Type_Subtype=n;...": the keys and their counts, as the plugin reads them.</summary>
        private static Dictionary<string, long> Parse(string text)
        {
            var result = new Dictionary<string, long>();
            foreach (var part in (text ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2 || !long.TryParse(kv[1].Trim(), out var n)) continue;
                var key = kv[0].Trim();
                result[key] = (result.TryGetValue(key, out var was) ? was : 0) + n;
            }
            return result;
        }

        /// <summary>The items in the ship by Type_Subtype (the type without MyObjectBuilder_).</summary>
        private static Dictionary<string, double> Items(MyCubeGrid grid)
        {
            var result = new Dictionary<string, double>();
            foreach (var block in grid.GetFatBlocks().Where(b => b.HasInventory))
            for (var i = 0; i < block.InventoryCount; i++)
            foreach (var item in block.GetInventory(i).GetItems())
            {
                var id = item.Content.GetObjectId();
                var key = id.TypeId.ToString().Replace("MyObjectBuilder_", "") + "_" + id.SubtypeName;
                result[key] = (result.TryGetValue(key, out var was) ? was : 0) + (double)item.Amount;
            }
            return result;
        }

        /// <summary>A heavy armour box over the minimum, a main cockpit, batteries and two large containers on top.</summary>
        private MyCubeGrid BuildShip(Vector3D at, long owner, int minBlocks)
        {
            var side = 2;
            while (side * side * side <= minBlocks + 10) side++;
            var blocks = new List<BlockSpec>();
            for (var x = 0; x < side; x++)
            for (var y = 0; y < side; y++)
            for (var z = 0; z < side; z++)
                blocks.Add(new BlockSpec("LargeHeavyBlockArmorBlock", new Vector3I(x, y, z)));
            blocks.Add(new BlockSpec("LargeBlockCockpitSeat", new Vector3I(0, side, 0)));
            for (var k = 0; k < 3; k++) blocks.Add(new BlockSpec("LargeBlockBatteryBlock", new Vector3I(1 + k, side, 0)));
            blocks.Add(new BlockSpec("LargeBlockLargeContainer", new Vector3I(0, side, 2)));
            blocks.Add(new BlockSpec("LargeBlockLargeContainer", new Vector3I(3, side, 2)));
            var grid = WorldApi.SpawnGrid(WorldApi.GridOb(WorldApi.EntityPrefix + "adv-sector-ship", MyCubeSize.Large, false, at, blocks));
            Track(grid);
            grid.ChangeGridOwnership(owner, MyOwnershipShareModeEnum.Faction);
            foreach (var b in grid.GetFatBlocks())
            {
                if (b is Sandbox.Game.Entities.MyBatteryBlock battery) battery.CurrentStoredPower = battery.MaxStoredPower;
                if (b is Sandbox.ModAPI.IMyShipController controller) controller.IsMainCockpit = true;
            }
            return grid;
        }

        private static void OnDamage(object target, ref MyDamageInformation info)
        {
            long id = 0;
            if (target is IMySlimBlock slim) id = slim.CubeGrid.EntityId;
            else if (target is VRage.ModAPI.IMyEntity e) id = e.EntityId;
            lock (Invulnerable)
                if (id != 0 && Invulnerable.Contains(id)) info.Amount = 0;
        }

        private static object P(object o, string name) => o.GetType().GetProperty(name, Any).GetValue(o);
        private static string Fmt(Vector3D v) => v.X.ToString("F0") + ":" + v.Y.ToString("F0") + ":" + v.Z.ToString("F0");

        public override void Cleanup()
        {
            try
            {
                lock (Invulnerable) Invulnerable.Clear();
                foreach (var id in _bots)
                    if (MyEntities.GetEntityById(id) is MyCubeGrid g && !g.MarkedForClose) g.Close();
                _bots.Clear();
                if (_ship != null && !_ship.MarkedForClose) _ship.Close();
                try { _behaviorType?.GetMethod("UpdateConfig").Invoke(_behavior, null); }
                catch (Exception e) { Log.Error(e, ScenarioName + ": resetting the sector"); }
                FakeClients.RemoveAll();
                _gameplay.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
