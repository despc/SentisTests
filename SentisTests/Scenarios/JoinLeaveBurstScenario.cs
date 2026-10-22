using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Many players joining, quitting and coming back at once, as after a restart or a network drop: <see cref="Players"/>
    /// fake players join together with characters, quit together (the game keeps their characters,
    /// <see cref="FakeClients.Quit"/>) and join again together (<see cref="FakeClients.Rejoin"/>: their player and the
    /// join's respawn), <see cref="Rounds"/> times. The worst frame of each step is written down ("PEAK"), and checked: every
    /// player back has the character they left, the characters stay put while their players are away, and when all is
    /// removed no fake player's identity or character is left.
    /// </summary>
    public sealed class JoinLeaveBurstScenario : TestScenario
    {
        public const string ScenarioName = "join_leave_burst";
        private const int Players = 20;
        private const int Rounds = 3;
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 60 };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        private readonly List<string> _results = new List<string>();

        public override IEnumerator Run()
        {
            var center = new Vector3D(310000, 310000, 310000);
            var quiet = WaitForSeconds(3, "a quiet baseline");
            TickMetrics.Take();
            while (quiet.MoveNext()) yield return quiet.Current;
            Note("PEAK quiet: worst frame " + TickMetrics.Take().MaxFrameMs.ToString("F1") + " ms");
            var identitiesBefore = MySession.Static.Players.GetAllIdentities().Count();

            for (var round = 1; round <= Rounds; round++)
            {
                var first = FakeClients.Count;
                TickMetrics.Take();
                FakeClients.Add(Players, Network, i => (center + new Vector3D((i % 10) * 15, (i / 10) * 15, round * 40), 0, 0), withCharacters: true);
                var joined = Wait(() => Enumerable.Range(first, Players).All(FakeClients.HasLiveCharacter), Players + " players joined with characters", 60);
                while (joined.MoveNext()) yield return joined.Current;
                var settle = WaitForSeconds(2, "the joins settling");
                while (settle.MoveNext()) yield return settle.Current;
                Peak("round " + round + ": " + Players + " join");

                var bodies = Enumerable.Range(first, Players).Select(FakeClients.Character).ToList();
                var at = bodies.Select(b => b.PositionComp.GetPosition()).ToList();
                TickMetrics.Take();
                for (var i = first; i < first + Players; i++) FakeClients.Quit(i);
                var away = WaitForSeconds(3, "the players away");
                while (away.MoveNext()) yield return away.Current;
                Peak("round " + round + ": " + Players + " quit");
                var gone = bodies.Count(b => b.MarkedForClose || b.IsDead);
                var moved = bodies.Select((b, i) => Vector3D.Distance(b.PositionComp.GetPosition(), at[i])).Max();
                Check(gone == 0, "round " + round + ": " + gone + " of the characters left by players who quit are gone");
                Check(moved < 5, "round " + round + ": a character left by a player who quit moved " + moved.ToString("F1") + " m");

                TickMetrics.Take();
                var back = Enumerable.Range(first, Players).Select(FakeClients.Rejoin).ToList();
                var own = Wait(() => back.Select((p, i) => p?.Character == bodies[i]).All(x => x), "every player back with their character", 20);
                while (own.MoveNext()) yield return own.Current;
                var settleBack = WaitForSeconds(2, "the rejoins settling");
                while (settleBack.MoveNext()) yield return settleBack.Current;
                Peak("round " + round + ": " + Players + " rejoin");
            }

            TickMetrics.Take();
            FakeClients.RemoveAll();
            var cleared = WaitForSeconds(3, "all removed");
            while (cleared.MoveNext()) yield return cleared.Current;
            Peak("all " + Players * Rounds + " removed");
            var left = MySession.Static.Players.GetAllIdentities().Count(i => i.DisplayName != null && i.DisplayName.StartsWith(FakeClients.NamePrefix));
            var bodiesLeft = Sandbox.Game.Entities.MyEntities.GetEntities().OfType<MyCharacter>().Count(c => !c.MarkedForClose && (c.DisplayName ?? "").StartsWith(FakeClients.NamePrefix));
            Check(left == 0 && bodiesLeft == 0, "after removing the fake players " + left + " identities and " + bodiesLeft + " characters of theirs are left");
            Note("JOIN LEAVE BURST RESULT | " + string.Join(" | ", _results) + " | identities " + identitiesBefore + " -> " + MySession.Static.Players.GetAllIdentities().Count());
        }

        private void Peak(string label)
        {
            var frames = TickMetrics.Take();
            var line = label + ": worst frame " + frames.MaxFrameMs.ToString("F1") + " ms";
            _results.Add(line);
            Note("PEAK " + line + " (" + frames.Format() + ")");
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
