using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Puts back as generated the ground the stand's scenarios dug before they filled in after themselves (30.09.2026):
    /// voxel_stream's 120 craters of 14 m in a line from the wheel scenarios' anchor, voxel_cache_resend's pits, and the
    /// deep holes ground_probe found where the wheel scenarios found no ground. A place with a static grid (a base of a
    /// bot or a player) within 100 m is left as it is and named in the log. Run by hand, once.
    /// </summary>
    public sealed class TerrainRestoreScenario : TestScenario
    {
        public const string ScenarioName = "terrain_restore";

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 600;

        private const double KeepAwayFromBasesM = 100;

        // the deep holes of ground_probe: reverted in a column this wide and this deep
        private static readonly Vector3D[] DeepHoles =
        {
            new Vector3D(-45833.566, -106942.305, -185390.145),
            new Vector3D(-45879.356, -106955.328, -185439.400),
            new Vector3D(-45883.734, -106942.658, -185457.602),
            new Vector3D(-45886.027, -106952.581, -185455.454),
            new Vector3D(-45974.747, -109880.636, -185473.874),
            new Vector3D(-45855.060, -108137.712, -185384.171),
        };
        private const double DeepHoleHalfWidthM = 40, DeepHoleDepthM = 120, DeepHoleAboveM = 40;

        // The wheel scenarios' lattices (from the log of 30.09.2026) and how far their vehicles drive from them: all of
        // that ground goes back to generated, in tiles, so they drive on untouched ground as they were written for.
        internal static readonly Vector3D[] WheelSites =
        {
            new Vector3D(-45446, -108033, -185487),
            new Vector3D(-45564, -105931, -185457),
            new Vector3D(-45417, -105933, -185487),
            new Vector3D(-44290, -109400, -185731),
            new Vector3D(-45351, -108235, -185508),
        };
        internal const double WheelSiteRadiusM = 1600;
        private const double TileM = 100, TileAboveM = 40, TileBelowM = 80;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + "terrain-anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet?.Storage != null, "no planet near the wheel anchor");
            var centre = planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchorM.Translation - centre);
            var east = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var bases = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.IsStatic && !g.MarkedForClose)
                .Select(g => (g.DisplayName, g.PositionComp.WorldAABB)).ToList();

            int reverted = 0, kept = 0;
            bool NearBase(BoundingBoxD box, out string name)
            {
                var inflated = box.GetInflated(KeepAwayFromBasesM);
                foreach (var b in bases)
                    if (inflated.Intersects(b.WorldAABB)) { name = b.DisplayName; return true; }
                name = null;
                return false;
            }
            void Revert(BoundingBoxD box, string what)
            {
                if (NearBase(box, out var name))
                {
                    kept++;
                    Note("kept " + what + " at " + box.Center.ToString("F0") + ": static grid '" + name + "' within " + KeepAwayFromBasesM + " m");
                    return;
                }
                WorldApi.RevertTerrain(planet, box);
                reverted++;
            }
            BoundingBoxD Sphere(Vector3D at, double radius) =>
                new BoundingBoxD(at - new Vector3D(radius + 2), at + new Vector3D(radius + 2));

            // voxel_stream: the same craters it digs
            for (var i = 0; i < VoxelStreamScenario.Holes; i++)
            {
                var at = centre + Vector3D.Normalize(up + east * (i * VoxelStreamScenario.HoleStepM / 6000.0)) *
                         VoxelStreamScenario.SurfaceDistance(planet, up, east, i);
                Revert(Sphere(at, VoxelStreamScenario.HoleRadiusM), "voxel_stream crater " + i);
                if (i % 10 == 0) yield return WaitForTicks(1);
            }
            Revert(Sphere(anchorM.Translation + up * 2, VoxelStreamScenario.HoleRadiusM), "voxel_stream crater at the anchor");

            // voxel_cache_resend: a pit of 8 m 5 km apart, the first place it could dig
            for (var i = 1; i <= 10; i++)
            {
                var direction = Vector3D.Normalize(up + east * (i * 5000.0 / planet.AverageRadius));
                var surface = planet.GetClosestSurfacePointGlobal(centre + direction * planet.AverageRadius);
                Revert(Sphere(surface - direction * 2, 8), "voxel_cache_resend pit " + i);
            }

            // the deep holes
            foreach (var hole in DeepHoles)
            {
                var generated = planet.GetClosestSurfacePointGlobal(hole);
                var holeUp = Vector3D.Normalize(generated - centre);
                var box = BoundingBoxD.CreateInvalid();
                foreach (var h in new[] { DeepHoleAboveM, -DeepHoleDepthM })
                foreach (var d in new[] { -DeepHoleHalfWidthM, DeepHoleHalfWidthM })
                    box.Include(generated + holeUp * h + new Vector3D(d, d, d));
                box.Include(generated + holeUp * DeepHoleAboveM + new Vector3D(-DeepHoleHalfWidthM, DeepHoleHalfWidthM, -DeepHoleHalfWidthM));
                box.Include(generated - holeUp * DeepHoleDepthM + new Vector3D(DeepHoleHalfWidthM, -DeepHoleHalfWidthM, DeepHoleHalfWidthM));
                Revert(box, "deep hole");
            }
            // the wheel sites, tile by tile
            var tiles = 0;
            foreach (var site in WheelSites)
            {
                var siteUp = Vector3D.Normalize(site - centre);
                var siteEast = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(siteUp));
                var siteNorth = Vector3D.Cross(siteUp, siteEast);
                var steps = (int)(WheelSiteRadiusM / TileM);
                for (var i = -steps; i <= steps; i++)
                for (var j = -steps; j <= steps; j++)
                {
                    if (i * i + j * j > steps * steps) continue;
                    var over = site + siteEast * (i * TileM) + siteNorth * (j * TileM);
                    var generated = planet.GetClosestSurfacePointGlobal(ref over);
                    var tileUp = Vector3D.Normalize(generated - centre);
                    var box = BoundingBoxD.CreateInvalid();
                    foreach (var h in new[] { TileAboveM, -TileBelowM })
                    foreach (var a in new[] { -0.5, 0.5 })
                    foreach (var b in new[] { -0.5, 0.5 })
                        box.Include(generated + tileUp * h + siteEast * (a * TileM) + siteNorth * (b * TileM));
                    Revert(box, "wheel site tile");
                    if (++tiles % 20 == 0) yield return WaitForTicks(1);
                }
            }
            Note(tiles + " tiles of the wheel sites looked at");

            yield return WaitForTicks(10);
            Note("TERRAIN RESTORE RESULT | " + reverted + " places put back as generated, " + kept + " kept next to static grids");
        }
    }
}
