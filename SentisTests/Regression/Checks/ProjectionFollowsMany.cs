using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// reg_projection_follows many times over, to make a rare fault come (the stand, 06.11.2026: Torch died of an access
    /// violation with nothing in its log, once while reg_projection_follows moved its projector): 20 static grids with a
    /// projector each, all moved and turned every few frames for a minute - from half a millimetre to 60 m, up to 45
    /// degrees - each hologram checked at its cells after each move, and every 10 s one rig deleted with its projection
    /// and built again; SentisOptimisations' "no welder near: once a second" held off.
    /// focus "rigs=N", "seconds=N".
    /// </summary>
    public sealed class ProjectionFollowsManyCheck : ProjectorRigCheck
    {
        public override string Name => Prefix + "projection_follows_many";

        // 20 rigs built on a loaded stand, then a minute of moves
        public override int TimeoutSeconds => 300;

        private sealed class Rig
        {
            public MyCubeGrid Grid;
            public MyProjectorBase Projector;
            public List<Vector3I> Cells;
            public Vector3D Home;
        }

        protected override IEnumerator Check()
        {
            var focus = Core.TestRunner.Focus ?? "";
            int Arg(string name, int fallback) => System.Text.RegularExpressions.Regex.Match(focus, name + @"=(\d+)") is var m && m.Success ? int.Parse(m.Groups[1].Value) : fallback;
            var count = Arg("rigs", 20);
            var seconds = Arg("seconds", 60);
            yield return Arrive(Site.At(0, 10, 10));
            var rigs = new List<Rig>();
            IEnumerator Make(int i)
            {
                var home = Site.Origin + Site.Right * (i % 5 * 40) - Site.Forward * (i / 5 * 40);
                yield return Project("follows-many-" + i, home);
                HoldOffIdleProjection(Projector);
                rigs.Add(new Rig { Grid = Grid, Projector = Projector, Cells = Cells, Home = home });
            }
            for (var i = 0; i < count; i++) yield return Make(i);
            Note($"{rigs.Count} rigs projecting");

            double Off(Rig r)
            {
                var hologram = r.Projector.ProjectedGrid;
                if (hologram == null) return double.NaN;
                return hologram.CubeBlocks.OrderBy(b => r.Grid.WorldToGridInteger(hologram.GridIntegerToWorld(b.Position)).X)
                    .Select((b, k) => k < r.Cells.Count ? Vector3D.Distance(hologram.GridIntegerToWorld(b.Position), r.Grid.GridIntegerToWorld(r.Cells[k])) : 0).Max();
            }

            var random = new Random(47351);
            var started = DateTime.UtcNow;
            var lastRebuild = started;
            int moves = 0, rebuilt = 0;
            double worst = 0;
            while ((DateTime.UtcNow - started).TotalSeconds < seconds)
            {
                foreach (var r in rigs)
                {
                    if (r.Grid.MarkedForClose) continue;
                    var m = r.Grid.WorldMatrix;
                    var size = random.NextDouble();
                    // mostly small - a ship's tremble - now and then far and turned
                    var meters = size < 0.5 ? 0.0005 + random.NextDouble() * 0.002 : size < 0.9 ? random.NextDouble() * 2 : random.NextDouble() * 60;
                    var degrees = size < 0.5 ? random.NextDouble() * 0.001 : random.NextDouble() * 45;
                    var turn = MatrixD.CreateFromAxisAngle(m.Up, MathHelper.ToRadians(degrees));
                    var direction = Vector3D.Normalize(new Vector3D(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5));
                    var at = m.Translation + direction * meters;
                    // kept near its place: a walk of many moves would carry it off into another's
                    if (Vector3D.Distance(at, r.Home) > 15) at = r.Home;
                    r.Grid.Teleport(MatrixD.CreateWorld(at, Vector3D.TransformNormal(m.Forward, turn), m.Up));
                    moves++;
                }
                yield return WaitForTicks(1 + random.Next(4));
                foreach (var r in rigs.Where(r => !r.Grid.MarkedForClose))
                {
                    var off = Off(r);
                    if (!double.IsNaN(off)) worst = Math.Max(worst, off);
                    Check(double.IsNaN(off) || off < 0.0015, $"the hologram of {r.Grid.DisplayName} did not follow: off by {off:F4} m after {moves} moves");
                }
                if ((DateTime.UtcNow - lastRebuild).TotalSeconds >= 10)
                {
                    lastRebuild = DateTime.UtcNow;
                    var k = random.Next(rigs.Count);
                    var gone = rigs[k];
                    gone.Grid.Close();
                    yield return WaitForTicks(3);
                    rigs.RemoveAt(k);
                    yield return Make(100 + rebuilt);
                    rebuilt++;
                }
            }
            Result($"{rigs.Count} rigs, {moves} moves and turns in {seconds} s, {rebuilt} rebuilt: every hologram followed, off by {worst * 1000:F2} mm at most");
        }
    }
}
