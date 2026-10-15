using System;
using System.Collections;
using Sandbox.Game.Entities;
using SentisTests.Game;
using SpaceWelder = SpaceEngineers.Game.Entities.Blocks.MyShipWelder;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Runs the real projector-weld scenario after the same welder ship has been frozen,
    /// had its live radius changed while frozen, and been thawed by disabling Freezer at runtime.
    /// </summary>
    public sealed class FrozenRadiusWeldScenario : ProjectorWeldScenario
    {
        public const string ScenarioName = "frozen_radius_weld";

        private bool _captured;
        private bool _initialFreezerEnabled;
        private float _initialWelderMultiplier;

        public override string Name => ScenarioName;

        /// <summary>Nobody stands here: the boat has to freeze, that is the whole subject.</summary>
        protected override bool KeepSiteAwake => false;

        protected override IEnumerator BeforeWelding(MyCubeGrid ship, SpaceWelder welder)
        {
            _initialFreezerEnabled = RuntimePluginControls.FreezerEnabled;
            _initialWelderMultiplier = RuntimePluginControls.WelderRadiusMultiplier;
            _captured = true;

            // x1 taken by the welder before the boat freezes: a frozen welder keeps the radius it had, and the stand's
            // multiplier is not always x1 (x2 with the prod configs, 14.10.2026: the "doubled" radius was the one it had)
            var startRadius = welder.DetectorSphere.Radius;
            RuntimePluginControls.SetWelderRadiusMultiplier(1f);
            if (Math.Abs(_initialWelderMultiplier - 1f) > 0.001f && _initialWelderMultiplier > 0f)
            {
                var single = startRadius / _initialWelderMultiplier;
                var waitSingle = Wait(() => Math.Abs(welder.DetectorSphere.Radius - single) < 0.001f,
                    "welder radius back at x1 before freezing", 30);
                while (waitSingle.MoveNext())
                    yield return waitSingle.Current;
            }
            RuntimePluginControls.SetFreezerEnabled(true);
            Note("runtime Freezer enabled; waiting for the construction boat to enter FrozenGrids");

            var waitFrozen = Wait(() => RuntimePluginControls.IsGridFrozen(ship.EntityId),
                "construction boat frozen", 90);
            while (waitFrozen.MoveNext())
                yield return waitFrozen.Current;

            var baseline = welder.DetectorSphere.Radius;
            Check(baseline > 0f, "welder baseline radius must be positive");
            Note("construction boat frozen=true; changing welder radius " +
                 baseline.ToString("F2") + " -> " + (baseline * 2f).ToString("F2"));

            RuntimePluginControls.SetWelderRadiusMultiplier(2f);
            var waitRadius = Wait(() =>
                    RuntimePluginControls.IsGridFrozen(ship.EntityId) &&
                    Math.Abs(welder.DetectorSphere.Radius - baseline * 2f) < 0.001f,
                "doubled radius applied while construction boat remains frozen", 30);
            while (waitRadius.MoveNext())
                yield return waitRadius.Current;

            Note("radius doubled while frozen; disabling Freezer at runtime");
            RuntimePluginControls.SetFreezerEnabled(false);
            var waitThawed = Wait(() => !RuntimePluginControls.IsGridFrozen(ship.EntityId),
                "construction boat thawed after runtime Freezer disable", 90);
            while (waitThawed.MoveNext())
                yield return waitThawed.Current;

            Check(Math.Abs(welder.DetectorSphere.Radius - baseline * 2f) < 0.001f,
                "welder radius changed during thaw");

            // Nobody stood here so that the boat would freeze; now somebody has to, or Havok does
            // not step this cluster (EnableSelectivePhysicsUpdates) and the thawed boat hangs where
            // it is, never reaching the slab. It only ever passed with the operator in the world.
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 50 },
                p => (ship.PositionComp.GetPosition() + new VRageMath.Vector3D(0, 40, 0), 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "a player arrives at the site");
            while (arrive.MoveNext())
                yield return arrive.Current;
            Note("construction boat thawed; doubled radius preserved; starting real vanilla projection welding");
        }

        public override void Cleanup()
        {
            try
            {
                RestoreRuntimeConfig();
            }
            finally
            {
                base.Cleanup();
            }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                RestoreRuntimeConfig();
            }
            finally
            {
                base.CleanupLeftovers();
            }
        }

        private void RestoreRuntimeConfig()
        {
            if (!_captured)
                return;
            RuntimePluginControls.SetWelderRadiusMultiplier(_initialWelderMultiplier);
            RuntimePluginControls.SetFreezerEnabled(_initialFreezerEnabled);
            _captured = false;
        }
    }
}
