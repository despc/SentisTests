using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.Engine.Voxels;
using Sandbox.Game.World.Generator;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// In what order a client that arrives among dug asteroids is streamed them. The game puts a stream in the client's
    /// queue a random number of frames out, within its replication layer: the asteroid at hand could come after far
    /// ones. SentisOptimisations' VoxelStreamNearestFirst orders them by the distance from the player.
    ///
    /// A spot nobody has been to; the server makes the asteroids round it (a fake client there for a moment); a dozen
    /// of them, from half a kilometre to eight, get a small cut each, so that they are streamed with their data; then a
    /// new fake client comes, and the time each of them is fully replicated to it is taken. A measure, not a check.
    /// </summary>
    public sealed class VoxelStreamOrderScenario : TestScenario
    {
        public const string ScenarioName = "voxel_stream_order";
        private const int Asteroids = 12;
        private const double MaxM = 9000;
        private static readonly Vector3D Start = new Vector3D(2500000, -3000000, 1000000);
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var generator = MyProceduralWorldGenerator.Static;
            Check(generator != null && generator.Enabled && MySession.Static.Settings.ProceduralDensity > 0, "procedural asteroids are off in this world");
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            // a spot with enough asteroids round it
            var rng = new Random();
            var spot = Vector3D.Zero;
            var seeds = new List<MyObjectSeed>();
            for (var attempt = 0; attempt < 30 && seeds.Count < Asteroids * 2; attempt++)
            {
                var found = new List<MyObjectSeed>();
                var here = Start + Vector3D.Normalize(new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5)) * rng.Next(0, 1000000);
                generator.OverlapAllAsteroidSeedsInSphere(new BoundingSphereD(here, MaxM), found);
                var asteroids = found.Where(s => s.Params.Type == MyObjectSeedType.Asteroid && !s.Params.Generated).ToList();
                if (asteroids.Count <= seeds.Count) continue;
                seeds = asteroids;
                spot = here;
            }
            Check(seeds.Count >= 12, "too few asteroid seeds within " + MaxM + " m of " + spot + ": " + seeds.Count);
            // spread over the distances: the nearest seed to each of Asteroids rings from half a kilometre out
            var chosen = new List<MyObjectSeed>();
            for (var i = 0; i < Asteroids; i++)
            {
                var ring = 500 + (MaxM - 500) * i / (Asteroids - 1);
                var seed = seeds.Where(s => !chosen.Contains(s)).OrderBy(s => Math.Abs(Vector3D.Distance(s.BoundingVolume.Center, spot) - ring)).FirstOrDefault();
                if (seed != null) chosen.Add(seed);
            }

            // the server makes them: a fake client there until they exist
            FakeClients.Add(1, Network, p => (spot, 0, 0), withCharacters: true);
            var made = new Dictionary<MyObjectSeed, MyVoxelBase>();
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < 60 && made.Count < chosen.Count)
            {
                foreach (var seed in chosen.Where(s => !made.ContainsKey(s)))
                {
                    var voxel = MySession.Static.VoxelMaps.Instances.FirstOrDefault(v => !(v is MyPlanet) &&
                        Vector3D.Distance(v.PositionComp.WorldAABB.Center, seed.BoundingVolume.Center) < seed.Size);
                    if (voxel != null) made[seed] = voxel;
                }
                yield return null;
            }
            Check(made.Count > 0, "the server made none of the asteroids");

            // a small cut each: changed, they are streamed with their data
            foreach (var voxel in made.Values)
                MyVoxelGenerator.CutOutShapeWithProperties(voxel, new MyShapeSphere { Center = voxel.PositionComp.WorldAABB.Center, Radius = 4 }, out _, out _, null, updateSync: true);
            var settle = WaitForSeconds(5, "the cuts settle");
            while (settle.MoveNext()) yield return settle.Current;
            // the first client goes only now: the generator takes back unchanged asteroids nobody is near
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            // a new client at the spot: when does each come
            var arrived = new Dictionary<MyVoxelBase, double>();
            var watch = Stopwatch.StartNew();
            FakeClients.Add(1, Network, p => (spot, 0, 0), withCharacters: true);
            // the server's queue for it, a moment later: the voxel streams in it, by the frame they are due at
            var queueOrder = "no queue seen";
            for (var t = 0; t < 600 && queueOrder == "no queue seen"; t++)
            {
                yield return null;
                var due = VoxelStreamsQueued(spot);
                if (due.Count >= 3)
                {
                    var bad = 0;
                    for (var i = 0; i < due.Count; i++)
                    for (var j = i + 1; j < due.Count; j++)
                        if (due[j].Due < due[i].Due) bad++;
                    queueOrder = due.Count + " voxel streams queued, out of distance order " + bad + " of " + due.Count * (due.Count - 1) / 2 + " pairs: " +
                                 string.Join(", ", due.Select(d => (d.Distance / 1000).ToString("F1") + " km@" + d.Due));
                }
            }
            Note("QUEUE | " + queueOrder);
            while (watch.Elapsed.TotalSeconds < 120 && arrived.Count < made.Count)
            {
                foreach (var voxel in made.Values.Where(v => !arrived.ContainsKey(v)))
                    if (FakeClients.Arrivals(new[] { (VRage.Game.Entity.MyEntity)voxel }).Contains("ready 1"))
                        arrived[voxel] = watch.Elapsed.TotalSeconds;
                yield return null;
            }

            var rows = made.Values.Select(v => (Distance: v.PositionComp.WorldAABB.Distance(spot), Time: arrived.TryGetValue(v, out var t) ? t : double.NaN))
                .OrderBy(r => r.Distance).ToList();
            var got = rows.Where(r => !double.IsNaN(r.Time)).ToList();
            var inversions = 0;
            for (var i = 0; i < got.Count; i++)
            for (var j = i + 1; j < got.Count; j++)
                if (got[j].Time < got[i].Time) inversions++;
            var pairs = got.Count * (got.Count - 1) / 2;
            Note("VOXEL STREAM ORDER RESULT | " + got.Count + "/" + rows.Count + " streamed in " + watch.Elapsed.TotalSeconds.ToString("F0") + " s | out of order: " +
                 inversions + " of " + pairs + " pairs (a farther one before a nearer one) | nearest came at " + (got.Count > 0 ? got[0].Time.ToString("F1") + " s" : "never") +
                 " | " + string.Join(", ", rows.Select(r => (r.Distance / 1000).ToString("F1") + " km: " + (double.IsNaN(r.Time) ? "-" : r.Time.ToString("F1") + " s"))) +
                 " | " + Counters());
        }

        /// <summary>The voxel map streams in the fake client's queue on the server, nearest first: their distance and the frame they are due at.</summary>
        private static List<(double Distance, long Due)> VoxelStreamsQueued(Vector3D spot)
        {
            var result = new List<(double, long)>();
            var server = Sandbox.Engine.Multiplayer.MyMultiplayer.Static?.ReplicationLayer as VRage.Network.MyReplicationServer;
            if (server == null) return result;
            var states = typeof(VRage.Network.MyReplicationServer).GetField("m_clientStates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(server) as IDictionary;
            if (states == null) return result;
            foreach (DictionaryEntry pair in states)
            {
                var client = pair.Value;
                var queue = client.GetType().GetField("DirtyQueue")?.GetValue(client) as IEnumerable;
                if (queue == null) continue;
                foreach (var node in queue)
                {
                    if (!(node is VRage.Network.MyStateDataEntry entry) || entry.Group == null || !entry.Group.IsStreaming) continue;
                    var voxel = entry.Owner?.GetType().GetProperty("Instance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.FlattenHierarchy)?.GetValue(entry.Owner) as MyVoxelBase;
                    if (voxel == null || voxel is MyPlanet) continue;
                    var priority = node.GetType().GetProperty("Priority")?.GetValue(node) ?? node.GetType().GetField("Priority", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(node);
                    result.Add((voxel.PositionComp.WorldAABB.Distance(spot), Convert.ToInt64(priority)));
                }
            }
            return result.OrderBy(r => r.Item1).ToList();
        }

        private static string Counters()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisationsPlugin.VoxelStreamNearestFirst")).FirstOrDefault(t => t != null);
            return type == null ? "no VoxelStreamNearestFirst" : "VoxelStreamNearestFirst placed " + type.GetField("Placed")?.GetValue(null);
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            catch { }
            finally { base.Cleanup(); }
        }
    }
}
