using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// One projection built by welders on two threads of SentisClusters at once. A preview is in no group of a cluster's
    /// grids, so a welder ship and the projector's station (or two ships) more than <c>SplitMarginM</c> apart are updated
    /// side by side, and SentisOptimisations' search state of the projection was changed from both: its HashSet went round
    /// in a loop for ever (prod, 18.11.2026: a ship welder in HashSet.Remove for 60 s, the watchdog stopped the server).
    ///
    /// A station shows a plate of light armour; a row of welders along its edge by the station and one along the far edge,
    /// <see cref="RunSeconds"/> s, with the split of heavy clusters made to take any list (in memory, restored). Then: the
    /// projection's search was run on two threads at the same time (else the check proved nothing), no exception in the
    /// clusters' threads, no update stuck, no error in the log, the plate built. Focus: <c>seconds=60</c>. Not part of
    /// the regression: it changes the clusters' settings.
    /// </summary>
    public sealed class ProjectionSharedWeldersCheck : RegressionCheck
    {
        public const string ScenarioName = "cluster_projection_shared";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 1800;
        private const int Plate = 30;
        private const int PerRow = 18;
        private int RunSeconds = 60;

        private readonly Scenarios.ConfigOverride _config = new Scenarios.ConfigOverride();
        private readonly Scenarios.ConfigOverride _clusters = new Scenarios.ConfigOverride(Scenarios.ConfigOverride.Clusters);
        private Harmony _harmony;
        private bool _splitWas = true;

        private static object ClustersSetting(string name)
        {
            var config = AccessTools.TypeByName("SentisClusters.SentisClustersPlugin")?.GetProperty("Config", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            return config?.GetType().GetProperty(name)?.GetValue(config);
        }

        private static int _inSearch, _mostInSearch;
        private static long _searches, _overlaps;

        private static void Enter()
        {
            Interlocked.Increment(ref _searches);
            var now = Interlocked.Increment(ref _inSearch);
            if (now > 1) Interlocked.Increment(ref _overlaps);
            int most;
            while (now > (most = Volatile.Read(ref _mostInSearch)) && Interlocked.CompareExchange(ref _mostInSearch, now, most) != most) { }
        }

        private static Exception Leave(Exception __exception)
        {
            Interlocked.Decrement(ref _inSearch);
            return __exception;
        }

        protected override IEnumerator Check()
        {
            Check(Integrations.IsLoaded("SentisClusters") || ClusterStressCheck.Command("!clusters status").Any(), "SentisClusters is not loaded");
            var search = AccessTools.TypeByName("Optimizer.Optimizations.WelderOptimization")?.GetMethod("FindProjectedBlocks", BindingFlags.Static | BindingFlags.NonPublic);
            Check(search != null, "SentisOptimisations' WelderOptimization.FindProjectedBlocks not found");
            var focus = TestRunner.Focus ?? "";
            var m = Regex.Match(focus, @"seconds=(\d+)");
            if (m.Success) RunSeconds = Math.Max(10, int.Parse(m.Groups[1].Value));

            _splitWas = ClustersSetting("SplitHeavy") is true;
            _config.Set("FreezerEnabled", false);
            // any list of a cluster split by its groups of grids: the welders by the station and those far off side by side
            _clusters.Set("SplitHeavy", true);
            _clusters.Set("SplitHeavyMs", 0f);
            _clusters.Set("SplitMinShare", 0f);
            _clusters.Set("MinParallelUpdates", 1);
            ClusterStressCheck.Command("!clusters set SplitHeavy on");
            yield return Arrive(Site.At(0, 10, -15));

            var before = ClusterStressCheck.Exceptions();
            var errorsAt = ClusterStressCheck.LogMark();

            var plateParts = new List<Part>();
            for (var x = 0; x < Plate; x++)
                for (var z = 0; z < Plate; z++)
                    plateParts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, 0, z));
            var source = Build("shared-plate", Site.At(0, 300, 0), true, plateParts);
            var plateOb = (MyObjectBuilder_CubeGrid)source.GetObjectBuilder(true);
            source.Close();
            yield return null;

            var station = Build("shared-station", Site.At(0, 40, 0), true, new[]
            {
                new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                new Part("MyObjectBuilder_Projector/LargeProjector", 1, 0, 0).With(b =>
                {
                    var projector = (MyObjectBuilder_ProjectorBase)b;
                    projector.ProjectedGrids = new List<MyObjectBuilder_CubeGrid> { (MyObjectBuilder_CubeGrid)plateOb.Clone() };
                    projector.ProjectionOffset = new Vector3I(1, 0, 0);
                    projector.KeepProjection = true;
                    projector.ProjectionsRemaining = 0;
                    projector.Scale = 1;
                }),
            });
            Put(Inventory(Block<MyReactor>(station)), "Ingot/Uranium", 20);
            var projectorBlock = Block<MyProjectorBase>(station);
            yield return Until(() => projectorBlock.IsWorking && projectorBlock.ProjectedGrid != null ? 1 : 0, v => v > 0, "the projection up", 30, () => State(projectorBlock));
            var shown = projectorBlock.ProjectedGrid;
            long added = 0;
            station.OnBlockAdded += b => Interlocked.Increment(ref added);

            // a row of welders along the plate's edge by the station, another along the far edge, ~75 m apart: a cluster's
            // list is split only when it holds 32 entities and more
            var corner = shown.GridIntegerToWorld(shown.Min);
            var acrossX = Vector3D.Normalize(shown.GridIntegerToWorld(shown.Min + Vector3I.Right) - corner);
            var acrossZ = Vector3D.Normalize(shown.GridIntegerToWorld(shown.Min + Vector3I.Backward) - corner);
            var farCorner = shown.GridIntegerToWorld(shown.Max);
            var welders = new List<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>();
            for (var k = 0; k < PerRow; k++)
                foreach (var (at, name) in new[]
                         {
                             (corner + acrossX * (2 + 3 * k) + acrossZ * 2, "near"),
                             (farCorner - acrossX * (2 + 3 * k) - acrossZ * 2, "far"),
                         })
                {
                    var rig = ToolRig("shared-welder-" + name + "-" + k, "ShipWelder/LargeShipWelder", at, -Site.Up);
                    var welder = Block<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(rig);
                    Put(Inventory(welder), "Component/SteelPlate", 3000);
                    welders.Add(welder);
                }

            // and beyond each row, away from the other, a block of welders switched off: the loop of every 10th frame takes
            // a tenth of a cluster's list each frame, and a share is split only from 32 entities on - 320 and more of them
            var fillers = new List<MyCubeGrid>();
            foreach (var (from, outward, name) in new[] { (corner, -acrossZ, "near"), (farCorner, acrossZ, "far") })
            {
                var parts = new List<Part>();
                for (var y = 0; y < 2; y++)
                    for (var x = 0; x < 20; x++)
                        for (var z = 0; z < 4; z++)
                            parts.Add(new Part("ShipWelder/LargeShipWelder", x, y, z * 2));
                parts.Add(new Part("Reactor/LargeBlockSmallGenerator", -2, 0, 0));
                var filler = Build("shared-filler-" + name, from + outward * 30 + Site.Up * 10, true, parts, forward: outward, up: Site.Up);
                Put(Inventory(Block<MyReactor>(filler)), "Ingot/Uranium", 5);
                foreach (var off in Blocks<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(filler)) Switch(off, false);
                fillers.Add(filler);
            }
            yield return Seconds(3);

            _inSearch = _mostInSearch = 0;
            _searches = _overlaps = 0;
            _harmony = new Harmony("SentisTests.ProjectionSharedWelders");
            _harmony.Patch(search, prefix: new HarmonyMethod(typeof(ProjectionSharedWeldersCheck).GetMethod(nameof(Enter), BindingFlags.Static | BindingFlags.NonPublic)),
                finalizer: new HarmonyMethod(typeof(ProjectionSharedWeldersCheck).GetMethod(nameof(Leave), BindingFlags.Static | BindingFlags.NonPublic)));
            foreach (var welder in welders) Switch(welder, true);
            Note("welders in clusters " + string.Join(", ", welders.Select(w => ClusterId(w.CubeGrid)).Distinct()));

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < RunSeconds)
            {
                foreach (var welder in welders)
                    if (Amount(Inventory(welder), "Component/SteelPlate") < 500) Put(Inventory(welder), "Component/SteelPlate", 2000);
                yield return Seconds(1);
            }
            foreach (var welder in welders) Switch(welder, false);
            yield return Seconds(2);
            _harmony.UnpatchAll(_harmony.Id);
            _harmony = null;

            var report = new List<string>();
            var failures = new List<string>();
            report.Add("searches of the projection " + _searches + ", run while another one was " + _overlaps + ", at most " + _mostInSearch + " at once");
            if (_mostInSearch < 2) failures.Add("the projection was never searched on two threads at once: the check proved nothing");
            var thrown = ClusterStressCheck.Exceptions() - before;
            report.Add("exceptions in the clusters' threads " + thrown);
            if (thrown > 0) failures.Add(thrown + " exceptions in the clusters' threads");
            var log = ClusterStressCheck.LogSince(errorsAt);
            var stuck = log.Count(l => l.Contains("has been updating"));
            report.Add("updates stuck " + stuck);
            if (stuck > 0) failures.Add("updates stuck: " + log.First(l => l.Contains("has been updating")));
            var errors = log.Where(l => (l.Contains("[ERROR]") || l.Contains("[FATAL]")) && !l.Contains("urlacl") && !l.Contains("may not listen") && !l.Contains("has been updating")).ToList();
            report.Add("errors in the log " + errors.Count);
            if (errors.Count > 0) failures.Add("errors in the log: " + string.Join(" | ", errors.Take(3).Select(l => l.Length > 300 ? l.Substring(0, 300) : l)));
            report.Add("blocks welded " + added + " of " + Plate * Plate);
            if (added < 20) failures.Add("almost nothing welded: " + added);
            Check(failures.Count == 0, string.Join(" || ", failures) + " ||| " + string.Join("; ", report));
            Result(string.Join("; ", report));
        }

        public override void Cleanup()
        {
            try { _harmony?.UnpatchAll(_harmony.Id); }
            catch (Exception) { }
            _harmony = null;
            // restored, then told to the clusters (the command sets the same value and applies the settings)
            try { _clusters.Restore(); ClusterStressCheck.Command("!clusters set SplitHeavy " + (_splitWas ? "on" : "off")); }
            catch (Exception) { }
            try { _config.Restore(); }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
