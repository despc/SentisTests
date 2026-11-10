using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The admins' <c>!pcu &lt;radius&gt; [x y z]</c> (SO PcuCommands): a grid of known PCU made at a point, the command asked
    /// for 30 m round it from the console: the grid is among those with the most PCU, with its PCU, and the total is at least
    /// its PCU; a radius of 0 is refused.
    /// </summary>
    public sealed class PcuCommandCheck : RegressionCheck
    {
        public override string Name => Prefix + "pcu_command";

        protected override IEnumerator Check()
        {
            var grid = Build("pcu", Site.Origin, true,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 1, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 2, 0));
            yield return Seconds(1);
            var at = grid.PositionComp.WorldAABB.Center;
            var answer = Command(string.Format(CultureInfo.InvariantCulture, "!pcu 30 {0:0.0} {1:0.0} {2:0.0}", at.X, at.Y, at.Z));
            Note("!pcu answered: " + answer);
            var pcu = grid.BlocksPCU.ToString("#,0", new NumberFormatInfo { NumberGroupSeparator = " " });
            Check(answer.Contains(grid.DisplayName), "the grid is not in the answer");
            Check(answer.Contains(pcu + " PCU - " + grid.DisplayName), "the grid's PCU (" + pcu + ") is not by its name");
            var total = answer.Split('\n')[0];
            var number = new string(total.Substring(total.IndexOf(':') + 1).TakeWhile(c => char.IsDigit(c) || c == ' ').Where(char.IsDigit).ToArray());
            Check(long.TryParse(number, out var sum) && sum >= grid.BlocksPCU, "the total (" + number + ") is less than the grid's PCU " + grid.BlocksPCU);
            var refused = Command("!pcu 0");
            Check(refused.Contains("больше нуля"), "a radius of 0 was not refused: " + refused);
            Result($"!pcu 30 round a grid of {grid.BlocksPCU} PCU: in the answer with its PCU, total {sum}; a radius of 0 refused");
        }

        internal static string Command(string text) => string.Join("\n", Answers(text));

        private static List<string> Answers(string text)
        {
            var answers = new List<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            commands?.HandleCommandFromServer(text, message => answers.Add(message.Message));
            return answers;
        }
    }
}
