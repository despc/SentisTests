using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SentisTests.Core;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A grid group with rotors and pistons pasted the way ALE ShipFixer's !fixship does it (its FixShipInParallel, on
    /// by default): the builders remapped, every grid made by <c>CreateFromObjectBuilderParallel</c>, all added to the
    /// scene at once when the last is made (<c>EntitySpawnCallback</c>). The prod hung after that, 30.10.2026: the game
    /// thread spun in <c>HashSet.Remove</c> of a grid's set of mechanical blocks waiting to attach. The world's mechanical
    /// group of the most grids is copied <c>runs=</c> times (5 by default), each copy 3 km up from it, and the copies are
    /// removed; the server must keep its frames (no frame over 10 s) and SentisClusters tells in the log a write of the
    /// set off the game's and the clusters' threads.
    /// </summary>
    public sealed class ParallelPasteScenario : TestScenario
    {
        public const string ScenarioName = "parallel_paste";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 600;

        public override IEnumerator Run()
        {
            var runs = Regex.Match(TestRunner.Focus ?? "", @"runs=(\d+)") is var m && m.Success ? int.Parse(m.Groups[1].Value) : 5;
            var group = MyCubeGridGroups.Static.Mechanical.Groups
                .Select(g => g.Nodes.Select(n => n.NodeData).Where(x => !x.MarkedForClose).ToList())
                .Where(g => g.Count >= 3)
                .OrderByDescending(g => g.Count).FirstOrDefault();
            Check(group != null, "no mechanical group of 3 grids or more in the world");
            Note($"group of {group.Count} grids: {string.Join(", ", group.Select(x => x.DisplayName).Distinct().Take(8))}; {group.Sum(x => x.BlocksCount)} blocks");
            var up = Vector3D.Normalize(group[0].PositionComp.GetPosition());
            var builders = group.Select(x => (MyObjectBuilder_CubeGrid)x.GetObjectBuilder(true)).ToList();
            var worst = 0.0;
            for (var run = 0; run < runs; run++)
            {
                var copies = builders.Select(b =>
                {
                    var c = (MyObjectBuilder_CubeGrid)b.Clone();
                    var p = c.PositionAndOrientation.Value;
                    p.Position = p.Position + up * 3000 * (run + 1);
                    c.PositionAndOrientation = p;
                    c.LinearVelocity = default;
                    c.AngularVelocity = default;
                    return (MyObjectBuilder_EntityBase)c;
                }).ToList();
                MyAPIGateway.Entities.RemapObjectBuilderCollection(copies);
                var made = new List<IMyEntity>();
                var added = false;
                foreach (var copy in copies)
                    MyAPIGateway.Entities.CreateFromObjectBuilderParallel(copy, false, e =>
                    {
                        lock (made)
                        {
                            MyEntities.SetEntityName((MyEntity)e, false);
                            made.Add(e);
                            if (made.Count < copies.Count) return;
                            foreach (var x in made) MyEntities.Add((MyEntity)x);
                            added = true;
                        }
                    });
                var watch = Stopwatch.StartNew();
                var frame = Stopwatch.StartNew();
                while (!added && watch.Elapsed.TotalSeconds < 60)
                {
                    worst = System.Math.Max(worst, frame.Elapsed.TotalMilliseconds);
                    frame.Restart();
                    yield return null;
                }
                Check(added, $"run {run + 1}: the copies were not all made in 60 s ({made.Count} of {copies.Count})");
                for (var f = 0; f < 120; f++)
                {
                    worst = System.Math.Max(worst, frame.Elapsed.TotalMilliseconds);
                    frame.Restart();
                    yield return null;
                }
                Note($"run {run + 1}: {made.Count} grids pasted in parallel and kept 2 s; the worst frame so far {worst:0} ms");
                foreach (var x in made.OfType<MyEntity>()) if (!x.MarkedForClose) x.Close();
                yield return null;
            }
            Note($"PARALLEL PASTE RESULT | {runs} pastes of a group of {group.Count} grids; the worst frame {worst:0} ms");
        }
    }
}
