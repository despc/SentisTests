using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// World save with the freezer on. 64 Refinery_test grids are spawned far from players; every
    /// eighth grid gets a large beacon, and the beacon subtype is added to the freezer's
    /// antifreeze list, so 56 grids freeze and 8 never do. Two saves through Torch's save path then
    /// check that SentisOptimisations' FrozenGridSaveCache prepared the frozen grids over earlier
    /// frames and that the snapshot frame built exactly the 8 unfrozen test grids. The first save
    /// also compares every used prepared builder with a fresh one (identical XML); the second one
    /// measures frames.
    /// </summary>
    public sealed class FrozenSavePerfScenario : TestScenario
    {
        public const string ScenarioName = "frozen_save_perf";
        private const string GridPrefix = "frozen-save-perf-";
        private const string BeaconSubtype = "LargeBlockBeacon";
        private const int GridCount = 64;
        private const int GridsPerRow = 8;
        private const int BeaconEvery = 8;
        private const double LatticeStep = 250.0;
        private const int Saves = 2;
        private const int FreezeTimeoutSeconds = 240;
        private const double AfterSaveSeconds = 5;
        private const double BetweenSavesSeconds = 10;
        private const int SaveTimeoutSeconds = 300;

        private bool _captured;
        private bool _initialFreezerEnabled;
        private string _initialAntifreeze;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180 + FreezeTimeoutSeconds + Saves * 60;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused("frozen_save_perf start");
            var torch = SentisTestsPlugin.TorchInstance;
            Check(torch != null, "Torch instance is not available to the test plugin");

            _initialFreezerEnabled = RuntimePluginControls.FreezerEnabled;
            _initialAntifreeze = RuntimePluginControls.AntifreezeBlocksSubtypes ?? "";
            _captured = true;
            var antifreeze = _initialAntifreeze.Split(':').Where(s => s.Length > 0).ToList();
            if (!antifreeze.Contains(BeaconSubtype)) antifreeze.Add(BeaconSubtype);
            RuntimePluginControls.SetAntifreezeBlocksSubtypes(string.Join(":", antifreeze));
            RuntimePluginControls.SetFreezerEnabled(true);
            Note("Freezer enabled, antifreeze subtypes: " + string.Join(":", antifreeze));

            var template = WorldApi.LoadAuthoredGrid(RefineryPerfScenario.ResourceName, WorldApi.EntityPrefix + GridPrefix + "template");
            var pose = template.PositionAndOrientation.Value;
            var origin = TestRunner.RunOrigin ?? new Vector3D(pose.Position.X + 12000.0, pose.Position.Y, pose.Position.Z);
            var beaconGrids = new HashSet<long>();
            var frozenCandidates = new List<MyCubeGrid>();
            for (var i = 0; i < GridCount; i++)
            {
                var ob = WorldApi.LoadAuthoredGrid(RefineryPerfScenario.ResourceName, WorldApi.EntityPrefix + GridPrefix + i.ToString("D2"));
                ob.PositionAndOrientation = new MyPositionAndOrientation(
                    origin + new Vector3D((i % GridsPerRow) * LatticeStep, 0, (i / GridsPerRow) * LatticeStep), pose.Forward, pose.Up);
                ob.IsStatic = true;
                var withBeacon = i % BeaconEvery == 0;
                if (withBeacon) AddBeacon(ob);
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                if (withBeacon) beaconGrids.Add(grid.EntityId);
                else frozenCandidates.Add(grid);
                yield return null;
            }
            var testGridIds = new HashSet<long>(Tracked.OfType<MyCubeGrid>().Select(g => g.EntityId));
            Note("spawned " + GridCount + " grids, " + beaconGrids.Count + " with a beacon");

            var watch = Stopwatch.StartNew();
            var lastLog = 0.0;
            while (true)
            {
                var frozen = frozenCandidates.Count(g => RuntimePluginControls.IsGridFrozen(g.EntityId));
                var frozenBeacon = beaconGrids.Count(RuntimePluginControls.IsGridFrozen);
                Check(frozenBeacon == 0, frozenBeacon + " grids with a beacon got frozen");
                if (frozen == frozenCandidates.Count) break;
                if (watch.Elapsed.TotalSeconds > FreezeTimeoutSeconds)
                    throw new ScenarioFailedException("only " + frozen + "/" + frozenCandidates.Count + " grids froze in " + FreezeTimeoutSeconds + "s");
                if (watch.Elapsed.TotalSeconds - lastLog >= 10)
                {
                    lastLog = watch.Elapsed.TotalSeconds;
                    Note("waiting for the freezer: " + frozen + "/" + frozenCandidates.Count + " frozen");
                }
                yield return WaitForTicks(30);
            }
            Note("frozen: " + frozenCandidates.Count + " grids, unfrozen with beacon: " + beaconGrids.Count +
                 " (after " + watch.Elapsed.TotalSeconds.ToString("F0") + "s)");

            // FrozenGridSaveCache only prepares grids that have been frozen for a few seconds.
            var settle = WaitForSeconds(6, "grids frozen long enough to be prepared");
            while (settle.MoveNext()) yield return settle.Current;

            for (var save = 1; save <= Saves; save++)
            {
                // Save 1 checks every prepared builder against a fresh one (slow); save 2 measures frames.
                var verify = save == 1;
                RuntimePluginControls.SetFrozenGridSaveCacheVerify(verify);
                yield return WaitForTicks(60);
                TickMetrics.Take();
                FrameProbe.Take();
                Note("SAVE " + save + " WINDOW START" + (verify ? " (verifying prepared builders)" : ""));
                var saveWatch = Stopwatch.StartNew();
                var task = torch.Save();
                Check(task != null, "Torch refused to start a save");
                while (!task.IsCompleted)
                {
                    if (saveWatch.Elapsed.TotalSeconds > SaveTimeoutSeconds)
                        throw new ScenarioFailedException("save " + save + " did not finish in " + SaveTimeoutSeconds + "s");
                    yield return null;
                }
                var saveMs = saveWatch.Elapsed.TotalMilliseconds;
                Check(!task.IsFaulted && task.Result.ToString() == "Success",
                    "save " + save + " failed: " + (task.IsFaulted ? task.Exception?.GetBaseException().Message : task.Result.ToString()));
                RuntimePluginControls.SetFrozenGridSaveCacheVerify(false);

                var builtIds = (HashSet<long>)RuntimePluginControls.FrozenGridSaveCacheStat("LastSnapshotBuiltGridIds");
                var builtTest = new HashSet<long>(builtIds.Where(testGridIds.Contains));
                var prepared = (int)RuntimePluginControls.FrozenGridSaveCacheStat("LastSnapshotPreparedGrids");
                var stale = (int)RuntimePluginControls.FrozenGridSaveCacheStat("LastSnapshotStaleGrids");
                var collected = (int)RuntimePluginControls.FrozenGridSaveCacheStat("LastCollectedGrids");
                var frames = (int)RuntimePluginControls.FrozenGridSaveCacheStat("LastCollectionFrames");
                var maxMs = (double)RuntimePluginControls.FrozenGridSaveCacheStat("LastCollectionMaxFrameMs");
                var mismatches = (int)RuntimePluginControls.FrozenGridSaveCacheStat("LastVerifyMismatches");
                var firstDifference = RuntimePluginControls.FrozenGridSaveCacheStatOrNull("LastVerifyFirstDifference") as string;

                var after = WaitForSeconds(AfterSaveSeconds, "after save " + save);
                while (after.MoveNext()) yield return after.Current;
                Note("SAVE " + save + " WINDOW END: saved in " + saveMs.ToString("F0") + " ms; prepared over " + frames +
                     " frames (max " + maxMs.ToString("F2") + " ms/frame, " + collected + " grids); snapshot used " + prepared +
                     " prepared, " + stale + " stale, built " + builtIds.Count + " grids (" + builtTest.Count + " test grids)" +
                     (verify ? "; prepared vs fresh mismatches: " + mismatches + (firstDifference != null ? " (" + firstDifference + ")" : "") : "") +
                     " | " + TickMetrics.Take().Format() + " | " + FrameProbe.Take());

                Check(builtTest.SetEquals(beaconGrids),
                    "snapshot frame built " + builtTest.Count + " test grids but " + beaconGrids.Count + " have a beacon" +
                    " (extra: " + builtTest.Except(beaconGrids).Count() + ", missing: " + beaconGrids.Except(builtTest).Count() + ")");
                Check(prepared >= frozenCandidates.Count, "only " + prepared + " prepared builders used for " + frozenCandidates.Count + " frozen test grids");
                Check(stale == 0, stale + " prepared builders were stale");
                Check(!verify || mismatches == 0, mismatches + " prepared builders differ from fresh ones: " + firstDifference);

                if (save < Saves)
                {
                    var gap = WaitForSeconds(BetweenSavesSeconds, "between saves");
                    while (gap.MoveNext()) yield return gap.Current;
                }
            }
        }

        private static void AddBeacon(MyObjectBuilder_CubeGrid ob)
        {
            // Directly below the lowest layer: guaranteed free cells, touching a block on that layer.
            var lowest = ob.CubeBlocks.OrderBy(b => b.Min.Y).First();
            var beaconId = Sandbox.Definitions.MyDefinitionManager.Static
                .GetDefinitionsOfType<Sandbox.Definitions.MyCubeBlockDefinition>()
                .First(d => d.Id.SubtypeName == BeaconSubtype).Id;
            var beacon = (MyObjectBuilder_CubeBlock)MyObjectBuilderSerializerKeen.CreateNewObject(beaconId);
            beacon.Min = new SerializableVector3I(lowest.Min.X, lowest.Min.Y - 2, lowest.Min.Z);
            beacon.Owner = lowest.Owner;
            beacon.BuiltBy = lowest.BuiltBy;
            beacon.ShareMode = lowest.ShareMode;
            ob.CubeBlocks.Add(beacon);
        }

        private static string Xml(MyObjectBuilder_Base builder)
        {
            using (var stream = new System.IO.MemoryStream())
            {
                MyObjectBuilderSerializerKeen.SerializeXML(stream, builder);
                return System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
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
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!(grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + GridPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                    grid.Close();
                }
            }
            finally { base.CleanupLeftovers(); }
        }

        private void Restore()
        {
            if (!_captured) return;
            RuntimePluginControls.SetFrozenGridSaveCacheVerify(false);
            RuntimePluginControls.SetAntifreezeBlocksSubtypes(_initialAntifreeze);
            RuntimePluginControls.SetFreezerEnabled(_initialFreezerEnabled);
            _captured = false;
        }
    }
}
