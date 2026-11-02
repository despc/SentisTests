using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using Torch.API.Managers;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Utils;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Real DoDamage -> DamageSystem -> loaded DeathCounter handler -> outgoing chat call.
    /// Grid/voxel cases inject the corresponding attacker entity; NOT a physics-collision test.
    /// No private DeathCounter damage method or formatting helper is invoked.
    /// Config and global counters are borrowed only for each synchronous game-thread DoDamage call.
    /// </summary>
    public sealed class DeathCounterChatScenario : TestScenario
    {
        public const string ScenarioName = "deathcounter_chat";
        public const string PluginName = "DeathCounter";
        private const string Prefix = "ST-DC-chat-";
        private const int ClientCount = 37;
        private static long _cancelVictim;
        private static int _canceled;
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static readonly string[] Templates = { "SuicideMessage", "LowPressureMessage", "BulletKillMessage", "GridRammingMessage", "VoxelCollisionMessage", "GeneralDamageMessage", "KillStreakMessage" };
        private static readonly Regex Tokens = new Regex(@"\{(killer|victim|damage|count|player)\}");
        private readonly Dictionary<string, object> _saved = new Dictionary<string, object>();
        private readonly List<ulong> _steam = new List<ulong>();
        private readonly List<MyEntity> _owned = new List<MyEntity>();
        private readonly Dictionary<string, object> _settings = new Dictionary<string, object>();
        private FakeClients.NetworkProfile _oldNetwork;
        private object _config, _core;
        private Type _coreType;
        private FieldInfo _last, _deathField, _streakField;
        private ConcurrentDictionary<ulong, int> _deaths, _streak;
        private ulong _testLast;
        private bool _stateSaved, _clientsStarted;
        private int _first, _kills, _cases, _messages;
        private PatchManager _patches;
        private PatchContext _patchContext;
        private List<Tuple<int, VRage.Game.ModAPI.BeforeDamageApplied>> _beforeHandlers;
        // Capture only the synchronous DoDamage call. Unrelated server broadcasts are not test output.
        [ThreadStatic] private static List<Sent> _capture;
        private struct Sent
        {
            public string Text, Author, Font;
            public long To;
            public override string ToString() => $"<{Author}> to={To} font={Font}: {Text}";
        }

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;
        public override string[] Requires => new[] { PluginName };
        // Opt out of the runner's blanket adoption of every newly appeared unowned grid.
        public override bool KeepsNewGrids => true;

        public override IEnumerator Run()
        {
            RequirePlugin(PluginName);
            SkipUnless(FakeClients.Count == 0, "other fake clients are active; refusing to remove or borrow them");
            // Configuration and counters are isolated/restored inside each synchronous damage call.
            _coreType = RequireType(Integrations.TypeOf(PluginName, "DeathCounter.Core"), "DeathCounter.Core");
            _core = _coreType.GetProperty("Instance", Any)?.GetValue(null);
            _config = _coreType.GetProperty("Config", Any)?.GetValue(_core);
            Check(_config != null, "DeathCounter has no live Config");
            var damage = RequireType(Integrations.TypeOf(PluginName, "DeathCounter.DamagePatch"), "DeathCounter.DamagePatch");
            Check(Equals(damage.GetField("_init", Any)?.GetValue(null), true), "DeathCounter did not register its Loaded DamageSystem handler");
            _deathField = damage.GetField("DeathCounter", Any);
            _streakField = damage.GetField("KillStreak", Any);
            Require<ConcurrentDictionary<ulong, int>>(_deathField?.GetValue(null), "death dictionary");
            Require<ConcurrentDictionary<ulong, int>>(_streakField?.GetValue(null), "streak dictionary");
            _deaths = new ConcurrentDictionary<ulong, int>();
            _streak = new ConcurrentDictionary<ulong, int>();
            _last = damage.GetField("_lastKiller", Any);
            Check(_last != null, "DeathCounter._lastKiller missing");
            _stateSaved = true;
            foreach (var name in Templates.Concat(new[] { "Enable", "AnnounceSuicide", "AnnounceCollisionDeath" })) _saved.Add(name, Get(name));
            Set("Enable", true);
            Set("AnnounceSuicide", true);
            Set("AnnounceCollisionDeath", true);
            InstallObserver();
            WorldApi.EnsureUnpaused(Name);

            _first = FakeClients.Count;
            _oldNetwork = FakeClients.Profile;
            _clientsStarted = true;
            // Every death has its own Steam player, identity and newly spawned character. No resurrection.
            try
            {
                FakeClients.Add(ClientCount, new FakeClients.NetworkProfile { RttMs = 50 },
                    i => (new Vector3D(650000, 650000 + i * 15, 650000) + Shift, 0, 0), withCharacters: true);
            }
            finally
            {
                for (var i = _first; i < FakeClients.Count; i++)
                {
                    var player = FakeClients.PlayerOf(i);
                    _steam.Add(player.Id.SteamId);
                    var body = FakeClients.Character(i);
                    if (body != null) Own(body);
                }
            }
            yield return Wait(() => _steam.Count == ClientCount && Enumerable.Range(_first, ClientCount).All(i => FakeClients.HasLiveCharacter(i)), ClientCount + " live synthetic player characters", 30);
            var killer = FakeClients.PlayerOf(_first);
            var killerSteam = killer.Id.SteamId;
            foreach (var steam in _steam) { _deaths.TryRemove(steam, out _); _streak.TryRemove(steam, out _); }
            _testLast = 0;
            Check(Enumerable.Range(_first, ClientCount).All(i => FakeClients.Character(i).CharacterCanDie), "the stand's synthetic characters cannot die (creative/invulnerability settings)");
            // Owned cube-block attribution and character attribution are exercised independently.
            var ob = WorldApi.GridOb(Prefix + "attacker", MyCubeSize.Large, true,
                new Vector3D(650100, 650000, 650000) + Shift,
                new[] { new BlockSpec("LargeBlockBatteryBlock", Vector3I.Zero) });
            foreach (var block in ob.CubeBlocks) { block.Owner = killer.Identity.IdentityId; block.BuiltBy = killer.Identity.IdentityId; }
            var grid = WorldApi.SpawnGrid(ob);
            Check(grid != null, "attacker grid did not spawn");
            Own(grid);
            yield return Wait(() => grid.BigOwners.Contains(killer.Identity.IdentityId), "attacker grid ownership", 10);
            var attackerBlock = grid.GetFatBlocks().FirstOrDefault();
            Check(attackerBlock != null && MyEntities.TryGetEntityById(attackerBlock.EntityId, out _), "owned attacker block absent from MyEntities");

            var storage = MyAPIGateway.Session.VoxelMaps.CreateStorage(new Vector3I(16));
            var data = new VRage.Voxels.MyStorageData();
            var min = new Vector3I(4); var max = new Vector3I(11);
            data.Resize(min, max);
            data.ClearContent(VRage.Voxels.MyVoxelConstants.VOXEL_CONTENT_FULL);
            data.ClearMaterials(Sandbox.Definitions.MyDefinitionManager.Static.GetVoxelMaterialDefinitions().First().Index);
            storage.WriteRange(data, VRage.Voxels.MyStorageDataTypeFlags.ContentAndMaterial, min, max);
            var voxel = MyAPIGateway.Session.VoxelMaps.CreateVoxelMap(Prefix + "rock", storage,
                new Vector3D(650200, 650000, 650000) + Shift, 0) as MyVoxelBase;
            Check(voxel != null, "owned voxel attacker did not spawn");
            Own(voxel);
            Check(MyEntities.TryGetEntityById(voxel.EntityId, out _), "voxel attacker absent from MyEntities");

            Note("event-pipeline tests: DoDamage with real owned block/grid/voxel attackers; not physical bullets or collisions");
            var baseline = Templates.Select((name, i) => Prefix + "A" + i + "|{killer}|{victim}|{damage}|{count}").ToArray();
            var edited = Templates.Select((name, i) => Prefix + "B" + i + "|{victim}|{count}|{damage}|{killer}").ToArray();
            var phases = new[] { baseline, edited, Templates.Select(_ => "").ToArray(), baseline };
            var labels = new[] { "baseline", "runtime edit", "empty suppression", "restore baseline" };
            for (var phase = 0; phase < phases.Length; phase++)
            {
                for (var t = 0; t < Templates.Length; t++) Set(Templates[t], phases[phase][t]);
                Check(ReferenceEquals(_config, _coreType.GetProperty("Config", Any).GetValue(_core)), "runtime edits replaced the Config object");
                for (var kind = 0; kind < 6; kind++)
                {
                    var index = _first + 1 + phase * 6 + kind;
                    var victim = FakeClients.Character(index);
                    Check(victim != null && victim.IsPlayer && !victim.IsBot && !victim.IsDead, "case victim is not a fresh live non-bot player");
                    var identity = FakeClients.PlayerOf(index).Identity;
                    Check(victim.GetPlayerIdentityId() == identity.IdentityId && MySession.Static.Players.TryGetSteamId(identity.IdentityId) == _steam[index - _first], "victim has no valid Steam identity");
                    Check(!_deaths.ContainsKey(_steam[index - _first]), "victim has pre-existing death count");
                    var type = kind == 0 ? MyDamageType.Suicide : kind == 1 ? MyStringHash.GetOrCompute("LowPressure") :
                        kind == 2 ? MyDamageType.Bullet : kind == 5 ? MyDamageType.Explosion : MyDamageType.Environment;
                    var attacker = kind < 2 ? 0 : kind == 3 ? grid.EntityId : kind == 4 ? voxel.EntityId : attackerBlock.EntityId;
                    var self = kind < 2 || kind == 4;
                    var expected = new List<string>();
                    AddExpected(expected, phases[phase][kind], self ? identity.DisplayName : killer.Identity.DisplayName, victim.DisplayName, type.String, 1);
                    if (!self)
                    {
                        _kills++;
                        if (_kills > 1) AddExpected(expected, phases[phase][6], killer.Identity.DisplayName, victim.DisplayName, type.String, _kills);
                    }
                    var sent = new List<Sent>();
                    Check(_capture == null, "chat observer already capturing");
                    _capture = sent;
                    try
                    {
                        WithTestState(() => victim.DoDamage(Math.Max(victim.Integrity, victim.StatComp.Health.Value) + 10000, type, true, attackerId: attacker));
                        _capture = null;
                        yield return Wait(() => victim.IsDead, labels[phase] + " " + Templates[kind] + " victim dead", 10);
                        yield return WaitForTicks(2); // production state is already restored before yielding
                    }
                    finally { _capture = null; }
                    var stamp = labels[phase] + "/" + Templates[kind];
                    Check(_deaths.TryGetValue(_steam[index - _first], out var deaths) && deaths == 1, stamp + ": death count != 1 (handler absent, duplicate or nonlethal event)");
                    Check(sent.Select(s => s.Text).SequenceEqual(expected), stamp + ": expected [" + string.Join(" / ", expected) + "] actual [" + string.Join(" / ", sent) + "]");
                    Check(sent.All(s => s.Author == "" && s.To == 0 && s.Font == "Blue"), stamp + ": wrong outgoing sender/recipient/font: " + string.Join(" / ", sent));
                    if (!self)
                    {
                        Check(_testLast == killerSteam, stamp + ": wrong last killer attribution");
                        if (_kills > 1) Check(_streak.TryGetValue(killerSteam, out var n) && n == _kills, stamp + ": wrong kill-streak count");
                    }
                    _cases++; _messages += sent.Count;
                    Note(stamp + ": dead=true deaths=1 chat=" + sent.Count + " | " + string.Join(" / ", sent.Select(s => s.Text)));
                    // Do not let the five-second corpse respawn timer create a different identity.
                    if (!victim.MarkedForClose) victim.Close();
                }
            }
            Check(_cases == 24 && _kills == 12, "six-branch totals mismatch");
            // No-attacker deaths must not silently disappear. The fallback oracle deliberately
            // does not prescribe a display name for the unknown killer.
            Set("GeneralDamageMessage", Prefix + "ENV|{victim}|{damage}|{count}");
            Set("LowPressureMessage", Prefix + "AIR|{victim}|{damage}|{count}");
            var extra = _first + 25;
            foreach (var environmental in new[] { "Temperature", "Asphyxia", "Fall", "Fire", "Hunger", "Radioactivity", "Unknown" })
            {
                var expected = new List<string>();
                var victim = FakeClients.Character(extra);
                var template = (string)Get(environmental == "Asphyxia" ? "LowPressureMessage" : "GeneralDamageMessage");
                AddExpected(expected, template, "", victim.DisplayName, environmental, 1);
                yield return ExtraDeath(extra++, MyStringHash.GetOrCompute(environmental), 0, expected, "environment/" + environmental);
            }
            Set("AnnounceSuicide", false);
            yield return ExtraDeath(extra++, MyDamageType.Suicide, 0, new List<string>(), "AnnounceSuicide=false");
            Set("AnnounceSuicide", true);
            Set("AnnounceCollisionDeath", false);
            yield return ExtraDeath(extra++, MyDamageType.Environment, grid.EntityId, new List<string>(), "AnnounceCollisionDeath=false/grid");
            yield return ExtraDeath(extra++, MyDamageType.Environment, voxel.EntityId, new List<string>(), "AnnounceCollisionDeath=false/voxel");
            Set("AnnounceCollisionDeath", true);

            // Cancel a lethal hit at the real DamageSystem boundary, after its before-handlers.
            // This must leave a live body and no counter/chat; the same body then really dies.
            var canceledVictim = FakeClients.Character(extra);
            var health = canceledVictim.StatComp.Health.Value;
            var canceledChat = new List<Sent>();
            _canceled = 0;
            _cancelVictim = canceledVictim.EntityId;
            _capture = canceledChat;
            try
            {
                WithTestState(() => canceledVictim.DoDamage(100000, MyDamageType.Bullet, true, attackerId: attackerBlock.EntityId));
                _capture = null;
                _cancelVictim = 0;
                yield return WaitForTicks(3);
            }
            finally { _cancelVictim = 0; _capture = null; }
            Check(_canceled == 1 && !canceledVictim.IsDead && canceledVictim.StatComp.Health.Value == health && canceledChat.Count == 0 && !_deaths.ContainsKey(_steam[extra - _first]),
                "canceled lethal hit changed health/death count/chat, or DamageSystem cancellation was not reached");
            Note("canceled lethal hit: BeforeDamage reached once; alive=true health unchanged deaths=0 chat=0");
            var afterCanceled = new List<string>();
            AddExpected(afterCanceled, (string)Get("GeneralDamageMessage"), "", canceledVictim.DisplayName, "Unknown", 1);
            yield return ExtraDeath(extra++, MyStringHash.GetOrCompute("Unknown"), 0, afterCanceled, "real death after canceled hit");

            // Direct character EntityId must resolve its real player's identity (not an identity ID masquerading as an entity).
            var characterExpected = new List<string>();
            var characterVictim = FakeClients.Character(extra);
            Set("BulletKillMessage", baseline[2]);
            // Suppress streak here: announcement-flag behavior may legitimately reset or retain the global streak.
            Set("KillStreakMessage", "");
            AddExpected(characterExpected, baseline[2], killer.Identity.DisplayName, characterVictim.DisplayName, MyDamageType.Bullet.String, 1);
            yield return ExtraDeath(extra++, MyDamageType.Bullet, FakeClients.Character(_first).EntityId, characterExpected, "character attacker attribution");
            Check(_testLast == killerSteam, "character attacker did not become last killer");
            Check(extra == _first + ClientCount && _cases == ClientCount - 1 && _deaths.Where(p => _steam.Contains(p.Key)).Sum(p => p.Value) == ClientCount - 1, "aggregate death count mismatch");
            Note("DEATHCOUNTER CHAT RESULT | " + _cases + " fresh-character deaths; " + _messages + " exact outgoing chat calls; six branches, seven no-attacker types, character attribution, announcement flags, canceled lethal hit; all seven templates edited/empty/restored on same Config; event pipeline only");
        }

        private IEnumerator ExtraDeath(int index, MyStringHash type, long attacker, List<string> expected, string stamp)
        {
            var victim = FakeClients.Character(index);
            var steam = _steam[index - _first];
            Check(victim != null && victim.IsPlayer && !victim.IsBot && !victim.IsDead && !_deaths.ContainsKey(steam), stamp + ": victim is not fresh");
            Check(MySession.Static.Players.TryGetSteamId(victim.GetPlayerIdentityId()) == steam, stamp + ": invalid victim identity");
            var sent = new List<Sent>();
            _capture = sent;
            try
            {
                WithTestState(() => victim.DoDamage(Math.Max(victim.Integrity, victim.StatComp.Health.Value) + 10000, type, true, attackerId: attacker));
                _capture = null;
                yield return Wait(() => victim.IsDead, stamp + " victim dead", 10);
                yield return WaitForTicks(2);
            }
            finally { _capture = null; }
            Check(_deaths.TryGetValue(steam, out var deaths) && deaths == 1, stamp + ": death count != 1");
            Check(sent.Select(s => s.Text).SequenceEqual(expected), stamp + ": expected [" + string.Join(" / ", expected) + "] actual [" + string.Join(" / ", sent) + "]");
            Check(sent.All(s => s.Author == "" && s.To == 0 && s.Font == "Blue"), stamp + ": wrong chat envelope");
            _cases++; _messages += sent.Count;
            Note(stamp + ": dead=true deaths=1 chat=" + sent.Count + " | " + string.Join(" / ", sent.Select(s => s.Text)));
            if (!victim.MarkedForClose) victim.Close();
        }

        private void Own(MyEntity entity) { _owned.Add(entity); Track(entity); }
        private object Get(string name) => _settings.TryGetValue(name, out var value) ? value : LiveGet(name);
        private object LiveGet(string name) => (_config.GetType().GetProperty(name, Any) ?? throw new MissingMemberException("DeathCounter.Config." + name)).GetValue(_config);
        private void LiveSet(string name, object value) => _config.GetType().GetProperty(name, Any).SetValue(_config, value);
        private void Set(string name, object value) => _settings[name] = value;

        private void WithTestState(Action damage)
        {
            Check(Sandbox.MySandboxGame.Static.UpdateThread == System.Threading.Thread.CurrentThread, "damage isolation requires game thread");
            Check(ReferenceEquals(_config, _coreType.GetProperty("Config", Any).GetValue(_core)), "live Config reference changed");
            var deaths = _deathField.GetValue(null);
            var streak = _streakField.GetValue(null);
            var last = _last.GetValue(null);
            var settings = _saved.Keys.ToDictionary(name => name, LiveGet);
            try
            {
                _deathField.SetValue(null, _deaths);
                _streakField.SetValue(null, _streak);
                _last.SetValue(null, _testLast);
                foreach (var pair in _settings) LiveSet(pair.Key, pair.Value);
                damage();
            }
            finally
            {
                _testLast = (ulong)_last.GetValue(null);
                _deathField.SetValue(null, deaths);
                _streakField.SetValue(null, streak);
                _last.SetValue(null, last);
                foreach (var pair in settings) LiveSet(pair.Key, pair.Value);
                Check(ReferenceEquals(_deathField.GetValue(null), deaths) && ReferenceEquals(_streakField.GetValue(null), streak) &&
                    Equals(_last.GetValue(null), last) && settings.All(p => Equals(LiveGet(p.Key), p.Value)), "synchronous production state restoration failed");
            }
        }
        private static void AddExpected(List<string> result, string template, string killer, string victim, string damage, int count)
        {
            // Independent oracle, never DeathCounter.MessageTemplates.Format.
            var text = Tokens.Replace(template ?? "", m => m.Groups[1].Value == "killer" ? killer : m.Groups[1].Value == "victim" ? victim :
                m.Groups[1].Value == "damage" ? damage : m.Groups[1].Value == "count" ? count.ToString(CultureInfo.InvariantCulture) : "").TrimEnd();
            if (text.Length != 0) result.Add(text);
        }
        private void InstallObserver()
        {
            _patches = SentisTestsPlugin.TorchInstance.Managers.GetManager<PatchManager>();
            Check(_patches != null, "Torch PatchManager missing");
            var target = typeof(MyVisualScriptLogicProvider).GetMethod("SendChatMessage", new[] { typeof(string), typeof(string), typeof(long), typeof(string) });
            Check(target != null, "SendChatMessage signature missing");
            _patchContext = _patches.AcquireContext();
            _patchContext.GetPattern(target).Prefixes.Add(typeof(DeathCounterChatScenario).GetMethod(nameof(ChatPrefix), Any));
            // Register a real handler: a runtime method patch can miss a previously inlined call.
            var system = Sandbox.Game.GameSystems.MyDamageSystem.Static;
            _beforeHandlers = (List<Tuple<int, VRage.Game.ModAPI.BeforeDamageApplied>>)typeof(Sandbox.Game.GameSystems.MyDamageSystem)
                .GetField("m_beforeDamageHandlers", Any).GetValue(system);
            system.RegisterBeforeDamageHandler(1000000, CancelSuffix);
            _patches.Commit();
        }
        private static void CancelSuffix(object target, ref VRage.Game.ModAPI.MyDamageInformation info)
        {
            if (_cancelVictim != 0 && target is MyCharacter body && body.EntityId == _cancelVictim)
            { info.Amount = 0; _canceled++; }
        }
        // Observer only: void prefix does not consume, replace, or suppress the actual chat call.
        private static void ChatPrefix(string message, string author, long playerId, string font)
        {
            _capture?.Add(new Sent { Text = message, Author = author, To = playerId, Font = font });
        }
        public override void CleanupLeftovers()
        {
            // Only exact owned instances, never proximity, generic FakeClient names or orphan heuristics.
            foreach (var entity in _owned) if (entity != null && !entity.MarkedForClose) entity.Close();
        }
        public override void Cleanup()
        {
            var errors = new List<string>();
            void Restore(Action action) { try { action(); } catch (Exception e) { errors.Add(e.ToString()); } }
            _capture = null;
            _cancelVictim = 0;
            Restore(() => { _beforeHandlers?.RemoveAll(h => h.Item2 == (VRage.Game.ModAPI.BeforeDamageApplied)CancelSuffix); });
            Restore(() => { if (_patchContext != null) { _patches.FreeContext(_patchContext); _patchContext = null; _patches.Commit(); } });
            // Every call restored its immediate live snapshot; never replay an old run-wide snapshot
            // over bot activity or operator edits that happened between ticks.
            _settings.Clear();
            Restore(CleanupLeftovers);
            if (_clientsStarted)
            {
                Restore(() => FakeClients.RemoveOwnedTail(_first, _steam));
                Restore(() => FakeClients.SetProfile(_oldNetwork));
            }
            // Private test dictionaries were never merged into production dictionaries.
            _deaths?.Clear();
            _streak?.Clear();
            Restore(base.Cleanup);
            Restore(() => Check(_owned.All(e => e == null || e.Closed || e.MarkedForClose), "an owned entity was not closed"));
            if (_clientsStarted) Restore(() => Check(FakeClients.Count == _first && !MySession.Static.Players.GetOnlinePlayers().Any(p => _steam.Contains(p.Id.SteamId)), "owned fake clients remain online"));
            if (errors.Count > 0) throw new InvalidOperationException("deathcounter_chat cleanup: " + string.Join(" | ", errors));
            if (_stateSaved) Note("cleanup verified: per-call live Config/counter references restored, owned clients/entities removed, private test counters discarded, chat observer freed; no config saved");
        }
    }
}
