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
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Controlled identity-map fixture, NOT a production NPC spawn: only this scenario's second
    /// fake identity is temporarily mapped to a reused Steam id with serial 17. Actual Watcher
    /// IsPlayer, inventory sweep/Balance and persisted WebData paths remain unmodified.
    /// </summary>
    public sealed class WatcherNpcLedgerScenario : TestScenario
    {
        public const string ScenarioName = "watcher_npc_ledger";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MyDefinitionId Medkit = new MyDefinitionId(typeof(MyObjectBuilder_ConsumableItem), "Medkit");
        private static readonly MyDefinitionId Credit = new MyDefinitionId(typeof(MyObjectBuilder_PhysicalObject), "SpaceCredit");
        private IDictionary _identityMap;
        private long _fixtureIdentity;
        private object _originalMapping;
        private bool _mappingSaved;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;
        public override string[] Requires => new[] { Integrations.Watcher };

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisWatcher");
            SkipUnless(assembly != null, "SentisWatcher is not loaded");
            var pluginType = assembly.GetType("SentisWatcher.SentisWatcherPlugin");
            var plugin = pluginType.GetProperty("Instance").GetValue(null);
            var store = pluginType.GetProperty("Store").GetValue(plugin);
            var sweep = pluginType.GetProperty("Sweep").GetValue(plugin);
            Check(store != null && sweep != null, "Watcher store/sweep unavailable");
            Check(assembly.GetType("SentisWatcher.Ledger.InventoryLedger").GetField("Current").GetValue(null) != null,
                "Watcher ledger hooks are off");
            var data = Activator.CreateInstance(assembly.GetType("SentisWatcher.Storage.WebData"), store);
            var clock = assembly.GetType("SentisWatcher.Storage.Clock");
            long Now() => (long)clock.GetProperty("Now").GetValue(null);
            var from = Now() - 1000;
            var predicate = assembly.GetType("SentisWatcher.Recording.Identities")
                .GetMethod("IsPlayer", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(long) }, null);
            Check(predicate != null, "Watcher production IsPlayer unavailable");
            bool IsPlayer(long id) => (bool)predicate.Invoke(null, new object[] { id });

            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + "npc-ledger-anchor");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(planet != null, "no planet for isolated character fixture");
            var up = Vector3D.Normalize(anchor.Translation - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var centre = anchor.Translation + up * 1000 - side * 3700 + Shift;
            Check(FakeClients.Count == 0, "scenario requires no pre-existing fake clients");
            try
            {
                FakeClients.Add(2, new FakeClients.NetworkProfile { RttMs = 50 },
                    i => (centre + side * (i * 15) + up * 10, 0, 0), withCharacters: true);
                var arrive = WaitForSeconds(5, "two fixture clients arrive");
                while (arrive.MoveNext()) yield return arrive.Current;
                var primary = FakeClients.Character(0);
                var npc = FakeClients.Character(1);
                Check(primary != null && npc != null, "fixture characters missing");
                var primaryIdentity = primary.GetPlayerIdentityId();
                _fixtureIdentity = npc.GetPlayerIdentityId();
                var players = MySession.Static.Players;
                Check(primaryIdentity != 0 && _fixtureIdentity != 0 && primaryIdentity != _fixtureIdentity,
                    "fixture identities must be distinct and nonzero");
                var primaryId = FakeClients.PlayerOf(0).Id;
                Check(primaryId.SteamId != 0 && primaryId.SerialId == 0, "primary fixture is not serial zero");
                Check(players.TryGetPlayerId(_fixtureIdentity, out var original), "fixture identity has no mapping");
                _identityMap = typeof(Sandbox.Game.Multiplayer.MyPlayerCollection)
                    .GetField("m_identityPlayerIds", Any)?.GetValue(players) as IDictionary;
                Check(_identityMap != null && _identityMap.Contains(_fixtureIdentity), "identity-map fixture unavailable");
                _originalMapping = _identityMap[_fixtureIdentity];
                Check(_originalMapping.Equals(original), "identity-map lookup disagrees");
                _mappingSaved = true;
                // Change ONLY our synthetic identity's reverse mapping, never player objects/NPC flags.
                _identityMap[_fixtureIdentity] = new MyPlayer.PlayerId(primaryId.SteamId, 17);
                Check(!players.IdentityIsNpc(_fixtureIdentity), "fixture must remain unmarked by IdentityIsNpc");
                Check(players.TryGetSteamId(_fixtureIdentity) == primaryId.SteamId,
                    "serial fixture must reproduce nonzero reused Steam id");
                Check(players.TryGetPlayerId(_fixtureIdentity, out var serial) && serial.SerialId == 17,
                    "serial 17 mapping did not take effect");
                Check(npc.GetPlayerIdentityId() == _fixtureIdentity && primary.GetPlayerIdentityId() == primaryIdentity,
                    "controlled identity-map fixture changed character attribution");
                var npcClassified = IsPlayer(_fixtureIdentity);
                var primaryClassified = IsPlayer(primaryIdentity);
                Note($"controlled identity-map fixture (not production spawn): identity={_fixtureIdentity}, serial=17, " +
                     $"IdentityIsNpc=false, SteamId!=0, production IsPlayer={npcClassified}; primary={primaryClassified}");

                var npcPocket = (MyInventory)npc.GetInventory(0);
                var primaryPocket = (MyInventory)primary.GetInventory(0);
                // Seed through actual hooked AddItems; settle before introducing unhooked gains.
                foreach (var pocket in new[] { npcPocket, primaryPocket })
                {
                    pocket.AddItems(1, new MyObjectBuilder_ConsumableItem { SubtypeName = "Medkit" });
                    pocket.AddItems(10, new MyObjectBuilder_PhysicalObject { SubtypeName = "SpaceCredit" });
                    Check(pocket.GetItemAmount(Medkit) >= 1 && pocket.GetItemAmount(Credit) >= 10,
                        "hooked pocket baseline missing");
                }
                var pass = Pass(sweep);
                while (pass.MoveNext()) yield return pass.Current;
                var flush = WaitForSeconds(3, "baseline database writes flush");
                while (flush.MoveNext()) yield return flush.Current;
                var beforeNpc = Sources(data, npc.EntityId, from, Now() + 1000);
                var beforePrimary = Sources(data, primary.EntityId, from, Now() + 1000);
                var beforeAlerts = Alerts(data, from, Now() + 1000);
                Check(!Relevant(beforeAlerts, npc.EntityId, _fixtureIdentity).Any() &&
                      !Relevant(beforeAlerts, primary.EntityId, primaryIdentity).Any(),
                    "baseline alerts would invalidate the fresh-entity alert-limiter control");

                Gain(npcPocket, Medkit, 1);
                Gain(npcPocket, Credit, 283);
                Gain(primaryPocket, Medkit, 1);
                Gain(primaryPocket, Credit, 283);
                pass = Pass(sweep);
                while (pass.MoveNext()) yield return pass.Current;
                flush = WaitForSeconds(3, "NPC and primary database writes flush");
                while (flush.MoveNext()) yield return flush.Current;
                Check(npc.GetPlayerIdentityId() == _fixtureIdentity, "NPC attribution changed during sweep");
                var afterNpc = Sources(data, npc.EntityId, from, Now() + 1000);
                var afterPrimary = Sources(data, primary.EntityId, from, Now() + 1000);
                var firstAlerts = Alerts(data, from, Now() + 1000);
                var npcAlerts = Relevant(firstAlerts, npc.EntityId, _fixtureIdentity).ToList();
                var realAlerts = Relevant(firstAlerts, primary.EntityId, primaryIdentity).ToList();
                var npcBooked = Delta(afterNpc, beforeNpc, "ConsumableItem/Medkit") == 1 &&
                                Delta(afterNpc, beforeNpc, "PhysicalObject/SpaceCredit") == 283;
                var realBooked = Delta(afterPrimary, beforePrimary, "ConsumableItem/Medkit") == 1 &&
                                 Delta(afterPrimary, beforePrimary, "PhysicalObject/SpaceCredit") == 283;
                Result($"NPC serial17: IsPlayer={npcClassified}, unexplained Medkit={Delta(afterNpc, beforeNpc, "ConsumableItem/Medkit")}, " +
                       $"SpaceCredit={Delta(afterNpc, beforeNpc, "PhysicalObject/SpaceCredit")}, bypass/unknown={npcAlerts.Count}; " +
                       $"primary: IsPlayer={primaryClassified}, booked={realBooked}, bypass={realAlerts.Count(a => (string)a["kind"] == "bypass")}");

                // Same identity AND entity become primary again; no baseline reset or limiter edits.
                // In GREEN no earlier NPC bypass exists, so its entity's limiter is still unused.
                _identityMap[_fixtureIdentity] = original;
                Check(original.SerialId == 0 && original.SteamId != 0, "original fixture mapping is not primary");
                Check(players.TryGetPlayerId(_fixtureIdentity, out var restored) && restored.Equals(original),
                    "primary lifecycle mapping not restored");
                Check(npc.GetPlayerIdentityId() == _fixtureIdentity, "lifecycle changed entity attribution");
                var lifecycleClassified = IsPlayer(_fixtureIdentity);
                Gain(npcPocket, Medkit, 1);
                Gain(npcPocket, Credit, 283);
                pass = Pass(sweep);
                while (pass.MoveNext()) yield return pass.Current;
                flush = WaitForSeconds(3, "lifecycle database writes flush");
                while (flush.MoveNext()) yield return flush.Current;
                var finalSources = Sources(data, npc.EntityId, from, Now() + 1000);
                var finalAlerts = Relevant(Alerts(data, from, Now() + 1000), npc.EntityId, _fixtureIdentity).ToList();
                var lifecycleAlert = finalAlerts.Count(a => (string)a["kind"] == "bypass") >
                                     npcAlerts.Count(a => (string)a["kind"] == "bypass");
                var lifecycleBooked = Delta(finalSources, afterNpc, "ConsumableItem/Medkit") == 1 &&
                                      Delta(finalSources, afterNpc, "PhysicalObject/SpaceCredit") == 283;
                Result($"NPC-to-primary same entity: IsPlayer={lifecycleClassified}, booked={lifecycleBooked}, " +
                       $"new bypass={lifecycleAlert}, limiter initially unused={npcAlerts.Count == 0}");
                // Delay verdict until all legs have reported, including on the old RED Watcher.
                Check(npcBooked, "NPC unknown positive gains were not persisted as unexplained (+1 medkit, +283 credits)");
                Check(realBooked && primaryClassified && realAlerts.Any(a => (string)a["kind"] == "bypass"),
                    "genuine primary inventory bypass did not retain its ledger and alarm");
                Check(!npcClassified && npcAlerts.Count == 0,
                    "unmarked serial17 NPC with reused nonzero Steam id was classified/alerted as a player");
                Check(lifecycleClassified && lifecycleBooked && lifecycleAlert,
                    "same entity's primary lifecycle did not restore bypass alarms and ledger recording");
                Result("all legs passed: NPC gains ledger-recorded without alarms; primary and NPC-to-primary bypass alarms preserved");
            }
            finally { RestoreMapping(); }
        }

        private void Result(string message) => Note(message);

        private static void Gain(MyInventory inventory, MyDefinitionId item, int amount)
        {
            var items = inventory.GetItems();
            var index = items.FindIndex(x => x.Content.GetId() == item);
            Check(index >= 0, "direct mutation baseline item missing: " + item);
            var before = inventory.GetItemAmount(item);
            var stack = items[index];
            stack.Amount += amount;
            items[index] = stack; // deliberately bypass every AddItems/content-change hook
            Check(inventory.GetItemAmount(item) == before + amount, "direct pocket mutation failed: " + item);
        }

        private IEnumerator Pass(object sweep)
        {
            var busy = sweep.GetType().GetProperty("Busy");
            var idle = Wait(() => !(bool)busy.GetValue(sweep), "previous inventory sweep ends", 30);
            while (idle.MoveNext()) yield return idle.Current;
            sweep.GetType().GetMethod("StartNow").Invoke(sweep, null);
            yield return null;
            yield return null;
            var done = Wait(() => !(bool)busy.GetValue(sweep), "inventory sweep ends", 30);
            while (done.MoveNext()) yield return done.Current;
        }

        private static Dictionary<string, Dictionary<string, double>> Sources(object data, long entity, long from, long to)
        {
            var ledger = (Dictionary<string, object>)data.GetType().GetMethod("Ledger")
                .Invoke(data, new object[] { "entity", entity, from, to });
            return (Dictionary<string, Dictionary<string, double>>)ledger["sources"];
        }

        private static double Delta(Dictionary<string, Dictionary<string, double>> after,
            Dictionary<string, Dictionary<string, double>> before, string item) => Unexplained(after, item) - Unexplained(before, item);

        private static double Unexplained(Dictionary<string, Dictionary<string, double>> sources, string item) =>
            sources.TryGetValue(item, out var kinds) && kinds.TryGetValue("unexplained", out var amount) ? amount : 0;

        private static List<Dictionary<string, object>> Alerts(object data, long from, long to) =>
            (List<Dictionary<string, object>>)((Dictionary<string, object>)data.GetType().GetMethod("Anomalies")
                .Invoke(data, new object[] { from, to }))["alerts"];

        private static IEnumerable<Dictionary<string, object>> Relevant(IEnumerable<Dictionary<string, object>> alerts, long entity, long actor) =>
            alerts.Where(a => a["entity"] as string == entity.ToString() && a["actor"] as string == actor.ToString() &&
                              ((string)a["kind"] == "bypass" || (string)a["kind"] == "unknown_source"));

        private void RestoreMapping()
        {
            if (!_mappingSaved) return;
            _identityMap[_fixtureIdentity] = _originalMapping;
            _mappingSaved = false;
        }

        public override void Cleanup()
        {
            try
            {
                RestoreMapping(); // runner may abandon an iterator without disposing it
                FakeClients.RemoveAll();
            }
            finally { base.Cleanup(); }
        }
    }
}
