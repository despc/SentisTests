using System.Collections;
using System.Linq;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Who a chat message sent by Steam id reaches (SentisTranslator, Discord messages duplicated once per bot, 23.10.2026):
    /// for every player online, whether Torch finds an identity for the id (<c>TryGetIdentityId</c>: none, and Torch's
    /// <c>SendMessageAsOther</c> sends to everyone) and whether a client of the id is connected (<c>Members</c>).
    /// </summary>
    public sealed class ChatReachScenario : TestScenario
    {
        public const string ScenarioName = "chat_reach";
        public override string Name => ScenarioName;

        public override IEnumerator Run()
        {
            var members = MyMultiplayer.Static.Members.ToList();
            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
            {
                var steam = player.Id.SteamId;
                Note("player " + player.DisplayName + " steam " + steam + " serial " + player.Id.SerialId + ": identity " + (player.Identity?.IdentityId ?? 0) +
                     ", Torch finds " + MySession.Static.Players.TryGetIdentityId(steam) + ", connected " + members.Contains(steam) +
                     ", name " + MyMultiplayer.Static.GetMemberName(steam) + (FakeClients.IsFake(steam) ? " (test fake)" : ""));
            }
            yield return null;
        }
    }
}
