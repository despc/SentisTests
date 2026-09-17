using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// World save benchmark. Spawns the same 64 Refinery_test grids as refinery_perf (~32k blocks)
    /// far from players, lets the spawn settle, then saves the world twice through Torch's save path
    /// (the one behind the !save admin command): MySession.Save builds the snapshot on the game thread,
    /// MySessionSnapshot.SaveParallel serializes and writes it in the background. Each save is a
    /// profiling window from the save request until 5 s after the write finished, so garbage
    /// collections caused by the snapshot are included.
    ///
    /// Before saving it checks the SentisOptimisations save patches: component container serialization
    /// against a literal copy of the vanilla code, and parallel grid object builders against sequential
    /// ones (identical XML), and logs an allocation profile of one grid snapshot.
    ///
    /// The saved world contains the test grids until the next save after cleanup.
    /// </summary>
    public sealed class SavePerfScenario : TestScenario
    {
        public const string ScenarioName = "save_perf";
        private const string GridPrefix = "save-perf-";
        private const int GridCount = 64;
        private const int GridsPerRow = 8;
        private const double LatticeStep = 250.0;
        private const int Saves = 2;
        private const double SettleSeconds = 15;
        private const double AfterSaveSeconds = 5;
        private const double BetweenSavesSeconds = 10;
        private const int SaveTimeoutSeconds = 300;
        private const int ConsistencyRounds = 3;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180 + Saves * (SaveTimeoutSeconds / 2);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused("save_perf start");
            var torch = SentisTestsPlugin.TorchInstance;
            Check(torch != null, "Torch instance is not available to the test plugin");

            var template = WorldApi.LoadAuthoredGrid(RefineryPerfScenario.ResourceName, WorldApi.EntityPrefix + GridPrefix + "template");
            var pose = template.PositionAndOrientation.Value;
            var origin = TestRunner.RunOrigin ??
                new Vector3D(pose.Position.X + 12000.0, pose.Position.Y, pose.Position.Z);
            var blocks = 0;
            for (var i = 0; i < GridCount; i++)
            {
                var ob = WorldApi.LoadAuthoredGrid(RefineryPerfScenario.ResourceName, WorldApi.EntityPrefix + GridPrefix + i.ToString("D2"));
                ob.PositionAndOrientation = new MyPositionAndOrientation(
                    origin + new Vector3D((i % GridsPerRow) * LatticeStep, 0, (i / GridsPerRow) * LatticeStep),
                    pose.Forward, pose.Up);
                ob.IsStatic = true;
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                blocks += grid.BlocksCount;
                yield return null;
            }
            Note("spawned " + GridCount + " grids, " + blocks + " blocks; settling " + SettleSeconds + "s");
            var settle = WaitForSeconds(SettleSeconds, "spawned grids settle");
            while (settle.MoveNext()) yield return settle.Current;

            // Parallel snapshot must produce exactly what the vanilla sequential one does.
            var testGrids = Tracked.OfType<MyCubeGrid>().ToList();
            Note("allocation profile of one grid snapshot: " + AllocationProfile(testGrids[0]));
            Note("component containers vs vanilla reference: " + CompareComponentContainers(testGrids));
            for (var round = 1; round <= ConsistencyRounds; round++)
            {
                var sequentialA = testGrids.Select(g => Xml(g.GetObjectBuilder())).ToArray();
                var sequentialB = testGrids.Select(g => Xml(g.GetObjectBuilder())).ToArray();
                var parallelBuilders = new VRage.ObjectBuilders.MyObjectBuilder_EntityBase[testGrids.Count];
                System.Threading.Tasks.Parallel.For(0, testGrids.Count, i => parallelBuilders[i] = testGrids[i].GetObjectBuilder());
                var parallel = parallelBuilders.Select(Xml).ToArray();
                var noise = Enumerable.Range(0, testGrids.Count).Count(i => sequentialA[i] != sequentialB[i]);
                var mismatches = Enumerable.Range(0, testGrids.Count).Count(i => sequentialA[i] != parallel[i]);
                Note("snapshot consistency round " + round + ": " + testGrids.Count + " grids, " +
                     sequentialA.Sum(x => x.Length) / 1024 + " KB xml, sequential-vs-sequential diffs=" + noise +
                     ", sequential-vs-parallel diffs=" + mismatches);
                Check(noise == 0 && mismatches == 0, "parallel grid object builders differ from sequential ones");
                yield return null;
            }

            for (var save = 1; save <= Saves; save++)
            {
                TickMetrics.Take();
                FrameProbe.Take();
                Note("SAVE " + save + " WINDOW START");
                var watch = Stopwatch.StartNew();
                var task = torch.Save();
                Check(task != null, "Torch refused to start a save (another save in progress?)");
                while (!task.IsCompleted)
                {
                    if (watch.Elapsed.TotalSeconds > SaveTimeoutSeconds)
                        throw new ScenarioFailedException("save " + save + " did not finish in " + SaveTimeoutSeconds + "s");
                    yield return null;
                }
                var saveMs = watch.Elapsed.TotalMilliseconds;
                Check(!task.IsFaulted && task.Result.ToString() == "Success",
                    "save " + save + " failed: " + (task.IsFaulted ? task.Exception?.GetBaseException().Message : task.Result.ToString()));

                var after = WaitForSeconds(AfterSaveSeconds, "after save " + save);
                while (after.MoveNext()) yield return after.Current;
                Note("SAVE " + save + " WINDOW END: saved in " + saveMs.ToString("F0") + " ms | " +
                     TickMetrics.Take().Format() + " | " + FrameProbe.Take());

                if (save < Saves)
                {
                    var gap = WaitForSeconds(BetweenSavesSeconds, "between saves");
                    while (gap.MoveNext()) yield return gap.Current;
                }
            }
        }

        /// <summary>
        /// Bytes allocated on this thread by the parts of MyCubeGrid.GetObjectBuilder, measured by calling
        /// the same vanilla methods separately (no patches). Parts overlap: block builders include their
        /// component containers, which include inventories.
        /// </summary>
        private static string AllocationProfile(MyCubeGrid grid)
        {
            Func<Action, long> bytes = action =>
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                action();
                return GC.GetAllocatedBytesForCurrentThread() - before;
            };
            grid.GetObjectBuilder(); // warm up lazy caches
            var whole = bytes(() => grid.GetObjectBuilder());
            var blocks = grid.GetBlocks().ToList();
            var perType = blocks.GroupBy(b => b.FatBlock?.GetType().Name ?? "slim:" + b.BlockDefinition.Id.SubtypeName)
                .Select(g => new
                {
                    Type = g.Key,
                    Count = g.Count(),
                    Block = g.Sum(b => bytes(() => b.GetObjectBuilder())),
                    Components = g.Where(b => b.FatBlock != null).Sum(b => bytes(() => b.FatBlock.Components.Serialize())),
                    Inventories = g.Where(b => b.FatBlock != null && b.FatBlock.HasInventory).Sum(b => bytes(() =>
                    {
                        for (var i = 0; i < b.FatBlock.InventoryCount; i++)
                            ((Sandbox.Game.MyInventory)b.FatBlock.GetInventoryBase(i)).GetObjectBuilder();
                    })),
                })
                .OrderByDescending(x => x.Block).ToList();
            var blocksTotal = perType.Sum(x => x.Block);
            var systems = bytes(() => grid.GridSystems.GetObjectBuilder(new VRage.Game.MyObjectBuilder_CubeGrid()));
            return "grid=" + whole / 1024 + "KB (" + blocks.Count + " blocks), blocks=" + blocksTotal / 1024 + "KB, gridSystems=" +
                   systems / 1024 + "KB, rest=" + (whole - blocksTotal - systems) / 1024 + "KB | per block type: " +
                   string.Join("; ", perType.Select(x => x.Type + " x" + x.Count + ": block " + x.Block / x.Count + "B, components " +
                                                        x.Components / x.Count + "B, inventories " + x.Inventories / x.Count + "B"));
        }

        private static readonly System.Reflection.FieldInfo ComponentsField = typeof(VRage.Game.Components.MyComponentContainer)
            .GetField("m_components", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// Checks MyComponentContainer.Serialize (possibly patched by SentisOptimisations) against a
        /// literal copy of the vanilla implementation, for every functional block of the test grids.
        /// </summary>
        private string CompareComponentContainers(System.Collections.Generic.List<MyCubeGrid> grids)
        {
            int blocks = 0, withContainer = 0, differences = 0;
            foreach (var grid in grids)
            foreach (var slim in grid.GetBlocks())
            {
                var fat = slim.FatBlock;
                if (fat == null) continue;
                blocks++;
                var actual = fat.Components.Serialize();
                var expected = VanillaSerialize((VRage.Game.Components.MyComponentContainer)(object)fat.Components);
                if (expected != null) withContainer++;
                var actualXml = actual == null ? "null" : Xml(actual);
                var expectedXml = expected == null ? "null" : Xml(expected);
                if (actualXml != expectedXml) differences++;
            }
            Check(differences == 0, "component container serialization differs from vanilla for " + differences + " blocks");
            return blocks + " blocks, " + withContainer + " with a container, differences=" + differences;
        }

        private static VRage.Game.ObjectBuilders.ComponentSystem.MyObjectBuilder_ComponentContainer VanillaSerialize(
            VRage.Game.Components.MyComponentContainer container, bool copy = false)
        {
            var components = (System.Collections.Generic.Dictionary<Type, System.Collections.Generic.List<VRage.Game.Components.MyComponentBase>>)
                ComponentsField.GetValue(container);
            var serialized = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Type,
                System.Collections.Generic.List<VRage.Game.Components.MyComponentBase>>>();
            foreach (var component in components)
            {
                var list = new System.Collections.Generic.List<VRage.Game.Components.MyComponentBase>();
                foreach (var item in component.Value)
                    if (item.IsSerialized()) list.Add(item);
                if (list.Count > 0)
                    serialized.Add(new System.Collections.Generic.KeyValuePair<Type,
                        System.Collections.Generic.List<VRage.Game.Components.MyComponentBase>>(component.Key, list));
            }
            if (serialized.Count == 0) return null;
            var result = new VRage.Game.ObjectBuilders.ComponentSystem.MyObjectBuilder_ComponentContainer();
            foreach (var entry in serialized)
            foreach (var item in entry.Value)
            {
                var builder = item.Serialize(copy);
                if (builder != null)
                    result.Components.Add(new VRage.Game.ObjectBuilders.ComponentSystem.MyObjectBuilder_ComponentContainer.ComponentData
                    {
                        TypeId = entry.Key.Name,
                        Component = builder,
                    });
            }
            return result;
        }

        private static string Xml(VRage.ObjectBuilders.MyObjectBuilder_Base builder)
        {
            using (var stream = new System.IO.MemoryStream())
            {
                VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen.SerializeXML(stream, builder);
                return System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!(grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + GridPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                    grid.Close();
                }
            }
            finally { base.CleanupLeftovers(); }
        }
    }
}
