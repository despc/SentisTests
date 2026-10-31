using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage;
using VRageMath;
using SentisTests.Core;
using SentisTests.Game;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The missile of the reported blueprint, fired for real. Production's PBFix log is full of
    /// these: "PB deconstructed for load: run 0.00 ms, 2.99 ms of every frame, 7 of last 20 runs
    /// over budget. PB - (ракетный WHAM) on - (small grid)". A run that costs nothing next to a
    /// per-frame average of three milliseconds is the thing to explain: either the script's cost
    /// comes in bursts (launch, homing) and the average is real, or the average outlives the work
    /// and cheap runs are punished for a spike that is already over.
    ///
    /// The small grid of the blueprint is spawned on its own, powered, and fired with the "_fire"
    /// argument its script (WHAM) understands. Measurement is without punishment first, then with
    /// the production thresholds, and every step reports the run time against the per-frame
    /// average so the two can be told apart.
    /// </summary>
    public sealed class TriadaMissileScenario : TestScenario
    {
        public const string ScenarioName = "triada_missile";
        private const string Prefix = "triada-missile-";
        private const double OffsetM = 30000;

        private readonly ConfigOverride _config = new ConfigOverride();
        private MyCubeGrid _missile;
        private MyProgrammableBlock _pb;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            Check(MySession.Static.EnableIngameScripts, "in-game scripts are disabled in this world");
            var path = RequireFile(TriadaPbScenario.BlueprintPath, "the player's blueprint");

            _config.Set("EnableScriptsPunish", false);
            _config.Set("ScriptsMaxExecTime", 8f);
            _config.Set("ScriptsMaxMsPerFrame", 0.5f);
            _config.Set("ScriptOvertimeExecTimesBeforePunish", 3);

            var anchorM = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + Prefix + "anchor");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var at = anchorM.Translation + up * OffsetM;

            var grids = WorldApi.LoadBlueprintFile(path, WorldApi.EntityPrefix + Prefix + "m");
            Check(grids.Count > 1, "the blueprint has no missile grid");
            // The whole ship, with the blueprint's own layout: WHAM refuses to fire a missile
            // that is not attached to a carrier holding a "Fire Control" group, and the merge
            // that holds the missile only exists when the grids sit where the blueprint put them.
            var shift = at - grids[0].PositionAndOrientation.Value.GetMatrix().Translation;
            foreach (var g in grids)
            {
                var m = g.PositionAndOrientation.Value.GetMatrix();
                m.Translation += shift;
                g.PositionAndOrientation = new MyPositionAndOrientation(m);
                var grid = WorldApi.SpawnGrid(g);
                Track(grid);
                WorldApi.ChargeBatteries(grid);
                if (g.DisplayName == grids[1].DisplayName) _missile = grid;
            }
            Check(_missile != null, "the missile grid did not spawn");
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 },
                i => (_missile.PositionComp.GetPosition() + up * 30, 0, 0), withCharacters: true);

            var settle = WaitForSeconds(10, "the missile settles");
            while (settle.MoveNext()) yield return settle.Current;
            _pb = WorldApi.FindFunctionals<MyProgrammableBlock>(_missile).FirstOrDefault();
            Check(_pb != null, "no working programmable block on the missile");
            Note("missile spawned: " + WorldApi.CountBlocks(_missile) + " blocks, PB " + _pb.CustomName +
                 ", power " + WorldApi.DescribePower(_missile));

            while (MySandboxGame.Static.SimulationFrameCounter <= 10800)
                yield return WaitForTicks(60);

            // Fire: WHAM runs its setup state machine and then guides every frame.
            _pb.Run("_fire", Sandbox.ModAPI.Ingame.UpdateType.Terminal);
            Note("fired with _fire");
            var diag = WaitForSeconds(8, "setup runs");
            while (diag.MoveNext()) yield return diag.Current;
            Note("after fire: enabled " + _pb.Enabled + " working " + _pb.IsWorking +
                 " functional " + _pb.IsFunctional + " on grid " + _pb.CubeGrid.DisplayName +
                 " (my grid " + (_pb.CubeGrid == _missile) + ")\nDETAILED INFO:\n" + _pb.DetailedInfo);
            TickMetrics.Take();
            FrameProbe.Take();

            var started = DateTime.UtcNow;
            var lastLog = started;
            while ((DateTime.UtcNow - started).TotalSeconds < 60)
            {
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 10)
                {
                    lastLog = DateTime.UtcNow;
                    Note("in flight " + (DateTime.UtcNow - started).TotalSeconds.ToString("F0") + "s: load " +
                         RuntimePluginControls.PbLoadMsPerFrame(_pb).ToString("F3") + " ms/frame, enabled " +
                         _pb.Enabled + ", speed " + _missile.Physics?.LinearVelocity.Length().ToString("F0") + " m/s, " +
                         RuntimePluginControls.PbTop(3));
                }
                yield return WaitForTicks(30);
            }
            Note("PB REPORT (missile, punish off)\n" + RuntimePluginControls.PbReport(8));
            Note("server: " + TickMetrics.Take().Format() + " | " + FrameProbe.Take());

            _config.Set("EnableScriptsPunish", true);
            var punishStart = DateTime.UtcNow;
            while ((DateTime.UtcNow - punishStart).TotalSeconds < 60 && _pb.Enabled)
                yield return WaitForTicks(30);
            Note(_pb.Enabled
                ? "the missile's block survived a minute with punishment on, load " +
                  RuntimePluginControls.PbLoadMsPerFrame(_pb).ToString("F3") + " ms/frame"
                : "PUNISHED in flight after " + (DateTime.UtcNow - punishStart).TotalSeconds.ToString("F1") +
                  "s - the production burn reproduces");
            Note("PB REPORT (end)\n" + RuntimePluginControls.PbReport(8));
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
