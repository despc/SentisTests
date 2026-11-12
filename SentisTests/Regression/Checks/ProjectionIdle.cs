using System;
using System.Collections;
using System.Linq;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisOptimisations moves a projection with no enabled welder near once a second (Multigrid Projector moved
    /// every projection of a moving ship every frame: 2.4 s of a minute of the live server's peak, 11.11.2026). The
    /// projector's grid moved: with no welder near, the hologram is behind for up to 60 frames and then where it
    /// belongs; with an enabled ship welder in reach of the hologram's buildable blocks, at its place after 3 frames; the welder switched off, behind
    /// again. Skipped without SentisOptimisations' projection slowdown or Multigrid Projector.
    /// </summary>
    public sealed class ProjectionIdleCheck : ProjectorRigCheck
    {
        public override string Name => Prefix + "projection_idle";

        protected override IEnumerator Check()
        {
            var slowdown = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisations.Slowdowns.ProjectionSlowdown", false)).FirstOrDefault(t => t != null);
            if (slowdown?.GetField("IdleFrames") == null) throw new Core.ScenarioSkippedException("SentisOptimisations' projection slowdown is not loaded");
            if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType("MultigridProjector.Logic.MultigridProjection", false) != null))
                throw new Core.ScenarioSkippedException("Multigrid Projector is not loaded");
            var idleFrames = (int)slowdown.GetField("IdleFrames").GetValue(null);
            yield return Arrive(Site.At(0, 10, 10));
            yield return Project("projection-idle", Site.Origin);
            var grid = Grid;
            var hologram = Projector.ProjectedGrid;
            var cells = Cells;
            Check(hologram != null && cells.Count > 0, "no hologram");
            double Off() => hologram.CubeBlocks.OrderBy(b => grid.WorldToGridInteger(hologram.GridIntegerToWorld(b.Position)).X)
                .Select((b, i) => Vector3D.Distance(hologram.GridIntegerToWorld(b.Position), grid.GridIntegerToWorld(cells[i]))).Max();
            void Move()
            {
                var m = grid.WorldMatrix;
                grid.Teleport(MatrixD.CreateWorld(m.Translation + m.Forward * 3, m.Forward, m.Up));
            }
            // looked up once in 60 frames: by now it knows no welder is near
            yield return WaitForTicks(130);

            // how long the hologram stays behind a move made right after one of its moves once a second: moved, waited
            // for the hologram to come (its move), and moved again at once - the next move is a second off (a move
            // a few frames after another would now and then meet the once-a-second move: the whole regression)
            var lag = -1;
            IEnumerator Lag()
            {
                lag = -1;
                Move();
                for (var f = 0; f <= idleFrames + 30 && Off() > 0.01; f++) yield return WaitForTicks(1);
                Move();
                for (var f = 1; f <= idleFrames + 30; f++)
                {
                    yield return WaitForTicks(1);
                    if (Off() > 1) continue;
                    lag = f;
                    yield break;
                }
                lag = idleFrames + 31;
            }

            yield return Lag();
            var none = lag;
            Note($"no welder: the hologram at its place {none} frames after a move");
            Check(none >= idleFrames / 2, $"with no welder near the hologram followed its projector in {none} frames (it is moved once in {idleFrames})");
            Check(none <= idleFrames + 30, $"with no welder near the hologram did not catch up in {idleFrames + 30} frames");

            // an enabled welder near: every frame
            yield return WelderBy("projection-idle-welder", Site.Origin + Site.Up * 6);
            Move();
            yield return WaitForTicks(3);
            var withWelder = Off();
            Note($"an enabled welder near: off by {withWelder:F4} m 3 frames after a move");
            Check(withWelder < 0.0015, $"with an enabled welder near the hologram did not follow at once: off by {withWelder:F4} m");

            // switched off: as with none
            Switch(Welder, false);
            yield return WaitForTicks(130);
            yield return Lag();
            var off = lag;
            Note($"the welder switched off: the hologram at its place {off} frames after a move");
            Check(off >= idleFrames / 2, $"with the welder switched off the hologram followed its projector in {off} frames");

            // an enabled welder 20 m over the hologram: near, but out of reach of the blocks it could build - behind still
            yield return WelderBy("projection-idle-far-welder", hologram.PositionComp.WorldAABB.Center + Site.Up * 20);
            yield return Lag();
            var far = lag;
            Note($"an enabled welder 20 m away: the hologram at its place {far} frames after a move");
            Check(far >= idleFrames / 2, $"an enabled welder 20 m from the buildable blocks, out of its reach, made the hologram follow in {far} frames");
            Result($"after a move the hologram at its place: no welder near {none} frames, an enabled welder in reach 3 or fewer, switched off {off}, 20 m away {far}");
        }
    }
}
