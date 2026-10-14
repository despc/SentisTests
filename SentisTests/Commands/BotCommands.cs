using System;
using SentisTests.Game;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Commands
{
    /// <summary>
    /// !add_bot | !remove_bots: a fake player hanging by the admin, so that the physics of his cluster steps with
    /// nobody real near (EnableSelectivePhysicsUpdates steps only the clusters with a character in them).
    /// </summary>
    public class BotCommands : CommandModule
    {
        private const double Distance = 10;
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        [Command("add_bot", "Spawn a fake player hanging 10 m in front of you: the physics of your cluster steps while it is there")]
        [Permission(MyPromoteLevel.Admin)]
        public void AddBot()
        {
            var character = Context.Player?.Character;
            if (character == null || character.IsDead)
            {
                Context.Respond("only for a player in the world: the bot is put 10 m in front of your character");
                return;
            }
            var matrix = character.WorldMatrix;
            var at = matrix.Translation + matrix.Forward * Distance + matrix.Up;
            try
            {
                FakeClients.Add(1, Network, _ => (at, 0, 0), withCharacters: true);
            }
            catch (Exception e)
            {
                SentisTestsPlugin.Log.Error(e, "!add_bot failed");
                Context.Respond("the bot was not added: " + e.Message);
                return;
            }
            var index = FakeClients.Count - 1;
            var bot = FakeClients.PlayerOf(index);
            Context.Respond((bot?.DisplayName ?? "a bot") + " hangs " + Distance + " m in front of you (" + FakeClients.Count +
                            " fake players now); !remove_bots takes them away, and so does any !test run");
        }

        [Command("remove_bots", "Remove every fake player: the ones of !add_bot and any a test left")]
        [Permission(MyPromoteLevel.Admin)]
        public void RemoveBots()
        {
            var count = FakeClients.Count;
            FakeClients.RemoveAll();
            Context.Respond(count == 0 ? "no fake players" : "removed " + count + " fake players");
        }
    }
}
