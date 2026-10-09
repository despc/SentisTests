using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;
using BuildCheckResult = Sandbox.ModAPI.BuildCheckResult;
using SpaceWelder = SpaceEngineers.Game.Entities.Blocks.MyShipWelder;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The welding load spread over the world, for SentisClusters: <see cref="Sites"/> copies of the welder_perf site -
    /// a platform with a projector holding the Spitfire (2757 blocks) and six welder ships round its hologram - on a
    /// lattice <see cref="Step"/> apart, each with a fake player of its own next to it. The freezer stays on, as on a
    /// live server: the players keep the sites awake (and their clusters of the physics stepped). Every welder starts
    /// in the same tick; the run lasts until every ship is built.
    ///
    /// Measured: the frames while they weld (TickMetrics, FrameProbe) and how long each site took. Run with the clusters
    /// on and off to compare.
    /// </summary>
    public sealed class WelderSpreadScenario : TestScenario
    {
        public const string ScenarioName = "welder_spread";
        private const int Sites = 16;
        private const int PerRow = 4;
        private const double Step = 20000;
        private const int MaxWeldSeconds = 1800;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private bool _captured;
        private readonly List<ulong> _protected = new List<ulong>();
        private readonly ConfigOverride _soConfig = new ConfigOverride();
        private float _initialWelderMultiplier;
        private bool _initialOwnAllDlcs;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => MaxWeldSeconds + 900;

        private sealed class Site
        {
            public int Index;
            public Vector3D Origin;
            public MyCubeGrid Platform;
            public MyProjectorBase Projector;
            public List<MyCubeGrid> Ships = new List<MyCubeGrid>();
            public List<SpaceWelder> Welders = new List<SpaceWelder>();
            public int Expected, BasePhysical, BaseFinished;
            public Vector3D Centre;
            public double? DoneAfter;
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _initialWelderMultiplier = RuntimePluginControls.WelderRadiusMultiplier;
            _initialOwnAllDlcs = Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS;
            _captured = true;
            // as in welder_perf: the ship carries a few DLC blocks, the welders' owner owns no DLC
            Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS = true;
            RuntimePluginControls.SetWelderRadiusMultiplier(WelderPerfScenario.RadiusMultiplier);
            // SentisOptimisations builds at most ProjectionBuildsPerFrame blocks of projections a frame on the whole
            // server (1 on the stand): 44 thousand blocks would take 12 minutes whatever else the server did, clusters
            // or not. One a welder a frame: the load is the welders', not the budget's
            _soConfig.Set("ProjectionBuildsPerFrame", Sites * WelderPerfScenario.WelderCount);
            Note("freezer " + (RuntimePluginControls.FreezerEnabled ? "on" : "OFF") + " (left as it is); welder radius x" + WelderPerfScenario.RadiusMultiplier);
            FakeClients.RemoveAll();

            var authored = WorldApi.LoadAuthoredGrid(WelderPerfScenario.ProjectionResource, WorldApi.EntityPrefix + "perf-projection");
            var pose = authored.PositionAndOrientation.Value;
            var origin = TestRunner.RunOrigin ?? new Vector3D(pose.Position.X + 12000.0, pose.Position.Y, pose.Position.Z);
            var up = (Vector3D)(Vector3)pose.Up;
            var sites = Enumerable.Range(0, Sites).Select(i => new Site
            {
                Index = i,
                Origin = origin + new Vector3D(i % PerRow * Step, 0, i / PerRow * Step)
            }).ToList();

            // the players first: the freezer would freeze a site before anyone came to it
            // over the site, clear of the hologram and the welder ships round it
            FakeClients.Add(Sites, Network, i => (sites[i].Origin + up * 150, 0, 0), withCharacters: true);
            // they died the moment the welders started (all 16 in one frame, 09.10.2026) and the freezer took the sites:
            // invulnerable and untargetable, the way the admin menu sets them
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
                var platformOb = WorldApi.LoadAuthoredGrid(WelderPerfScenario.ProjectionResource, WorldApi.EntityPrefix + "spread-projection-" + site.Index);
                platformOb.PositionAndOrientation = new MyPositionAndOrientation(site.Origin, pose.Forward, pose.Up);
                platformOb.IsStatic = true;
                var note = WelderPerfScenario.UseAsBlueprint(platformOb, source);
                if (site.Index == 0) Note(note);
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
                site.Expected = preview.CubeBlocks.Count;
                var bounds = preview.PositionComp.WorldAABB;
                site.Centre = bounds.Center;
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
                    var shipOb = WorldApi.LoadAuthoredGrid(WelderPerfScenario.WelderResource, WorldApi.EntityPrefix + "perf-welder-" + w);
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
            Note("spawned " + Sites + " sites " + Step / 1000 + " km apart, " + sites.Sum(s => s.Expected) + " blocks to weld, " +
                 sites.Sum(s => s.Welders.Count) + " welders, " + FakeClients.Count + " players");

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

            foreach (var site in sites)
            {
                var grids = WelderPerfScenario.PhysicalFixtureGrids(site.Centre, 400, site.Projector.ProjectedGrid);
                site.BasePhysical = grids.Sum(WorldApi.CountBlocks);
                site.BaseFinished = grids.Sum(WorldApi.CountFinished);
            }
            TickMetrics.Take();
            FrameProbe.Take();
            foreach (var welder in welders)
                if (!WorldApi.ToolIsActivated(welder)) WorldApi.ToolStartShooting(welder);
            var started = DateTime.UtcNow;
            Note("PROFILE WINDOW START: " + welders.Count + " welders at " + Sites + " sites armed in one tick");

            var frame = 0;
            var lastLog = DateTime.UtcNow;
            while ((DateTime.UtcNow - started).TotalSeconds < MaxWeldSeconds && sites.Any(s => s.DoneAfter == null))
            {
                // the harness's own work stays sparse: one site looked at every 10 frames
                if (frame++ % 10 == 0)
                {
                    var site = sites[frame / 10 % Sites];
                    WorldApi.ChargeBatteries(site.Platform);
                    foreach (var ship in site.Ships) WorldApi.ChargeBatteries(ship);
                    if (site.DoneAfter == null && site.Projector.ProjectedGrid == null) site.DoneAfter = (DateTime.UtcNow - started).TotalSeconds;
                }
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 30)
                {
                    lastLog = DateTime.UtcNow;
                    Note("welding " + (DateTime.UtcNow - started).TotalSeconds.ToString("F0") + "s: sites done " + sites.Count(s => s.DoneAfter != null) + "/" + Sites);
                }
                yield return null;
            }
            var metrics = TickMetrics.Take();
            var probe = FrameProbe.Take();
            var seconds = (DateTime.UtcNow - started).TotalSeconds;

            var problems = new List<string>();
            foreach (var site in sites)
            {
                var grids = WelderPerfScenario.PhysicalFixtureGrids(site.Centre, 400, site.Projector.ProjectedGrid);
                var built = grids.Sum(WorldApi.CountBlocks) - site.BasePhysical;
                var finished = grids.Sum(WorldApi.CountFinished) - site.BaseFinished;
                if (site.Projector.ProjectedGrid != null || built < site.Expected || finished != built)
                    problems.Add("site " + site.Index + ": built " + built + "/" + site.Expected + ", finished " + finished);
            }
            var times = sites.Where(s => s.DoneAfter != null).Select(s => s.DoneAfter.Value).OrderBy(t => t).ToList();
            Note("PROFILE WINDOW END: " + sites.Count(s => s.DoneAfter != null) + "/" + Sites + " ships built in " + seconds.ToString("F1") + "s" +
                 (times.Count > 0 ? " (first " + times.First().ToString("F0") + "s, median " + times[times.Count / 2].ToString("F0") + "s, last " + times.Last().ToString("F0") + "s)" : "") +
                 ", players alive " + Enumerable.Range(0, FakeClients.Count).Count(FakeClients.HasLiveCharacter) + "/" + FakeClients.Count +
                 " | " + metrics.Format() + " | " + probe);
            Check(problems.Count == 0, string.Join("; ", problems));
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
