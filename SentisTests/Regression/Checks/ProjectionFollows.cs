using System.Collections;
using System.Linq;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A projection follows its projector: the projector's grid moved and turned, each block of the hologram stays where
    /// its cell of the projector's grid is. MultigridProjector sets the hologram's place only when it is not there
    /// already within a millimetre (a ship at rest trembles by less, and every block of its holograms was moved with it
    /// every frame: prod 06.11.2026, 9 ms a second): the moves here are big and small, down to half a millimetre, and the
    /// hologram is never more than that millimetre off. SentisOptimisations' "no welder near: once a second" is held off
    /// for it (reg_projection_idle checks that).
    /// </summary>
    public sealed class ProjectionFollowsCheck : ProjectorRigCheck
    {
        public override string Name => Prefix + "projection_follows";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, 10));
            yield return Project("projection-follows", Site.Origin);
            HoldOffIdleProjection(Projector);
            var grid = Grid;
            var hologram = Projector.ProjectedGrid;
            var cells = Cells;
            Check(hologram != null && cells.Count > 0, "no hologram");

            // how far each hologram block is from its cell of the projector's grid, m
            double Off() => hologram.CubeBlocks.OrderBy(b => grid.WorldToGridInteger(hologram.GridIntegerToWorld(b.Position)).X)
                .Select((b, i) => Vector3D.Distance(hologram.GridIntegerToWorld(b.Position), grid.GridIntegerToWorld(cells[i]))).Max();

            Note($"standing: off by {Off():F5} m");
            Check(Off() < 0.01, $"the hologram is not at its cells to start with: {Off():F4} m");
            var moves = new[] { (7.3, 10.0), (0.0, 1.0), (0.4, 0.0), (0.001, 0.0), (0.0005, 0.0), (0.0005, 0.0), (0.0, 0.0005), (25.0, 35.0) };
            foreach (var (meters, degrees) in moves)
            {
                var m = grid.WorldMatrix;
                var turn = MatrixD.CreateFromAxisAngle(m.Up, MathHelper.ToRadians(degrees));
                var at = m.Translation + m.Forward * meters;
                m = MatrixD.CreateWorld(at, Vector3D.TransformNormal(m.Forward, turn), m.Up);
                grid.Teleport(m);
                yield return WaitForTicks(3);
                var off = Off();
                Note($"moved {meters} m, turned {degrees} deg: off by {off:F5} m");
                Check(off < 0.0015, $"the hologram did not follow a move of {meters} m and {degrees} deg: off by {off:F4} m");
            }
            Result($"the hologram followed {moves.Length} moves of its projector, the least 0.5 mm: off by {Off():F5} m at most");
        }
    }
}
