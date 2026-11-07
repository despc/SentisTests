using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;
using VRageMath.Spatial;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The clusters of the physics reordered as often as players can make them, by planets (the stand's crash of
    /// 07.11.2026: an access violation in Havok as a planet's piece of voxel physics went into a world, or as a
    /// reordering put a connector's body back - each time with SentisClusters' 5 km clusters being made and parted).
    /// <see cref="TotalPlayers"/> fake players, half of them flying, half teleported:
    /// <list type="bullet">
    /// <item>the flyers as in planet_flight: each on its own circle 60 m over the ground at 90 m/s, the circles' centres
    /// a grid 6 km apart round the site, each dragging the planet's 1 km pieces of physics through its cluster and the
    /// next;</item>
    /// <item>the teleported ones every 2-5 s to a place 60 m over the ground of a planet: half of them where no cluster is
    /// yet (a cluster's size round the place clear of every cluster) - the game makes a cluster and its Havok world
    /// there, the planet its pieces -, half just outside an existing cluster by a planet (closer to it than half a
    /// cluster's size) - the game reorders the clusters round it; the place each left is emptied or reordered.</item>
    /// </list>
    /// Measured, as planet_flight: the frames, and the cluster tree's events (objects added and removed, reorders)
    /// a second. The server living to the end is the check. Focus: <c>players=24 seconds=300</c>.
    /// </summary>
    public sealed class PlanetReorderScenario : TestScenario
    {
        public const string ScenarioName = "planet_reorder";
        private const string Prefix = "reorder-";
        private const double AltitudeM = 60;
        private const double SpeedMps = 90;
        private const double CircleRadiusM = 2500;
        private const double SettleSeconds = 15;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private static int Arg(string name, int fallback)
        {
            var m = System.Text.RegularExpressions.Regex.Match(TestRunner.Focus ?? "", name + @"=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : fallback;
        }

        private static int TotalPlayers => Arg("players", 24);
        private static int FlySeconds => Arg("seconds", 300);

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => FlySeconds + 300;

        private long _added, _removed, _reordered;
        private readonly List<ulong> _protected = new List<ulong>();

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().Where(p => !p.MarkedForClose).ToList();
            Check(planets.Count > 0, "no planet in the world");
            // the anchor over a planet in the stand's own world; in another world the planet nearest to it, the anchor
            // moved onto its ground
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            if (planet == null)
            {
                planet = planets.OrderBy(p => Vector3D.Distance(p.PositionComp.GetPosition(), anchor)).First();
                anchor = planet.GetClosestSurfacePointGlobal(ref anchor);
                Note("no planet by the anchor: over " + planet.StorageName + " instead");
            }
            var centre = planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchor - centre);
            var east = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var north = Vector3D.Normalize(Vector3D.Cross(up, east));

            var flyers = TotalPlayers / 2;
            var jumpers = TotalPlayers - flyers;
            // the flyers' circles: a grid 6 km apart round the anchor, each its own phase
            var paths = new List<Func<double, Vector3D>>();
            var columns = (int)Math.Ceiling(Math.Sqrt(flyers));
            for (var i = 0; i < flyers; i++)
            {
                var offset = (i % columns - (columns - 1) / 2.0) * 6000.0 * east + (i / columns - (columns - 1) / 2.0) * 6000.0 * north;
                var circleCentre = planet.GetClosestSurfacePointGlobal(anchor + offset);
                var axis = Vector3D.Normalize(circleCentre - centre);
                var u = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(axis));
                var v = Vector3D.Cross(axis, u);
                var phase = i * 2 * Math.PI / Math.Max(1, flyers);
                var omega = SpeedMps / CircleRadiusM;
                paths.Add(seconds =>
                {
                    var angle = phase + omega * seconds;
                    var over = circleCentre + (u * Math.Cos(angle) + v * Math.Sin(angle)) * CircleRadiusM;
                    var ground = planet.GetClosestSurfacePointGlobal(ref over);
                    return ground + Vector3D.Normalize(ground - centre) * AltitudeM;
                });
            }
            var random = new Random(47351);
            // the jumpers start over the anchor, apart
            var starts = Enumerable.Range(0, jumpers).Select(i =>
            {
                var over = anchor + (i - jumpers / 2.0) * 300.0 * east - 20000.0 * north;
                var ground = planet.GetClosestSurfacePointGlobal(ref over);
                return ground + Vector3D.Normalize(ground - centre) * AltitudeM;
            }).ToList();
            FakeClients.Add(TotalPlayers, Network, i => (i < flyers ? paths[i](0) : starts[i - flyers], 0, 0), withCharacters: true);
            for (var i = 0; i < flyers; i++) FakeClients.SetPath(i, paths[i]);
            for (var i = 0; i < FakeClients.Count; i++)
            {
                var steamId = FakeClients.PlayerOf(i).Id.SteamId;
                _protected.Add(steamId);
                Sandbox.Game.World.MySession.Static.RemoteAdminSettings[steamId] =
                    Sandbox.Game.World.AdminSettingsEnum.Invulnerable | Sandbox.Game.World.AdminSettingsEnum.Untargetable;
            }

            var settle = WaitForSeconds(SettleSeconds, "flyers take off");
            while (settle.MoveNext()) yield return settle.Current;
            for (var i = 0; i < flyers; i++) FakeClients.Character(i)?.JetpackComp?.TurnOnJetpack(true);

            // a place over some planet with no cluster within a cluster's size of it
            Vector3D? FreePlace()
            {
                var size = MyClusterTree.IdealClusterSize.X;
                var clusters = MyPhysics.Clusters.GetClusters();
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    var p = planets[random.Next(planets.Count)];
                    var c = p.PositionComp.GetPosition();
                    var direction = Vector3D.Normalize(new Vector3D(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5));
                    var far = c + direction * p.MaximumRadius * 1.5;
                    var ground = p.GetClosestSurfacePointGlobal(ref far);
                    var at = ground + Vector3D.Normalize(ground - c) * AltitudeM;
                    var room = new BoundingBoxD(at - size, at + size);
                    var taken = false;
                    foreach (var cluster in clusters)
                        if (cluster.AABB.Intersects(room)) { taken = true; break; }
                    if (!taken) return at;
                }
                return null;
            }

            // a place over a planet just outside an existing cluster (closer to it than half a cluster's size): the
            // game cannot give the player a cluster of its own there and reorders the clusters round it
            Vector3D? EdgePlace()
            {
                var size = MyClusterTree.IdealClusterSize.X;
                var clusters = MyPhysics.Clusters.GetClusters();
                if (clusters.Count == 0) return null;
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    var c = clusters[random.Next(clusters.Count)];
                    var p = planets.OrderBy(x => Vector3D.Distance(x.PositionComp.GetPosition(), c.AABB.Center)).First();
                    if (Vector3D.Distance(p.PositionComp.GetPosition(), c.AABB.Center) > p.MaximumRadius + size) continue;
                    var pc = p.PositionComp.GetPosition();
                    var direction = Vector3D.Normalize(new Vector3D(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5));
                    var reach = c.AABB.HalfExtents.Max() + 100 + random.NextDouble() * size * 0.4;
                    var near = c.AABB.Center + direction * reach;
                    var ground = p.GetClosestSurfacePointGlobal(ref near);
                    var at = ground + Vector3D.Normalize(ground - pc) * AltitudeM;
                    var inside = false;
                    var close = false;
                    foreach (var other in clusters)
                    {
                        if (other.AABB.Contains(at) != ContainmentType.Disjoint) { inside = true; break; }
                        if (other.AABB.Distance(at) < size / 2) close = true;
                    }
                    if (!inside && close) return at;
                }
                return null;
            }

            var tree = MyPhysics.Clusters;
            Action<long, int> added = (e, c) => _added++;
            Action<long, int> removed = (e, c) => _removed++;
            Action reordered = () => _reordered++;
            tree.EntityAdded += added;
            tree.EntityRemoved += removed;
            tree.OnClustersReordered += reordered;
            try
            {
                var frames = new List<double>();
                var watch = Stopwatch.StartNew();
                var last = watch.Elapsed.TotalMilliseconds;
                var nextJump = Enumerable.Range(0, jumpers).Select(i => 2.0 + random.NextDouble() * 3).ToArray();
                int jumps = 0, edgeJumps = 0, noRoom = 0, minClusters = int.MaxValue, maxClusters = 0;
                var told = 0.0;
                while (watch.Elapsed.TotalSeconds < FlySeconds)
                {
                    yield return null;
                    var now = watch.Elapsed.TotalMilliseconds;
                    frames.Add(now - last);
                    last = now;
                    var seconds = now / 1000;
                    for (var k = 0; k < jumpers; k++)
                    {
                        if (seconds < nextJump[k]) continue;
                        nextJump[k] = seconds + 2 + random.NextDouble() * 3;
                        // the even ones by a cluster's edge (a reordering), the odd ones where no cluster is (a new one)
                        var at = k % 2 == 0 ? EdgePlace() : FreePlace();
                        if (at == null) { noRoom++; continue; }
                        if (k % 2 == 0) edgeJumps++;
                        FakeClients.MoveTo(flyers + k, at.Value);
                        jumps++;
                    }
                    var count = tree.GetClusters().Count;
                    minClusters = Math.Min(minClusters, count);
                    maxClusters = Math.Max(maxClusters, count);
                    if (seconds - told >= 30)
                    {
                        told = seconds;
                        Note($"{seconds:F0} s: {jumps} jumps ({noRoom} found no free place), clusters {count} (from {minClusters} to {maxClusters}); " +
                             $"the tree so far: added {_added}, removed {_removed}, reordered {_reordered}");
                    }
                }
                var sorted = frames.OrderBy(f => f).ToList();
                double P(double q) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];
                var alive = Enumerable.Range(0, FakeClients.Count).Select(FakeClients.Character).Count(c => c != null && !c.MarkedForClose && !c.IsDead);
                Note("PLANET REORDER RESULT | " + flyers + " flying, " + jumpers + " teleported (" + jumps + " jumps, " + edgeJumps + " of them by a cluster's edge, " + noRoom + " with no place found), " + alive + "/" + FakeClients.Count + " alive | " +
                     "frames " + frames.Count + ", avg gap " + frames.Average().ToString("F1") + " ms, p99 " + P(0.99).ToString("F1") + ", max " + sorted.LastOrDefault().ToString("F1") +
                     " | cluster tree a second: added " + (_added / (double)FlySeconds).ToString("F0") + ", removed " + (_removed / (double)FlySeconds).ToString("F0") +
                     ", reordered " + (_reordered / (double)FlySeconds).ToString("F2") + "; clusters from " + minClusters + " to " + maxClusters);
            }
            finally
            {
                tree.EntityAdded -= added;
                tree.EntityRemoved -= removed;
                tree.OnClustersReordered -= reordered;
            }
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                foreach (var steamId in _protected) Sandbox.Game.World.MySession.Static?.RemoteAdminSettings.Remove(steamId);
                _protected.Clear();
            }
            finally { base.Cleanup(); }
        }
    }
}
