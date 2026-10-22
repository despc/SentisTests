using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisTranslator's notices: what the server and its plugins tell players goes to each in their native language
    /// only. Two fake players, A reads Russian, B English; what the plugin delivers to whom it reports
    /// (<c>Notices.Delivered</c>):
    /// - a scripted chat message in Russian to everyone (SGI's "no beacon"): A gets it as it is, B only in English, the
    ///   author's name too;
    /// - a line on B's screen with a number ("capture 45 %"), then the same with another number: the second at once,
    ///   from the same translation, with its own number;
    /// - an English line to everyone's screen: A in Russian, B as it is;
    /// - a window (Essentials' MOTD) in Russian to B: the content line by line in English, the title as it is; to A
    ///   nothing is translated (the window goes as it is);
    /// - a message in both languages sent as it is (the plugin's own help line): not taken.
    /// The plugin's settings are changed in memory and put back; real players online are left out (translations off).
    /// </summary>
    public sealed class TranslatorNoticesScenario : TestScenario
    {
        public const string ScenarioName = "translator_notices";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int ReadySeconds = 300;
        private const int TranslateSeconds = 30;
        private static readonly string[] Settings = { "Enabled", "TranslateNotices", "OptedOutReceivers", "NativeLanguages" };

        private static readonly ConcurrentQueue<KeyValuePair<ulong, string>> Got = new ConcurrentQueue<KeyValuePair<ulong, string>>();
        private readonly Dictionary<string, object> _saved = new Dictionary<string, object>();
        private object _config;
        private Type _plugin;
        private EventInfo _event;
        private Delegate _listener;
        private ulong _a, _b;
        private long _ia, _ib;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => ReadySeconds + 240;
        public override string[] Requires => new[] { TranslatorChatScenario.PluginName };

        public override IEnumerator Run()
        {
            _plugin = RequireType(Integrations.TypeOf(TranslatorChatScenario.PluginName, "SentisTranslator.SentisTranslatorPlugin"), "SentisTranslator");
            _config = _plugin.GetProperty("Config", Any)?.GetValue(null);
            Check(_config != null, "SentisTranslator has no config");
            var notices = RequireType(Integrations.TypeOf(TranslatorChatScenario.PluginName, "SentisTranslator.Notices"), "SentisTranslator.Notices");
            Check(_config.GetType().GetProperty("TranslateNotices") != null, "SentisTranslator has no TranslateNotices");

            var ready = Wait(() => notices.GetProperty("Current", Any)?.GetValue(null) != null && Ready(), "the translator's llama-server running", ReadySeconds);
            while (ready.MoveNext()) yield return ready.Current;

            FakeClients.Add(2, new FakeClients.NetworkProfile { RttMs = 50 }, i => (new Vector3D(300000, 300000 + i * 20, 300000), 0, 0));
            var joined = Wait(() => FakeClients.PlayerOf(0)?.Identity != null && FakeClients.PlayerOf(1)?.Identity != null, "two players joined", 30);
            while (joined.MoveNext()) yield return joined.Current;
            _a = FakeClients.PlayerOf(0).Id.SteamId;
            _b = FakeClients.PlayerOf(1).Id.SteamId;
            _ia = FakeClients.PlayerOf(0).Identity.IdentityId;
            _ib = FakeClients.PlayerOf(1).Identity.IdentityId;

            foreach (var name in Settings) _saved[name] = Get(name);
            var real = MySession.Static.Players.GetOnlinePlayers().Select(p => p.Id.SteamId).Where(s => s != 0 && s != _a && s != _b);
            Set("Enabled", true);
            Set("TranslateNotices", true);
            Set("OptedOutReceivers", string.Join(";", real));
            Set("NativeLanguages", $"{_a}=ru;{_b}=en");
            _event = notices.GetEvent("Delivered", Any);
            Check(_event != null, "SentisTranslator's Notices.Delivered is not there");
            _listener = new Action<ulong, string>((steam, text) => Got.Enqueue(new KeyValuePair<ulong, string>(steam, text)));
            _event.AddEventHandler(null, _listener);
            Drain();

            // --- chat, Russian, to everyone
            MyVisualScriptLogicProvider.SendChatMessage("На постройке Тестовая база не установлен маяк", "Сервер", 0, "Red");
            var toA = Drain().Where(p => p.Key == _a).ToList();
            Check(toA.Count == 1 && toA[0].Value == "На постройке Тестовая база не установлен маяк | Сервер",
                "chat, Russian: A (reads Russian) did not get it as it is at once: " + Show(toA));
            var step = For(_b, "chat, Russian: to B in English", text => Latin(text) && text.Contains("|"));
            while (step.MoveNext()) yield return step.Current;
            Note("chat, Russian, to B: " + _last);
            Check(!_last.Split('|')[1].Any(Cyrillic), "chat, Russian: the author's name to B is not in English: " + _last);

            // --- a line on the screen with a number, twice
            MyVisualScriptLogicProvider.ShowNotification("Захват зоны: 45%", 1000, "Green", _ib);
            step = For(_b, "screen, Russian: to B in English", text => Latin(text) && text.Contains("45"));
            while (step.MoveNext()) yield return step.Current;
            Note("screen, Russian, to B: " + _last);
            MyVisualScriptLogicProvider.ShowNotification("Захват зоны: 46%", 1000, "Green", _ib);
            var again = Drain().Where(p => p.Key == _b).ToList();
            Check(again.Count == 1 && again[0].Value.Contains("46") && Latin(again[0].Value),
                "screen, the same line with another number: not at once from the same translation: " + Show(again));
            Check(again[0].Value == _last.Replace("45", "46"), "screen, another number: \"" + again[0].Value + "\" is not \"" + _last + "\" with 46");

            // --- English to everyone's screen
            MyVisualScriptLogicProvider.ShowNotificationToAll("The server restarts in 5 minutes", 3000, "Red");
            var toB = Drain().Where(p => p.Key == _b).ToList();
            Check(toB.Count == 1 && toB[0].Value == "The server restarts in 5 minutes", "screen to all, English: B (reads English) did not get it as it is at once: " + Show(toB));
            step = For(_a, "screen to all, English: to A in Russian", text => text.Any(Cyrillic) && text.Contains("5"));
            while (step.MoveNext()) yield return step.Current;
            Note("screen to all, English, to A: " + _last);

            // --- a window, Russian, to B; to A as it is
            const string motd = "Добро пожаловать на сервер!\nНе забудьте поставить маяк на свою базу.";
            Torch.Mod.ModCommunication.SendMessageTo(new Torch.Mod.Messages.DialogMessage("Sentis", "Сообщение дня", motd), _b);
            step = For(_b, "window, Russian: to B in English", text => Latin(text) && text.Split('|').Length == 3);
            while (step.MoveNext()) yield return step.Current;
            Note("window, Russian, to B: " + _last);
            Torch.Mod.ModCommunication.SendMessageTo(new Torch.Mod.Messages.DialogMessage("Sentis", "Сообщение дня", motd), _a);
            var windowA = Drain().Where(p => p.Key == _a).ToList();
            Check(windowA.Count == 0, "window, Russian, to A (reads Russian): taken, though nobody needs it: " + Show(windowA));

            // --- in both languages, sent as it is
            var asIs = Integrations.TypeOf(TranslatorChatScenario.PluginName, "SentisTranslator.NoticeHook")?.GetMethod("AsIs", Any);
            Check(asIs != null, "SentisTranslator's NoticeHook.AsIs is not there");
            asIs.Invoke(null, new object[] { new Action(() => MyVisualScriptLogicProvider.SendChatMessage("команды — в окне / commands are in the window", "", _ib, "Yellow")) });
            var wait = WaitForSeconds(2, "the help line: nothing taken");
            while (wait.MoveNext()) yield return wait.Current;
            var both = Drain();
            Check(both.Count == 0, "a line sent as it is was taken: " + Show(both));

            // --- translated beforehand (Notices.Prepare): shown, it goes at once; a MOTD with the player's name too
            var prepare = notices.GetMethod("Prepare", Any);
            var localize = notices.GetMethod("Localize", Any);
            Check(prepare != null && localize != null, "SentisTranslator's Notices.Prepare / Localize are not there");
            const string constant = "Внимание: очистка мира пройдёт ночью";
            const string greeting = "Рады видеть тебя снова, %player%!";
            prepare.Invoke(null, new object[] { constant, new string[0] });
            prepare.Invoke(null, new object[] { greeting, new[] { "%player%" } });
            var nameB = FakeClients.PlayerOf(1).Identity.DisplayName;
            var greetingB = greeting.Replace("%player%", nameB);
            var known = Wait(() => (string)localize.Invoke(null, new object[] { constant, _ib }) != constant &&
                                   (string)localize.Invoke(null, new object[] { greetingB, _ib }) != greetingB,
                "the prepared texts translated", TranslateSeconds);
            while (known.MoveNext()) yield return known.Current;
            Drain();
            MyVisualScriptLogicProvider.ShowNotification(constant, 3000, "White", _ib);
            var prepared = Drain().Where(p => p.Key == _b).ToList();
            Check(prepared.Count == 1 && Latin(prepared[0].Value), "a prepared text: not to B in English at once: " + Show(prepared));
            Note("prepared, to B at once: " + prepared[0].Value);
            Torch.Mod.ModCommunication.SendMessageTo(new Torch.Mod.Messages.DialogMessage("Sentis", "Message Of The Day", greetingB), _b);
            var motdB = Drain().Where(p => p.Key == _b).ToList();
            Check(motdB.Count == 1 && motdB[0].Value.Contains(nameB) && !motdB[0].Value.Split('|')[1].Any(Cyrillic),
                "a prepared MOTD with the player's name: not to B in English at once with their name: " + Show(motdB));
            Note("prepared MOTD, to B at once: " + motdB[0].Value);

            // --- the anomaly zone's GPS (SentisAnomalyZone, when it is here): in B's language, A's as it is
            var zoneApi = Integrations.TypeOf("SentisAnomalyZone", "SentisAnomalyZone.AnomalyZoneApi");
            var gpsCount = 0;
            if (zoneApi != null)
            {
                var recheck = zoneApi.GetMethod("GpsRecheck", Any);
                bool Zone(VRage.Game.ModAPI.IMyGps g, Func<string, bool> text) => g.Description != null && text(g.Description) && !g.Name.StartsWith("FakeClient");
                var english = Sandbox.ModAPI.MyAPIGateway.Session.GPS;
                var got = Wait(() =>
                {
                    recheck?.Invoke(null, new object[] { _ib });
                    return english.GetGpsList(_ib).Any(g => Zone(g, Latin));
                }, "the anomaly zone's GPS of B in English", 60);
                while (got.MoveNext()) yield return got.Current;
                var mineB = english.GetGpsList(_ib).Where(g => Zone(g, Latin)).ToList();
                var mineA = english.GetGpsList(_ia).Where(g => Zone(g, t => t.Any(Cyrillic))).ToList();
                Check(mineA.Count > 0, "the anomaly zone's GPS of A (reads Russian) is not in Russian: " +
                                       string.Join(" / ", english.GetGpsList(_ia).Select(g => g.Name + ": " + g.Description)));
                gpsCount = mineB.Count;
                Note("GPS of B: " + string.Join(" / ", mineB.Select(g => g.Name + ": " + g.Description)) + "; of A: " + string.Join(" / ", mineA.Select(g => g.Name + ": " + g.Description)));
            }
            else Note("no SentisAnomalyZone here: its GPS not checked");

            Note("TRANSLATOR NOTICES RESULT | chat, screen, screen to all, window: each reader in their language only; a number changed - the same translation at once; as-is lines left; prepared texts and a MOTD with the name at once; anomaly zone GPS " + (gpsCount > 0 ? "in B's language" : "not checked"));
        }

        private string _last;

        // waits for what goes to one player, and keeps it in _last
        private IEnumerator For(ulong steam, string what, Func<string, bool> wanted)
        {
            _last = null;
            var got = Wait(() =>
            {
                foreach (var pair in Drain())
                    if (pair.Key == steam && _last == null && wanted(pair.Value)) _last = pair.Value;
                return _last != null;
            }, what, TranslateSeconds);
            while (got.MoveNext()) yield return got.Current;
        }

        private static bool Cyrillic(char c) => (c >= 'А' && c <= 'я') || c == 'Ё' || c == 'ё';

        private static bool Latin(string text) => !text.Any(Cyrillic) && text.Any(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));

        private static string Show(List<KeyValuePair<ulong, string>> got) =>
            got.Count == 0 ? "nothing" : string.Join(" / ", got.Select(p => p.Key + ": " + p.Value));

        private static List<KeyValuePair<ulong, string>> Drain()
        {
            var list = new List<KeyValuePair<ulong, string>>();
            while (Got.TryDequeue(out var pair)) list.Add(pair);
            return list;
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
                if (_event != null && _listener != null) _event.RemoveEventHandler(null, _listener);
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
