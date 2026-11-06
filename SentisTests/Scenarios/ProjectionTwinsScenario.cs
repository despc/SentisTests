using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game.Entity;
using VRage.ModAPI;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A survey of the world's projections: a block a projector builds takes the entity id of its ghost, and the ghost
    /// stays in the projection, in the scene and updated (prod 06.11.2026, the torpedo tanks: SentisOptimisations kept a
    /// tank's gas by its id and the ghost took it). What ghosts there are and of what kinds, which of them have a built
    /// twin under the same id, which are updated, which hold items, and whose inventories change over the watch (the
    /// ghosts' conveyors moving their blueprint's items): what a plugin keeping things by a block's id could mix up.
    /// Nothing is spawned or changed. focus "watch=N" for N seconds of watching (30).
    /// </summary>
    public sealed class ProjectionTwinsScenario : TestScenario
    {
        public const string ScenarioName = "projection_twins";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 240;

        public override IEnumerator Run()
        {
            var focus = TestRunner.Focus ?? "";
            var watch = System.Text.RegularExpressions.Regex.Match(focus, @"watch=(\d+)") is var m && m.Success ? int.Parse(m.Groups[1].Value) : 30;
            WorldApi.EnsureUnpaused(Name);

            var previews = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && g.IsPreview).ToList();
            var projectors = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && !g.IsPreview)
                .SelectMany(g => g.GetFatBlocks().OfType<MyProjectorBase>()).Where(p => p.ProjectedGrid != null).ToList();
            var ghosts = previews.SelectMany(g => g.GetFatBlocks()).ToList();
            Note($"projections: {previews.Count} preview grids, {projectors.Count} projectors projecting, {ghosts.Count} ghost blocks");

            // twins: a built block in the world under the ghost's id
            var real = new Dictionary<long, MyCubeBlock>();
            foreach (var g in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && !g.IsPreview))
            foreach (var b in g.GetFatBlocks())
                real[b.EntityId] = b;
            var twins = ghosts.Where(x => real.TryGetValue(x.EntityId, out var r) && r != x).ToList();
            var resolvesToGhost = ghosts.Count(x => MyEntities.TryGetEntityById(x.EntityId, out var e) && ReferenceEquals(e, x));
            Note($"ghosts with a built twin under the same id: {twins.Count}; ghosts the id lookup finds (MyEntities.TryGetEntityById): {resolvesToGhost}");
            foreach (var kind in twins.GroupBy(x => x.GetType().Name).OrderByDescending(k => k.Count()))
                Note($"   twins {kind.Key}: {kind.Count()}");

            // updated: what the game calls on them
            foreach (var kind in ghosts.GroupBy(x => x.GetType().Name).OrderByDescending(k => k.Count()).Take(40))
            {
                var updated = kind.Count(x => x.NeedsUpdate != MyEntityUpdateEnum.NONE);
                var enabled = kind.Count(x => x is MyFunctionalBlock f && f.Enabled);
                var working = kind.Count(x => x.IsWorking);
                var flags = string.Join("|", kind.Select(x => x.NeedsUpdate).Distinct().Select(f => f.ToString()));
                Note($"   ghosts {kind.Key}: {kind.Count()}, updated {updated} ({flags}), enabled {enabled}, working {working}");
            }

            // items: what ghosts hold, and their inventories changing
            var inventories = ghosts.Where(x => x.HasInventory)
                .SelectMany(x => Enumerable.Range(0, x.InventoryCount).Select(i => (Block: x, Inventory: x.GetInventory(i) as MyInventory)))
                .Where(p => p.Inventory != null).ToList();
            var holding = inventories.Where(p => p.Inventory.ItemCount > 0).ToList();
            Note($"ghost inventories: {inventories.Count}, holding items: {holding.Count} ({holding.Sum(p => (double)p.Inventory.CurrentMass):F0} kg)");
            foreach (var kind in holding.GroupBy(p => p.Block.GetType().Name).OrderByDescending(k => k.Count()))
                Note($"   holding {kind.Key}: {kind.Count()}, {kind.Sum(p => (double)p.Inventory.CurrentMass):F0} kg");

            var changes = new Dictionary<MyInventory, int>();
            var handlers = new List<(MyInventory, Action<MyInventoryBase>)>();
            foreach (var p in inventories)
            {
                var inventory = p.Inventory;
                Action<MyInventoryBase> h = _ => { changes.TryGetValue(inventory, out var n); changes[inventory] = n + 1; };
                inventory.ContentsChanged += h;
                handlers.Add((inventory, h));
            }
            var before = inventories.ToDictionary(p => p.Inventory, p => (double)p.Inventory.CurrentMass);
            try
            {
                for (var s = 0; s < watch; s += 5)
                    yield return WaitForSeconds(5, "watching the ghosts' inventories");
            }
            finally
            {
                foreach (var (inventory, h) in handlers) inventory.ContentsChanged -= h;
            }
            var changed = inventories.Where(p => changes.ContainsKey(p.Inventory)).ToList();
            Note($"in {watch} s: {changed.Count} ghost inventories changed ({changes.Values.Sum()} changes)");
            foreach (var p in changed.OrderByDescending(p => changes[p.Inventory]).Take(20))
            {
                var twin = real.TryGetValue(p.Block.EntityId, out var r) && r != p.Block ? $"twin {r.GetType().Name} on {r.CubeGrid.DisplayName}" : "no twin";
                Note($"   {p.Block.GetType().Name} {p.Block.EntityId} '{(p.Block as MyTerminalBlock)?.CustomName}' on {p.Block.CubeGrid.DisplayName}: {changes[p.Inventory]} changes, " +
                     $"{before[p.Inventory]:F1} -> {(double)p.Inventory.CurrentMass:F1} kg; {twin}");
            }
            Note("result: " + $"{ghosts.Count} ghosts, {twins.Count} with a twin, {holding.Count} inventories holding, {changed.Count} changed in {watch} s");
        }
    }
}
