using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// The idle-welder slowdown, end to end:
    ///  - the slow mode engages only after the configured number of consecutive empty activations;
    ///  - six idle welders on one grid spread their slow activations over different frames;
    ///  - a damaged block wakes the welder at once: activations during the repair come far more
    ///    often than the idle interval, and the block ends fully repaired;
    ///  - with the work done, the slow cadence returns.
    /// The numbers are SO's own WelderDiagnostics, including per-welder activation frames.
    /// </summary>
    public sealed class WelderIdleScenario : TestScenario
    {
        public const string ScenarioName = "welder_idle";
        private const string Prefix = "welder-idle-";
        private const int IdleSeconds = 3;
        private const int ActivationsBeforeSlowdown = 4;
        private const double MeasureSeconds = 12;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _config = new ConfigOverride();
        private bool _destructibleBlocks;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            // DoDamage is a no-op while destruction is off, and a grid keeps the flag it was
            // spawned with: set it before the grid exists, whatever earlier scenarios left behind.
            _destructibleBlocks = MySession.Static.Settings.DestructibleBlocks;
            MySession.Static.Settings.DestructibleBlocks = true;
            _config.Set("WelderTweaksEnabled", true);
            _config.Set("WelderIdleActivationSeconds", IdleSeconds);
            _config.Set("WelderIdleActivationsBeforeSlowdown", ActivationsBeforeSlowdown);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            var anchorM = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + Prefix + "anchor");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var forward = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var site = anchorM.Translation + up * 200;

            var welderSubtype = WorldApi.FindSubtype(MyCubeSize.Large, "shipwelder");
            var grid = WorldApi.SpawnGrid(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "grid", MyCubeSize.Large, true, site, new[]
            {
                new BlockSpec("LargeBlockBatteryBlock", new Vector3I(0, 0, 0)),
                new BlockSpec(welderSubtype, new Vector3I(1, 0, 0)),
                new BlockSpec(welderSubtype, new Vector3I(3, 0, 0)),
                new BlockSpec(welderSubtype, new Vector3I(5, 0, 0)),
                new BlockSpec(welderSubtype, new Vector3I(7, 0, 0)),
                new BlockSpec(welderSubtype, new Vector3I(9, 0, 0)),
                new BlockSpec(welderSubtype, new Vector3I(11, 0, 0)),
            }, forward, up));
            Track(grid);
            FakeClients.Add(1, Network, i => (site + up * 5, 0, 0), withCharacters: true);
            yield return WaitForTicks(30);

            WorldApi.ChargeBatteries(grid);
            var welders = grid.GetFatBlocks<MyShipWelder>().ToList();
            Check(welders.Count == 6, $"expected 6 welders, got {welders.Count}");
            foreach (var w in welders) w.Enabled = true;
            var welder = Require<MyShipWelder>(WorldApi.FindFunctional<MyShipWelder>(grid), "the welder");
            var ids = new HashSet<long>(welders.Select(w => w.EntityId));
            yield return WaitForSeconds(2, "the welders settle into their cadence");

            // Idle: twelve seconds with nothing in range. Vanilla activates ~48 times per welder;
            // with N=4 empty activations before the slowdown this one gets a few fast ones, then
            // one per idle interval. The counters are the welders' own - the global ones mix
            // every grid on the server.
            var idleStart = MySession.Static.GameplayFrameCounter;
            var a = RuntimePluginControls.WelderCounter(welder.EntityId, "ActivationsByWelder");
            var aSkips = RuntimePluginControls.WelderCounter(welder.EntityId, "IdleSkipsByWelder");
            yield return WaitForSeconds(MeasureSeconds, "measuring the idle cadence");
            var idleEnd = MySession.Static.GameplayFrameCounter;
            var b = RuntimePluginControls.WelderCounter(welder.EntityId, "ActivationsByWelder");
            var bSkips = RuntimePluginControls.WelderCounter(welder.EntityId, "IdleSkipsByWelder");
            var idleActivations = b - a;
            var idleSkips = bSkips - aSkips;
            Note($"idle {MeasureSeconds:0}s: {idleActivations} activations, {idleSkips} skipped (vanilla ~{MeasureSeconds * 4:0} activations)");
            Check(idleActivations >= 0 && idleActivations <= ActivationsBeforeSlowdown + MeasureSeconds / IdleSeconds + 2,
                $"an idle welder activated {idleActivations} times in {MeasureSeconds:0}s, the idle interval is {IdleSeconds}s after {ActivationsBeforeSlowdown} empty activations");
            Check(idleSkips > 0, "no ActivateCommon calls were skipped");

            // Staggering: the slow activations of the six welders must not share one frame.
            var frames = RuntimePluginControls.WelderActivationFrames(ids, idleStart)
                .Where(f => f[1] <= idleEnd).ToList();
            var perWelder = frames.GroupBy(f => f[0]).ToDictionary(g => g.Key, g => g.Count());
            var distinctFrames = frames.Select(f => f[1]).Distinct().Count();
            var busiestFrame = frames.GroupBy(f => f[1]).Select(g => g.Count()).DefaultIfEmpty(0).Max();
            Note($"idle spread: {frames.Count} activations over {distinctFrames} distinct frames, busiest frame {busiestFrame}, per welder [{string.Join(", ", perWelder.Values)}]");
            Check(perWelder.Count == 6 && perWelder.Values.All(n => n >= 1),
                $"only {perWelder.Count}/6 welders activated during the idle window");
            Check(frames.Count == 0 || distinctFrames >= 3,
                $"slow activations are synchronized: {frames.Count} activations on only {distinctFrames} frames");

            // Work: a damaged block inside the actual detector sphere and steel in the tool.
            // Picking a merely adjacent cell is not enough: the sensor is an offset sphere and a
            // diagonal neighbour can sit outside it, which looks exactly like a broken welder.
            var armor = WorldApi.FindSubtype(MyCubeSize.Large, "blockarmorblock");
            var sphere = new BoundingSphereD(
                Vector3D.Transform(welder.DetectorSphere.Center, grid.WorldMatrix),
                welder.DetectorSphere.Radius);
            var cell = welder.Position;
            var bestDistance = double.MaxValue;
            for (var x = -2; x <= 2; x++)
            for (var y = -2; y <= 2; y++)
            for (var z = -2; z <= 2; z++)
            {
                var candidate = welder.Position + new Vector3I(x, y, z);
                if (!grid.CanAddCubes(candidate, candidate)) continue;
                var distance = Vector3D.Distance(grid.GridIntegerToWorld(candidate), sphere.Center);
                if (distance > sphere.Radius * 0.85 || distance >= bestDistance) continue;
                bestDistance = distance;
                cell = candidate;
            }
            Require<MyCubeGrid>(bestDistance < double.MaxValue ? grid : null,
                $"a free cell inside the detector sphere (radius {sphere.Radius:0.00})");
            WorldApi.AddRealBlock(grid, armor, cell);
            Note($"detector radius {sphere.Radius:0.00} m, target cell {cell} at {bestDistance:0.00} m from its center");
            // Plain armor has no FatBlock: work with the slim block.
            var slim = grid.GetCubeBlock(cell);
            Require<MySlimBlock>(slim, "the repair target's slim block");
            var before = slim.Integrity;
            ((IMySlimBlock)slim).DoDamage(slim.MaxIntegrity * 0.6f, MyDamageType.Bullet, true);
            // DoDamage only accumulates: the integrity drops when the grid applies the damage on
            // a later frame. Wait for that instead of reading it synchronously.
            var damageLands = Wait(() => slim.Integrity < slim.MaxIntegrity, "the damage lands", 15);
            while (damageLands.MoveNext()) yield return damageLands.Current;
            WorldApi.StockComponents(welder.GetInventory(0), new Dictionary<string, int> { { "SteelPlate", 100 } });
            var wakeStart = MySession.Static.GameplayFrameCounter;
            Note($"repair target at {cell}, integrity {before:0} -> {slim.Integrity:0} of {slim.MaxIntegrity:0}");

            var repaired = false;
            for (var t = 1; t <= 12 && !repaired; t++)
            {
                yield return WaitForSeconds(5, "the welder repairs the damaged block");
                repaired = slim.IsFullIntegrity;
                Note($"repair t+{t * 5}s: integrity {slim.Integrity:0}/{slim.MaxIntegrity:0}, activations {RuntimePluginControls.WelderCounter(welder.EntityId, "ActivationsByWelder")}, targets {RuntimePluginControls.WelderTargetCount(welder.EntityId)}, steel {welder.GetInventory(0).GetItemAmount(new MyDefinitionId(typeof(MyObjectBuilder_Component), "SteelPlate"))}, {RuntimePluginControls.WelderSnapshot()}");
            }
            Check(repaired, "the welder did not finish the repair in 60s");
            var repairEnd = MySession.Static.GameplayFrameCounter;
            Note($"the damaged block is back at full integrity in {(repairEnd - wakeStart) / 60.0:0.0}s");

            var wakeFrames = RuntimePluginControls.WelderActivationFrames(new HashSet<long> { welder.EntityId }, wakeStart)
                .Where(f => f[1] <= repairEnd).Select(f => f[1]).OrderBy(f => f).ToList();
            var minGap = wakeFrames.Count < 2 ? long.MaxValue
                : wakeFrames.Zip(wakeFrames.Skip(1), (x, y) => y - x).Min();
            Note($"wake-up: {wakeFrames.Count} activations during the repair, smallest gap {minGap} frames (idle interval {IdleSeconds * 60})");
            Check(wakeFrames.Count >= 2 && minGap < IdleSeconds * 60,
                "the welder stayed on the slow cadence while it had real work");

            // Back to idle: once nothing is left to weld the slow cadence returns.
            yield return WaitForSeconds(2, "the repair settles");
            var c = RuntimePluginControls.WelderCounter(welder.EntityId, "ActivationsByWelder");
            yield return WaitForSeconds(MeasureSeconds, "measuring the cadence after the work");
            var d = RuntimePluginControls.WelderCounter(welder.EntityId, "ActivationsByWelder");
            var afterActivations = d - c;
            Note($"after the repair, {MeasureSeconds:0}s: {afterActivations} activations");
            Check(afterActivations >= 0 && afterActivations <= ActivationsBeforeSlowdown + MeasureSeconds / IdleSeconds + 2,
                $"after the work the welder activated {afterActivations} times in {MeasureSeconds:0}s");

            Note($"WELDER IDLE RESULT | idle {idleActivations} activations/{MeasureSeconds:0}s ({idleSkips} skips), spread over {distinctFrames} frames, wake-up gap {minGap}f, repair done, idle again {afterActivations}");
        }

        public override void Cleanup()
        {
            MySession.Static.Settings.DestructibleBlocks = _destructibleBlocks;
            try { _config.Restore(); }
            finally
            {
                try { FakeClients.RemoveAll(); }
                finally { base.Cleanup(); }
            }
        }
    }
}
