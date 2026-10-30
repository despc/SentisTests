using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisGameplayImprovements' commands for a character the client sees elsewhere than the server (<c>!sync</c>,
    /// <c>!sync reset</c>, <c>!sync resend</c>; an admin's <c>!sync player</c>), each run by the server for the check's
    /// player: the character stays where it was, alive, the same entity, controlled by its player; "all" resends grids
    /// round it (two built 20 m off) and not the floor it stands on (a planet site); "resend" gives the character to its client anew and
    /// the client has it again (ready) - as the control is given back.
    /// </summary>
    public sealed class CharacterSyncCommandsCheck : RegressionCheck
    {
        public override string Name => Prefix + "character_sync";
        public override int TimeoutSeconds => 90;
        // on a planet: the character stands on the floor, which "all" leaves as it is
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            var floor = Build("sync-floor", Site.At(0, 0, 0), true,
                Enumerable.Range(-2, 5).SelectMany(x => Enumerable.Range(-2, 5).Select(z => new Part("CubeBlock/LargeBlockArmorBlock", x, 0, z))));
            Build("sync-near-a", Site.At(20, 3, 0), true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            Build("sync-near-b", Site.At(-20, 3, 0), true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            yield return Arrive(Site.At(0, 2.5, 0));
            // let go: it stands on the floor, its support
            FakeClients.SetFree(Me, true);
            yield return Seconds(3);
            var body = Body;
            var player = FakeClients.PlayerOf(Me);
            Check(body != null && !body.IsDead, "no live character");
            var steam = player.Id.SteamId;
            var at = body.PositionComp.GetPosition();

            var answers = Command("!sync player " + steam + " reset");
            yield return WaitForTicks(10);
            Note("reset: " + string.Join(" | ", answers));
            Same(body, at, "after reset");

            answers = Command("!sync player " + steam);
            yield return WaitForTicks(10);
            Note("all: " + string.Join(" | ", answers));
            var said = string.Join(" ", answers);
            var resent = System.Text.RegularExpressions.Regex.Match(said, @"отправлено заново: (\d+)");
            Check(resent.Success && int.Parse(resent.Groups[1].Value) >= 2, "the grids round it were not resent: " + said);
            var kept = System.Text.RegularExpressions.Regex.Match(said, @"не трогали: (\d+)");
            Check(kept.Success && int.Parse(kept.Groups[1].Value) >= 1, "the floor it stands on was resent too: " + said);
            Same(body, at, "after all");
            Check(!floor.MarkedForClose, "the floor is gone");

            answers = Command("!sync player " + steam + " resend");
            Note("resend: " + string.Join(" | ", answers));
            yield return Seconds(5);
            Same(body, at, "after resend");
            Check(FakeClients.ArrivedCount(new[] { (VRage.Game.Entity.MyEntity)body }) == 0, "the character is not on its client again in 5 s");
            Result("reset, all (grids resent: " + resent.Groups[1].Value + ") and resend: the character where it was, alive, controlled, on its client");
        }

        private void Same(Sandbox.Game.Entities.Character.MyCharacter body, Vector3D at, string when)
        {
            var player = FakeClients.PlayerOf(Me);
            Check(!body.MarkedForClose && !body.IsDead, when + ": the character is gone or dead");
            Check(player.Character == body, when + ": the player has another character");
            Check(player.Controller.ControlledEntity == body, when + ": the player does not control its character (" + player.Controller.ControlledEntity + ")");
            var moved = Vector3D.Distance(body.PositionComp.GetPosition(), at);
            Check(moved < 1.5, $"{when}: the character moved {moved:0.00} m");
        }

        private static List<string> Command(string text)
        {
            var answers = new List<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            commands?.HandleCommandFromServer(text, message => answers.Add(message.Message));
            return answers;
        }
    }
}
