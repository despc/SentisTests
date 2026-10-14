using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// SentisAnomalyZone end to end, in open space far from everything: a zone of 60 m captured in 8 s by players on foot
    /// of two factions at war (fake clients with characters).
    /// - the center cube stands: a static 5x5x5 of heavy armor around a beacon and a battery, not destructible, not
    ///   editable, a hit on it does nothing;
    /// - a player of A alone inside captures: A gets points, a reward structure appears near the center, its container
    ///   holds the rewards and belongs to the player;
    /// - a player of B comes in: the zone is contested;
    /// - B goes, A captures again: the first structure is gone and its contents are in the new one;
    /// - the new structure emptied: it goes away;
    /// - A's player dies inside: A loses a point;
    /// - the weekly rewards given by hand: a structure for the week's winner.
    /// The plugin is driven through SentisAnomalyZone.AnomalyZoneApi by reflection; its settings come back as they were.
    /// </summary>
    public sealed class AnomalyZoneScenario : TestScenario
    {
        public const string ScenarioName = "anomaly_zone";
        private const string ZoneId = "scenario";
        private const double Radius = 60;
        private const double CaptureSeconds = 8;
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly Dictionary<string, object> _settingsBefore = new Dictionary<string, object>();
        private readonly List<long> _factions = new List<long>();
        private Type _api;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 240;
        public override string[] Requires => new[] { Integrations.AnomalyZone };

        private object Call(string method, params object[] args)
        {
            var m = _api.GetMethod(method, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException("AnomalyZoneApi", method);
            return m.Invoke(null, args);
        }

        private void Setting(string name, object value) => _settingsBefore[name] = Call("Set", name, value);

        private long CenterGrid => (long)Call("CenterGrid", ZoneId);
        private long RewardGrid => (long)Call("RewardGrid", ZoneId);
        private int Captures => (int)Call("Captures", ZoneId);
        private string Status => (string)Call("Status", ZoneId) ?? "";
        private int Points(long faction) => (int)Call("Points", ZoneId, faction);

        public override IEnumerator Run()
        {
            _api = RequireType(Integrations.TypeOf(Integrations.AnomalyZone, "SentisAnomalyZone.AnomalyZoneApi"), "SentisAnomalyZone.AnomalyZoneApi");
            Check((bool)_api.GetProperty("Running").GetValue(null), "SentisAnomalyZone is not running");
            WorldApi.EnsureUnpaused(Name);

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + "az-anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var site = anchor + Vector3D.Normalize(new Vector3D(-0.6, 0.7, 0.4)) * 400000;
            var away = site + new Vector3D(400, 0, 0);

            Setting("CountOnFoot", true);
            Setting("PointsLostOnDeath", 1);
            Setting("SpawnRewards", true);
            Setting("CenterCube", true);
            Call("Forget", ZoneId);
            Call("PutZone", ZoneId, "Сценарий", site.X, site.Y, site.Z, Radius, CaptureSeconds);
            Note("zone of " + Radius + " m, capture " + CaptureSeconds + " s at " + site);

            FakeClients.Add(2, Network, i => (away + new Vector3D(0, i * 10, 0), 0, 0), withCharacters: true);
            var bodies = Wait(() => FakeClients.HasLiveCharacter(0) && FakeClients.HasLiveCharacter(1), "two players with bodies", 30);
            while (bodies.MoveNext()) yield return bodies.Current;
            var a = FakeClients.PlayerOf(0).Identity.IdentityId;
            var b = FakeClients.PlayerOf(1).Identity.IdentityId;

            // two factions at war, one player each
            var suffix = (DateTime.UtcNow.Ticks % 900 + 100).ToString();
            var factions = MySession.Static.Factions;
            ((IMyFactionCollection)factions).CreateFaction(a, "AZA" + suffix, "Anomaly A " + suffix, "", "");
            ((IMyFactionCollection)factions).CreateFaction(b, "AZB" + suffix, "Anomaly B " + suffix, "", "");
            var made = Wait(() => factions.GetPlayerFaction(a) != null && factions.GetPlayerFaction(b) != null, "the two factions made", 15);
            while (made.MoveNext()) yield return made.Current;
            var fa = factions.GetPlayerFaction(a).FactionId;
            var fb = factions.GetPlayerFaction(b).FactionId;
            _factions.Add(fa);
            _factions.Add(fb);
            MyFactionCollection.DeclareWar(fa, fb);
            MyFactionCollection.DeclareWar(fb, fa);
            var war = Wait(() => factions.AreFactionsEnemies(fa, fb), "the factions at war", 10);
            while (war.MoveNext()) yield return war.Current;

            // the center cube
            var cube = Wait(() => Grid(CenterGrid) != null, "the center cube made", 20);
            while (cube.MoveNext()) yield return cube.Current;
            var center = Grid(CenterGrid);
            Note($"center cube {center.EntityId}: {center.BlocksCount} blocks, static {center.IsStatic}, destructible {center.DestructibleBlocks}, editable {center.Editable}");
            Check(center.BlocksCount == 100, "the center cube has " + center.BlocksCount + " blocks, not 98 armor + beacon + battery");
            Check(center.IsStatic && !center.DestructibleBlocks && !center.Editable, "the center cube is not a static, indestructible, uneditable grid");
            Check(Vector3D.Distance(center.PositionComp.WorldAABB.Center, site) < 3, "the center cube is not at the zone's center");
            var slim = center.GetBlocks().First(s => s.FatBlock == null);
            var before = slim.Integrity;
            ((IMySlimBlock)slim).DoDamage(10000, MyDamageType.Bullet, true);
            yield return WaitForTicks(2);
            Check(!slim.IsDestroyed && slim.Integrity >= before, $"a hit on the center cube took it from {before} to {slim.Integrity}");

            // A alone captures
            FakeClients.MoveTo(0, site);
            var firstCapture = Wait(() => Points(fa) > 0, "A captures the zone", 30);
            while (firstCapture.MoveNext()) yield return firstCapture.Current;
            Note($"A: {Points(fa)} points, {Captures} captures, status {Status}");
            var reward1 = Wait(() => Grid(RewardGrid) != null, "the reward structure", 5);
            while (reward1.MoveNext()) yield return reward1.Current;
            var first = Grid(RewardGrid);
            var firstItems = Items(first);
            Note($"reward structure {first.EntityId}: {first.BlocksCount} blocks; {Describe(firstItems)}");
            Check(firstItems.Count > 0, "the reward structure's container is empty");
            var container = first.GetFatBlocks<MyCargoContainer>().First();
            Check(container.OwnerId == a, "the reward container belongs to " + container.OwnerId + ", not to the capturing player " + a);
            Check(!first.DestructibleBlocks && !first.Editable && first.IsStatic, "the reward structure is not static and protected");
            Check(first.GetFatBlocks<MyShipConnector>().Any(), "the reward structure has no connector");

            // B comes in: contested
            FakeClients.MoveTo(1, site + new Vector3D(5, 0, 0));
            var contested = Wait(() => Status.StartsWith("Contested"), "the zone contested with B inside", 10);
            while (contested.MoveNext()) yield return contested.Current;
            var capturesContested = Captures;
            var hold = WaitForSeconds(CaptureSeconds + 2, "no capture while contested");
            while (hold.MoveNext()) yield return hold.Current;
            Check(Captures == capturesContested, "a capture happened while the zone was contested");

            // B goes; A captures again; the first structure's contents move into the new one
            FakeClients.MoveTo(1, away + new Vector3D(0, 30, 0));
            var firstId = first.EntityId;
            var second = Wait(() => RewardGrid != 0 && RewardGrid != firstId, "A captures again, a new reward structure", 30);
            while (second.MoveNext()) yield return second.Current;
            yield return WaitForTicks(2);
            Check(Grid(firstId) == null, "the first reward structure is still there after the next capture");
            var secondGrid = Grid(RewardGrid);
            var secondItems = Items(secondGrid);
            Note("second structure: " + Describe(secondItems));
            foreach (var item in firstItems)
                Check(secondItems.TryGetValue(item.Key, out var now) && now >= item.Value,
                    $"{item.Key}: {item.Value} left in the first structure, {(secondItems.TryGetValue(item.Key, out var n) ? n : 0)} in the second");

            // emptied: it goes away (A out first, or it captures again meanwhile)
            FakeClients.MoveTo(0, away);
            secondGrid.GetFatBlocks<MyCargoContainer>().First().GetInventory().Clear();
            var gone = Wait(() => RewardGrid == 0 && Grid(secondGrid.EntityId) == null, "the emptied structure goes away", 10);
            while (gone.MoveNext()) yield return gone.Current;

            // a death inside costs a point
            FakeClients.MoveTo(0, site);
            yield return WaitForTicks(30);
            var pointsBefore = Points(fa);
            FakeClients.Character(0).DoDamage(100000, MyDamageType.Bullet, true);
            var died = Wait(() => Points(fa) == pointsBefore - 1, $"A loses a point for a death inside ({pointsBefore} before)", 10);
            while (died.MoveNext()) yield return died.Current;

            // the week by hand
            var weekly = (string)Call("GiveWeeklyNow");
            Note("weekly: " + weekly);
            var weeklyGrids = (long[])Call("WeeklyGrids");
            Check(weeklyGrids.Length >= 1 && weeklyGrids.Any(id => Grid(id) != null), "no weekly reward structure");
            foreach (var id in weeklyGrids) Track(Grid(id));
            Note("PASS: center cube, capture, reward structure, contest, carry-over, emptied structure, death, weekly");
        }

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
            items.Count == 0 ? "empty" : string.Join(", ", items.Select(i => i.Key + " x" + i.Value.ToString("0.##")));

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
                Log.Warn(e, "anomaly_zone: the zone's settings were not restored");
            }
            foreach (var faction in _factions) MyFactionCollection.RemoveFaction(faction);
            FakeClients.RemoveAll();
            base.Cleanup();
        }
    }
}
