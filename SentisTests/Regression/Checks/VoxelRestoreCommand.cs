using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using VRage.Voxels;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The admins' <c>!voxelrestore</c> (SO VoxelRestoreCommands), from the console: on the planet a grid, a cave dug 8 m
    /// under the ground 20 m from it and another 400 m away. <c>!voxelrestore 50</c> only counts - the far cave among
    /// what would be restored, the near among what stays - and fills nothing; <c>!voxelrestore 50 delete</c> fills the far
    /// cave with the planet's own terrain and leaves the near one dug; after it only what was kept is left changed.
    /// The grid stays; a distance below 0 is refused.
    /// </summary>
    public sealed class VoxelRestoreCommandCheck : RegressionCheck
    {
        public override string Name => Prefix + "voxel_restore_command";
        public override SiteKind Kind => SiteKind.Planet;

        private MyPlanet _planet;
        private Vector3D _near;

        protected override IEnumerator Check()
        {
            Type restore = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                restore = restore ?? assembly.GetType("SentisOptimisations.Commands.VoxelRestoreCommands", false);
            if (restore == null) throw new Core.ScenarioSkippedException("SentisOptimisations' !voxelrestore is not loaded");
            object Get(string name) => (object)restore.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ??
                                      restore.GetProperty(name, BindingFlags.Public | BindingFlags.Static).GetValue(null);

            var planet = _planet = MyGamePruningStructure.GetClosestPlanet(Site.Origin);
            Check(planet != null, "no planet by the site");
            var grid = Build("voxel-keep", Site.Origin + Site.Up * 3, true, new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0));
            yield return Arrive(Site.Origin + Site.Up * 4 + Site.Right * 6);

            Vector3D Under(Vector3D at)
            {
                var surface = planet.GetClosestSurfacePointGlobal(at);
                return surface - Vector3D.Normalize(surface - planet.PositionComp.GetPosition()) * 8;
            }
            byte Content(Vector3D world)
            {
                var local = Vector3D.Transform(world, planet.PositionComp.WorldMatrixNormalizedInv) + (Vector3D)planet.StorageMin + planet.SizeInMetresHalf;
                var voxel = Vector3I.Floor(local);
                var data = new MyStorageData();
                data.Resize(Vector3I.One);
                planet.Storage.ReadRange(data, MyStorageDataTypeFlags.Content, 0, voxel, voxel);
                return data.Content(0);
            }
            var near = _near = Under(Site.Origin + Site.Forward * 20);
            var far = Under(Site.Origin - Site.Forward * 400);
            var nearBefore = Content(near);
            var farBefore = Content(far);
            Check(nearBefore > 0 && farBefore > 0, $"the ground 8 m under is not solid: near {nearBefore}, far {farBefore}");
            MyVoxelGenerator.CutOutShape(planet, new MyShapeSphere { Center = near, Radius = 4 });
            MyVoxelGenerator.CutOutShape(planet, new MyShapeSphere { Center = far, Radius = 4 });
            yield return Seconds(1);
            Check(Content(near) == 0 && Content(far) == 0, $"the caves were not dug: near {Content(near)}, far {Content(far)}");
            // a load: 400 caves round the far one (a square of 320 m, at least 240 m from the grid), 20 a frame
            var dug = 0;
            for (var x = -10; x < 10; x++)
            for (var z = -10; z < 10; z++)
            {
                if (x == 0 && z == 0) continue;
                MyVoxelGenerator.CutOutShape(planet, new MyShapeSphere { Center = Under(Site.Origin - Site.Forward * (400 + z * 16) + Site.Right * (x * 16)), Radius = 6 });
                if (++dug % 20 == 0) yield return null;
            }
            yield return Seconds(2);

            var preview = PcuCommandCheck.Command("!voxelrestore 50");
            Note("preview: " + preview);
            Check((int)Get("LastToRestore") >= 1 && (int)Get("LastKept") >= 1, "the count does not have the far cave to restore and the near one kept: " + preview);
            yield return Seconds(1);
            Check(Content(far) == 0 && (int)Get("Pending") == 0, "the count restored: far " + Content(far));
            Check(PcuCommandCheck.Command("!voxelrestore -1").Contains("число метров"), "a distance below 0 was not refused");

            // frames of the game before the restore, to compare
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var idle = new System.Collections.Generic.List<double>();
            for (var f = 0; f < 300; f++)
            {
                var t = clock.Elapsed.TotalMilliseconds;
                yield return null;
                idle.Add(clock.Elapsed.TotalMilliseconds - t);
            }
            var restored0 = (long)Get("Restored");
            var answer = PcuCommandCheck.Command("!voxelrestore 50 delete");
            Note("delete: " + answer);
            var keptBefore = (int)Get("LastKept");
            var busy = new System.Collections.Generic.List<double>();
            for (var f = Frames(120); f > 0 && (int)Get("Pending") > 0; f--)
            {
                var t = clock.Elapsed.TotalMilliseconds;
                yield return null;
                busy.Add(clock.Elapsed.TotalMilliseconds - t);
            }
            for (var f = 0; f < 120; f++)   // the game's notifications of the last pieces, a frame later
            {
                var t = clock.Elapsed.TotalMilliseconds;
                yield return null;
                busy.Add(clock.Elapsed.TotalMilliseconds - t);
            }
            string Timing(System.Collections.Generic.List<double> ms)
            {
                var sorted = ms.OrderBy(x => x).ToList();
                return $"{sorted.Count} frames, mean {sorted.Average():0.0} ms, 99% {sorted[(int)(sorted.Count * 0.99)]:0.0} ms, max {sorted.Last():0.0} ms, over 50 ms {sorted.Count(x => x > 50)}";
            }
            Note("frames before the restore: " + Timing(idle) + "; during: " + Timing(busy));
            Check((int)Get("Pending") == 0, $"the restore did not end in 120 s: {Get("Pending")} pieces left");
            yield return Seconds(1);
            var restored = (long)Get("Restored") - restored0;
            Note($"restored {restored} pieces; far cave {farBefore} -> {Content(far)}, near cave {Content(near)}");
            Check(Content(far) == farBefore, $"the far cave was not filled with the planet's terrain: {Content(far)} (was {farBefore})");
            Check(Content(near) == 0, $"the cave 20 m from the grid was filled: {Content(near)}");
            Check(!grid.Closed, "the grid went");
            var again = PcuCommandCheck.Command("!voxelrestore 50");
            Note("after: " + again);
            // what is left changed is what was kept (a grid of another check gone meanwhile turns some of it far)
            Check((int)Get("LastToRestore") + (int)Get("LastKept") == keptBefore && (int)Get("LastKept") >= 1,
                $"after the restore {Get("LastToRestore")} far and {Get("LastKept")} near are left changed, {keptBefore} were kept: " + again);
            Result($"!voxelrestore 50 counted the far cave to restore and the near one kept, filled nothing; with delete restored {restored} pieces: " +
                   $"the cave 400 m away filled ({farBefore}), the one 20 m from the grid left dug; only what was kept left changed");
        }

        public override void Cleanup()
        {
            try
            {
                if (_planet != null && _near != Vector3D.Zero)
                    MyVoxelGenerator.RevertShape(_planet, new MyShapeBox { Boundaries = new BoundingBoxD(_near - 8, _near + 8), Transformation = MatrixD.Identity });
            }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
