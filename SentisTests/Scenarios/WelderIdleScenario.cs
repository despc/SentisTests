using System;
using System.Collections;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The idle-welder slowdown, end to end: an enabled welder with nothing in range activates
    /// once per the configured idle interval instead of four times a second; given a damaged
    /// block it wakes up and repairs it at full speed, and goes back to the slow cadence when
    /// the work is done. The counters are SO's own WelderDiagnostics.
    /// </summary>
    public sealed class WelderIdleScenario : TestScenario
    {
        public const string ScenarioName = "welder_idle";
        private const string Prefix = "welder-idle-";
        private const int IdleSeconds = 5;
        private const double MeasureSeconds = 12;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _config = new ConfigOverride();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _config.Set("WelderTweaksEnabled", true);
            _config.Set("WelderIdleActivationSeconds", IdleSeconds);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            var anchorM = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + Prefix + "anchor");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var forward = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var site = anchorM.Translation + up * 200;

            var grid = WorldApi.SpawnGrid(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "grid", MyCubeSize.Large, true, site, new[]
            {
                new BlockSpec("LargeBlockBatteryBlock", new Vector3I(0, 0, 0)),
                new BlockSpec(WorldApi.FindSubtype(MyCubeSize.Large, "shipwelder"), new Vector3I(1, 0, 0)),
            }, forward, up));
            Track(grid);
            FakeClients.Add(1, Network, i => (site + up * 5, 0, 0), withCharacters: true);
            yield return WaitForTicks(30);

            WorldApi.ChargeBatteries(grid);
            var welder = Require<MyShipWelder>(WorldApi.FindFunctional<MyShipWelder>(grid), "the welder");
            welder.Enabled = true;
            yield return WaitForSeconds(2, "the welder settles into its cadence");

            // Idle: twelve seconds with nothing in range. Vanilla activates ~48 times here.
            // The counters are this welder's own - the global ones mix every grid on the server.
            var a = RuntimePluginControls.WelderCounter(welder.EntityId, "ActivationsByWelder");
            var aSkips = RuntimePluginControls.WelderCounter(welder.EntityId, "IdleSkipsByWelder");
            yield return WaitForSeconds(MeasureSeconds, "measuring the idle cadence");
            var b = RuntimePluginControls.WelderCounter(welder.EntityId, "ActivationsByWelder");
            var bSkips = RuntimePluginControls.WelderCounter(welder.EntityId, "IdleSkipsByWelder");
            var idleActivations = b - a;
            var idleSkips = bSkips - aSkips;
            Note($"idle {MeasureSeconds:0}s: {idleActivations} activations, {idleSkips} skipped (vanilla ~{MeasureSeconds * 4:0} activations)");
            Check(idleActivations >= 0 && idleActivations <= MeasureSeconds / IdleSeconds + 2,
                $"an idle welder activated {idleActivations} times in {MeasureSeconds:0}s, the idle interval is {IdleSeconds}s");
            Check(idleSkips > 0, "no ActivateCommon calls were skipped");

            // Work: a damaged block in front of the sensor and steel in the tool. The welder has
            // to notice it and finish the repair - the slowdown must not starve real work.
            var armor = WorldApi.FindSubtype(MyCubeSize.Large, "blockarmorblock");
            var detector = welder.WorldMatrix.Translation +
                           welder.WorldMatrix.Forward * (grid.GridSize * 1.5f);
            var cell = welder.Position;
            var bestDistance = double.MaxValue;
            foreach (var offset in new[] { Vector3I.Left, Vector3I.Right, Vector3I.Up, Vector3I.Down, Vector3I.Forward, Vector3I.Backward })
            {
                var candidate = welder.Position + offset;
                if (!grid.CanAddCubes(candidate, candidate)) continue;
                var distance = Vector3D.DistanceSquared(grid.GridIntegerToWorld(candidate), detector);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                cell = candidate;
            }
            Require<MyCubeGrid>(bestDistance < double.MaxValue ? grid : null, "a free cell next to the welder");
            WorldApi.AddRealBlock(grid, armor, cell);
            // Plain armor has no FatBlock: work with the slim block.
            var slim = grid.GetCubeBlock(cell);
            Require<MySlimBlock>(slim, "the repair target's slim block");
            ((IMySlimBlock)slim).DoDamage(slim.MaxIntegrity * 0.6f, MyDamageType.Bullet, true);
            WorldApi.StockComponents(welder.GetInventory(0), new System.Collections.Generic.Dictionary<string, int> { { "SteelPlate", 100 } });
            Note($"repair target at {cell}, integrity {slim.Integrity / slim.MaxIntegrity:P0}");

            var repair = Wait(() => slim.IsFullIntegrity, "the welder repairs the damaged block", 90);
            while (repair.MoveNext()) yield return repair.Current;
            Note("the damaged block is back at full integrity");

            // Back to idle: once nothing is left to weld the slow cadence returns.
            yield return WaitForSeconds(2, "the repair settles");
            var c = RuntimePluginControls.WelderCounter(welder.EntityId, "ActivationsByWelder");
            yield return WaitForSeconds(MeasureSeconds, "measuring the cadence after the work");
            var d = RuntimePluginControls.WelderCounter(welder.EntityId, "ActivationsByWelder");
            var afterActivations = d - c;
            Note($"after the repair, {MeasureSeconds:0}s: {afterActivations} activations");
            Check(afterActivations >= 0 && afterActivations <= MeasureSeconds / IdleSeconds + 2,
                $"after the work the welder activated {afterActivations} times in {MeasureSeconds:0}s");

            Note($"WELDER IDLE RESULT | idle {idleActivations} activations/{MeasureSeconds:0}s ({idleSkips} skips), repair done, idle again {afterActivations}");
        }

        public override void Cleanup()
        {
            try { _config.Restore(); }
            finally
            {
                try { FakeClients.RemoveAll(); }
                finally { base.Cleanup(); }
            }
        }
    }
}
