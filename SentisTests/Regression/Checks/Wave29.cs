using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage.Network;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The routing of a player's chat on the server (<c>MyMultiplayerBase.OnChatMessageReceived_Server</c>): a global
    /// message goes to every other player; a faction one only to the faction's members, and not at all when a stranger
    /// sends it; a whisper only to the player it names. The addressee of each delivery is taken from the event the
    /// server raises to its endpoint (a patch of the check's), so nothing of the game's routing is skipped.
    /// </summary>
    public sealed class ChatRoutingCheck : FactionCheck
    {
        public override string Name => Prefix + "chat_routing";

        private const string Marker = "reg-chat ";
        private static readonly HashSet<(string Text, ulong Endpoint)> Sent = new HashSet<(string, ulong)>();
        private static Harmony _harmony;
        public static int Seen;
        public static int Entered;
        public static string Probe = "";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -12));
            int mate = -1, stranger = -1;
            yield return Join(Site.At(-6, 6, -12), i => mate = i);
            yield return Join(Site.At(6, 6, -12), i => stranger = i);
            var faction = Found(mate);
            Enter(Me, faction, mate);
            Patch();

            var chat = typeof(Sandbox.Engine.Multiplayer.MyMultiplayerBase).GetMethod("OnChatMessageReceived_Server", BindingFlags.Static | BindingFlags.NonPublic);
            void Send(int from, Sandbox.Game.Gui.ChatChannel channel, long target, string text)
            {
                lock (Sent) Sent.Clear();
                var msg = new ChatMsg { Text = Marker + text, Author = FakeClients.PlayerOf(from).Id.SteamId, Channel = (byte)channel, TargetId = target, CrossChatEnabled = true };
                FakeClients.AsClient(from, () => chat.Invoke(null, new object[] { msg }));
            }
            List<ulong> To(string text)
            {
                lock (Sent) return Sent.Where(d => d.Text == Marker + text).Select(d => d.Endpoint).OrderBy(x => x).ToList();
            }
            string Got(string text) => string.Join(",", To(text));
            // the game's anti-spam keeps, per sender, the time of the last message: the check's players start clean, and
            // after the anti-spam's own test its sender is let off the timeout it got (the setting stays as the server has it)
            var spam = (IDictionary)typeof(Sandbox.Engine.Multiplayer.MyMultiplayerBase).GetField("m_chatAntiSpamDatas", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(Sandbox.Engine.Multiplayer.MyMultiplayer.Static);
            Check(spam != null, "MyMultiplayerBase has no m_chatAntiSpamDatas");
            void Forget(int who) => spam.Remove(IdentityOf(who));
            Forget(Me);
            Forget(mate);
            Forget(stranger);

            var meSteam = FakeClients.PlayerOf(Me).Id.SteamId;
            var mateSteam = FakeClients.PlayerOf(mate).Id.SteamId;
            var strangerSteam = FakeClients.PlayerOf(stranger).Id.SteamId;

            Send(Me, Sandbox.Game.Gui.ChatChannel.Global, 0, "global");
            // players of the stand online besides the check's take it too: only that the two joined and the sender
            // did not are this check's
            var global = To("global");
            Check(global.Contains(mateSteam) && global.Contains(strangerSteam) && !global.Contains(meSteam),
                "a global message went to " + string.Join(",", global) + " (patch saw " + Seen + " of " + Entered + " entries: " + Probe + "), not to both other players and not the sender");

            // the game's anti-spam of the dedicated server: a second message of the same player within its spam time is
            // dropped whole, and the sender put on a timeout
            var spamTime = Sandbox.MySandboxGame.ConfigDedicated.SpamMessagesTime;
            var antiSpam = Sandbox.MySandboxGame.ConfigDedicated.ChatAntiSpamEnabled && spamTime > 0;
            if (antiSpam)
            {
                Send(Me, Sandbox.Game.Gui.ChatChannel.Global, 0, "twice");
                Check(To("twice").Count == 0, "the second message within the spam time of " + spamTime + " s came to " + Got("twice"));
            }

            Forget(Me);
            Send(Me, Sandbox.Game.Gui.ChatChannel.Faction, ((MyFaction)faction).FactionId, "faction");
            Check(Got("faction") == mateSteam.ToString(), "a faction message went to " + Got("faction") + ", not to the mate alone");

            Send(stranger, Sandbox.Game.Gui.ChatChannel.Faction, ((MyFaction)faction).FactionId, "intruder");
            Check(To("intruder").Count == 0, "a faction message from a stranger went to " + Got("intruder"));

            Forget(Me);
            Send(Me, Sandbox.Game.Gui.ChatChannel.Private, IdentityOf(mate), "whisper");
            Check(Got("whisper") == mateSteam.ToString(), "a whisper went to " + Got("whisper") + ", not to the player it named");
            Result("global came to both others and not the sender" + (antiSpam ? " (a second within its " + spamTime + " s was dropped by the anti-spam)" : "") +
                   "; the faction one to the mate alone (a stranger's was dropped); the whisper to its addressee alone");
        }

        private static void Patch()
        {
            if (_harmony != null) return;
            // the sending of a chat event to one endpoint: the layer serialises and queues it there. The RaiseEvent of the
            // single-argument chat event is too small for a patch to hold (the JIT inlines it), the dispatch is not
            var raise = typeof(VRage.Network.MyReplicationLayer).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .First(m => m.Name == "DispatchEvent" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 8)
                .MakeGenericMethod(typeof(IMyEventOwner), typeof(ChatMsg), typeof(DBNull), typeof(DBNull), typeof(DBNull), typeof(DBNull), typeof(DBNull), typeof(IMyEventOwner));
            _harmony = new Harmony("SentisTests.ChatRouting");
            _harmony.Patch(raise, prefix: new HarmonyMethod(typeof(ChatRoutingCheck).GetMethod(nameof(Delivered), BindingFlags.Static | BindingFlags.NonPublic)));
            var enter = typeof(Sandbox.Engine.Multiplayer.MyMultiplayerBase).GetMethod("OnChatMessageReceived_Server", BindingFlags.Static | BindingFlags.NonPublic);
            _harmony.Patch(enter, prefix: new HarmonyMethod(typeof(ChatRoutingCheck).GetMethod(nameof(EnteredPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void EnteredPrefix(ChatMsg msg)
        {
            if (msg.Text == null || !msg.Text.StartsWith(Marker)) return;
            Entered++;
            Probe = "sender=" + VRage.Network.MyEventContext.Current.Sender.Value + ", online=" +
                    string.Join(",", Sandbox.Game.World.MySession.Static.Players.GetOnlinePlayers().Where(p => p.Id.SteamId != 0).Select(p => p.Id.SteamId)) +
                    ", spam=" + Sandbox.MySandboxGame.ConfigDedicated.ChatAntiSpamEnabled + ", identity=" +
                    Sandbox.Game.World.MySession.Static.Players.TryGetIdentityId(VRage.Network.MyEventContext.Current.Sender.Value);
        }

        private static void Delivered(VRage.Network.CallSite callSite, EndpointId recipient, ref ChatMsg arg2)
        {
            if (arg2.Text == null || !arg2.Text.StartsWith(Marker)) return;
            Seen++;
            lock (Sent) Sent.Add((arg2.Text, recipient.Value));
        }

        public override void Cleanup()
        {
            try { _harmony?.UnpatchAll("SentisTests.ChatRouting"); }
            catch (Exception e) { Log.Warn("unpatching the chat probe of {0}: {1}", Name, e.Message); }
            _harmony = null;
            base.Cleanup();
        }
    }
}
