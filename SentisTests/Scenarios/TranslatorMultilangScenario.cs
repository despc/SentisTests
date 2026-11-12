using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.Game;
using SentisTests.Core;
using SentisTests.Game;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System;
using Torch.API.Managers;
using VRage.Game.ModAPI;
using VRage.Game;
using VRage.Network;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisTranslator with five languages, with the stand's model (llama-server) translating for real. Five fake
    /// players reading Russian, English, French, German and Polish (chosen, as "!st lang" does): each writes in global
    /// chat in their own language, and each of the other four gets one translation, in their own language (told by the
    /// plugin's own detector), the author none; the time until the last of them has it is noted. Then the same in
    /// inline mode ("text (translation)" under the author's name to each who needs it). Then a player who has not chosen
    /// a language and reads English writes a short English line ("server restart soon?"): their language stays English
    /// (before the review of 12.11.2026 the line was taken for German, and the player became German). The plugin's settings are changed in memory and put back, real
    /// players online are left out (translations off while it runs).
    /// </summary>
    public sealed class TranslatorMultilangScenario : TestScenario
    {
        public const string ScenarioName = "translator_multilang";
        private const string PluginName = TranslatorChatScenario.PluginName;
        private const int Players = 5;
        private const int TranslateSeconds = 120;
        private const double QuietSeconds = 3;
        private const int ReadySeconds = 300;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private static readonly string[] Codes = { "ru", "en", "fr", "de", "pl" };
        private static readonly string[] Texts =
        {
            "Привет всем, кто хочет вместе строить базу у планеты?",
            "Anyone want to team up and build a base near the moon?",
            "Salut tout le monde, qui veut construire une base avec moi près de la lune ?",
            "Hallo zusammen, wer will mit mir eine Basis beim Mond bauen?",
            "Cześć wszystkim, kto chce ze mną zbudować bazę przy księżycu?",
        };

        private static readonly string[] Settings =
            { "AutoRestartServer", "Enabled", "TranslateGlobal", "InlineMode", "OptedOutReceivers", "NativeLanguages", "ChosenLanguages", "WrittenLanguages" };

        private struct Sent
        {
            public string Text, Author, Font;
            public long To;
        }

        private static readonly ConcurrentQueue<Sent> Captured = new ConcurrentQueue<Sent>();
        private static volatile bool _capturing;
        private static volatile HashSet<long> _fakes = new HashSet<long>();
        private static Harmony _harmony;

        private readonly Dictionary<string, object> _saved = new Dictionary<string, object>();
        private object _config;
        private Type _plugin;
        private MethodInfo _sourceLang, _languageCode;
        private object _chat;
        private MethodInfo _raise;
        private ulong[] _steam;
        private long[] _identity;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => ReadySeconds + 900;
        public override string[] Requires => new[] { PluginName };

        public override IEnumerator Run()
        {
            _plugin = RequireType(Integrations.TypeOf(PluginName, "SentisTranslator.SentisTranslatorPlugin"), "SentisTranslator");
            _config = _plugin.GetProperty("Config", Any)?.GetValue(null);
            Check(_config != null, "SentisTranslator has no config");
            var translator = Integrations.TypeOf(PluginName, "SentisTranslator.Translate.LlmTranslator");
            _sourceLang = translator?.GetMethod("SourceLang", Any, null, new[] { typeof(string) }, null);
            _languageCode = translator?.GetMethod("LanguageCode", Any);
            Check(_sourceLang != null && _languageCode != null, "SentisTranslator has no five languages (LlmTranslator.SourceLang / LanguageCode)");
            _chat = SentisTestsPlugin.TorchInstance.CurrentSession.Managers.GetManager<IChatManagerServer>();
            _raise = _chat?.GetType().GetMethod("RaiseMessageRecieved", Any, null, new[] { typeof(ChatMsg), typeof(bool).MakeByRefType() }, null);
            Check(_raise != null, "Torch's ChatManagerServer.RaiseMessageRecieved(ChatMsg, ref bool) is not there");

            var ready = Wait(Ready, "the translator's llama-server running", ReadySeconds);
            while (ready.MoveNext()) yield return ready.Current;

            FakeClients.Add(Players, Network, i => (new Vector3D(310000, 300000 + i * 20, 300000), 0, 0));
            var joined = Wait(() => Enumerable.Range(0, Players).All(i => FakeClients.PlayerOf(i)?.Identity != null), "five players joined", 30);
            while (joined.MoveNext()) yield return joined.Current;
            _steam = Enumerable.Range(0, Players).Select(i => FakeClients.PlayerOf(i).Id.SteamId).ToArray();
            _identity = Enumerable.Range(0, Players).Select(i => FakeClients.PlayerOf(i).Identity.IdentityId).ToArray();
            _fakes = new HashSet<long>(_identity);

            foreach (var name in Settings) _saved[name] = Get(name);
            var real = MySession.Static.Players.GetOnlinePlayers().Select(p => p.Id.SteamId).Where(s => s != 0 && !_steam.Contains(s));
            Set("Enabled", true);
            Set("TranslateGlobal", true);
            Set("InlineMode", false);
            Set("OptedOutReceivers", string.Join(";", real));
            Set("NativeLanguages", string.Join(";", Enumerable.Range(0, Players).Select(i => _steam[i] + "=" + Codes[i])));
            Set("ChosenLanguages", string.Join(";", _steam));
            Patch();

            // --- each language to the four others, in theirs
            var times = new List<string>();
            for (var from = 0; from < Players; from++)
            {
                var step = EachInTheirs(Codes[from] + " in global chat", from, Texts[from], false, times);
                while (step.MoveNext()) yield return step.Current;
            }
            Note("two messages: " + string.Join("; ", times));

            // --- inline: the message held, "text (translation)" to each who needs it
            Set("InlineMode", true);
            var inlineTimes = new List<string>();
            for (var from = 2; from < Players; from++)
            {
                var step = EachInTheirs(Codes[from] + " in global chat, inline", from, Texts[from], true, inlineTimes);
                while (step.MoveNext()) yield return step.Current;
            }
            Note("inline: " + string.Join("; ", inlineTimes));
            Set("InlineMode", false);

            // --- a short English line from a player whose language is not chosen
            Set("ChosenLanguages", string.Join(";", _steam.Where((s, i) => i != 1)));
            Set("WrittenLanguages", "");
            const string shortEnglish = "server restart soon?";
            var detected = Code(shortEnglish);
            Say(1, shortEnglish);
            var settle = WaitForSeconds(QuietSeconds, "the short line handled");
            while (settle.MoveNext()) yield return settle.Current;
            Drain();
            Note($"the short English line \"{shortEnglish}\" was taken for \"{detected}\"; the English reader's language is now \"{Native(1)}\" (written: {Get("WrittenLanguages")})");
            // a short line told by no word of its own is English by default and says nothing of who writes it
            Check(Native(1) == "en", $"one short English line \"{shortEnglish}\" made the English reader's language \"{Native(1)}\"");

            Note("TRANSLATOR MULTILANG RESULT | five languages each to the four others in theirs, two-message and inline mode; short English line: " + detected + " -> reader " + Native(1));
        }

        /// <summary>A message, and each of the other four gets it in their own language, the author nothing.</summary>
        private IEnumerator EachInTheirs(string what, int from, string text, bool inline, List<string> times)
        {
            var source = Code(text);
            Check(source == Codes[from], what + ": the plugin takes \"" + text + "\" for " + source + ", not " + Codes[from]);
            var to = Enumerable.Range(0, Players).Where(i => i != from).ToArray();
            var started = DateTime.UtcNow;
            var consumed = Say(from, text);
            Check(consumed == inline, what + ": the message was " + (consumed ? "" : "not ") + "held");
            // each reader's first translation, when it came
            var came = new Dictionary<long, double>();
            while ((DateTime.UtcNow - started).TotalSeconds < TranslateSeconds && came.Count < to.Length)
            {
                foreach (var s in Captured.Where(s => IsOurs(s, inline)))
                    if (!came.ContainsKey(s.To)) came[s.To] = (DateTime.UtcNow - started).TotalSeconds;
                yield return null;
            }
            var took = (DateTime.UtcNow - started).TotalSeconds;
            var when = string.Join(", ", to.Select(r => Codes[r] + (came.TryGetValue(_identity[r], out var t) ? " " + t.ToString("F1") + " s" : " none")));
            Note(what + ": translations came - " + when);
            Check(came.Count == to.Length, what + ": not every reader got a translation in " + TranslateSeconds + " s: " + when + "; captured " +
                                           string.Join(" / ", Captured.Select(s => Who(s.To) + " <" + s.Author + "|" + s.Font + "> " + s.Text)));
            var quiet = WaitForSeconds(QuietSeconds, what + ": nothing more");
            while (quiet.MoveNext()) yield return quiet.Current;

            var sent = Drain().Where(s => IsOurs(s, inline)).ToList();
            Check(!sent.Any(s => s.To == _identity[from]), what + ": the author got a translation of their own message");
            var lines = new List<string>();
            foreach (var reader in to)
            {
                var theirs = sent.Where(s => s.To == _identity[reader]).ToList();
                Check(theirs.Count == 1, what + ": " + Codes[reader] + " got " + theirs.Count + " translations: " + string.Join(" / ", theirs.Select(s => s.Text)));
                var translation = theirs[0].Text;
                if (inline)
                {
                    Check(translation.Contains(text), what + ": " + Codes[reader] + " got \"" + translation + "\" without the original");
                    translation = translation.Replace(text, "").Trim(' ', '(', ')', '[', ']', '-', '—', '|');
                }
                var language = Code(translation);
                Check(language == Codes[reader], what + ": " + Codes[reader] + " got \"" + translation + "\", taken for " + language);
                lines.Add(Codes[reader] + ": " + translation);
            }
            times.Add(Codes[from] + " " + took.ToString("F1") + " s");
            Note(what + " (" + took.ToString("F1") + " s): " + string.Join(" | ", lines));
        }

        private string Who(long identity)
        {
            var i = Array.IndexOf(_identity, identity);
            return i >= 0 ? Codes[i] : identity.ToString();
        }

        private bool IsOurs(Sent s, bool inline) =>
            inline ? s.Font == "Player" && s.Text.Contains("(") : s.Font.StartsWith("Colored") && s.Author.EndsWith((string)Get("TranslationSuffix"));

        private string Code(string text) => (string)_languageCode.Invoke(null, new[] { _sourceLang.Invoke(null, new object[] { text }) });

        private bool Say(int from, string text)
        {
            Drain();
            var msg = new ChatMsg { Text = text, Author = _steam[from], Channel = (byte)ChatChannel.Global, TargetId = 0 };
            var args = new object[] { msg, false };
            FakeClients.AsClient(from, () => _raise.Invoke(_chat, args));
            return (bool)args[1];
        }

        private bool Ready()
        {
            var instance = _plugin.GetProperty("Instance", Any)?.GetValue(null);
            var setup = instance?.GetType().GetField("_setup", Any)?.GetValue(instance);
            return setup != null && (bool)(setup.GetType().GetProperty("Ready", Any) ?? setup.GetType().GetProperty("ServerRunning", Any)).GetValue(setup);
        }

        private object Get(string name) => _config.GetType().GetProperty(name).GetValue(_config);

        private void Set(string name, object value) => _config.GetType().GetProperty(name).SetValue(_config, value);

        private string Native(int player)
        {
            var m = Regex.Match((string)Get("NativeLanguages") ?? "", "(^|;)" + _steam[player] + "=(\\w+)");
            return m.Success ? m.Groups[2].Value : "en";
        }

        private static List<Sent> Drain()
        {
            var list = new List<Sent>();
            while (Captured.TryDequeue(out var s)) list.Add(s);
            return list;
        }

        private static void Patch()
        {
            _capturing = true;
            if (_harmony != null) return;
            _harmony = new Harmony("SentisTests.TranslatorMultilang");
            var colored = typeof(MyVisualScriptLogicProvider).GetMethod("SendChatMessageColored", new[] { typeof(string), typeof(Color), typeof(string), typeof(long), typeof(string) });
            if (colored != null)
                _harmony.Patch(colored, prefix: new HarmonyMethod(typeof(TranslatorMultilangScenario).GetMethod(nameof(SendColoredPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            // the plugin sends only to players Torch can address (ChatHook.Reachable: a connected client, since v0.3.3); the
            // fake players have no client: taken as reachable while the scenario runs
            var reachable = Integrations.TypeOf(PluginName, "SentisTranslator.ChatHook")?.GetMethod("Reachable", Any);
            if (reachable != null)
                _harmony.Patch(reachable, prefix: new HarmonyMethod(typeof(TranslatorMultilangScenario).GetMethod(nameof(ReachablePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            var asPlayer = Integrations.TypeOf(PluginName, "SentisTranslator.ChatHook")?.GetMethod("SendAsPlayer", Any);
            if (asPlayer != null)
                _harmony.Patch(asPlayer, prefix: new HarmonyMethod(typeof(TranslatorMultilangScenario).GetMethod(nameof(SendAsPlayerPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void SendColoredPrefix(string message, Color color, string author, long playerId, string font)
        {
            if (_capturing && _fakes.Contains(playerId))
                Captured.Enqueue(new Sent { Text = message ?? "", Author = author ?? "", To = playerId, Font = "Colored " + font });
        }

        private static bool ReachablePrefix(long identity, ref bool __result)
        {
            if (!_capturing || !_fakes.Contains(identity)) return true;
            __result = true;
            return false;
        }

        private static void SendAsPlayerPrefix(IEnumerable readers, string text, object author)
        {
            if (!_capturing || readers == null) return;
            var name = author?.GetType().GetField("Name")?.GetValue(author) as string ?? "";
            foreach (var reader in readers)
            {
                var identity = (long)reader.GetType().GetField("Identity").GetValue(reader);
                if (_fakes.Contains(identity)) Captured.Enqueue(new Sent { Text = text ?? "", Author = name, To = identity, Font = "Player" });
            }
        }

        public override void Cleanup()
        {
            try
            {
                _capturing = false;
                Drain();
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
