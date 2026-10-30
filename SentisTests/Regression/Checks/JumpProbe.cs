using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A jump of a grid of the world at its drives' full reach, asked as a client asks it (<c>MyCubeGrid.OnJumpRequested</c>,
    /// the target the reach away along the grid's cockpit), watched frame by frame: the reach
    /// (<c>GetMaxJumpDistance</c>, less as the grid's mass grows past the drives' MaxJumpMass), the length of the jump
    /// the server holds (<c>m_jumpDirection</c>), the grid's mass, whether it counts down. At the countdown's end the server
    /// jumps only when the jump is not longer than the reach then, and aborts it without a word otherwise. Focus
    /// <c>id=</c> the grid, <c>ratio=</c> of the reach (1 by default). A fake player by the grid keeps it thawed; a grid that jumped is put back where it was.
    /// Not part of the regression: it jumps a grid of the world.
    /// </summary>
    public sealed class JumpProbeCheck : RegressionCheck
    {
        public const string ScenarioName = "jump_probe";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        protected override IEnumerator Check()
        {
            var focus = TestRunner.Focus ?? "";
            var id = Regex.Match(focus, @"id=(\d+)");
            Check(id.Success, "focus id=<grid> is needed");
            Check(MyEntities.TryGetEntityById(long.Parse(id.Groups[1].Value), out MyCubeGrid grid), "no grid " + id.Groups[1].Value);
            var ratio = Regex.Match(focus, @"ratio=([\d.]+)") is var r && r.Success ? double.Parse(r.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 1.0;
            yield return Arrive(grid.PositionComp.WorldAABB.Center + grid.WorldMatrix.Up * (grid.PositionComp.WorldAABB.HalfExtents.Length() + 30));
            yield return Seconds(3);

            var system = grid.GridSystems.JumpSystem;
            var drives = grid.GetFatBlocks().OfType<Sandbox.Game.Entities.MyJumpDrive>().ToList();
            Check(drives.Count > 0, grid.DisplayName + " has no jump drive");
            var user = drives[0].OwnerId;
            var direction = typeof(Sandbox.Game.GameSystems.MyGridJumpDriveSystem).GetField("m_jumpDirection", BindingFlags.Instance | BindingFlags.NonPublic);
            Func<double> length = () => ((Vector3D)direction.GetValue(system)).Length();
            Func<double> mass = () => grid.GetCurrentMass();
            Note($"{grid.DisplayName}: {drives.Count} jump drives ({string.Join(", ", drives.Select(d => $"{d.BlockDefinition.Id.SubtypeName} {((Sandbox.ModAPI.Ingame.IMyJumpDrive)d).CurrentStoredPower:F2}/{d.BlockDefinition.PowerNeededForJump:F2} MWh, " +
                 $"max {d.BlockDefinition.MaxJumpDistance / 1000:F0} km at {d.BlockDefinition.MaxJumpMass:F0} kg, working {d.IsWorking}, can jump {d.CanJumpAndHasAccess(user)}"))}); " +
                 $"mass {mass():F1} kg, physics {(grid.Physics == null ? "none" : grid.Physics.IsStatic ? "static" : "dynamic")}; reach {system.GetMaxJumpDistance(user):F3} m, least {system.GetMinJumpDistance(user):F0} m");

            var seat = grid.GetFatBlocks().OfType<MyShipController>().FirstOrDefault(s => s.IsMainCockpit) ?? grid.GetFatBlocks().OfType<MyShipController>().FirstOrDefault();
            var forward = (seat ?? (MyCubeBlock)drives[0]).WorldMatrix.Forward;
            var reach = system.GetMaxJumpDistance(user);
            var target = grid.WorldMatrix.Translation + forward * reach * ratio;
            Request(grid, "OnJumpRequested", target, user, 10f, (long?)null);

            var from = grid.PositionComp.GetPosition();
            var home = grid.WorldMatrix;
            var counted = false;
            var lines = new List<string>();
            var within = Within(20);
            var frame = 0;
            while (within())
            {
                var jumping = system.IsJumping;
                counted |= jumping;
                if (frame % 60 == 0 || jumping && frame % 10 == 0)
                    lines.Add($"f{frame}: jumping {jumping}, reach {system.GetMaxJumpDistance(user):F3} m, the jump {length():F3} m, mass {mass():F1} kg");
                if (counted && !jumping) break;
                frame++;
                yield return null;
            }
            yield return Seconds(1);
            foreach (var line in lines) Note("  " + line);
            var moved = Vector3D.Distance(grid.PositionComp.GetPosition(), from);
            Note($"JUMP PROBE RESULT | asked {reach * ratio:F3} m (reach {reach:F3} m); counted down {counted}; moved {moved:F0} m; reach at the end {system.GetMaxJumpDistance(user):F3} m, the jump {length():F3} m, mass {mass():F1} kg");
            // the grid back where it was
            if (moved > 100) grid.Teleport(home);
            Check(moved > reach * ratio * 0.5, $"the grid did not jump: moved {moved:F0} m of {reach * ratio:F0}");
        }
    }
}
