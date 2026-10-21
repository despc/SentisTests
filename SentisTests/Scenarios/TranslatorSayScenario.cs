using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using Torch.API.Managers;
using VRage.Game.ModAPI;
using VRage.Network;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A fake player says something in global chat, for a real player online to see SentisTranslator at work: the
    /// message goes to everyone as a player's would, and through Torch's chat pipeline, where the plugin translates it
    /// for the readers who need it. The text is the run's focus (<c>api('test', {'name': 'translator_say', 'focus':
    /// '...'})</c>), an English line by default. The plugin's settings are not touched; the fake player stays a minute.
    /// <see cref="InlineName"/>: the same in inline mode ("text (translation)" under the author's name), switched on in
    /// memory for this message only and put back. <see cref="DiscordName"/>: a message from Discord, as SEDiscordBridge
    /// shows it (a stand-in of the bridge: <see cref="SEDiscordBridge.DiscordBridge"/>), by "[D] John"; no fake player.
    /// </summary>
    public sealed class TranslatorSayScenario : TestScenario
    {
        public const string ScenarioName = "translator_say";
        public const string InlineName = "translator_say_inline";
        public const string DiscordName = "translator_say_discord";
        private const string DefaultText = "Hey, anyone around? I found a big ice field near the north pole, want to mine it together?";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly bool _inline;
        private readonly bool _discord;
        private object _config;
        private object _savedInline;
        private ulong _fakeSteam;

        public TranslatorSayScenario(bool inline = false, bool discord = false)
        {
            _inline = inline;
            _discord = discord;
        }

        public override string Name => _discord ? DiscordName : _inline ? InlineName : ScenarioName;
        public override int TimeoutSeconds => 120;
        public override string[] Requires => new[] { TranslatorChatScenario.PluginName };

        public override IEnumerator Run()
        {
            var text = string.IsNullOrWhiteSpace(TestRunner.Focus) ? DefaultText : TestRunner.Focus;
            var plugin = RequireType(Integrations.TypeOf(TranslatorChatScenario.PluginName, "SentisTranslator.SentisTranslatorPlugin"), "SentisTranslator");
            var config = _config = plugin.GetProperty("Config", Any)?.GetValue(null);
            Check(config != null, "SentisTranslator has no config");
            object Get(string name) => config.GetType().GetProperty(name)?.GetValue(config);
            var chat = SentisTestsPlugin.TorchInstance.CurrentSession.Managers.GetManager<IChatManagerServer>();
            var raise = chat?.GetType().GetMethod("RaiseMessageRecieved", Any, null, new[] { typeof(ChatMsg), typeof(bool).MakeByRefType() }, null);
            Check(raise != null, "Torch's ChatManagerServer.RaiseMessageRecieved(ChatMsg, ref bool) is not there");
            if (_discord)
            {
                var harmony = new HarmonyLib.Harmony("SentisTests.TranslatorSayDiscord");
                var send = typeof(MyVisualScriptLogicProvider).GetMethod("SendChatMessage", new[] { typeof(string), typeof(string), typeof(long), typeof(string) });
                harmony.Patch(send, prefix: new HarmonyLib.HarmonyMethod(typeof(TranslatorSayScenario).GetMethod(nameof(SentPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                var colored = typeof(MyVisualScriptLogicProvider).GetMethod("SendChatMessageColored", new[] { typeof(string), typeof(Color), typeof(string), typeof(long), typeof(string) });
                harmony.Patch(colored, prefix: new HarmonyLib.HarmonyMethod(typeof(TranslatorSayScenario).GetMethod(nameof(SentColoredPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                try
                {
                    while (Sent.TryDequeue(out _)) { }
                    SEDiscordBridge.DiscordBridge.Discord_MessageCreated(chat, "[D] John", text, 0);
                    Note($"[D] John said \"{text}\" (from Discord)");
                    var shown = WaitForSeconds(10, "the translation sent");
                    while (shown.MoveNext()) yield return shown.Current;
                }
                finally { harmony.UnpatchAll(harmony.Id); }
                Note("the plugin sent: " + (Sent.IsEmpty ? "nothing" : string.Join(" / ", Sent)));
                yield break;
            }

            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 50 }, i => (new Vector3D(300000, 300000, 300000), 0, 0));
            var joined = Wait(() => FakeClients.PlayerOf(0)?.Identity != null, "the fake player joined", 30);
            while (joined.MoveNext()) yield return joined.Current;
            var player = FakeClients.PlayerOf(0);
            var name = player.Identity.DisplayName;
            _fakeSteam = player.Id.SteamId;
            var opted = ((string)Get("OptedOutReceivers") ?? "").Split(';').Select(s => s.Trim()).ToList();
            foreach (var real in MySession.Static.Players.GetOnlinePlayers().Where(p => p != player))
                Note($"online: {real.DisplayName} ({real.Id.SteamId}) translations {(opted.Contains(real.Id.SteamId.ToString()) ? "off" : "on")}");
            Note($"translator: enabled {Get("Enabled")}, global {Get("TranslateGlobal")}, inline {Get("InlineMode")}");

            if (_inline)
            {
                _savedInline = Get("InlineMode");
                config.GetType().GetProperty("InlineMode").SetValue(config, true);
            }
            yield return null;
            // the message as everyone sees a player's (inline mode holds the original and sends it itself)
            var held = (bool)Get("InlineMode");
            var args = new object[] { new ChatMsg { Text = text, Author = player.Id.SteamId, Channel = (byte)ChatChannel.Global }, false };
            raise.Invoke(chat, args);
            if (!(bool)args[1]) SendAsPlayer(player, text);
            Note($"{name} said \"{text}\"{(held ? " (inline mode)" : "")}");
            if (_inline)
            {
                // the held message goes once translated (under a second on the stand's GPU)
                var translated = WaitForSeconds(15, "the inline message sent");
                while (translated.MoveNext()) yield return translated.Current;
                PutBack();
            }
            var stay = WaitForSeconds(60, "a minute online");
            while (stay.MoveNext()) yield return stay.Current;
        }

        /// <summary>
        /// The message to everyone online but the author as the game's server sends a player's
        /// (<c>MyMultiplayerBase.OnChatMessageReceived_Server</c>): each client shows it under the author's name coloured by
        /// its relation to them. The name goes with it: a fake player has no Steam member name on the clients.
        /// </summary>
        private static void SendAsPlayer(MyPlayer author, string text)
        {
            var single = typeof(MyMultiplayerBase).GetMethod("OnChatMessageReceived_SingleTarget", BindingFlags.Static | BindingFlags.NonPublic)
                         ?? throw new MissingMethodException("MyMultiplayerBase", "OnChatMessageReceived_SingleTarget");
            var msg = new ChatMsg
            {
                Text = text,
                Author = author.Id.SteamId,
                Channel = (byte)ChatChannel.Global,
                CrossChatEnabled = true,
                CustomData = new VRage.GameServices.ChatMessageCustomData { AuthorName = author.Identity.DisplayName, SenderId = (ulong)author.Identity.IdentityId },
            };
            foreach (var reader in MySession.Static.Players.GetOnlinePlayers())
                if (reader != author && reader.Id.SteamId != 0)
                    Torch.Managers.NetworkManager.RaiseStaticEvent<ChatMsg>(single, msg, new EndpointId(reader.Id.SteamId));
        }

        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Sent = new System.Collections.Concurrent.ConcurrentQueue<string>();

        private static void SentPrefix(string message, string author, long playerId, string font) =>
            Sent.Enqueue($"to {playerId} <{author}|{font}> {message}");

        private static void SentColoredPrefix(string message, Color color, string author, long playerId, string font) =>
            Sent.Enqueue($"to {playerId} <{author}|{font} {color}> {message}");

        /// <summary>The fake player's language, which the plugin learnt from the message, out of its config.</summary>
        private void ForgetFake()
        {
            if (_config == null || _fakeSteam == 0) return;
            var property = _config.GetType().GetProperty("NativeLanguages");
            var stored = property?.GetValue(_config) as string ?? "";
            var kept = string.Join(";", stored.Split(';').Where(e => e.Length > 0 && !e.Trim().StartsWith(_fakeSteam + "=")));
            if (kept == stored) return;
            property.SetValue(_config, kept);
            Integrations.TypeOf(TranslatorChatScenario.PluginName, "SentisTranslator.SentisTranslatorPlugin")?.GetMethod("SaveConfig", Any)?.Invoke(null, null);
        }

        private void PutBack()
        {
            if (_savedInline == null) return;
            _config.GetType().GetProperty("InlineMode").SetValue(_config, _savedInline);
            _savedInline = null;
            // the plugin may have saved its config meanwhile (a writer's language): the file as it was too
            Integrations.TypeOf(TranslatorChatScenario.PluginName, "SentisTranslator.SentisTranslatorPlugin")?.GetMethod("SaveConfig", Any)?.Invoke(null, null);
        }

        public override void Cleanup()
        {
            try
            {
                PutBack();
                ForgetFake();
                FakeClients.RemoveAll();
            }
            finally { base.Cleanup(); }
        }
    }
}
