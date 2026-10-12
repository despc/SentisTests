using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using VRage;
using VRage.Game;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What it costs SentisOptimisations' freezer to find an assembler's missing resources in the other blocks of its grid
    /// (FreezerPatches.GetItemsCountInOtherBlocks / HasItemsInOtherBlocks, the compensation of a frozen assembler): on the
    /// server (12.10.2026) one assembler took 16 ms of a frame there, in the conveyor's path search. A probe of the world as
    /// it is: the assemblers of the biggest grids, every prerequisite of what they make, both lookups timed - they only read,
    /// nothing in the world changes. The totals found are written too, so two builds can be compared for the same answer.
    /// </summary>
    public sealed class AssemblerPullProbeScenario : TestScenario
    {
        public const string ScenarioName = "assembler_pull_probe";
        private const int Grids = 8;
        private const int WantedCount = 50;
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        public override IEnumerator Run()
        {
            var patches = Integrations.TypeOf("SentisOptimisations", "FreezerPatches")
                          ?? AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                              ?.GetTypes().FirstOrDefault(t => t.Name == "FreezerPatches");
            Check(patches != null, "SentisOptimisations' FreezerPatches not found");
            var count = patches.GetMethod("GetItemsCountInOtherBlocks", Any);
            var has = patches.GetMethod("HasItemsInOtherBlocks", Any);
            Check(count != null && has != null, "the lookups not found");
            var memory = patches.GetField("Reachable", Any);
            Note(memory == null ? "build without the reachability memory" : "build with the reachability memory");

            var grids = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && g.GetFatBlocks().OfType<MyAssembler>().Any())
                .OrderByDescending(g => g.GetFatBlocks().Count(b => b.HasInventory)).Take(Grids).ToList();
            Check(grids.Count > 0, "no grid with an assembler");
            double total = 0, worst = 0;
            long found = 0;
            var lookups = 0;
            foreach (var grid in grids)
            {
                var inventories = grid.GetFatBlocks().Count(b => b.HasInventory);
                double gridMs = 0;
                foreach (var assembler in grid.GetFatBlocks().OfType<MyAssembler>())
                {
                    var blueprints = assembler.Queue.Select(q => q.Blueprint).Distinct().ToList();
                    if (blueprints.Count == 0)
                        blueprints = MyDefinitionManager.Static.GetBlueprintDefinitions().Where(b => b.Results.Length == 1 && b.Results[0].Id.SubtypeName == "SteelPlate"
                                                                                                      && b.Prerequisites.Length > 0).Take(1).ToList();
                    var watch = Stopwatch.StartNew();
                    foreach (var blueprint in blueprints)
                    {
                        (memory?.GetValue(null) as IDictionary)?.Clear();   // a pass of one assembler
                        var efficiency = (MyFixedPoint)(1f / assembler.GetEfficiencyMultiplierForBlueprint(blueprint));
                        foreach (var prerequisite in blueprint.Prerequisites)
                        {
                            found += Convert.ToInt64(count.Invoke(null, new object[] { assembler, prerequisite, efficiency, WantedCount }));
                            var plan = new Dictionary<MyInventory, List<KeyValuePair<MyFixedPoint?, MyDefinitionId>>>();
                            if ((bool)has.Invoke(null, new object[] { assembler, prerequisite, efficiency, plan, WantedCount })) found += 1000000;
                            found += plan.Sum(p => p.Value.Sum(v => (long)(v.Key ?? 0)));
                            lookups++;
                        }
                    }
                    var ms = watch.Elapsed.TotalMilliseconds;
                    gridMs += ms;
                    worst = Math.Max(worst, ms);
                    yield return null;
                }
                total += gridMs;
                Note($"ASSEMBLER PULL | '{grid.DisplayName}': {inventories} blocks with inventories, {grid.GetFatBlocks().OfType<MyAssembler>().Count()} assemblers, lookups {gridMs:0.0} ms");
            }
            Note($"ASSEMBLER PULL | {grids.Count} grids, {lookups} prerequisites looked up: {total:0.0} ms in all, the worst assembler {worst:0.0} ms; found (to compare builds) {found}");
            (memory?.GetValue(null) as IDictionary)?.Clear();
        }
    }
}
