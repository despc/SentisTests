using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World.Generator;
using SentisTests.Core;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Do the asteroids the world keeps stand where the procedural generator puts them now? Reported from the server:
    /// players' asteroids moved or went. A procedural asteroid is made by the server and by every client alike, from the
    /// world's seed - "Asteroid_cellX_cellY_cellZ_index_seed" at a place the seed decides - and is kept in the save only
    /// once it is changed (drilled). If the world's seed or density changed after that, the kept asteroid stays at the
    /// old place while the generator puts a new one elsewhere; the client sees the generator's. A probe of a world copied
    /// from the server, it moves nothing: for every kept procedural asteroid, the generator's seeds about it - the same
    /// name and place, another place, or none.
    /// </summary>
    public sealed class AsteroidSeedsScenario : TestScenario
    {
        public const string ScenarioName = "asteroid_seeds";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 60;

        public override IEnumerator Run()
        {
            var generator = MyProceduralWorldGenerator.Static;
            Check(generator != null, "no procedural world generator");
            var settings = Sandbox.Game.World.MySession.Static.Settings;
            Note($"world: seed {settings.ProceduralSeed}, density {settings.ProceduralDensity}, generator version {settings.VoxelGeneratorVersion}");
            var maps = MyEntities.GetEntities().OfType<MyVoxelMap>().Where(v => !v.Closed && v.StorageName != null && v.StorageName.StartsWith("Asteroid_")).ToList();
            Note($"{maps.Count} kept procedural asteroids");
            int same = 0, moved = 0, none = 0;
            var offsets = new List<(double FromCentre, double Corner, double Centre)>();
            var seeds = new List<MyObjectSeed>();
            foreach (var map in maps)
            {
                var centre = map.PositionComp.WorldAABB.Center;
                seeds.Clear();
                generator.OverlapAllAsteroidSeedsInSphere(new BoundingSphereD(centre, Math.Max(200, map.PositionComp.WorldAABB.HalfExtents.Length() * 3)), seeds);
                var names = seeds.Select(s => (Seed: s, Name: $"Asteroid_{s.CellId.X}_{s.CellId.Y}_{s.CellId.Z}_{s.Params.Index}_{s.Params.Seed}")).ToList();
                var match = names.FirstOrDefault(n => n.Name == map.StorageName);
                string verdict;
                if (match.Seed != null)
                {
                    var off = Vector3D.Distance(match.Seed.BoundingVolume.Center, centre);
                    // the voxel map's corner against the seed's box corner: the game puts the map's corner there, so the gap is
                    // what the placing lost (a float far from the centre of the world would lose up to half a metre)
                    var corner = Vector3D.Distance(map.PositionLeftBottomCorner, match.Seed.BoundingVolume.Center - map.Storage.Size * 0.5);
                    offsets.Add((centre.Length(), corner, off));
                    if (off < 50) { same++; verdict = $"the generator's own, centre {off:0.000} m off its seed, corner {corner:0.000} m"; }
                    else { moved++; verdict = $"the generator puts it {off:0} m away"; }
                }
                else
                {
                    none++;
                    var near = names.OrderBy(n => Vector3D.Distance(n.Seed.BoundingVolume.Center, centre)).FirstOrDefault();
                    verdict = "NOT the generator's now" + (near.Seed == null ? ", no seed near" :
                        $"; nearest seed '{near.Name}' {Vector3D.Distance(near.Seed.BoundingVolume.Center, centre):0} m away");
                }
                Note($"ASTEROID | '{map.StorageName}' at {centre.X:0} {centre.Y:0} {centre.Z:0}, changed {map.ContentChanged}: {verdict}");
                yield return null;
            }
            foreach (var band in offsets.GroupBy(o => o.FromCentre < 500000 ? "under 500 km" : o.FromCentre < 2000000 ? "500 - 2000 km" : "over 2000 km"))
                Note($"ASTEROID PRECISION | {band.Key} from the centre of the world: {band.Count()} asteroids, the corner off its place by up to {band.Max(b => b.Corner):0.000} m " +
                     $"(on average {band.Average(b => b.Corner):0.000}), the centre off its seed by up to {band.Max(b => b.Centre):0.000} m");
            Note($"ASTEROID SEEDS | {maps.Count} kept: {same} where the generator puts them, {moved} the generator puts elsewhere, {none} the generator makes no more");
        }
    }
}
