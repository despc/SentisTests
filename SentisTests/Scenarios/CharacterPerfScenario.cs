using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What a character costs the server: <see cref="Characters"/> of them hovering on their
    /// jetpacks a hundred metres over a planet, dampeners on.
    ///
    /// Hovering is the expensive state on purpose. The dampeners run the jetpack's thrust component
    /// every frame to hold the character against gravity, the character keeps its own physics proxy
    /// stepping instead of resting on the ground, and every one of them is replicated to the clients
    /// that own them.
    ///
    /// Measured: <see cref="IdleSeconds"/> of an empty sky, then <see cref="RunSeconds"/> with the
    /// characters in it, so the difference is what the characters themselves cost.
    /// </summary>
    public sealed class CharacterPerfScenario : TestScenario
    {
        public const string ScenarioName = "character_perf";
        private const string Prefix = "char-";
        private const int Characters = 64;
        private const double AltitudeM = 100;
        private const double SpacingM = 25;
        private const double SettleSeconds = 15;
        private const double IdleSeconds = 15;
        private const double RunSeconds = 60;
        private const double LogEverySeconds = 20;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(RunSeconds + 600);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            // Characters left by an earlier run would be measured as part of the empty sky.
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");

            var centre = planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchorM.Translation - centre);
            var east = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var north = Vector3D.Normalize(Vector3D.Cross(up, east));
            var gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(anchorM.Translation).Length();
            Check(gravity > 0.1, "there is no gravity here: " + gravity.ToString("F2") + " m/s2");

            // The empty sky first: everything else in the world is the baseline.
            TickMetrics.Take();
            FrameProbe.Take();
            var idle = WaitForSeconds(IdleSeconds, "empty sky");
            while (idle.MoveNext()) yield return idle.Current;
            var idleMetrics = TickMetrics.Take();
            var idleProbe = FrameProbe.Take();

            // A square of characters a hundred metres up.
            var side = (int)Math.Ceiling(Math.Sqrt(Characters));
            FakeClients.Add(Characters, Network, index =>
            {
                var x = index % side - side / 2.0;
                var z = index / side - side / 2.0;
                var ground = planet.GetClosestSurfacePointGlobal(anchorM.Translation + east * (x * SpacingM) + north * (z * SpacingM));
                return (ground + up * AltitudeM, 0, 0);
            }, withCharacters: true);

            var settle = WaitForSeconds(SettleSeconds, "characters settle");
            while (settle.MoveNext()) yield return settle.Current;

            var characters = Enumerable.Range(0, FakeClients.Count).Select(FakeClients.Character)
                .Where(c => c != null && !c.IsDead).ToList();
            Check(characters.Count >= Characters / 2, "only " + characters.Count + " characters of " + Characters + " are alive");

            var flying = 0;
            foreach (var character in characters)
                if (Hover(character)) flying++;
            Note(characters.Count + " characters at " + AltitudeM + " m, gravity " + gravity.ToString("F2") +
                 " m/s2, jetpacks on: " + flying);

            var settleFlight = WaitForSeconds(SettleSeconds, "characters take off");
            while (settleFlight.MoveNext()) yield return settleFlight.Current;

            TickMetrics.Take();
            FrameProbe.Take();
            var started = DateTime.UtcNow;
            var lastLog = started;
            while ((DateTime.UtcNow - started).TotalSeconds < RunSeconds)
            {
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= LogEverySeconds)
                {
                    lastLog = DateTime.UtcNow;
                    Note("hovering " + (DateTime.UtcNow - started).TotalSeconds.ToString("F0") + "s: " + Describe(characters, planet));
                }

                yield return null;
            }

            var metrics = TickMetrics.Take();
            var probe = FrameProbe.Take();
            var idleFrame = FrameAvg(idleProbe);
            var busyFrame = FrameAvg(probe);
            var alive = characters.Count(c => c != null && !c.MarkedForClose && !c.IsDead);
            var perCharacter = alive > 0 ? (busyFrame - idleFrame) * 1000 / alive : 0;

            Note("CHARACTER RESULT | " + alive + " characters hovering | empty sky: " + Summary(idleProbe) +
                 " | with characters: " + Summary(probe) + " | that is " + (busyFrame - idleFrame).ToString("F2") +
                 " ms of every frame, " + perCharacter.ToString("F0") + " us per character | " +
                 Describe(characters, planet) + " | " + metrics.Format() + " | " + probe);

            Check(alive >= Characters / 2, "only " + alive + " characters survived");
            Check(flying > 0, "no jetpack was turned on");
        }

        /// <summary>Jetpack on with dampeners, which is what makes a character hold its altitude.</summary>
        private static bool Hover(MyCharacter character)
        {
            try
            {
                var thrust = character.Components.Get<Sandbox.Game.GameSystems.MyEntityThrustComponent>();
                if (thrust != null) thrust.DampenersEnabled = true;
                var jetpack = character.JetpackComp;
                if (jetpack == null) return false;
                jetpack.TurnOnJetpack(true);
                return jetpack.TurnedOn;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Describe(List<MyCharacter> characters, MyPlanet planet)
        {
            var alive = characters.Where(c => c != null && !c.MarkedForClose && !c.IsDead).ToList();
            if (alive.Count == 0) return "no characters left";
            var flying = alive.Count(c => c.JetpackComp != null && c.JetpackComp.TurnedOn);
            var heights = alive.Select(c =>
                (c.PositionComp.GetPosition() - planet.GetClosestSurfacePointGlobal(c.PositionComp.GetPosition())).Length()).ToList();
            var speeds = alive.Select(c => (double)(c.Physics?.LinearVelocity.Length() ?? 0)).ToList();
            return alive.Count + " alive, " + flying + " on jetpacks, height " + heights.Min().ToString("F0") + "-" +
                   heights.Max().ToString("F0") + " m, speed " + speeds.Average().ToString("F1") + " m/s";
        }

        private static double FrameAvg(string probe)
        {
            var m = System.Text.RegularExpressions.Regex.Match(probe, @"sim-work frames=\d+ avg=([\d.]+)ms");
            return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }

        private static string Summary(string probe)
        {
            string Pick(string name)
            {
                var m = System.Text.RegularExpressions.Regex.Match(probe,
                    System.Text.RegularExpressions.Regex.Escape(name) + @"=\d+ms/\d+calls\([\d.]+ms each, ([\d.]+)ms/frame");
                return m.Success ? m.Groups[1].Value : "-";
            }

            var sim = System.Text.RegularExpressions.Regex.Match(probe, @"sim-work frames=\d+ avg=([\d.]+)ms p50=[\d.]+ p95=[\d.]+ p99=([\d.]+)");
            return "frame " + (sim.Success ? sim.Groups[1].Value : "?") + " ms, p99 " + (sim.Success ? sim.Groups[2].Value : "?") +
                   ", physics " + Pick("physics") + ", char.update " + Pick("char.update") +
                   ", char.updateAfter " + Pick("char.updateAfter") + ", char.simulate " + Pick("char.simulate") +
                   ", char.jetpack " + Pick("char.jetpack") + ", char.parallel " + Pick("char.parallel") +
                   ", char.update10 " + Pick("char.update10") + ", char.update100 " + Pick("char.update100") +
                   ", replication.sendUpdate " + Pick("replication.sendUpdate");
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
