using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
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
    /// SentisTranslator in the chat of a live server, with the stand's model (llama-server) translating for real.
    ///
    /// Four fake players: A and D read Russian, B and C English. Their messages go through Torch's chat pipeline as a
    /// player's do (ChatManagerServer.RaiseMessageRecieved, what Torch calls for every chat message the server gets), and
    /// what the plugin sends (MyVisualScriptLogicProvider.SendChatMessage) is taken down by player:
    /// - global chat, Russian: the translation, in English, to B and C only; English: in Russian, to A and D only;
    /// - "!st off": C gets no more translations, "!st on" again; "!status" and the like are not "!st" (no reply);
    /// - commands, "ok", a link alone, a message of the server itself: nothing;
    /// - the language a player writes in becomes theirs: C writes Russian, C reads Russian;
    /// - faction chat: the translation to the members of the faction who need it, not to the author; private chat: to
    ///   the addressee if they need it;
    /// - inline mode: the message held, the readers who need it get "text (translation)", the others the original at once;
    /// - "!st disable" from a player who is not an admin: refused.
    /// The plugin's settings are changed in memory and put back (the players' languages too), real players online are
    /// left out of it (taken as having translations off while it runs).
    ///
    /// <see cref="RestartName"/>: the model's process dies (killed, as a crash would); the plugin starts it again by
    /// itself and the chat is translated again.
    /// </summary>
    public sealed class TranslatorChatScenario : TestScenario
    {
        public const string ScenarioName = "translator_chat";
        public const string RestartName = "translator_restart";
        private const int RestartSeconds = 180;
        public const string PluginName = "SentisTranslator";
        private const int A = 0, B = 1, C = 2, D = 3;
        private const int TranslateSeconds = 30;
        private const double QuietSeconds = 2.5;
        private const int ReadySeconds = 300;
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private const string RuText = "Привет всем, кто хочет вместе строить базу у планеты?";
        private const string EnText = "Anyone want to team up and build a base near the moon?";

        public struct Sent
        {
            public string Text, Author, Font;
            public long To;
            public override string ToString() => $"{To} <{Author}|{Font}> {Text}";
        }

        private static readonly ConcurrentQueue<Sent> Captured = new ConcurrentQueue<Sent>();
        private static volatile bool _capturing;
        // what goes to the fake players, and to identity 0 (everyone): real players online are not this scenario's
        private static volatile HashSet<long> _fakes = new HashSet<long>();
        private static Harmony _harmony;

        private static readonly string[] Settings =
            { "AutoRestartServer", "Enabled", "TranslateGlobal", "TranslateFaction", "TranslatePrivate", "InlineMode", "OptedOutReceivers", "NativeLanguages" };

        private readonly Dictionary<string, object> _saved = new Dictionary<string, object>();
        private readonly List<long> _factions = new List<long>();
        private object _config;
        private Type _plugin;
        private object _chat;
        private MethodInfo _raise;
        private ulong[] _steam;
        private long[] _identity;

        private readonly bool _restart;

        public TranslatorChatScenario(bool restart = false)
        {
            _restart = restart;
        }

        public override string Name => _restart ? RestartName : ScenarioName;
        public override int TimeoutSeconds => ReadySeconds + (_restart ? RestartSeconds + 60 : 300);
        public override string[] Requires => new[] { PluginName };

        public override IEnumerator Run()
        {
            _plugin = RequireType(Integrations.TypeOf(PluginName, "SentisTranslator.SentisTranslatorPlugin"), "SentisTranslator");
            _config = _plugin.GetProperty("Config", Any)?.GetValue(null);
            Check(_config != null, "SentisTranslator has no config");
            _chat = SentisTestsPlugin.TorchInstance.CurrentSession.Managers.GetManager<IChatManagerServer>();
            _raise = _chat?.GetType().GetMethod("RaiseMessageRecieved", Any, null, new[] { typeof(ChatMsg), typeof(bool).MakeByRefType() }, null);
            Check(_raise != null, "Torch's ChatManagerServer.RaiseMessageRecieved(ChatMsg, ref bool) is not there");

            var ready = Wait(Ready, "the translator's llama-server running", ReadySeconds);
            while (ready.MoveNext()) yield return ready.Current;

            FakeClients.Add(4, Network, i => (new Vector3D(300000, 300000 + i * 20, 300000), 0, 0));
            var joined = Wait(() => Enumerable.Range(0, 4).All(i => FakeClients.PlayerOf(i)?.Identity != null), "four players joined", 30);
            while (joined.MoveNext()) yield return joined.Current;
            _steam = Enumerable.Range(0, 4).Select(i => FakeClients.PlayerOf(i).Id.SteamId).ToArray();
            _identity = Enumerable.Range(0, 4).Select(i => FakeClients.PlayerOf(i).Identity.IdentityId).ToArray();
            _fakes = new HashSet<long>(_identity);

            foreach (var name in Settings) _saved[name] = Get(name);
            var real = MySession.Static.Players.GetOnlinePlayers().Select(p => p.Id.SteamId).Where(s => s != 0 && !_steam.Contains(s));
            Set("Enabled", true);
            Set("TranslateGlobal", true);
            Set("TranslateFaction", true);
            Set("TranslatePrivate", true);
            Set("InlineMode", false);
            Set("OptedOutReceivers", string.Join(";", real));
            Set("NativeLanguages", $"{_steam[A]}=ru;{_steam[B]}=en;{_steam[C]}=en;{_steam[D]}=ru");
            Patch();

            if (_restart)
            {
                var r = Restart();
                while (r.MoveNext()) yield return r.Current;
                yield break;
            }

            // --- global chat, both ways
            var step = Translated("Russian in global chat", A, RuText, ChatChannel.Global, 0, new[] { B, C }, english: true);
            while (step.MoveNext()) yield return step.Current;
            step = Translated("English in global chat", B, EnText, ChatChannel.Global, 0, new[] { A, D }, english: false);
            while (step.MoveNext()) yield return step.Current;

            // --- !st off / on
            step = StReply("!st off", C, "Translations OFF");
            while (step.MoveNext()) yield return step.Current;
            Check(OptedOut().Contains(_steam[C].ToString()), "C is not among those with translations off after \"!st off\": " + Get("OptedOutReceivers"));
            step = Translated("Russian with C's translations off", A, "Кто-нибудь видел, где тут лёд на планете?", ChatChannel.Global, 0, new[] { B }, english: true);
            while (step.MoveNext()) yield return step.Current;
            step = StReply("!st on", C, "Translations ON");
            while (step.MoveNext()) yield return step.Current;
            Check(!OptedOut().Contains(_steam[C].ToString()), "C is still among those with translations off after \"!st on\"");

            // --- other plugins' commands are not !st, and what is not to be translated is not
            foreach (var text in new[] { "!status", "!stats", "!stuck" })
            {
                step = Nothing("another command: " + text, A, text, ChatChannel.Global, 0, null);
                while (step.MoveNext()) yield return step.Current;
            }
            foreach (var text in new[] { "!help please, anyone", "ok", "https://example.com/some/page", "/give me ice" })
            {
                step = Nothing("not to be translated: " + text, A, text, ChatChannel.Global, 0, null);
                while (step.MoveNext()) yield return step.Current;
            }
            step = Nothing("a message of the server itself", -1, RuText, ChatChannel.Global, 0, null);
            while (step.MoveNext()) yield return step.Current;

            // --- the language a player writes in becomes theirs
            step = Translated("C writes Russian", C, "Ребята, у кого есть лишние стальные пластины?", ChatChannel.Global, 0, new[] { B }, english: true);
            while (step.MoveNext()) yield return step.Current;
            Check(Native(C) == "ru", "C wrote Russian and reads " + Native(C) + ": " + Get("NativeLanguages"));
            step = WaitForSeconds(1.2, "past the plugin's one second between language changes");
            while (step.MoveNext()) yield return step.Current;
            step = Translated("C writes English again", C, "Never mind, I found some steel plates in the base.", ChatChannel.Global, 0, new[] { A, D }, english: false);
            while (step.MoveNext()) yield return step.Current;
            Check(Native(C) == "en", "C wrote English and reads " + Native(C));

            // --- faction chat: A's faction with B and D in it, C outside
            var suffix = (DateTime.UtcNow.Ticks % 900 + 100).ToString();
            var factions = MySession.Static.Factions;
            ((IMyFactionCollection)factions).CreateFaction(_identity[A], "TR" + suffix, "Translator " + suffix, "", "");
            var made = Wait(() => factions.GetPlayerFaction(_identity[A]) != null, "A's faction made", 15);
            while (made.MoveNext()) yield return made.Current;
            var faction = factions.GetPlayerFaction(_identity[A]);
            _factions.Add(faction.FactionId);
            factions.AddPlayerToFaction(_identity[B], faction.FactionId);
            factions.AddPlayerToFaction(_identity[D], faction.FactionId);
            var members = Wait(() => faction.IsMember(_identity[B]) && faction.IsMember(_identity[D]), "B and D in A's faction", 15);
            while (members.MoveNext()) yield return members.Current;
            step = Translated("Russian in faction chat", A, "Давайте соберёмся на базе через десять минут.", ChatChannel.Faction, faction.FactionId, new[] { B }, english: true);
            while (step.MoveNext()) yield return step.Current;
            step = Translated("English in faction chat", B, "Sure, I will bring the drills with me.", ChatChannel.Faction, faction.FactionId, new[] { A, D }, english: false);
            while (step.MoveNext()) yield return step.Current;

            // --- private chat: the addressee
            step = Translated("Russian in private to B", A, "Можешь прислать мне координаты своей базы?", ChatChannel.Private, _identity[B], new[] { B }, english: true);
            while (step.MoveNext()) yield return step.Current;
            step = Nothing("Russian in private to D (reads Russian)", A, "Можешь прислать мне координаты своей базы?", ChatChannel.Private, _identity[D], false);
            while (step.MoveNext()) yield return step.Current;

            // --- inline mode: held, combined to those who need it, the original to the others at once
            Set("InlineMode", true);
            step = Inline();
            while (step.MoveNext()) yield return step.Current;
            Set("InlineMode", false);

            // --- an admin's command from a player who is not one
            if (!MySession.Static.IsUserAdmin(_steam[A]))
            {
                var before = Get("OptedOutReceivers");
                step = StReply("!st disable " + FakeClients.PlayerOf(B).DisplayName, A, "admin only");
                while (step.MoveNext()) yield return step.Current;
                Check(Equals(Get("OptedOutReceivers"), before), "\"!st disable\" from a player who is not an admin changed who has translations off");
            }
            else Note("A is an admin on this server: the refusal of \"!st disable\" is not checked");

            Note("TRANSLATOR CHAT RESULT | global both ways, !st off/on, other commands and untranslatable text left, the writer's language learnt, faction, private, inline, admin command refused");
        }

        // ------------------------------------------------------------------ steps

        /// <summary>The message is said and translated to exactly <paramref name="to"/>, in the language expected, and nothing more.</summary>
        private IEnumerator Translated(string what, int from, string text, ChatChannel channel, long target, int[] to, bool english)
        {
            var consumed = Say(from, text, channel, target);
            Check(!consumed, what + ": the message was held (only inline mode does that, and it is off)");
            var want = to.Select(i => _identity[i]).OrderBy(x => x).ToArray();
            var got = Wait(() => Translations().Select(s => s.To).Distinct().Count() >= want.Length, what + ": translated to " + string.Join(",", to.Select(Letter)), TranslateSeconds);
            var started = DateTime.UtcNow;
            while (got.MoveNext()) yield return got.Current;
            var took = (DateTime.UtcNow - started).TotalSeconds;
            var quiet = WaitForSeconds(QuietSeconds, what + ": nothing more");
            while (quiet.MoveNext()) yield return quiet.Current;

            var sent = Drain();
            var translations = sent.Where(IsTranslation).ToList();
            Check(sent.Count == translations.Count, what + ": besides the translations the plugin sent " + string.Join(" / ", sent.Where(s => !IsTranslation(s))));
            Check(translations.Select(s => s.To).OrderBy(x => x).SequenceEqual(want),
                what + ": translations went to " + string.Join(",", translations.Select(s => Who(s.To))) + ", not " + string.Join(",", to.Select(Letter)));
            var translation = translations[0].Text;
            Check(translations.All(s => s.Text == translation), what + ": the readers got different translations");
            Check(english ? Cyrillic(translation) < 0.05 : Cyrillic(translation) > 0.5, what + ": \"" + translation + "\" is not " + (english ? "English" : "Russian"));
            Note(what + ": \"" + translation + "\" to " + string.Join(",", to.Select(Letter)) + " in " + took.ToString("F1") + " s");
        }

        /// <summary>The message is said, and the plugin sends nothing for it. <paramref name="consumedWant"/>: held or not, null when others decide (Torch takes every "!" and "/").</summary>
        private IEnumerator Nothing(string what, int from, string text, ChatChannel channel, long target, bool? consumedWant)
        {
            var consumed = Say(from, text, channel, target);
            if (consumedWant != null) Check(consumed == consumedWant, what + ": the message " + (consumed ? "was" : "was not") + " held");
            var quiet = WaitForSeconds(QuietSeconds + 1, what + ": nothing sent");
            while (quiet.MoveNext()) yield return quiet.Current;
            var sent = Drain();
            Check(sent.Count == 0, what + ": the plugin sent " + string.Join(" / ", sent));
            Note(what + ": nothing sent");
        }

        /// <summary>A "!st" command: held, and the reply to its author only.</summary>
        private IEnumerator StReply(string text, int from, string reply)
        {
            var consumed = Say(from, text, ChatChannel.Global, 0);
            Check(consumed, text + ": not held");
            var got = Wait(() => Captured.Any(s => s.Text.StartsWith("[ST]")), text + ": a reply", 10);
            while (got.MoveNext()) yield return got.Current;
            var quiet = WaitForSeconds(QuietSeconds, text + ": nothing more");
            while (quiet.MoveNext()) yield return quiet.Current;
            var sent = Drain();
            Check(sent.Count == 1 && sent[0].To == _identity[from] && sent[0].Text.Contains(reply),
                text + ": sent " + string.Join(" / ", sent) + ", not \"" + reply + "\" to " + Letter(from));
            Note(text + ": " + sent[0].Text);
        }

        private IEnumerator Inline()
        {
            const string what = "inline mode";
            var consumed = Say(A, RuText, ChatChannel.Global, 0);
            Check(consumed, what + ": the message was not held");
            // D reads Russian: the original, at once (in the same call)
            var first = Drain();
            Check(first.Count == 1 && first[0].To == _identity[D] && first[0].Text == RuText && !first[0].Author.Contains("→"),
                what + ": at once sent " + string.Join(" / ", first) + ", not the original to D");
            var got = Wait(() => Captured.Select(s => s.To).Distinct().Count() >= 2, what + ": combined to B and C", TranslateSeconds);
            while (got.MoveNext()) yield return got.Current;
            var quiet = WaitForSeconds(QuietSeconds, what + ": nothing more");
            while (quiet.MoveNext()) yield return quiet.Current;
            var sent = Drain();
            Check(sent.Select(s => s.To).OrderBy(x => x).SequenceEqual(new[] { _identity[B], _identity[C] }.OrderBy(x => x)),
                what + ": combined sent to " + string.Join(",", sent.Select(s => Who(s.To))) + ", not B,C");
            var combined = sent[0].Text;
            Check(combined.StartsWith(RuText) && combined.Length > RuText.Length + 5 && Cyrillic(combined.Substring(RuText.Length)) < 0.05,
                what + ": \"" + combined + "\" is not the original and its English");
            Note(what + ": original to D at once, \"" + combined + "\" to B,C");
        }

        private IEnumerator Restart()
        {
            SkipUnless((bool)Get("AutoRestartServer"), "AutoRestartServer is off in SentisTranslator.cfg");
            var process = ServerProcess();
            Check(process != null, "no llama-server process of the plugin");
            var pid = process.Id;
            process.Kill();
            var down = Wait(() => !Ready(), "the plugin sees its llama-server gone", 15);
            while (down.MoveNext()) yield return down.Current;
            var started = DateTime.UtcNow;
            var up = Wait(() => Ready() && ServerProcess()?.Id != pid, "llama-server started again by the plugin", RestartSeconds);
            while (up.MoveNext()) yield return up.Current;
            var took = (DateTime.UtcNow - started).TotalSeconds;
            Note("llama-server " + pid + " killed, " + ServerProcess()?.Id + " up in " + took.ToString("F0") + " s");
            var step = Translated("Russian in global chat after the restart", A, RuText, ChatChannel.Global, 0, new[] { B, C }, english: true);
            while (step.MoveNext()) yield return step.Current;
            var left = System.Diagnostics.Process.GetProcessesByName("llama-server").Count(p => p.Id == pid);
            Check(left == 0, "the killed llama-server " + pid + " is still there");
            Note("TRANSLATOR RESTART RESULT | llama-server killed, started again by the plugin in " + took.ToString("F0") + " s, translating again");
        }

        private System.Diagnostics.Process ServerProcess()
        {
            var instance = _plugin.GetProperty("Instance", Any)?.GetValue(null);
            var setup = instance?.GetType().GetField("_setup", Any)?.GetValue(instance);
            return setup?.GetType().GetField("_serverProcess", Any)?.GetValue(setup) as System.Diagnostics.Process;
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>A chat message from fake player <paramref name="from"/> (-1: the server) through Torch's chat pipeline; whether it was held.</summary>
        private bool Say(int from, string text, ChatChannel channel, long target)
        {
            Drain();
            var msg = new ChatMsg
            {
                Text = text,
                Author = from < 0 ? 0 : _steam[from],
                Channel = (byte)channel,
                TargetId = target,
            };
            var args = new object[] { msg, false };
            _raise.Invoke(_chat, args);
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

        private HashSet<string> OptedOut() =>
            new HashSet<string>(((string)Get("OptedOutReceivers") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));

        private string Native(int player)
        {
            var m = Regex.Match((string)Get("NativeLanguages") ?? "", "(^|;)" + _steam[player] + "=(\\w+)");
            return m.Success ? m.Groups[2].Value : "en";
        }

        private IEnumerable<Sent> Translations() => Captured.Where(IsTranslation);

        private static bool IsTranslation(Sent s) => s.Font == "Green" && s.Author.EndsWith("→");

        private static List<Sent> Drain()
        {
            var list = new List<Sent>();
            while (Captured.TryDequeue(out var s)) list.Add(s);
            return list;
        }

        private static double Cyrillic(string text)
        {
            int cyr = text.Count(c => c >= 'Ѐ' && c <= 'ӿ');
            int lat = text.Count(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z');
            return cyr + lat == 0 ? 0 : (double)cyr / (cyr + lat);
        }

        private static string Letter(int i) => ((char)('A' + i)).ToString();

        private string Who(long identity)
        {
            var i = Array.IndexOf(_identity, identity);
            return i >= 0 ? Letter(i) : identity.ToString();
        }

        private static void Patch()
        {
            _capturing = true;
            if (_harmony != null) return;
            var target = typeof(MyVisualScriptLogicProvider).GetMethod("SendChatMessage", new[] { typeof(string), typeof(string), typeof(long), typeof(string) })
                         ?? throw new MissingMethodException("MyVisualScriptLogicProvider", "SendChatMessage");
            _harmony = new Harmony("SentisTests.TranslatorChat");
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(TranslatorChatScenario).GetMethod(nameof(SendPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void SendPrefix(string message, string author, long playerId, string font)
        {
            if (_capturing && (playerId == 0 || _fakes.Contains(playerId))) Captured.Enqueue(new Sent { Text = message ?? "", Author = author ?? "", To = playerId, Font = font ?? "" });
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
                foreach (var faction in _factions) MyFactionCollection.RemoveFaction(faction);
                _factions.Clear();
                FakeClients.RemoveAll();
            }
            finally { base.Cleanup(); }
        }
    }
}
