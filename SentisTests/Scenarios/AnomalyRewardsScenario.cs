using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
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
    /// The rewards set in SentisAnomalyZone's settings, given on the stand (anomaly_zone tests the zone's own lines, this
    /// one the common ones as the server has them):
    /// - every item of the settings is an item of the game;
    /// - a zone without lines of its own captured by a player on foot (multiplier 1): the reward structure holds every
    ///   sure line (chance 1) of the common capture rewards, the others at most, nothing else; the faction got its credits;
    /// - three factions with points of the week: the weekly rewards give each place a structure with exactly its lines;
    /// - who may take: the capture's structure (0 minutes) is open to everyone at once; the weekly ones are the places'
    ///   factions' only, then open to everyone (their owners kept) once their minutes are up (6 s here).
    /// The plugin is driven through SentisAnomalyZone.AnomalyZoneApi by reflection; its settings come back as they were.
    /// </summary>
    public class AnomalyRewardsScenario : TestScenario
    {
        public const string ScenarioName = "anomaly_rewards";
        private const string ZoneId = "scenario-rewards";
        private const double Radius = 60;
        private const double CaptureSeconds = 8;
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly Dictionary<string, object> _settingsBefore = new Dictionary<string, object>();
        private readonly List<long> _factions = new List<long>();
        private Type _api;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;
        public override string[] Requires => new[] { Integrations.AnomalyZone };

        private object Call(string method, params object[] args)
        {
            var m = _api.GetMethod(method, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException("AnomalyZoneApi", method);
            return m.Invoke(null, args);
        }

        /// <summary>Sets a plugin setting; the value it had before the scenario is kept for the cleanup (the first one).</summary>
        private void Setting(string name, object value)
        {
            var old = Call("Set", name, value);
            if (!_settingsBefore.ContainsKey(name)) _settingsBefore[name] = old;
        }

        public override IEnumerator Run()
        {
            _api = RequireType(Integrations.TypeOf(Integrations.AnomalyZone, "SentisAnomalyZone.AnomalyZoneApi"), "SentisAnomalyZone.AnomalyZoneApi");
            Check((bool)_api.GetProperty("Running").GetValue(null), "SentisAnomalyZone is not running");
            WorldApi.EnsureUnpaused(Name);

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + "ar-anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var site = anchor + Vector3D.Normalize(new Vector3D(0.5, 0.6, -0.6)) * 400000;
            var away = site + new Vector3D(400, 0, 0);

            Setting("CountOnFoot", true);
            Setting("SpawnRewards", true);
            Setting("K1", 0.0); // the multiplier at its least, 1: the amounts as set
            Setting("PointsPerCapture", 0);
            Setting("CaptureRewardOpenMinutes", 0.0);
            Setting("WeeklyRewardOpenMinutes", 0.1);
            var config = Integrations.TypeOf(Integrations.AnomalyZone, "SentisAnomalyZone.SentisAnomalyZonePlugin").GetProperty("Config").GetValue(null);
            var credits = Convert.ToInt64(config.GetType().GetProperty("CreditsPerPoint").GetValue(config));
            Call("Forget", ZoneId);
            Call("PutZone", ZoneId, "Сценарий наград", site.X, site.Y, site.Z, Radius, CaptureSeconds);

            // the rewards this zone gets: the common lines of the server's settings
            var lines = (string[])Call("Rewards", ZoneId);
            Note("rewards: " + string.Join("; ", lines));
            var capture = lines.Where(l => l.StartsWith("capture|")).Select(l => l.Split('|')).ToList();
            var week = lines.Where(l => l.StartsWith("week|")).Select(l => l.Split('|')).ToList();
            Check(capture.Count > 0, "no capture rewards set");
            Check(week.Count > 0, "no weekly rewards set");
            foreach (var line in capture.Select(p => (p[1], p[2])).Concat(week.Select(p => (p[2], p[3]))))
                Check(MyDefinitionId.TryParse("MyObjectBuilder_" + line.Item1, line.Item2, out var id) && MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out _),
                    $"{line.Item1}/{line.Item2} is no item of the game");

            FakeClients.Add(3, Network, i => (away + new Vector3D(0, i * 10, 0), 0, 0), withCharacters: true);
            var bodies = Wait(() => Enumerable.Range(0, 3).All(FakeClients.HasLiveCharacter), "three players with bodies", 30);
            while (bodies.MoveNext()) yield return bodies.Current;
            var players = Enumerable.Range(0, 3).Select(i => FakeClients.PlayerOf(i).Identity.IdentityId).ToArray();

            // three factions, one player each
            var suffix = (DateTime.UtcNow.Ticks % 900 + 100).ToString();
            var factions = MySession.Static.Factions;
            for (var i = 0; i < 3; i++)
                ((IMyFactionCollection)factions).CreateFaction(players[i], "AR" + (i + 1) + suffix, "Anomaly rewards " + (i + 1) + " " + suffix, "", "");
            var made = Wait(() => players.All(p => factions.GetPlayerFaction(p) != null), "the three factions made", 15);
            while (made.MoveNext()) yield return made.Current;
            var ids = players.Select(p => factions.GetPlayerFaction(p).FactionId).ToArray();
            _factions.AddRange(ids);
            var first = factions.TryGetFactionById(ids[0]);
            ((IMyFaction)first).TryGetBalanceInfo(out var balanceBefore);

            // the first faction captures
            FakeClients.MoveTo(0, site);
            var captured = Wait(() => RewardGrid != 0 && Grid(RewardGrid) != null, "a capture and its reward structure", 30);
            while (captured.MoveNext()) yield return captured.Current;
            var items = Items(Grid(RewardGrid));
            Note("capture's structure: " + Describe(items));
            var sure = new Dictionary<string, double>();
            var most = new Dictionary<string, double>();
            foreach (var p in capture)
            {
                var key = "MyObjectBuilder_" + p[1] + "/" + p[2];
                var amount = Number(p[3]);
                var chance = Number(p[4]);
                var every = int.Parse(p[5], CultureInfo.InvariantCulture);
                if (every != 1) continue; // due at a later capture
                most[key] = (most.TryGetValue(key, out var m) ? m : 0) + amount;
                if (chance >= 1) sure[key] = (sure.TryGetValue(key, out var s) ? s : 0) + amount;
            }
            foreach (var item in sure)
                Check(items.TryGetValue(item.Key, out var got) && got >= item.Value - 0.01,
                    $"{item.Key}: {item.Value} set (chance 1), {(items.TryGetValue(item.Key, out var g) ? g : 0)} given");
            foreach (var item in items)
                Check(most.TryGetValue(item.Key, out var limit) && item.Value <= limit + 0.01,
                    $"{item.Key} x{item.Value} given, {(most.TryGetValue(item.Key, out var l) ? l : 0)} set at most");
            var points = (int)Call("Points", ZoneId, ids[0]);
            var balance = 0L;
            var paid = Wait(() => ((IMyFaction)first).TryGetBalanceInfo(out balance) && balance - balanceBefore >= points * credits, "the faction's credits", 10);
            while (paid.MoveNext()) yield return paid.Current;
            Note($"{points} points, the faction's balance {balanceBefore} -> {balance} ({credits} a point)");
            Check(balance - balanceBefore == points * credits, $"the faction got {balance - balanceBefore} credits, not {points * credits}");
            Check(Open(Grid(RewardGrid)), "the capture's structure (0 minutes) is not open to everyone");
            FakeClients.MoveTo(0, away);

            // the week: three places
            Call("AddPoints", ZoneId, ids[0], 30);
            Call("AddPoints", ZoneId, ids[1], 20);
            Call("AddPoints", ZoneId, ids[2], 10);
            var weeklyBefore = new HashSet<long>((long[])Call("WeeklyGrids"));
            Note("weekly: " + (string)Call("GiveWeeklyNow"));
            var weekly = ((long[])Call("WeeklyGrids")).Where(id => !weeklyBefore.Contains(id)).Select(Grid).Where(g => g != null).ToList();
            foreach (var grid in weekly) Track(grid);
            var places = week.Select(p => int.Parse(p[1], CultureInfo.InvariantCulture)).Distinct().OrderBy(p => p).ToList();
            foreach (var place in places.Where(p => p <= 3))
            {
                var founder = factions.TryGetFactionById(ids[place - 1]).FounderId;
                var grid = weekly.FirstOrDefault(g => g.GetFatBlocks<MyCargoContainer>().Any(c => c.OwnerId == founder));
                Check(grid != null, $"no weekly structure for place {place}");
                var got = Items(grid);
                Note($"place {place}: {Describe(got)}");
                var set = new Dictionary<string, double>();
                foreach (var p in week.Where(p => int.Parse(p[1], CultureInfo.InvariantCulture) == place))
                {
                    var key = "MyObjectBuilder_" + p[2] + "/" + p[3];
                    set[key] = (set.TryGetValue(key, out var s) ? s : 0) + Number(p[4]);
                }
                Check(set.Count == got.Count && set.All(i => got.TryGetValue(i.Key, out var n) && Math.Abs(n - i.Value) < 0.01),
                    $"place {place}: {Describe(set)} set, {Describe(got)} given");
            }

            // the weekly structures: the places' only, open to everyone after their minutes
            Check(weekly.Count > 0 && weekly.All(g => !Open(g)), "a weekly structure is open to everyone at once");
            var owners = weekly.ToDictionary(g => g.EntityId, g => g.GetFatBlocks<MyCargoContainer>().First().OwnerId);
            var opened = Wait(() => weekly.All(Open), "the weekly structures open to everyone after 6 s", 30);
            while (opened.MoveNext()) yield return opened.Current;
            Check(weekly.All(g => g.GetFatBlocks<MyCargoContainer>().First().OwnerId == owners[g.EntityId]), "a weekly structure's owner changed when it opened");

            Note($"PASS: {capture.Count} capture and {week.Count} weekly lines are items of the game; a capture gave the sure ones and credits, open to everyone; " +
                 $"places {string.Join(", ", places.Where(p => p <= 3))} got theirs, open to everyone after their minutes");
        }

        private long RewardGrid => (long)Call("RewardGrid", ZoneId);

        /// <summary>Every block of the structure shared with everyone.</summary>
        private static bool Open(MyCubeGrid grid) =>
            grid != null && grid.GetFatBlocks().All(b => b.IDModule == null || b.IDModule.ShareMode == MyOwnershipShareModeEnum.All);

        private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

        private static MyCubeGrid Grid(long id) =>
            id != 0 && MyEntities.TryGetEntityById(id, out MyCubeGrid grid) && !grid.MarkedForClose ? grid : null;

        private static Dictionary<string, double> Items(MyCubeGrid grid)
        {
            var result = new Dictionary<string, double>();
            var inventory = grid?.GetFatBlocks<MyCargoContainer>().FirstOrDefault()?.GetInventory();
            if (inventory == null) return result;
            foreach (var item in inventory.GetItems())
            {
                var key = item.Content.GetObjectId().ToString();
                result[key] = (result.TryGetValue(key, out var n) ? n : 0) + (double)item.Amount;
            }
            return result;
        }

        private static string Describe(Dictionary<string, double> items) =>
            items.Count == 0 ? "empty" : string.Join(", ", items.Select(i => i.Key.Replace("MyObjectBuilder_", "") + " x" + i.Value.ToString("0.##", CultureInfo.InvariantCulture)));

        public override void Cleanup()
        {
            try
            {
                if (_api != null)
                {
                    Call("Forget", ZoneId);
                    Call("RemoveZone", ZoneId);
                    foreach (var setting in _settingsBefore) Call("Set", setting.Key, setting.Value);
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "anomaly_rewards: the zone's settings were not restored");
            }
            foreach (var faction in _factions) MyFactionCollection.RemoveFaction(faction);
            FakeClients.RemoveAll();
            base.Cleanup();
        }
    }
}
