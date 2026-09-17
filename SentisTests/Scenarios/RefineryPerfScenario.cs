using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game;
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
    /// Refinery load benchmark. The operator's Refinery_test grid (101 large refineries, 21 large
    /// containers, 20 fuelled reactors on one conveyor network) is spawned 64 times on a lattice far
    /// from players, every copy's containers are filled with a mix of ores sized so the vanilla
    /// refineries need about <see cref="ProcessingSeconds"/> to chew through them, and the run lasts
    /// until all the ore is refined. The processing phase is the profiling window: frame metrics
    /// (<see cref="FrameProbe"/>) cover only refining, not spawning or stocking.
    /// </summary>
    public sealed class RefineryPerfScenario : TestScenario
    {
        public const string ScenarioName = "refinery_perf";
        private const string ResourceName = "SentisTests.Resources.RefineryTest.xml";
        private const string GridPrefix = "refinery-perf-";
        private const int GridCount = 64;
        private const int GridsPerRow = 8;
        private const double LatticeStep = 250.0;
        private const double ProcessingSeconds = 90.0;
        private const int MaxProcessingSeconds = 300;
        private const int LogEverySeconds = 10;
        private const double SpawnSettleSeconds = 15;

        // Iron only. A refinery pull grabs up to OreAmountPerPullRequest of EVERY ore type in reach
        // and refines them one after another, so with slower ores (even Nickel) the last tons end up
        // hoarded in a handful of refineries while the rest idle, and the run drags on without
        // adding CPU load.
        private static readonly string[] Ores = { "Iron" };

        private bool _captured;
        private bool _initialFreezerEnabled;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => MaxProcessingSeconds + 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused("refinery_perf start");
            _initialFreezerEnabled = RuntimePluginControls.FreezerEnabled;
            _captured = true;
            // Every copy is parked far from players; with the freezer on they would simply stop.
            RuntimePluginControls.SetFreezerEnabled(false);
            Note("Freezer disabled for the benchmark");

            var template = WorldApi.LoadAuthoredGrid(ResourceName, WorldApi.EntityPrefix + GridPrefix + "template");
            var pose = template.PositionAndOrientation.Value;
            var origin = TestRunner.RunOrigin ??
                new Vector3D(pose.Position.X + 12000.0, pose.Position.Y, pose.Position.Z);

            var grids = new List<MyCubeGrid>(GridCount);
            for (var i = 0; i < GridCount; i++)
            {
                var ob = WorldApi.LoadAuthoredGrid(ResourceName, WorldApi.EntityPrefix + GridPrefix + i.ToString("D2"));
                var position = origin + new Vector3D((i % GridsPerRow) * LatticeStep, 0, (i / GridsPerRow) * LatticeStep);
                ob.PositionAndOrientation = new MyPositionAndOrientation(position, pose.Forward, pose.Up);
                ob.IsStatic = true;
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                grids.Add(grid);
                // One heavy grid per frame instead of a single multi-second spawn frame.
                yield return null;
            }

            yield return WaitForTicks(120);
            var refineries = 0;
            var containers = new List<List<MyInventory>>(GridCount);
            foreach (var grid in grids)
            {
                WorldApi.EnsureDistributor(grid);
                var gridRefineries = WorldApi.FindFunctionals<MyRefinery>(grid);
                refineries += gridRefineries.Count;
                Check(gridRefineries.Count > 0, grid.DisplayName + " has no refineries");
                var cargo = WorldApi.FindFunctionals<MyCargoContainer>(grid)
                    .Select(c => c.GetInventory(0)).Where(inv => inv != null).ToList();
                Check(cargo.Count > 0, grid.DisplayName + " has no cargo containers");
                containers.Add(cargo);
            }
            var perGrid = refineries / GridCount;

            yield return WaitForTicks(120);
            Check(grids.All(g => WorldApi.FindFunctionals<MyRefinery>(g).All(r => r.IsWorking)),
                "not every refinery is working (power/ownership) after spawn");

            // Spawning 64 grids leaves tens of MB of fresh long-lived objects; the gen0/gen1 collections
            // that promote them land in the next seconds. Let them pass so the window measures the
            // ore unload itself, not the echo of the spawn.
            var settle = WaitForSeconds(SpawnSettleSeconds, "spawned grids settle");
            while (settle.MoveNext()) yield return settle.Current;

            var orePerGrid = OreLoad(perGrid);
            Note("spawned " + GridCount + " grids, " + refineries + " refineries; ore per grid: " +
                 string.Join(", ", orePerGrid.Select(o => o.Key + "=" + o.Value.ToString("F0") + "kg")));

            // Harness work inside the measured window must stay tiny: inventories are collected here,
            // and during refining only one grid is recounted per frame (a full pass takes GridCount frames).
            var inventories = grids.Select(InventoriesOf).ToList();
            var initialOre = 0.0;
            for (var g = 0; g < GridCount; g++)
            {
                StockOre(containers[g], orePerGrid);
                initialOre += CountOre(inventories[g]);
                if ((g + 1) % 8 == 0) yield return null;
            }
            yield return null;
            Check(initialOre > 0.99 * orePerGrid.Values.Sum() * GridCount,
                "ore did not fit in the containers: stocked " + initialOre.ToString("F0") + "kg");

            TickMetrics.Take();
            FrameProbe.Take();
            var started = DateTime.UtcNow;
            Note("PROFILE WINDOW START: " + refineries + " refineries refining " + (initialOre / 1000).ToString("F0") +
                 " t of ore (expected ~" + ProcessingSeconds + "s)");

            var orePerGridLeft = inventories.Select(CountOre).ToArray();
            var remaining = orePerGridLeft.Sum();
            var cursor = 0;
            var lastLog = DateTime.UtcNow;
            while (true)
            {
                orePerGridLeft[cursor] = CountOre(inventories[cursor]);
                cursor = (cursor + 1) % GridCount;
                if (cursor != 0)
                {
                    yield return null;
                    continue;
                }
                remaining = orePerGridLeft.Sum();
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                if (remaining <= initialOre * 0.0001) break;
                if (elapsed >= MaxProcessingSeconds)
                    throw new ScenarioFailedException("ore not refined in " + MaxProcessingSeconds + "s: remaining " +
                        (remaining / 1000).ToString("F1") + " t of " + (initialOre / 1000).ToString("F1") + " t | " +
                        TickMetrics.Take().Format() + " | " + FrameProbe.Take());
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= LogEverySeconds)
                {
                    lastLog = DateTime.UtcNow;
                    var laggards = orePerGridLeft.Select((ore, index) => new { ore, index })
                        .OrderByDescending(g => g.ore).Take(3)
                        .Select(g => "#" + g.index + "=" + (g.ore / 1000).ToString("F0") + "t");
                    Note("refining: elapsed=" + elapsed.ToString("F0") + "s, ore left=" + (remaining / 1000).ToString("F1") +
                         " t (" + (100 * remaining / initialOre).ToString("F1") + "%), grids with ore=" +
                         orePerGridLeft.Count(ore => ore > 1) + ", most left: " + string.Join(" ", laggards));
                }
                yield return null;
            }

            var metrics = TickMetrics.Take();
            var simWork = FrameProbe.Take();
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            var ingots = inventories.Sum(inv => CountItems(inv, "MyObjectBuilder_Ingot", null));
            Check(ingots > 0, "no ingots produced");
            Note("PROFILE WINDOW END: refined " + (initialOre / 1000).ToString("F0") + " t in " + seconds.ToString("F1") +
                 "s by " + refineries + " refineries on " + GridCount + " grids | " + metrics.Format() + " | " + simWork);
        }

        /// <summary>
        /// Ore per grid so that the grid's refineries need about ProcessingSeconds in total, split
        /// evenly in time across the ore types. Rates come from the live blueprints and world speed
        /// multiplier, so the load tracks the server's settings instead of hard-coded kilograms.
        /// </summary>
        private static Dictionary<string, double> OreLoad(int refineriesPerGrid)
        {
            var refinery = MyDefinitionManager.Static.GetCubeBlockDefinition(
                new MyDefinitionId(typeof(Sandbox.Common.ObjectBuilders.MyObjectBuilder_Refinery), "LargeRefinery")) as MyRefineryDefinition;
            Check(refinery != null, "LargeRefinery definition not found");
            var load = new Dictionary<string, double>();
            var secondsPerOre = ProcessingSeconds / Ores.Length;
            foreach (var ore in Ores)
            {
                var oreId = new MyDefinitionId(typeof(MyObjectBuilder_Ore), ore);
                var blueprint = refinery.BlueprintClasses.SelectMany(c => c)
                    .FirstOrDefault(b => b.Prerequisites.Length == 1 && b.Prerequisites[0].Id == oreId);
                Check(blueprint != null, "no refinery blueprint for " + ore + " ore");
                // Same rate formula as MyRefinery.ProcessQueueItems, no upgrade modules.
                var kgPerSecond = refinery.RefineSpeed * MySession.Static.RefinerySpeedMultiplier /
                                  blueprint.BaseProductionTimeInSeconds * (double)blueprint.Prerequisites[0].Amount;
                load[ore] = kgPerSecond * refineriesPerGrid * secondsPerOre;
            }
            return load;
        }

        private static void StockOre(List<MyInventory> containers, Dictionary<string, double> ore)
        {
            foreach (var entry in ore)
            {
                var left = (MyFixedPoint)entry.Value;
                var content = new MyObjectBuilder_Ore { SubtypeName = entry.Key };
                foreach (var inventory in containers)
                {
                    if (left <= 0) break;
                    var fits = MyFixedPoint.Min(left, inventory.ComputeAmountThatFits(content.GetId()));
                    if (fits <= 0) continue;
                    inventory.AddItems(fits, content);
                    left -= fits;
                }
            }
        }

        private static List<MyInventory> InventoriesOf(MyCubeGrid grid)
        {
            var result = new List<MyInventory>();
            foreach (var block in grid.GetFatBlocks())
            {
                if (!block.HasInventory) continue;
                for (var i = 0; i < block.InventoryCount; i++)
                {
                    var inventory = block.GetInventory(i);
                    if (inventory != null) result.Add(inventory);
                }
            }
            return result;
        }

        private static double CountOre(List<MyInventory> inventories) => CountItems(inventories, "MyObjectBuilder_Ore", Ores);

        private static double CountItems(List<MyInventory> inventories, string typeId, string[] subtypes)
        {
            double total = 0;
            foreach (var inventory in inventories)
            foreach (var item in inventory.GetItems())
            {
                if (item.Content.TypeId.ToString() != typeId) continue;
                if (subtypes != null && Array.IndexOf(subtypes, item.Content.SubtypeName) < 0) continue;
                total += (double)item.Amount;
            }
            return total;
        }

        public override void Cleanup()
        {
            try { RestoreRuntimeConfig(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                RestoreRuntimeConfig();
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

        private void RestoreRuntimeConfig()
        {
            if (!_captured) return;
            RuntimePluginControls.SetFreezerEnabled(_initialFreezerEnabled);
            _captured = false;
        }
    }
}
