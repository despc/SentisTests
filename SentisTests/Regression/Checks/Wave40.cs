using System;
using System.Collections;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Game;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The editor of a programmable block (<c>MyProgrammableBlock.OpenEditorRequest</c>): one player at a time has it open.
    /// The first player who asks gets it; a second player who asks while it is open is told it is busy and stays on the
    /// server; the holder who asks again (a second click before the answer) gets it again; once the holder closes it,
    /// the second player gets it.
    ///
    /// (The game answers "busy" as an event of the block while the event is a static one: the replication layer does not
    /// find it, throws, and the server drops the player who asked - seen on prod, fixed in SentisOptimisations.)
    /// </summary>
    public sealed class ProgrammableEditorCheck : RegressionCheck
    {
        public override string Name => Prefix + "programmable_editor";

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly FieldInfo Open = typeof(MyProgrammableBlock).GetField("m_consoleOpen", Any);
        private static readonly PropertyInfo Holder = typeof(MyProgrammableBlock).GetProperty("UserId", Any);

        protected override IEnumerator Check()
        {
            Check(Open != null && Holder != null, "MyProgrammableBlock has no m_consoleOpen or UserId in this game");
            yield return Arrive(Site.At(0, 0, -4));
            var other = -1;
            yield return Join(Site.At(2, 0, -4), i => other = i);
            var station = Build("pb-editor", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("MyProgrammableBlock/LargeProgrammableBlock", 1, 0, 0));
            var pb = Block<MyProgrammableBlock>(station);
            ulong Steam(int client) => FakeClients.PlayerOf(client).Id.SteamId;
            string Who() => "open " + Open.GetValue(pb) + ", holder " + Holder.GetValue(pb) + " (me " + Steam(Me) + ", other " + Steam(other) + ")";
            string Ask(int client)
            {
                try
                {
                    RequestAs(client, pb, "OpenEditorRequest");
                    return null;
                }
                catch (Exception e)
                {
                    // what the server's receive queue would do with it: drop the client
                    return e.GetType().Name + ": " + e.Message;
                }
            }

            var first = Ask(Me);
            Check(first == null, "opening the editor of a closed block threw: " + first);
            Check((bool)Open.GetValue(pb) && (ulong)Holder.GetValue(pb) == Steam(Me), "the editor not given to the first player: " + Who());

            var busy = Ask(other);
            Check(busy == null, "a second player opening the editor someone has open: the server would drop them - " + busy);
            Check((ulong)Holder.GetValue(pb) == Steam(Me), "the editor taken from its holder by a second player: " + Who());

            var again = Ask(Me);
            Check(again == null, "the holder opening the editor again: the server would drop them - " + again);
            Check((bool)Open.GetValue(pb) && (ulong)Holder.GetValue(pb) == Steam(Me), "the holder lost the editor on asking again: " + Who());

            Request(pb, "CloseEditor");
            Check(!(bool)Open.GetValue(pb), "the editor still open after its holder closed it: " + Who());
            var next = Ask(other);
            Check(next == null, "the second player opening the closed editor threw: " + next);
            Check((ulong)Holder.GetValue(pb) == Steam(other), "the editor not given to the second player once closed: " + Who());
            RequestAs(other, pb, "CloseEditor");
            Result("the editor: one holder at a time, a second player told it is busy (not dropped), the holder asking again keeps it, closed it goes to the next");
        }
    }
}
