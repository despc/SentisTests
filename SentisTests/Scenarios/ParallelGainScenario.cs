using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A load SentisClusters' parallel mode speeds up, and how much: the frame and the simulation's speed in one run with
    /// the mode switched live.
    ///
    /// The load is refineries, RefineryPerfScenario's grid (101 refineries, containers, reactors) in <c>groups</c> groups of
    /// <c>grids</c> copies (64 of 9 by default: 58 176 refineries, the simulation at 0.6 with the mode off), a group a
    /// cluster of the physics: 20 km apart along the world's X axis (the game parts clusters
    /// only along the axes), its copies 250 m apart. Refineries update often and evenly (their timers, spread over the
    /// frames) and each update costs about the same, so every frame has work in every cluster - what the clusters' threads
    /// share. Welders (heavy_neighbour, medium_clusters) are no such load: an update builds a projected block now and then,
    /// tens of ms, and the frame waits for that one cluster whatever the others do. Many small clusters, not a few big
    /// ones: a cluster's part of one loop (eight loops a frame - before, after, every 10th, 100th, the timers) has to be
    /// long against waking the threads and sorting the entities; 8 groups of 32 grids gave no gain (28.10.2026: the
    /// game thread waited 8.9 ms for 11.9 ms of updates, the sorting 4-5 ms a frame), 64 of 4 did.
    ///
    /// 28.10.2026, 64 groups of 9: off sim 0.59, frame 28.1 ms, p95 61.5; parallel 0.73, 22.9 ms, p95 33.6; off again 0.62.
    ///
    /// SentisOptimisations stretches production rounds in busy frames (ProductionFrameBudget, up to 4 times): in a slow
    /// mode the refineries refine less, so the ore refined in each phase is reported too.
    ///
    /// Phases (<c>modes=off,parallel,off</c>; also <c>detachonly</c>, <c>detach</c>), <c>phase</c> s each after 5 s for
    /// the switch: frames (fps, average, p95, p99), the simulation's speed (frames a second against 60), ore refined.
    /// SentisClusters' settings are put back at the end.
    /// </summary>
    internal sealed class ParallelGainScenario : TestScenario
    {
        public const string ScenarioName = "parallel_gain";
        private const string GridPrefix = "parallel-gain-";
        private const double GroupsApart = 20000;
        private const double GridStep = 250;
        private const int GridsPerRow = 6;
        private const double SettleSeconds = 15;
        private const double SwitchSeconds = 5;

        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private bool _captured;
        private bool _initialFreezerEnabled;
        private uint? _autoSave;
        private object _clusters;
        private bool _initialParallel, _initialDetach;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 1800;

        private static int Arg(string name, int otherwise)
        {
            var m = Regex.Match(TestRunner.Focus ?? "", @"\b" + name + @"=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : otherwise;
        }

        private static string Text(string name, string otherwise)
        {
            var m = Regex.Match(TestRunner.Focus ?? "", @"\b" + name + @"=([\w,]+)");
            return m.Success ? m.Groups[1].Value : otherwise;
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var groups = Arg("groups", 64);
            var perGroup = Arg("grids", 9);
            var phaseSeconds = Arg("phase", 40);
            var modes = Text("modes", "off,parallel,off").Split(',');
            foreach (var mode in modes)
                Check(mode == "off" || mode == "parallel" || mode == "detachonly" || mode == "detach", "no mode " + mode + ": off, parallel, detachonly, detach");

            _clusters = Integrations.TypeOf("SentisClusters", "SentisClusters.SentisClustersPlugin")?.GetProperty("Config")?.GetValue(null);
            Check(_clusters != null, "SentisClusters is not loaded");
            _initialParallel = Get("Parallel");
            _initialDetach = Get("Detach");
            _initialFreezerEnabled = RuntimePluginControls.FreezerEnabled;
            _autoSave = MySession.Static.Settings.AutoSaveInMinutes;
            _captured = true;
            MySession.Static.Settings.AutoSaveInMinutes = 0;
            // nobody is by the grids: the freezer would stop them
            RuntimePluginControls.SetFreezerEnabled(false);

            var template = WorldApi.LoadAuthoredGrid(RefineryPerfScenario.ResourceName, WorldApi.EntityPrefix + GridPrefix + "template");
            var pose = template.PositionAndOrientation.Value;
            var origin = TestRunner.RunOrigin ?? new Vector3D(pose.Position.X + 12000.0, pose.Position.Y, pose.Position.Z);
            var spots = new List<Vector3D>();
            for (var g = 0; g < groups; g++)
            for (var i = 0; i < perGroup; i++)
                spots.Add(origin + new Vector3D(g * GroupsApart + (i % GridsPerRow) * GridStep, 0, (i / GridsPerRow) * GridStep));
            var shift = HeavyNeighbourScenario.OutOfPlanets(spots);
            if (shift.LengthSquared() > 0) Note("the groups moved " + shift.Length().ToString("F0") + " m out of a planet and its gravity");

            foreach (var spot in spots)
            {
                var ob = WorldApi.LoadAuthoredGrid(RefineryPerfScenario.ResourceName, WorldApi.EntityPrefix + GridPrefix + _grids.Count.ToString("D3"));
                ob.PositionAndOrientation = new MyPositionAndOrientation(spot + shift, pose.Forward, pose.Up);
                ob.IsStatic = true;
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                _grids.Add(grid);
                // a heavy grid a frame, not a spawn of seconds in one
                yield return null;
            }
            yield return WaitForTicks(120);

            var refineries = 0;
            var containers = new List<List<MyInventory>>();
            foreach (var grid in _grids)
            {
                WorldApi.EnsureDistributor(grid);
                refineries += WorldApi.FindFunctionals<MyRefinery>(grid).Count;
                containers.Add(WorldApi.FindFunctionals<MyCargoContainer>(grid).Select(c => c.GetInventory(0)).Where(inv => inv != null).ToList());
            }
            yield return WaitForTicks(120);
            Check(_grids.All(g => WorldApi.FindFunctionals<MyRefinery>(g).All(r => r.IsWorking)), "not every refinery is working (power, ownership) after the spawn");

            // ore for all the phases at full speed, twice over: the refineries never run dry
            var ore = RefineryPerfScenario.OreLoad(refineries / _grids.Count, 2 * modes.Length * (phaseSeconds + SwitchSeconds));
            var inventories = _grids.Select(RefineryPerfScenario.InventoriesOf).ToList();
            for (var g = 0; g < _grids.Count; g++)
            {
                RefineryPerfScenario.StockOre(containers[g], ore);
                if ((g + 1) % 8 == 0) yield return null;
            }
            var settle = WaitForSeconds(SettleSeconds, "the grids settle");
            while (settle.MoveNext()) yield return settle.Current;
            Note("PARALLEL GAIN | " + groups + " groups of " + perGroup + " grids " + GroupsApart / 1000 + " km apart, " + refineries + " refineries; phases " +
                 string.Join(", ", modes) + ", " + phaseSeconds + " s each");

            var results = new List<string>();
            foreach (var mode in modes)
            {
                Set("Parallel", mode == "parallel" || mode == "detach");
                Set("Detach", mode == "detachonly" || mode == "detach");
                var wait = WaitForSeconds(SwitchSeconds, "switched to " + mode);
                while (wait.MoveNext()) yield return wait.Current;
                var oreBefore = inventories.Sum(RefineryPerfScenario.CountOre);
                TickMetrics.Take();
                var frame0 = MySession.Static.GameplayFrameCounter;
                var started = DateTime.UtcNow;
                wait = WaitForSeconds(phaseSeconds, mode);
                while (wait.MoveNext()) yield return wait.Current;
                var metrics = TickMetrics.Take();
                var seconds = (DateTime.UtcNow - started).TotalSeconds;
                var sim = Math.Min(1.0, (MySession.Static.GameplayFrameCounter - frame0) / seconds / 60.0);
                var refined = oreBefore - inventories.Sum(RefineryPerfScenario.CountOre);
                var line = mode + ": sim " + sim.ToString("F2") + ", fps " + (1000.0 / Math.Max(0.001, metrics.AvgFrameMs)).ToString("F1") +
                           ", frame avg " + metrics.AvgFrameMs.ToString("F2") + " ms, p95 " + metrics.P95FrameMs.ToString("F2") + ", p99 " +
                           metrics.P99FrameMs.ToString("F2") + ", refined " + (refined / 1000).ToString("F0") + " t";
                Note(line);
                results.Add(line);
            }
            Note("PARALLEL GAIN RESULT | " + string.Join(" || ", results));
        }

        private bool Get(string name) => (bool)_clusters.GetType().GetProperty(name).GetValue(_clusters);

        private void Set(string name, bool value) => _clusters.GetType().GetProperty(name).SetValue(_clusters, value);

        private void Restore()
        {
            if (!_captured) return;
            _captured = false;
            Set("Parallel", _initialParallel);
            Set("Detach", _initialDetach);
            RuntimePluginControls.SetFreezerEnabled(_initialFreezerEnabled);
            if (_autoSave.HasValue) MySession.Static.Settings.AutoSaveInMinutes = _autoSave.Value;
        }

        public override void Cleanup()
        {
            try { Restore(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                Restore();
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                    if (!grid.MarkedForClose && (grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + GridPrefix, StringComparison.OrdinalIgnoreCase))
                        grid.Close();
            }
            finally { base.CleanupLeftovers(); }
        }
    }
}
