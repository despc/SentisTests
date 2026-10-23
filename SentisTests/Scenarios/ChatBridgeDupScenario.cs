using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using Torch.API.Managers;
using VRage.Network;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A Discord message as SEDiscordBridge brings it into the game through SentisTranslator (prod, 23.10.2026: each player
    /// saw it once per player online). The translator's own path is taken - <c>ChatHook.OnBridgeMessage</c> with the send
    /// its <c>DiscordBridgeHook</c> gives it, Torch's <c>SendMessageAsOther(…, target)</c> - in inline mode, as on prod. Every
    /// scripted chat event the server sends is recorded with its recipient and its target, and for each player online the
    /// copies their client shows are counted: those whose target is the player's identity or none
    /// (<c>MyMultiplayerBase.OnScriptedChatMessageReceived</c>).
    /// </summary>
    public sealed class ChatBridgeDupScenario : TestScenario
    {
        public const string ScenarioName = "chat_bridge_dup";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        private const string Marker = "Проверка моста ";
        private static readonly List<(ulong Recipient, long Target, string Text)> Sent = new List<(ulong, long, string)>();
        private static Harmony _harmony;
        private object _config;
        private bool _inlineWas;
        private bool _added;
        private Type _native;
        private List<ulong> _members;
        private readonly List<ulong> _addedMembers = new List<ulong>();
        private ulong _englishSteam;
        private string _englishWas;

        public override IEnumerator Run()
        {
            var hookType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisTranslator.ChatHook")).FirstOrDefault(t => t != null);
            SkipUnless(hookType != null, "SentisTranslator is not loaded");
            var hook = hookType.GetProperty("Current", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            SkipUnless(hook != null, "SentisTranslator has no chat hook running");
            _config = hookType.GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(hook);
            var inline = _config.GetType().GetProperty("InlineMode");
            _inlineWas = (bool)inline.GetValue(_config);
            inline.SetValue(_config, true);

            var raise = typeof(MyReplicationLayer).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .First(m => m.Name == "DispatchEvent" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 8)
                .MakeGenericMethod(typeof(IMyEventOwner), typeof(ScriptedChatMsg), typeof(DBNull), typeof(DBNull), typeof(DBNull), typeof(DBNull), typeof(DBNull), typeof(IMyEventOwner));
            _harmony = new Harmony("SentisTests.ChatBridgeDup");
            _harmony.Patch(raise, prefix: new HarmonyMethod(typeof(ChatBridgeDupScenario).GetMethod(nameof(Delivered), BindingFlags.Static | BindingFlags.NonPublic)));
            lock (Sent) Sent.Clear();
            // clients to send to: three fake ones by the planet's first site (the bots online have none)
            if (FakeClients.Count == 0)
            {
                var planet = Sandbox.Game.Entities.MyEntities.GetEntities().OfType<Sandbox.Game.Entities.MyPlanet>().First();
                var far = planet.PositionComp.GetPosition() + VRageMath.Vector3D.Up * (planet.MaximumRadius + 20000);
                FakeClients.Add(3, new FakeClients.NetworkProfile { RttMs = 50 }, i => (far + VRageMath.Vector3D.Right * (i * 10), 0, 0), withCharacters: true);
                _added = true;
            }
            var ready = WaitForSeconds(10, "the fake clients in");
            while (ready.MoveNext()) yield return ready.Current;

            // the fake clients as connected clients for the time of the run: the translator sends only to those
            // (ChatHook.Reachable), and the stand's fakes are not among MyMultiplayer.Static.Members
            _members = typeof(MyDedicatedServerBase).GetField("m_members", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(MyMultiplayer.Static) as List<ulong>;
            if (_members != null)
                foreach (var p in MySession.Static.Players.GetOnlinePlayers().Where(p => p.DisplayName.StartsWith("FakeClient")))
                    if (!_members.Contains(p.Id.SteamId))
                    {
                        _members.Add(p.Id.SteamId);
                        _addedMembers.Add(p.Id.SteamId);
                    }

            // one reader in English, so the message is held for a translation and goes to the players one at a time
            _native = hookType.Assembly.GetType("SentisTranslator.NativeLang");
            var english = MySession.Static.Players.GetOnlinePlayers().FirstOrDefault(p => p.DisplayName.StartsWith("FakeClient"));
            if (english != null && _native != null)
            {
                _englishSteam = english.Id.SteamId;
                _englishWas = (string)_native.GetMethod("Get").Invoke(null, new[] { _config, (object)_englishSteam });
                _native.GetMethod("Set").Invoke(null, new[] { _config, (object)_englishSteam, "en", "chat_bridge_dup" });
                Note(english.DisplayName + " reads English (was " + _englishWas + ")");
            }

            // what DiscordBridgeHook.SendPrefix does with the bridge's message: the hook, and its sends by Torch
            var chat = Torch.TorchBase.Instance?.CurrentSession?.Managers?.GetManager<IChatManagerServer>();
            Check(chat != null, "no Torch chat manager");
            var noticeHook = hookType.Assembly.GetType("SentisTranslator.NoticeHook");
            var asIs = noticeHook.GetMethod("AsIs", BindingFlags.Static | BindingFlags.Public);
            const string author = "[D]test";
            const string font = "White";
            var text = Marker + "Я немного тестирую античит, напишите если что будет троить";
            // as DiscordBridgeHook sends: to everyone by Torch, to one player straight (no line of Torch's chat log each)
            var sendTo = hookType.Assembly.GetType("SentisTranslator.DiscordBridgeHook")?.GetMethod("SendTo", BindingFlags.Static | BindingFlags.NonPublic);
            Action<ulong, string> send = (to, t) => asIs.Invoke(null, new object[]
            {
                (Action)(() =>
                {
                    if (to == 0 || sendTo == null) chat.SendMessageAsOther(author, t, font, to);
                    else sendTo.Invoke(null, new object[] { author, t, font, to });
                })
            });
            Note("sending " + (sendTo != null ? "as the translator does (SendTo)" : "by Torch's SendMessageAsOther (an older translator)"));
            var readers = hookType.GetMethod("BridgeReaders", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { 0UL }) as System.Collections.IList;
            Note("the translator's readers: " + (readers == null ? "?" : readers.Count.ToString()));
            var onBridge = hookType.GetMethod("OnBridgeMessage");
            var held = (bool)onBridge.Invoke(hook, new object[] { author, text, font, 0UL, send });
            if (!held) chat.SendMessageAsOther(author, text, font, 0);
            Note("the translator " + (held ? "held the message (inline)" : "let it go on"));

            var waited = DateTime.UtcNow;
            while ((DateTime.UtcNow - waited).TotalSeconds < 40)
            {
                lock (Sent) if (Sent.Select(x => x.Text).Distinct().Count() >= 2 && (DateTime.UtcNow - waited).TotalSeconds > 15) break;
                yield return null;
            }

            List<(ulong Recipient, long Target, string Text)> sent;
            lock (Sent) sent = Sent.ToList();
            var players = MySession.Static.Players.GetOnlinePlayers().ToList();
            var targets = sent.Select(x => x.Target).Distinct().ToList();
            Note("events sent " + sent.Count + " to " + sent.Select(x => x.Recipient).Distinct().Count() + " endpoints; targets: " +
                 string.Join(", ", targets.Select(t => t == 0 ? "none" : (players.FirstOrDefault(p => p.Identity?.IdentityId == t)?.DisplayName ?? t.ToString()) + " x" + sent.Count(x => x.Target == t))));
            var worst = 0;
            foreach (var player in players)
            {
                var steam = player.Id.SteamId;
                var identity = player.Identity?.IdentityId ?? 0;
                // a scripted chat message goes to every client (recipient 0): each shows it if it is its target or nobody's
                var received = sent.Where(x => x.Recipient == steam || x.Recipient == 0).ToList();
                var shown = received.Count(x => x.Target == 0 || x.Target == identity);
                worst = Math.Max(worst, shown);
                Note("player " + player.DisplayName + " (" + steam + "): received " + received.Count + ", shows " + shown);
            }
            Note("CHAT BRIDGE RESULT | the most copies a client shows: " + worst);
            Check(worst <= 1, "a client shows the Discord message " + worst + " times");
            Check(worst >= 1 || players.All(p => !p.DisplayName.StartsWith("FakeClient")), "no client shows the Discord message");
        }

        private static void Delivered(CallSite callSite, EndpointId recipient, ref ScriptedChatMsg arg2)
        {
            if (arg2.Text == null || !arg2.Text.StartsWith(Marker)) return;
            lock (Sent) Sent.Add((recipient.Value, arg2.Target, arg2.Text));
        }

        public override void Cleanup()
        {
            try { foreach (var steam in _addedMembers) _members?.Remove(steam); }
            catch { }
            _addedMembers.Clear();
            if (_added) FakeClients.RemoveAll();
            try { _harmony?.UnpatchAll("SentisTests.ChatBridgeDup"); }
            catch { }
            _harmony = null;
            try { _config?.GetType().GetProperty("InlineMode").SetValue(_config, _inlineWas); }
            catch { }
            try { if (_englishSteam != 0 && _englishWas != null) _native.GetMethod("Set").Invoke(null, new[] { _config, (object)_englishSteam, _englishWas, "chat_bridge_dup" }); }
            catch { }
            base.Cleanup();
        }
    }
}
