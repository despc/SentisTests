using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A fake player standing at a point for a while, so that what is there runs as with a player by it (the freezer,
    /// the clusters stepped only with someone in them): focus <c>x= y= z= seconds=</c>. Then the player goes.
    /// </summary>
    internal sealed class PlayerAtScenario : TestScenario
    {
        public const string ScenarioName = "player_at";
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private bool _added;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 3700;

        private static double Arg(string name, double otherwise)
        {
            var m = Regex.Match(TestRunner.Focus ?? "", @"\b" + name + @"=(-?[\d.]+)");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : otherwise;
        }

        public override IEnumerator Run()
        {
            var at = new Vector3D(Arg("x", double.NaN), Arg("y", double.NaN), Arg("z", double.NaN));
            Check(!double.IsNaN(at.X) && !double.IsNaN(at.Y) && !double.IsNaN(at.Z), "focus x= y= z= is needed");
            var seconds = Arg("seconds", 120);
            FakeClients.Add(1, Network, i => (at, 0, 0), withCharacters: true);
            _added = true;
            Note("PLAYER AT " + at.ToString("F0") + " for " + seconds + " s");
            var wait = WaitForSeconds(seconds, "the player stands there");
            while (wait.MoveNext()) yield return wait.Current;
        }

        public override void Cleanup()
        {
            try { if (_added) FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
