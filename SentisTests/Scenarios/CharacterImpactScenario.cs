using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// One character flown into another in space: does the hit hurt, and does it reach the damage system (where
    /// SentisWatcher records damage)? The game hurts a character in a contact (<c>MyCharacter.RigidBody_ContactPointCallback</c>,
    /// <c>DoDamage(…, Environment, attacker = the other entity)</c>) unless both stand on something. Two fake players 30 m
    /// apart in space, the second flown at <see cref="SpeedMps"/> m/s through the first. Noted: every Environment hit
    /// between the two as the damage system sees it after it was applied. Passes when there is one.
    /// </summary>
    public sealed class CharacterImpactScenario : TestScenario
    {
        public const string ScenarioName = "character_impact";
        private const double SpeedMps = 25;

        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private static bool _listening;
        private static long _a, _b;
        private static int _hits;
        private static float _amount;
        private static string _first;
        private static bool _registered;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 90;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _gameplay.Set("DisableAnyDamageAfterStartTime", 0);
            var spot = Vector3D.Zero;
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            for (var i = 0; i < 40; i++)
            {
                var probe = new Vector3D(210000 + i * 20000, -75000, 95000 + i * 15000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() < 1e-6 &&
                    planets.All(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) > p.MaximumRadius + 5000))
                {
                    spot = probe;
                    break;
                }
            }
            Check(spot != Vector3D.Zero, "no place in space without gravity");
            if (!_registered)
            {
                _registered = true;
                MyAPIGateway.Session.DamageSystem.RegisterAfterDamageHandler(int.MaxValue - 1, (target, info) =>
                {
                    if (!_listening || !(target is MyCharacter hurt) || info.Type != MyDamageType.Environment) return;
                    if (hurt.EntityId != _a && hurt.EntityId != _b) return;
                    if (info.AttackerId != _a && info.AttackerId != _b) return;
                    _hits++;
                    _amount += info.Amount;
                    if (_first == null) _first = $"{info.Amount:0.0} to {(hurt.EntityId == _a ? "the standing one" : "the flying one")}";
                });
            }

            FakeClients.RemoveAll();
            FakeClients.Add(2, new FakeClients.NetworkProfile { RttMs = 30 }, i => (spot + new Vector3D(0, 0, i * 30), 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the players are there");
            while (arrive.MoveNext()) yield return arrive.Current;
            var standing = FakeClients.Character(0);
            var flying = FakeClients.Character(1);
            Check(standing != null && flying != null, "the fake players have no characters");
            _a = standing.EntityId;
            _b = flying.EntityId;
            _hits = 0;
            _amount = 0;
            _first = null;
            _listening = true;
            var from = flying.PositionComp.GetPosition();
            var through = standing.PositionComp.GetPosition();
            var direction = Vector3D.Normalize(through - from);
            double? t0 = null;
            FakeClients.SetPath(1, s =>
            {
                t0 = t0 ?? s;
                return from + direction * (s - t0.Value) * SpeedMps;
            });
            var healthBefore = standing.StatComp?.Health?.Value ?? 0;
            var fly = WaitForSeconds(4, "the flying one goes through");
            while (fly.MoveNext()) yield return fly.Current;
            _listening = false;
            Note($"IMPACT | {SpeedMps} m/s into a character in space: {_hits} Environment hits between the two, {_amount:0.0} in all" +
                 (_first != null ? $" (first {_first})" : "") + $"; the standing one's health {healthBefore:0} -> {standing.StatComp?.Health?.Value ?? 0:0}");
            Check(_hits > 0, "a character flown into another got no Environment hit from it in the damage system");
        }

        public override void Cleanup()
        {
            try
            {
                _listening = false;
                FakeClients.RemoveAll();
                _gameplay.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
