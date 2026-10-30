using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisTests.Core;
using VRage.Game.Entity;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What a safe zone goes over every frame, and what it costs: the game's MySafeZone.UpdateBeforeSimulation walks
    /// the entities in the zone each frame and, for each one that moves, asks whether it is let in (IsSafe: the grid's
    /// mechanical group, its owners, their factions); one not let in it pushes out and has its motors locked. A zone
    /// stays in the update while anything is in it, frozen grids too. Focus <c>id=</c> the zone (else the one with
    /// the most entities in it). Notes the zone, then every entity in it: type, name, static or kinematic (passed over),
    /// what IsSafe says and what it took, and the whole walk timed as the game does it (without the pushing); then how
    /// many of the grids SentisOptimisations' SafeZoneGroupCheck answered from their group's answer in 2 s.
    /// </summary>
    public sealed class SafeZoneContentsScenario : TestScenario
    {
        public const string ScenarioName = "safezone_contents";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 60;

        public override IEnumerator Run()
        {
            var zones = MySessionComponentSafeZones.SafeZones.ToList();
            Check(zones.Count > 0, "no safe zones in the world");
            var contained = typeof(MySafeZone).GetField("m_containedEntities", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new ScenarioFailedException("MySafeZone.m_containedEntities is not in this game");
            var isSafe = typeof(MySafeZone).GetMethod("IsSafe", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(MyEntity) }, null)
                         ?? throw new ScenarioFailedException("MySafeZone.IsSafe(MyEntity) is not in this game");
            IEnumerable<long> In(MySafeZone z) => ((IEnumerable<long>)contained.GetValue(z)).ToList();

            var m = Regex.Match(TestRunner.Focus ?? "", @"id=(\d+)");
            var zone = m.Success ? zones.FirstOrDefault(z => z.EntityId == long.Parse(m.Groups[1].Value)) : zones.OrderByDescending(z => In(z).Count()).First();
            Check(zone != null, "no safe zone " + m.Groups[1].Value);
            Note($"{zones.Count} safe zones; entities in each: " + string.Join(", ", zones.OrderByDescending(z => In(z).Count()).Take(10).Select(z => z.EntityId + " " + In(z).Count())));
            Note($"zone {zone.EntityId}: {zone.Shape}, radius {zone.Radius:F0} m, enabled {zone.Enabled}, at {zone.PositionComp.GetPosition():F0}; " +
                 $"access players {zone.AccessTypePlayers} ({zone.Players.Count}), factions {zone.AccessTypeFactions} ({zone.Factions.Count}: {string.Join(", ", zone.Factions.Select(f => f.Tag))}), " +
                 $"grids {zone.AccessTypeGrids} ({zone.Entities.Count}); update flags {zone.NeedsUpdate}");

            var rows = new List<string>();
            var walked = 0;
            var asked = 0;
            foreach (var id in In(zone))
            {
                if (!MyEntities.TryGetEntityById(id, out var entity))
                {
                    rows.Add(id + " not found");
                    continue;
                }
                walked++;
                var physics = entity.Physics;
                var passed = physics != null && (physics.IsKinematic || physics.IsStatic);
                string safe = "-";
                double ms = 0;
                if (!passed)
                {
                    asked++;
                    var watch = Stopwatch.StartNew();
                    var result = (bool)isSafe.Invoke(zone, new object[] { entity });
                    for (var i = 0; i < 99; i++) isSafe.Invoke(zone, new object[] { entity });
                    ms = watch.Elapsed.TotalMilliseconds / 100;
                    safe = result ? "let in" : "NOT let in (pushed out each frame)";
                }
                var grid = entity as MyCubeGrid;
                var group = grid == null ? 0 : MyCubeGridGroups.Static.Mechanical.GetGroupNodes(grid).Count;
                rows.Add($"{entity.GetType().Name} {entity.DisplayName} {id}{(grid != null ? $" ({grid.BlocksCount} blocks, mechanical group {group}, owners {string.Join("/", grid.BigOwners.Select(o => MySession.Static.Players.TryGetIdentity(o)?.DisplayName ?? o.ToString()))})" : "")}: " +
                         (passed ? (physics.IsStatic ? "static" : "kinematic") + ", passed over" : $"moves; {safe}; IsSafe {ms:F4} ms"));
            }
            foreach (var row in rows) Note("  " + row);

            // the walk as the game does it each frame, the pushing left out
            var frames = 0;
            var total = Stopwatch.StartNew();
            for (; frames < 200; frames++)
                foreach (var id in In(zone))
                {
                    if (!MyEntities.TryGetEntityById(id, out var entity)) continue;
                    var physics = entity.Physics;
                    if (physics != null && (physics.IsKinematic || physics.IsStatic)) continue;
                    isSafe.Invoke(zone, new object[] { entity });
                }
            var walk = total.Elapsed.TotalMilliseconds / frames;
            // SentisOptimisations' answers kept for a group in the zones' own walks, over 2 s of frames
            var counters = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("SentisOptimisationsPlugin.SafeZoneGroupCheck", false)).FirstOrDefault(x => x != null);
            Func<string, long> read = name => counters == null ? -1 : (long)counters.GetField(name).GetValue(null);
            var askedBefore = read("Asked");
            var keptBefore = read("Kept");
            var frozenBefore = counters?.GetField("Frozen") == null ? 0 : read("Frozen");
            for (var f = 0; f < 120; f++) yield return null;
            var kept = counters == null ? "SentisOptimisations' SafeZoneGroupCheck not here" :
                $"SentisOptimisations in the zones' walks over 120 frames: {read("Frozen") - frozenBefore} frozen grids passed over, of {read("Asked") - askedBefore} asked {read("Kept") - keptBefore} answered by their group";
            Note($"SAFE ZONE RESULT | zone {zone.EntityId}: {walked} entities in it, {asked} that move asked IsSafe each frame; the walk {walk:F3} ms a frame (reflection included, every grid asked); {kept}");
        }
    }
}
