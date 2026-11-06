using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// How the world's projections move: every frame for a while, each preview grid's world matrix against the frame
    /// before - how many stand still (MultigridProjector no longer sets their matrix again) and how many move, by how
    /// much, and on what kind of projector's grid (static, a ship, frozen by SentisOptimisations' freezer). Nothing is
    /// spawned or changed. focus "frames=N" (120).
    /// </summary>
    public sealed class ProjectionMotionScenario : TestScenario
    {
        public const string ScenarioName = "projection_motion";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        private sealed class Track
        {
            public MyCubeGrid Preview;
            public MyProjectorBase Projector;
            public MatrixD Last;
            public int Moved, Seen;
            public double MaxStep;
        }

        public override IEnumerator Run()
        {
            var focus = TestRunner.Focus ?? "";
            var frames = System.Text.RegularExpressions.Regex.Match(focus, @"frames=(\d+)") is var m && m.Success ? int.Parse(m.Groups[1].Value) : 120;
            WorldApi.EnsureUnpaused(Name);
            var projectors = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && !g.IsPreview)
                .SelectMany(g => g.GetFatBlocks().OfType<MyProjectorBase>()).Where(p => p.ProjectedGrid != null).ToList();
            // every preview grid with the projector showing it (MultigridProjector: one per subgrid)
            var byPreview = new Dictionary<MyCubeGrid, MyProjectorBase>();
            foreach (var g in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.IsPreview && !g.MarkedForClose))
            {
                var p = projectors.FirstOrDefault(x => x.ProjectedGrid == g) ??
                        projectors.OrderBy(x => Vector3D.DistanceSquared(x.PositionComp.GetPosition(), g.PositionComp.GetPosition())).FirstOrDefault();
                if (p != null) byPreview[g] = p;
            }
            var tracks = byPreview.Select(kv => new Track { Preview = kv.Key, Projector = kv.Value, Last = kv.Key.WorldMatrix }).ToList();
            Note($"{projectors.Count} projectors projecting, {tracks.Count} preview grids, {tracks.Sum(t => t.Preview.GetFatBlocks().Count)} ghost blocks");
            for (var f = 0; f < frames; f++)
            {
                yield return WaitForTicks(1);
                foreach (var t in tracks)
                {
                    if (t.Preview.MarkedForClose) continue;
                    var now = t.Preview.WorldMatrix;
                    var step = Vector3D.Distance(now.Translation, t.Last.Translation) + Vector3D.Distance(now.Forward, t.Last.Forward) + Vector3D.Distance(now.Up, t.Last.Up);
                    t.Seen++;
                    if (step > 0) { t.Moved++; t.MaxStep = System.Math.Max(t.MaxStep, step); }
                    t.Last = now;
                }
            }
            string Kind(Track t)
            {
                var g = t.Projector.CubeGrid;
                var frozen = g.Physics == null || !g.InScene ? "no physics" : g.IsStatic ? "static" : g.Physics.LinearVelocity.Length() > 0.01 || g.Physics.AngularVelocity.Length() > 1e-4 ? "a moving ship" : "a ship at rest";
                return frozen;
            }
            foreach (var group in tracks.GroupBy(Kind).OrderBy(x => x.Key))
            {
                var ghosts = group.Sum(t => t.Preview.GetFatBlocks().Count);
                var still = group.Count(t => t.Moved == 0);
                var always = group.Count(t => t.Moved >= t.Seen - 1);
                Note($"on {group.Key}: {group.Count()} previews ({ghosts} ghosts): still {still}, moving every frame {always}, now and then {group.Count() - still - always}; " +
                     $"ghosts of moving ones {group.Where(t => t.Moved > 0).Sum(t => t.Preview.GetFatBlocks().Count)}");
            }
            foreach (var t in tracks.Where(t => t.Moved > 0).OrderByDescending(t => t.Preview.GetFatBlocks().Count).Take(15))
                Note($"   moving: '{t.Projector.CustomName}' on {t.Projector.CubeGrid.DisplayName} ({Kind(t)}): preview of {t.Preview.GetFatBlocks().Count} ghosts, moved {t.Moved}/{t.Seen} frames, " +
                     $"max step {t.MaxStep:G3}");

            // the cost: MultigridProjector's UpdateGridTransformations of every projection, against setting every preview's
            // matrix as it is (what it did every frame before it skipped a preview standing still)
            var type = System.AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("MultigridProjector.Logic.MultigridProjection")).FirstOrDefault(x => x != null);
            var find = type?.GetMethod("TryFindProjectionByProjector", new[] { typeof(MyProjectorBase), type.MakeByRefType() });
            var update = type?.GetMethod("UpdateGridTransformations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (find != null && update != null)
            {
                var projections = projectors.Select(p => { var a = new object[] { p, null }; return (bool)find.Invoke(null, a) ? a[1] : null; }).Where(x => x != null).ToList();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                const int rounds = 50;
                for (var r = 0; r < rounds; r++) foreach (var x in projections) update.Invoke(x, null);
                var aligned = watch.Elapsed.TotalMilliseconds / rounds;
                watch.Restart();
                for (var r = 0; r < rounds; r++)
                    foreach (var t in tracks.Where(t => !t.Preview.MarkedForClose))
                    {
                        var wm = t.Preview.WorldMatrix;
                        t.Preview.PositionComp.SetWorldMatrix(ref wm, skipTeleportCheck: true);
                    }
                var set = watch.Elapsed.TotalMilliseconds / rounds;
                Note($"a frame's UpdateGridTransformations of {projections.Count} projections: {aligned:F3} ms; setting all {tracks.Count} previews' matrices as they are: {set:F3} ms");
            }
            else Note("no MultigridProjector to time");
            Note($"result: {tracks.Count(t => t.Moved == 0)} of {tracks.Count} previews still over {frames} frames");
        }
    }
}
