using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game;
using Sandbox.Game.AI;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using Torch.API.Managers;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The DeathCounter bot gate against a real SentisAi bot on the live stand.
    /// Bots are classified by the synthetic Steam id range of SentisAi.Net.BotConnections
    /// (0x5E17_A100_0000_0000 + 256), exactly what DamagePatch.IsVirtualClient gates on,
    /// never by display-name heuristics.
    /// Case A (CountBots=false): a real lethal DoDamage must neither count nor announce.
    /// Case B (CountBots=true): after the bot respawns, the same lethal hit must count and
    /// announce exactly one chat line. Config and counters are borrowed only per synchronous
    /// game-thread call and restored from the live snapshot, as in deathcounter_chat.
    /// </summary>
    public sealed class DeathCounterBotsScenario : TestScenario
    {
        public const string ScenarioName = "deathcounter_bots";
        public const string PluginName = "DeathCounter";
        private const ulong BotBaseId = 0x5E17_A100_0000_0000UL;
        private const int BotMax = 256;
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private readonly Dictionary<string, object> _saved = new Dictionary<string, object>();
        private readonly Dictionary<string, object> _settings = new Dictionary<string, object>();
        private object _config, _core;
        private Type _coreType, _damage;
        private FieldInfo _last, _deathField, _streakField;
        private ConcurrentDictionary<ulong, int> _deaths, _streak;
        private ulong _testLast;
        private bool _stateSaved;
        private PatchManager _patches;
        private PatchContext _patchContext;
        [ThreadStatic] private static List<string> _capture;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 720;
        public override string[] Requires => new[] { PluginName };

        public override IEnumerator Run()
        {
            RequirePlugin(PluginName);
            _coreType = RequireType(Integrations.TypeOf(PluginName, "DeathCounter.Core"), "DeathCounter.Core");
            _core = _coreType.GetProperty("Instance", Any)?.GetValue(null);
            _config = _coreType.GetProperty("Config", Any)?.GetValue(_core);
            Check(_config != null, "DeathCounter has no live Config");
            _damage = RequireType(Integrations.TypeOf(PluginName, "DeathCounter.DamagePatch"), "DeathCounter.DamagePatch");
            Check(Equals(_damage.GetField("_init", Any)?.GetValue(null), true), "DeathCounter did not register its Loaded handler");
            var isVirtual = _damage.GetMethod("IsVirtualClient");
            Check(isVirtual != null, "DeathCounter has no virtual-client classifier");
            _deathField = _damage.GetField("DeathCounter", Any);
            _streakField = _damage.GetField("KillStreak", Any);
            _last = _damage.GetField("_lastKiller", Any);
            Require<ConcurrentDictionary<ulong, int>>(_deathField?.GetValue(null), "death dictionary");
            Require<ConcurrentDictionary<ulong, int>>(_streakField?.GetValue(null), "streak dictionary");
            _deaths = new ConcurrentDictionary<ulong, int>();
            _streak = new ConcurrentDictionary<ulong, int>();
            _stateSaved = true;
            foreach (var name in new[] { "Enable", "CountBots", "GeneralDamageMessage", "BulletKillMessage", "KillStreakMessage" }) _saved.Add(name, LiveGet(name));
            Set("Enable", true);
            InstallObserver();
            WorldApi.EnsureUnpaused(Name);

            // The oracle range is checked against the plugin's own classifier for the very bot used.
            MyPlayer bot = null;
            yield return Wait(() =>
            {
                foreach (var player in MySession.Static.Players.GetOnlinePlayers())
                {
                    if (player.Id.SteamId < BotBaseId || player.Id.SteamId >= BotBaseId + BotMax) continue;
                    if (player.Character == null || player.Character.IsDead || !player.Character.CharacterCanDie) continue;
                    bot = player;
                    return true;
                }
                return false;
            }, "a live SentisAi bot with a killable body", 60);
            var steam = bot.Id.SteamId;
            Check(Equals(true, isVirtual.Invoke(null, new object[] { steam })), "the plugin does not classify the live bot " + bot.DisplayName + " as a virtual client");
            var name0 = bot.Character.DisplayName;
            Note($"target bot {bot.DisplayName} steam={steam:X} classified=virtual");

            // Case A: CountBots=false (the shipped default) — neither counted nor announced.
            Set("CountBots", false);
            Set("GeneralDamageMessage", "ST-DC-bots|{victim}|{damage}|{count}");
            var silent = new List<string>();
            _capture = silent;
            try
            {
                WithTestState(() => bot.Character.DoDamage(Math.Max(bot.Character.Integrity, bot.Character.StatComp.Health.Value) + 10000,
                    MyDamageType.Bullet, true, attackerId: 0));
                _capture = null;
                yield return Wait(() => bot.Character == null || bot.Character.IsDead, "case A: the bot body died", 20);
                yield return WaitForTicks(2);
            }
            finally { _capture = null; }
            Check(silent.Count == 0, "case A (CountBots=false): announced " + string.Join(" / ", silent));
            Check(!_deaths.ContainsKey(steam), "case A (CountBots=false): the bot death was counted");
            Note("case A: bot really died; chat=0 deaths=0");

            // The stand's SentisAi respawns its bots; wait for a fresh live body.
            yield return Wait(() =>
            {
                var fresh = MySession.Static.Players.GetOnlinePlayers().FirstOrDefault(p => p.Id.SteamId == steam);
                return fresh?.Character != null && !fresh.Character.IsDead && fresh.Character.CharacterCanDie;
            }, "the bot respawned with a fresh killable body", 180);

            // Case B: CountBots=true — the same hit now counts and announces exactly once.
            Set("CountBots", true);
            var body = MySession.Static.Players.GetOnlinePlayers().First(p => p.Id.SteamId == steam).Character;
            var announced = new List<string>();
            _capture = announced;
            try
            {
                WithTestState(() => body.DoDamage(Math.Max(body.Integrity, body.StatComp.Health.Value) + 10000,
                    MyDamageType.Bullet, true, attackerId: 0));
                _capture = null;
                yield return Wait(() => body.IsDead, "case B: the respawned bot body died", 20);
                yield return WaitForTicks(2);
            }
            finally { _capture = null; }
            Check(announced.Count == 1, "case B (CountBots=true): expected 1 chat line, got " + string.Join(" / ", announced));
            Check(_deaths.TryGetValue(steam, out var n) && n == 1, "case B (CountBots=true): death was not counted (got " + n + ")");
            Note("case B: respawned bot died; chat=" + announced.Count + " deaths=1 | " + string.Join(" / ", announced));

            // Case C: an animal (wolf/spider) dies through the same pipeline. Animals sit under
            // pseudo-players with SerialId > 0; the real-player gate must drop them entirely.
            // Prefer a world animal; if none is around, spawn a wolf through the game's OWN bot
            // spawner (MyAIComponent.SpawnNewBot) — that creates the same pseudo-player mapping
            // as natural wildlife, unlike a bare CreateFromObjectBuilder character.
            var spawnAnchor = body.PositionComp.GetPosition();
            MyCharacter FindAnimal() => MyEntities.GetEntities().OfType<MyCharacter>()
                .FirstOrDefault(c => !c.IsDead && !c.MarkedForClose && c.Physics != null && c.GetIdentity() != null &&
                    ((c.Definition?.Id.SubtypeName ?? "").Contains("Wolf") ||
                     (c.Definition?.Id.SubtypeName ?? "").Contains("Spider")));
            MyCharacter animal = FindAnimal();
            if (animal == null)
            {
                var agentDef = MyDefinitionManager.Static.GetAllDefinitions<MyAgentDefinition>()
                    .FirstOrDefault(d => d.Id.SubtypeName.Contains("Wolf"));
                Check(agentDef != null, "no wolf agent definition in the game");
                MyAIComponent.Static.SpawnNewBot(agentDef, spawnAnchor + Vector3D.Up * 2, createdByPlayer: false, isWildlifeAgent: true);
                yield return Wait(() => { animal = FindAnimal(); return animal != null; }, "case C: the spawned wolf appears with physics and identity", 30);
            }
            if (animal == null) Check(false, "case C: neither a world animal nor a spawned wolf is available; animal gate unproven");
            else
            {
                // Diagnostics: what does the animal identity actually map to?
                var aIdent = animal.GetIdentity().IdentityId;
                var aSteam = MySession.Static.Players.TryGetSteamId(aIdent);
                string pidInfo = "no-player";
                MyPlayer.PlayerId aPid;
                if (MySession.Static.Players.TryGetPlayerId(aIdent, out aPid))
                    pidInfo = "pid{steam=" + aPid.SteamId + ",serial=" + aPid.SerialId + "}";
                var aPlayer = MySession.Static.Players.TryGetPlayer(aIdent);
                var dump = "animal[" + animal.Definition?.Id.TypeId + "|" + animal.Definition?.Id.SubtypeName +
                    "] ident=" + aIdent + " steam=" + aSteam + " " + pidInfo +
                    " npc=" + MySession.Static.Players.IdentityIsNpc(aIdent) +
                    " charIsBot=" + animal.IsBot +
                    " playerNull=" + (aPlayer == null) +
                    (aPlayer != null ? " p.IsBot=" + (aPlayer.GetType().GetProperty("IsBot")?.GetValue(aPlayer) ?? aPlayer.GetType().GetField("IsBot")?.GetValue(aPlayer)) : "") +
                    " pName=" + (aPlayer?.DisplayName ?? "-");
                Note("case C diag: " + dump);
                Check(Equals(false, isVirtual.Invoke(null, new object[] { aSteam })), "animal classified as virtual client");
                var animalName = animal.DisplayName;
                var animalChat = new List<string>();
                Set("CountBots", false);
                _capture = animalChat;
                try
                {
                    WithTestState(() => animal.DoDamage(animal.Integrity + 10000, MyDamageType.Bullet, true, attackerId: 0));
                    _capture = null;
                    yield return Wait(() => animal.IsDead || animal.MarkedForClose, "case C: the animal died", 20);
                    yield return WaitForTicks(2);
                }
                finally { _capture = null; }
                Check(animalChat.Count == 0, "case C (animal victim): announced " + string.Join(" / ", animalChat));
                var animalSteam = MySession.Static.Players.TryGetSteamId(animal.GetIdentity()?.IdentityId ?? 0);
                Check(!_deaths.ContainsKey(animalSteam), "case C (animal victim): the animal death was counted");
                Note("case C: " + animalName + " really died; chat=0 deaths=0");
            }
            Note("DEATHCOUNTER BOTS RESULT | live SentisAi bot killed twice through the real damage pipeline; " +
                "CountBots=false: no count no chat; CountBots=true: counted and announced once; animal victim: no count no chat; " +
                "config and counters restored per call");
        }

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

        private void InstallObserver()
        {
            _patches = SentisTestsPlugin.TorchInstance.Managers.GetManager<PatchManager>();
            Check(_patches != null, "Torch PatchManager missing");
            var target = typeof(MyVisualScriptLogicProvider).GetMethod("SendChatMessage", new[] { typeof(string), typeof(string), typeof(long), typeof(string) });
            Check(target != null, "SendChatMessage signature missing");
            _patchContext = _patches.AcquireContext();
            _patchContext.GetPattern(target).Prefixes.Add(typeof(DeathCounterBotsScenario).GetMethod(nameof(ChatPrefix), Any));
            _patches.Commit();
        }
        // Observer only: the void prefix neither consumes nor suppresses the real chat call.
        private static void ChatPrefix(string message, string author, long playerId, string font) => _capture?.Add(message);

        public override void Cleanup()
        {
            var errors = new List<string>();
            void Restore(Action action) { try { action(); } catch (Exception e) { errors.Add(e.ToString()); } }
            _capture = null;
            _settings.Clear();
            Restore(() => { if (_patchContext != null) { _patches.FreeContext(_patchContext); _patchContext = null; _patches.Commit(); } });
            _deaths?.Clear();
            _streak?.Clear();
            Restore(base.Cleanup);
            if (errors.Count > 0) throw new InvalidOperationException("deathcounter_bots cleanup: " + string.Join(" | ", errors));
            if (_stateSaved) Note("cleanup verified: live Config/counter references restored per call, chat observer freed, private counters discarded, no config saved");
        }
    }
}
