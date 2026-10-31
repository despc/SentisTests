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
    /// A player's blueprint from production, where PBFix kept deconstructing its programmable
    /// blocks. The same punishment settings as production and this stand: a single run may take
    /// 8 ms, a script may own 0.5 ms of every frame on average, four overruns of the recent
    /// window punish.
    ///
    /// What is measured: each script's run time, its per-frame average, its worst run and the
    /// overrun window, next to the frame time of the server itself. The production log lines
    /// showed runs of 0.00 ms with a per-frame average of 3 ms, which is either a script whose
    /// cost comes in bursts, or a bookkeeping fault in PbLoad - the two tell very different
    /// stories about what to fix, so the scenario reports both rather than guessing.
    ///
    /// Phase one measures without punishing, phase two switches punishment on exactly as
    /// production runs it and watches whether any block is destroyed and by what numbers.
    ///
    /// The blueprint is not part of the repository: it is a player's build. It is read from
    /// <see cref="BlueprintPath"/> on the machine that runs the stand; without the file the
    /// scenario is skipped, not failed.
    /// </summary>
    public sealed class TriadaPbScenario : TestScenario
    {
        public const string ScenarioName = "triada_pb";
        public const string BlueprintPath = @"C:\SE\tools\blueprints\triada\bp.sbc";
        private const string Prefix = "triada-";
        private const double OffsetM = 30000;
        private const double MeasureSeconds = 60;
        private const double PunishWatchSeconds = 60;

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private readonly List<MyProgrammableBlock> _pbs = new List<MyProgrammableBlock>();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            Check(MySession.Static.EnableIngameScripts, "in-game scripts are disabled in this world");
            var path = RequireFile(BlueprintPath, "the player's blueprint");

            _config.Set("EnableScriptsPunish", false);
            _config.Set("ScriptsMaxExecTime", 8f);
            _config.Set("ScriptsMaxMsPerFrame", 0.5f);
            _config.Set("ScriptOvertimeExecTimesBeforePunish", 3);

            var anchorM = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + Prefix + "anchor");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var at = anchorM.Translation + up * OffsetM;

            var blueprints = WorldApi.LoadBlueprintFile(path, WorldApi.EntityPrefix + Prefix + "ship");
            Check(blueprints.Count > 0, "the blueprint holds no grids");
            for (var i = 0; i < blueprints.Count; i++)
            {
                var ob = blueprints[i];
                ob.PositionAndOrientation = new MyPositionAndOrientation(
                    MatrixD.CreateWorld(at + up * (i * 300), Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up)), up));
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                _grids.Add(grid);
            }
            Note("spawned " + _grids.Count + " grids, " + _grids.Sum(WorldApi.CountBlocks) + " blocks");
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 },
                i => (at + up * 40, 0, 0), withCharacters: true);

            var settle = WaitForSeconds(15, "the ship settles and its scripts are instantiated");
            while (settle.MoveNext()) yield return settle.Current;

            foreach (var grid in _grids)
                _pbs.AddRange(WorldApi.FindFunctionals<MyProgrammableBlock>(grid));
            Note("programmable blocks: " + (_pbs.Count == 0
                ? "none"
                : string.Join("; ", _pbs.Select(pb => pb.CustomName + " on " + pb.CubeGrid.DisplayName))));
            Check(_pbs.Count > 0, "no working programmable block on the spawned ship");

            // The scripts of this ship run on Update1; the world must be past the plugin's quiet
            // period before anything is judged, which a freshly started stand still is not.
            while (MySandboxGame.Static.SimulationFrameCounter <= 10800)
                yield return WaitForTicks(60);

            TickMetrics.Take();
            FrameProbe.Take();
            var measured = DateTime.UtcNow;
            var lastLog = measured;
            while ((DateTime.UtcNow - measured).TotalSeconds < MeasureSeconds)
            {
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 15)
                {
                    lastLog = DateTime.UtcNow;
                    Note("measuring: " + RuntimePluginControls.PbTop(5));
                }
                yield return WaitForTicks(30);
            }
            var metrics = TickMetrics.Take();
            var probe = FrameProbe.Take();
            Note("PB REPORT (punish off)\n" + RuntimePluginControls.PbReport(10));
            Note("server: " + metrics.Format() + " | " + probe);
            ReportEach("after the measurement window");

            // Phase two: exactly what production does to this ship.
            _config.Set("EnableScriptsPunish", true);
            var punishedAt = new Dictionary<MyProgrammableBlock, double>();
            var start = DateTime.UtcNow;
            while ((DateTime.UtcNow - start).TotalSeconds < PunishWatchSeconds && punishedAt.Count < _pbs.Count)
            {
                foreach (var pb in _pbs)
                {
                    if (punishedAt.ContainsKey(pb) || pb.Enabled) continue;
                    punishedAt[pb] = (DateTime.UtcNow - start).TotalSeconds;
                    Note("PUNISHED after " + punishedAt[pb].ToString("F1") + "s: " + pb.CustomName +
                         " on " + pb.CubeGrid.DisplayName + " (functional " + pb.IsFunctional + ")");
                }
                yield return WaitForTicks(30);
            }
            ReportEach("after the punishment window");
            Note("punished " + punishedAt.Count + " of " + _pbs.Count + " programmable blocks");
            if (punishedAt.Count == 0)
                Note("no block was punished on this stand - the production burn does not reproduce as is");
        }

        private void ReportEach(string when)
        {
            foreach (var pb in _pbs)
            {
                var load = RuntimePluginControls.PbLoadMsPerFrame(pb);
                Note(when + ": " + pb.CustomName + " on " + pb.CubeGrid.DisplayName +
                     " | load " + load.ToString("F3") + " ms/frame | enabled " + pb.Enabled +
                     " | working " + pb.IsWorking);
            }
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
