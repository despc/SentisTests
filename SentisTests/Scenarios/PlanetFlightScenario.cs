using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Players flying low over a planet: <see cref="Flyers"/> fake players on their jetpacks, each on its own circle
    /// <see cref="AltitudeM"/> over the ground at <see cref="SpeedMps"/>. A player over a planet makes it build its
    /// voxel physics around him every 10 frames (MyPlanet.UpdateAfterSimulation10, pieces of 1 km) and drop the pieces
    /// left behind; each piece goes into the clusters of the physics or out of them. On the stand a player flying there
    /// felt the server freeze (09.10.2026): frames of 110-130 ms every 10 frames, the cluster tree adding some 3300
    /// objects a frame.
    ///
    /// Measured: the frames while they fly - how many over 50 and 100 ms, the longest, the average - and the cluster
    /// tree's events (objects added and removed, reorders) a second. To compare the clusters on and off, and the size
    /// of the clusters.
    /// </summary>
    public sealed class PlanetFlightScenario : TestScenario
    {
        public const string ScenarioName = "planet_flight";
        private const string Prefix = "char-";
        private const int Flyers = 4;
        private const double AltitudeM = 60;
        private const double SpeedMps = 90;
        private const double CircleRadiusM = 2500;
        private const double SettleSeconds = 15;
        private const double FlySeconds = 120;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(FlySeconds + 300);

        private long _added, _removed, _reordered;
        private readonly List<ulong> _protected = new List<ulong>();

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchor - centre);
            var east = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var north = Vector3D.Normalize(Vector3D.Cross(up, east));

            // each on a circle round its own centre, the centres 6 km apart: they meet different pieces of the planet
            var paths = new List<Func<double, Vector3D>>();
            for (var i = 0; i < Flyers; i++)
            {
                var offset = (i % 2 * 2 - 1) * 3000.0 * east + (i / 2 * 2 - 1) * 3000.0 * north;
                var circleCentre = planet.GetClosestSurfacePointGlobal(anchor + offset);
                var axis = Vector3D.Normalize(circleCentre - centre);
                var u = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(axis));
                var v = Vector3D.Cross(axis, u);
                var phase = i * Math.PI / 2;
                var omega = SpeedMps / CircleRadiusM;
                paths.Add(seconds =>
                {
                    var angle = phase + omega * seconds;
                    var over = circleCentre + (u * Math.Cos(angle) + v * Math.Sin(angle)) * CircleRadiusM;
                    var ground = planet.GetClosestSurfacePointGlobal(ref over);
                    return ground + Vector3D.Normalize(ground - centre) * AltitudeM;
                });
            }
            FakeClients.Add(Flyers, Network, i => (paths[i](0), 0, 0), withCharacters: true);
            for (var i = 0; i < Flyers; i++) FakeClients.SetPath(i, paths[i]);
            // at 90 m/s low over the ground one of them flew into a mountain side now and then (the steering follows
            // the ground a frame late): the test is about the load a flight makes, so they are invulnerable, the way
            // the admin menu sets it
            for (var i = 0; i < FakeClients.Count; i++)
            {
                var steamId = FakeClients.PlayerOf(i).Id.SteamId;
                _protected.Add(steamId);
                Sandbox.Game.World.MySession.Static.RemoteAdminSettings[steamId] =
                    Sandbox.Game.World.AdminSettingsEnum.Invulnerable | Sandbox.Game.World.AdminSettingsEnum.Untargetable;
            }

            var settle = WaitForSeconds(SettleSeconds, "flyers take off");
            while (settle.MoveNext()) yield return settle.Current;
            var flyers = Enumerable.Range(0, FakeClients.Count).Select(FakeClients.Character).Where(c => c != null && !c.IsDead).ToList();
            Check(flyers.Count > 0, "no flyer is alive");
            foreach (var c in flyers) c.JetpackComp?.TurnOnJetpack(true);

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
                var startPositions = flyers.Select(c => c.PositionComp.GetPosition()).ToList();
                while (watch.Elapsed.TotalSeconds < FlySeconds)
                {
                    yield return null;
                    var now = watch.Elapsed.TotalMilliseconds;
                    frames.Add(now - last);
                    last = now;
                }
                var flown = flyers.Select((c, i) => c.MarkedForClose ? 0 : (c.PositionComp.GetPosition() - startPositions[i]).Length()).ToList();
                var alive = flyers.Count(c => !c.MarkedForClose && !c.IsDead);
                var altitudes = flyers.Where(c => !c.MarkedForClose).Select(c =>
                {
                    var p = c.PositionComp.GetPosition();
                    return (p - centre).Length() - (planet.GetClosestSurfacePointGlobal(ref p) - centre).Length();
                }).ToList();
                var sorted = frames.OrderBy(f => f).ToList();
                double P(double q) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];
                Note("PLANET FLIGHT RESULT | " + alive + "/" + flyers.Count + " flyers at " + SpeedMps + " m/s, " + AltitudeM + " m up, now " +
                     string.Join("/", altitudes.Select(a => a.ToString("F0"))) + " m up, " + string.Join("/", flown.Select(f => f.ToString("F0"))) + " m from the start | " +
                     "frames " + frames.Count + " (" + (frames.Count / FlySeconds).ToString("F1") + "/s), avg gap " + frames.Average().ToString("F1") + " ms, p99 " + P(0.99).ToString("F1") +
                     ", max " + sorted.LastOrDefault().ToString("F1") + ", over 50 ms " + frames.Count(f => f > 50) + ", over 100 ms " + frames.Count(f => f > 100) +
                     " | cluster tree a second: added " + (_added / FlySeconds).ToString("F0") + ", removed " + (_removed / FlySeconds).ToString("F0") +
                     ", reordered " + (_reordered / FlySeconds).ToString("F2") + "; clusters now " + tree.GetClusters().Count);
                Check(alive == flyers.Count, (flyers.Count - alive) + " flyers died");
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
