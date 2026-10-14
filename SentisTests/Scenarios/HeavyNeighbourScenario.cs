using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;
using SpaceWelder = SpaceEngineers.Game.Entities.Blocks.MyShipWelder;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// One heavy cluster next to light ones, for SentisClusters' independent clusters: a heavy cluster must not slow
    /// the others down - only its own simulation may drop.
    ///
    /// The heavy one: <see cref="HeavySites"/> welder_perf sites (a projector holding the Spitfire, 2757 blocks, six
    /// welder ships round its hologram, a fake player) in a heap, <see cref="HeavyStep"/> apart. The light ones:
    /// <see cref="LightSites"/> spots <see cref="LightAway"/> off the heap, each with a fake player. Every spot has a
    /// probe - a one-block grid drifting at <see cref="ProbeMps"/> with nothing to slow it - whose path over a stretch
    /// of real time is its cluster's speed of simulation: 1.0 when its physics is stepped 60 times a second.
    ///
    /// Measured over <see cref="MeasureSeconds"/> of welding: each probe's speed of simulation, the server's frames
    /// (TickMetrics) and what SentisClusters says of its clusters. A measure: it fails only when the welding did not start.
    /// </summary>
    public sealed class HeavyNeighbourScenario : TestScenario
    {
        public const string ScenarioName = "heavy_neighbour";
        private const int HeavySites = 36;
        private const int HeavyPerRow = 6;
        private const double HeavyStep = 450;
        private const int LightSites = 4;
        private const double LightAway = 25000;
        private const double ProbeMps = 10;
        private const double MeasureSeconds = 60;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly List<ulong> _protected = new List<ulong>();
        private readonly ConfigOverride _soConfig = new ConfigOverride();
        private bool _captured;
        private float _initialWelderMultiplier;
        private bool _initialOwnAllDlcs;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        private sealed class Site
        {
            public int Index;
            public Vector3D Origin;
            public MyCubeGrid Platform;
            public MyProjectorBase Projector;
            public List<MyCubeGrid> Ships = new List<MyCubeGrid>();
            public List<SpaceWelder> Welders = new List<SpaceWelder>();
        }

        private sealed class Probe
        {
            public string Name;
            public MyCubeGrid Grid;
            public Vector3D From;
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _initialWelderMultiplier = RuntimePluginControls.WelderRadiusMultiplier;
            _initialOwnAllDlcs = Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS;
            _captured = true;
            Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS = true;
            RuntimePluginControls.SetWelderRadiusMultiplier(WelderPerfScenario.RadiusMultiplier);
            _soConfig.Set("ProjectionBuildsPerFrame", HeavySites * WelderPerfScenario.WelderCount);
            FakeClients.RemoveAll();

            var authored = WorldApi.LoadAuthoredGrid(WelderPerfScenario.ProjectionResource, WorldApi.EntityPrefix + "perf-projection");
            var pose = authored.PositionAndOrientation.Value;
            var origin = TestRunner.RunOrigin ?? new Vector3D(pose.Position.X + 12000.0, pose.Position.Y, pose.Position.Z);
            var up = (Vector3D)(Vector3)pose.Up;
            var sites = Enumerable.Range(0, HeavySites).Select(i => new Site
            {
                Index = i,
                Origin = origin + new Vector3D(i % HeavyPerRow * HeavyStep, 0, i / HeavyPerRow * HeavyStep)
            }).ToList();
            var heapCentre = sites.Aggregate(Vector3D.Zero, (s, x) => s + x.Origin) / sites.Count;
            var lights = Enumerable.Range(0, LightSites).Select(i =>
            {
                var angle = Math.PI * 2 * i / LightSites;
                return heapCentre + new Vector3D(Math.Cos(angle), 0, Math.Sin(angle)) * LightAway;
            }).ToList();

            // the players first, invulnerable: the freezer would take a spot nobody is at
            var spots = sites.Select(s => s.Origin + up * 150).Concat(lights).ToList();
            FakeClients.Add(spots.Count, Network, i => (spots[i], 0, 0), withCharacters: true);
            for (var i = 0; i < FakeClients.Count; i++)
            {
                var steamId = FakeClients.PlayerOf(i).Id.SteamId;
                _protected.Add(steamId);
                Sandbox.Game.World.MySession.Static.RemoteAdminSettings[steamId] =
                    Sandbox.Game.World.AdminSettingsEnum.Invulnerable | Sandbox.Game.World.AdminSettingsEnum.Untargetable;
            }
            var arrive = WaitForSeconds(5, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;

            var source = WorldApi.LoadTemplateXml(WelderPerfScenario.BlueprintResource);
            foreach (var site in sites)
            {
                var platformOb = WorldApi.LoadAuthoredGrid(WelderPerfScenario.ProjectionResource, WorldApi.EntityPrefix + "heavy-projection-" + site.Index);
                platformOb.PositionAndOrientation = new MyPositionAndOrientation(site.Origin, pose.Forward, pose.Up);
                platformOb.IsStatic = true;
                WelderPerfScenario.UseAsBlueprint(platformOb, source);
                site.Platform = WorldApi.SpawnGrid(platformOb);
                Track(site.Platform);
                yield return WaitForTicks(30);
                WorldApi.EnsureDistributor(site.Platform);
                Check(WorldApi.ChargeBatteries(site.Platform) > 0f, "the platform has no chargeable batteries");
                site.Projector = WorldApi.FindFunctional<MyProjectorBase>(site.Platform);
                Check(site.Projector != null, "the platform has no projector");
                site.Projector.Enabled = true;
            }
            var waitProjections = Wait(() =>
            {
                foreach (var site in sites)
                {
                    WorldApi.ChargeBatteries(site.Platform);
                    var distributor = WorldApi.EnsureDistributor(site.Platform);
                    distributor.MarkForUpdate();
                    distributor.UpdateBeforeSimulation();
                }
                return sites.All(s => s.Projector.IsWorking && s.Projector.ProjectedGrid != null);
            }, "every projection active", 120);
            while (waitProjections.MoveNext()) yield return waitProjections.Current;

            foreach (var site in sites)
            {
                var preview = site.Projector.ProjectedGrid;
                var bounds = preview.PositionComp.WorldAABB;
                var places = new[]
                {
                    new Vector3D(bounds.Max.X + 20.0, bounds.Center.Y, bounds.Center.Z),
                    new Vector3D(bounds.Min.X - 20.0, bounds.Center.Y, bounds.Center.Z),
                    new Vector3D(bounds.Center.X, bounds.Max.Y + 20.0, bounds.Center.Z),
                    new Vector3D(bounds.Center.X, bounds.Min.Y - 20.0, bounds.Center.Z),
                    new Vector3D(bounds.Center.X, bounds.Center.Y, bounds.Max.Z + 20.0),
                    new Vector3D(bounds.Center.X, bounds.Center.Y, bounds.Min.Z - 20.0),
                };
                var needs = WelderPerfScenario.RuntimeComponentsNeeded(preview.CubeBlocks, 2);
                for (var w = 0; w < WelderPerfScenario.WelderCount; w++)
                {
                    var shipOb = WorldApi.LoadAuthoredGrid(WelderPerfScenario.WelderResource, WorldApi.EntityPrefix + "heavy-welder-" + site.Index + "-" + w);
                    var shipPose = shipOb.PositionAndOrientation.Value;
                    shipOb.PositionAndOrientation = new MyPositionAndOrientation(places[w], shipPose.Forward, shipPose.Up);
                    shipOb.IsStatic = true;
                    var ship = WorldApi.SpawnGrid(shipOb);
                    Track(ship);
                    site.Ships.Add(ship);
                }
                yield return WaitForTicks(30);
                foreach (var ship in site.Ships)
                {
                    WorldApi.EnsureDistributor(ship);
                    Check(WorldApi.ChargeBatteries(ship) > 0f, "a welder ship has no charged battery");
                    var welder = WorldApi.FindFunctional<SpaceWelder>(ship);
                    var container = WorldApi.FindFunctional<MyCargoContainer>(ship);
                    Check(welder != null && container != null, "a welder ship has no welder or no cargo container");
                    WorldApi.StockComponents(container.GetInventory(), needs);
                    site.Welders.Add(welder);
                }
                yield return null;
            }

            // the probes: one in the heap, one at every light spot, drifting
            var probes = new List<Probe> { SpawnProbe("heavy", heapCentre + up * 400) };
            for (var i = 0; i < lights.Count; i++) probes.Add(SpawnProbe("light-" + i, lights[i] + up * 40));
            yield return WaitForTicks(10);
            foreach (var p in probes) Push(p);

            var welders = sites.SelectMany(s => s.Welders).ToList();
            var waitRadius = Wait(() =>
            {
                foreach (var ship in sites.SelectMany(s => s.Ships)) WorldApi.ChargeBatteries(ship);
                return welders.All(w => WorldApi.SensorSphere(w).Radius > 200.0);
            }, "the welders' detector radius", 60);
            while (waitRadius.MoveNext()) yield return waitRadius.Current;
            foreach (var welder in welders) welder.Enabled = true;
            var waitWorking = Wait(() =>
            {
                foreach (var ship in sites.SelectMany(s => s.Ships)) WorldApi.ChargeBatteries(ship);
                return welders.All(w => w.IsWorking);
            }, "every welder working", 90);
            while (waitWorking.MoveNext()) yield return waitWorking.Current;
            foreach (var welder in welders)
                if (!WorldApi.ToolIsActivated(welder)) WorldApi.ToolStartShooting(welder);
            Note("HEAVY NEIGHBOUR | " + HeavySites + " welding sites in a heap " + HeavyStep + " m apart, " + welders.Count + " welders, " +
                 LightSites + " light spots " + LightAway / 1000 + " km off, " + FakeClients.Count + " players");

            // a few seconds for the welding to get going, then the measured stretch
            var warm = WaitForSeconds(10, "the welding gets going");
            while (warm.MoveNext()) yield return warm.Current;
            foreach (var p in probes) { Push(p); p.From = p.Grid.PositionComp.GetPosition(); }
            var welderBefore = WelderCounters();
            var platformBlocksBefore = sites.Sum(s => s.Platform.BlocksCount);
            var remainingBefore = sites.Sum(s => s.Projector.ProjectedGrid != null ? ((Sandbox.ModAPI.Ingame.IMyProjector)s.Projector).RemainingBlocks : 0);
            TickMetrics.Take();
            var clusterLines = new List<string>();
            var watch = Stopwatch.StartNew();
            var nextSnap = 15.0;
            while (watch.Elapsed.TotalSeconds < MeasureSeconds)
            {
                if (watch.Elapsed.TotalSeconds >= nextSnap)
                {
                    nextSnap += 15;
                    clusterLines.Add(Clusters(probes));
                }
                yield return null;
            }
            var seconds = watch.Elapsed.TotalSeconds;
            var metrics = TickMetrics.Take();
            var speeds = probes.Select(p => p.Name + " " + SimSpeed(p, seconds).ToString("F2")).ToList();
            var remainingAfter = sites.Sum(s => s.Projector.ProjectedGrid != null ? ((Sandbox.ModAPI.Ingame.IMyProjector)s.Projector).RemainingBlocks : 0);
            Note("HEAVY NEIGHBOUR RESULT | sim speed by probe: " + string.Join(", ", speeds) + " | welded " + (remainingBefore - remainingAfter) +
                 " blocks in " + seconds.ToString("F0") + " s (" + remainingAfter + " left), platforms grew by " + (sites.Sum(s => s.Platform.BlocksCount) - platformBlocksBefore) + " blocks | welders: " + WelderDelta(welderBefore, WelderCounters()) + " | " + metrics.Format() +
                 " | clusters: " + string.Join(" || ", clusterLines));
        }

        private static Dictionary<string, long> WelderCounters()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisationsPlugin.WelderDiagnostics") ?? a.GetType("Optimizer.Optimizations.WelderDiagnostics"))
                .FirstOrDefault(t => t != null);
            var result = new Dictionary<string, long>();
            if (type == null) return result;
            foreach (var f in type.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public))
                if (f.FieldType == typeof(long)) result[f.Name] = (long)f.GetValue(null);
            return result;
        }

        private static string WelderDelta(Dictionary<string, long> before, Dictionary<string, long> after) =>
            after.Count == 0 ? "no counters" : string.Join(", ", after.Select(p => p.Key + " " + (p.Value - (before.TryGetValue(p.Key, out var b) ? b : 0))));

        private MyCubeGrid SpawnProbeGrid(string name, Vector3D at)
        {
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + "probe-" + name, MyCubeSize.Small, false, at,
                new[] { new BlockSpec("SmallBlockArmorBlock", Vector3I.Zero) });
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        private Probe SpawnProbe(string name, Vector3D at) => new Probe { Name = name, Grid = SpawnProbeGrid(name, at) };

        private static void Push(Probe p)
        {
            if (p.Grid.Physics == null) return;
            p.Grid.Physics.LinearDamping = 0;
            p.Grid.Physics.AngularDamping = 0;
            p.Grid.Physics.LinearVelocity = new Vector3(ProbeMps, 0, 0);
            p.Grid.Physics.AngularVelocity = Vector3.Zero;
        }

        /// <summary>The probe's path over the real time, against what a full speed would have made of it.</summary>
        private static double SimSpeed(Probe p, double seconds) =>
            p.Grid.Closed ? double.NaN : Vector3D.Distance(p.Grid.PositionComp.GetPosition(), p.From) / (ProbeMps * seconds);

        /// <summary>SentisClusters' clusters now: the heaviest ones, their players and the probes in them.</summary>
        private static string Clusters(List<Probe> probes)
        {
            try
            {
                var stats = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisClusters.Stats.ClusterStats")).FirstOrDefault(t => t != null);
                var snapshot = stats?.GetField("Current")?.GetValue(null);
                if (snapshot == null) return "no SentisClusters";
                var frame = (double)snapshot.GetType().GetField("FrameMs").GetValue(snapshot);
                var rows = ((Array)snapshot.GetType().GetField("Clusters").GetValue(snapshot)).Cast<object>().ToList();
                double Num(object r, string f) => Convert.ToDouble(r.GetType().GetField(f).GetValue(r));
                var shown = rows.OrderByDescending(r => Num(r, "Ms")).Take(6).Select(r =>
                {
                    var centre = (Vector3D)r.GetType().GetProperty("Centre").GetValue(r);
                    var size = (double)r.GetType().GetProperty("Size").GetValue(r);
                    var inside = probes.Where(p => Vector3D.Distance(p.Grid.PositionComp.GetPosition(), centre) <= size / 2 + 50).Select(p => p.Name);
                    return Num(r, "Ms").ToString("F1") + " ms, " + Num(r, "Players") + " players" + (inside.Any() ? " (" + string.Join("/", inside) + ")" : "");
                });
                return "frame " + frame.ToString("F1") + " ms, " + rows.Count + " clusters: " + string.Join("; ", shown);
            }
            catch (Exception e) { return "cluster stats: " + e.Message; }
        }

        public override void Cleanup()
        {
            try
            {
                if (_captured)
                {
                    RuntimePluginControls.SetWelderRadiusMultiplier(_initialWelderMultiplier);
                    Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS = _initialOwnAllDlcs;
                    _captured = false;
                }
                _soConfig.Restore();
                FakeClients.RemoveAll();
                foreach (var steamId in _protected) Sandbox.Game.World.MySession.Static?.RemoteAdminSettings.Remove(steamId);
                _protected.Clear();
            }
            finally { base.Cleanup(); }
        }
    }
}
