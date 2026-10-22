using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using Torch.API.Managers;
using VRage.Game.ModAPI;
using VRage.GameServices;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
using VRage.Network;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Whether SentisTranslator costs the game thread frames. Sixteen fake players, half reading Russian, half English,
    /// and a load heavier than a live server's chat: two player messages a second (Russian and English, some the same
    /// text again), ten screen lines a second to one player each with a changing number (the anomaly zone's progress),
    /// a Russian notice in the chat to everyone each second, an English line to everyone's screen every two seconds.
    /// The same load twice, <see cref="PhaseSeconds"/> each: with the translator off, then on. Reports the frames of
    /// both and the time the translator's own methods take on the game thread (MethodTimerProbe: calls a frame,
    /// microseconds a call, milliseconds a frame). Fails when the translator takes more than <see cref="MaxMsPerFrame"/>
    /// a frame. The plugin's settings are changed in memory and put back; real players online are left out.
    /// </summary>
    public sealed class TranslatorLoadScenario : TestScenario
    {
        public const string ScenarioName = "translator_load";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int Players = 16;
        private const int PhaseSeconds = 45;
        private const double MaxMsPerFrame = 0.5;
        private static readonly string[] Settings = { "Enabled", "TranslateGlobal", "TranslateNotices", "InlineMode", "OptedOutReceivers", "NativeLanguages" };

        private static readonly string[] Russian =
        {
            "Кто-нибудь продаёт уран? Куплю дорого", "Народ, где найти платину поближе к Земле?", "Осторожно, у южного полюса пираты",
            "Кто со мной на Луну за льдом?", "Спасибо за помощь с кораблём!",
        };

        private static readonly string[] English =
        {
            "Anyone selling uranium? Paying well", "Where can I find platinum close to Earth?", "Careful, pirates near the south pole",
            "Who wants to go to the Moon for ice?", "Thanks for the help with the ship!",
        };

        private readonly Dictionary<string, object> _saved = new Dictionary<string, object>();
        private object _config;
        private Type _plugin;
        private object _chat;
        private MethodInfo _raise;
        private ulong[] _steam;
        private long[] _identity;
        private int _sent;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300 + PhaseSeconds * 2 + 120;
        public override string[] Requires => new[] { TranslatorChatScenario.PluginName };

        public override IEnumerator Run()
        {
            _plugin = RequireType(Integrations.TypeOf(TranslatorChatScenario.PluginName, "SentisTranslator.SentisTranslatorPlugin"), "SentisTranslator");
            _config = _plugin.GetProperty("Config", Any)?.GetValue(null);
            Check(_config != null, "SentisTranslator has no config");
            _chat = SentisTestsPlugin.TorchInstance.CurrentSession.Managers.GetManager<IChatManagerServer>();
            _raise = _chat?.GetType().GetMethod("RaiseMessageRecieved", Any, null, new[] { typeof(ChatMsg), typeof(bool).MakeByRefType() }, null);
            Check(_raise != null, "Torch's ChatManagerServer.RaiseMessageRecieved(ChatMsg, ref bool) is not there");
            var ready = Wait(Ready, "the translator's llama-server running", 300);
            while (ready.MoveNext()) yield return ready.Current;

            FakeClients.Add(Players, new FakeClients.NetworkProfile { RttMs = 50 }, i => (new Vector3D(300000, 300000 + i * 20, 300000), 0, 0));
            var joined = Wait(() => Enumerable.Range(0, Players).All(i => FakeClients.PlayerOf(i)?.Identity != null), "the players joined", 60);
            while (joined.MoveNext()) yield return joined.Current;
            _steam = Enumerable.Range(0, Players).Select(i => FakeClients.PlayerOf(i).Id.SteamId).ToArray();
            _identity = Enumerable.Range(0, Players).Select(i => FakeClients.PlayerOf(i).Identity.IdentityId).ToArray();

            foreach (var name in Settings) _saved[name] = Get(name);
            var real = MySession.Static.Players.GetOnlinePlayers().Select(p => p.Id.SteamId).Where(s => s != 0 && !_steam.Contains(s));
            Set("TranslateGlobal", true);
            Set("TranslateNotices", true);
            Set("InlineMode", false);
            Set("OptedOutReceivers", string.Join(";", real));
            Set("NativeLanguages", string.Join(";", _steam.Select((s, i) => s + "=" + (i % 2 == 0 ? "ru" : "en"))));

            var report = new List<string>();
            foreach (var on in new[] { false, true })
            {
                Set("Enabled", on);
                yield return null;
                var targets = Targets();
                var missing = MethodTimerProbe.Start(targets);
                if (missing.Length > 0) Note("method timer: " + missing);
                TickMetrics.Take();
                var from = Sandbox.MySandboxGame.Static.SimulationFrameCounter;
                _sent = 0;
                for (var frame = 0; frame < PhaseSeconds * 60; frame++)
                {
                    Load(frame);
                    yield return null;
                }
                var frames = TickMetrics.Take();
                var counted = (long)(Sandbox.MySandboxGame.Static.SimulationFrameCounter - from);
                var methods = MethodTimerProbe.Format(counted);
                var msPerFrame = TranslatorMs(counted);
                MethodTimerProbe.Stop();
                var line = "translator " + (on ? "ON" : "OFF") + ": " + _sent + " sends; " + frames.Format() + " | translator " + msPerFrame.ToString("F3") + " ms/frame";
                Note(line);
                Note("methods " + (on ? "ON" : "OFF") + ": " + methods);
                report.Add(line);
                if (on) Check(msPerFrame <= MaxMsPerFrame, "the translator takes " + msPerFrame.ToString("F3") + " ms a frame of the game thread, more than " + MaxMsPerFrame);
            }
            // the last translations land a second or two later
            var settle = WaitForSeconds(5, "the last translations delivered");
            while (settle.MoveNext()) yield return settle.Current;
            Note("TRANSLATOR LOAD RESULT | " + string.Join(" || ", report));
        }

        // one frame of the load
        private void Load(int frame)
        {
            var rnd = frame / 6;
            if (frame % 30 == 0)
            {
                var from = (frame / 30) % Players;
                var pool = from % 2 == 0 ? Russian : English;
                var text = pool[(frame / 30) % pool.Length] + ((frame / 30) % 3 == 0 ? " #" + frame : "");
                Say(from, text);
            }
            if (frame % 6 == 0)
            {
                MyVisualScriptLogicProvider.ShowNotification("Захват зоны: " + (rnd % 100) + "%", 1000, "Green", _identity[rnd % Players]);
                _sent++;
            }
            if (frame % 60 == 0)
            {
                MyVisualScriptLogicProvider.SendChatMessage("На постройке без маяка: она будет удалена при следующей очистке", "Сервер", 0, "Red");
                _sent++;
            }
            if (frame % 120 == 0)
            {
                MyVisualScriptLogicProvider.ShowNotificationToAll("Server restart in " + (10 - (frame / 120) % 10) + " minutes", 2000, "Red");
                _sent++;
            }
        }

        private void Say(int from, string text)
        {
            var msg = new ChatMsg { Text = text, Author = _steam[from], Channel = (byte)ChatChannel.Global };
            var args = new object[] { msg, false };
            FakeClients.AsClient(from, () => _raise.Invoke(_chat, args));
            _sent++;
        }

        // the translator's methods that run on the game thread
        private static List<(Type, string)> Targets()
        {
            Type T(string name) => Integrations.TypeOf(TranslatorChatScenario.PluginName, "SentisTranslator." + name);
            return new List<(Type, string)>
            {
                (T("ChatHook"), "OnMessageProcessing"),
                (T("ChatHook"), "PostTranslation"),
                (T("ChatHook"), "SendAsPlayer"),
                (T("Notices"), "Take"),
                (T("Notices"), "Deliver"),
                (T("NoticeHook"), "ChatPrefix"),
                (T("NoticeHook"), "NotificationPrefix"),
                (T("NoticeHook"), "NotificationToAllPrefix"),
                (T("DiscordBridgeHook"), "SendPrefix"),
                (T("NativeLang"), "Get"),
            };
        }

        // the time of the methods that are not inside another one of them (no time counted twice): the hooks and the
        // chat handler, and the deliveries made a frame later
        private static double TranslatorMs(long frames)
        {
            var calls = typeof(MethodTimerProbe).GetField("Ticks", Any)?.GetValue(null) as long[];
            var names = typeof(MethodTimerProbe).GetField("Names", Any)?.GetValue(null) as string[];
            if (calls == null || names == null) return -1;
            var outer = new[] { "ChatHook.OnMessageProcessing", "ChatHook.PostTranslation", "ChatHook.SendAsPlayer", "NoticeHook.ChatPrefix",
                                "NoticeHook.NotificationPrefix", "NoticeHook.NotificationToAllPrefix", "DiscordBridgeHook.SendPrefix" };
            double ticks = 0;
            for (var i = 0; i < names.Length; i++)
                if (names[i] != null && outer.Contains(names[i])) ticks += calls[i];
            return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, frames);
        }

        private bool Ready()
        {
            var instance = _plugin.GetProperty("Instance", Any)?.GetValue(null);
            var setup = instance?.GetType().GetField("_setup", Any)?.GetValue(instance);
            return setup?.GetType().GetProperty("Ready", Any)?.GetValue(setup) as bool? == true;
        }

        private object Get(string name) => _config.GetType().GetProperty(name).GetValue(_config);

        private void Set(string name, object value) => _config.GetType().GetProperty(name).SetValue(_config, value);

        public override void Cleanup()
        {
            try
            {
                MethodTimerProbe.Stop();
                if (_config != null)
                {
                    foreach (var pair in _saved) Set(pair.Key, pair.Value);
                    if (_saved.Count > 0) _plugin.GetMethod("SaveConfig", Any)?.Invoke(null, null);
                }
                FakeClients.RemoveAll();
            }
            finally { base.Cleanup(); }
        }
    }
}
