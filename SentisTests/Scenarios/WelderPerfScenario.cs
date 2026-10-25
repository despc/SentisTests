using System;
using Sandbox.Common.ObjectBuilders;
using VRage.Game.ObjectBuilders.Components;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;
using BuildCheckResult = Sandbox.ModAPI.BuildCheckResult;
using SpaceWelder = SpaceEngineers.Game.Entities.Blocks.MyShipWelder;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A real ship welded from a projection by six welder ships at once, one on each side.
    ///
    /// The blueprint is <see cref="BlueprintName"/> - a full warship of 2757 blocks with thrusters,
    /// turrets, reactors and conveyors, not a solid cube - shipped with the plugin as
    /// <see cref="BlueprintResource"/> (extracted from a save with tools/extract_grid.py), so the world
    /// needs no copy of it. It is put into the projector of the reactor-backed platform in place of the
    /// blueprint that platform was authored with, and aligned so one of its armor blocks sits right
    /// against one of the platform's: that is the block the welders start from, and everything else
    /// grows out of it.
    ///
    /// Variants: the platform dynamic out in open space, and the site inside a safe zone with damage off
    /// (static or dynamic platform, with or without the zone probe, with SentisOptimisations' safe zone
    /// grid tracking on or off).
    ///
    /// The fixture is moved away from the operator's originals; the welder ships are parked outside
    /// the hologram with one deliberately huge detector sphere, so every projection scan stresses the
    /// real vanilla welder path without moving the hologram or steering the ships.
    /// </summary>
    public sealed class WelderPerfScenario : TestScenario
    {
        public const string ScenarioName = "welder_perf";
        /// <summary>The same inside a safe zone (damage off) that covers the whole site (the platform stays static).</summary>
        public const string ZoneScenarioName = "welder_perf_sz";
        /// <summary>Inside the zone, with the platform - and so the ship growing on it - dynamic, out in open space.</summary>
        public const string ZoneDynamicScenarioName = "welder_perf_sz_dynamic";
        /// <summary>The dynamic one without the safe zone probe, whose hooks cost time themselves: frame times only.</summary>
        public const string ZoneDynamicUnprobedScenarioName = "welder_perf_sz_dynamic_noprobe";
        /// <summary>The same with SentisOptimisations' safe zone grid tracking off: the zone as the game has it.</summary>
        public const string ZoneDynamicGameScenarioName = "welder_perf_sz_dynamic_vanilla";
        /// <summary>The dynamic platform without a zone: what the zone adds to the dynamic runs.</summary>
        public const string DynamicScenarioName = "welder_perf_dynamic";
        /// <summary>
        /// For SentisClusters' detaching: heavy clusters of rigs (a dynamic platform and its six welders each; clusters=N rigs=M in
        /// the run's focus, 3 and 3 by default) 30 km apart along the world's X, the rigs of a cluster 1 km apart, a fake player
        /// by each, and <see cref="DetachLightClusters"/> light clusters with a fake player only (tools/detach_speed.py).
        /// </summary>
        public const string DetachScenarioName = "welder_perf_detach";
        public const int DetachLightClusters = 3;
        private const double ClustersApart = 30000;
        private const double RigsApart = 1000;
        private const float ZoneRadius = 300f;
        internal const string ProjectionResource = "SentisTests.Resources.PerfProjection.xml";
        internal const string WelderResource = "SentisTests.Resources.PerfWelderShip.xml";
        internal const string BlueprintName = "Spitfire Evolution (Vanilla)";
        /// <summary>The ship welded from the projection, shipped with the plugin (tools/extract_grid.py).</summary>
        internal const string BlueprintResource = "SentisTests.Resources.Spitfire.xml";
        internal const float RadiusMultiplier = 100f;
        internal const int WelderCount = 6;
        private const int DefaultWeldSeconds = 1200;
        // focus weld=N: how long the welders are given (a shorter run to look at where the weld stops)
        private static int MaxWeldSeconds => System.Text.RegularExpressions.Regex.Match(TestRunner.Focus ?? "", @"weld=(\d+)") is var m && m.Success ? int.Parse(m.Groups[1].Value) : DefaultWeldSeconds;

        private bool _captured;
        private bool _initialFreezerEnabled;
        private float _initialWelderMultiplier;
        private bool _initialOwnAllDlcs;
        private Vector3D? _fixturePosition;
        private readonly string _name;
        private readonly bool _inZone;
        private readonly bool _dynamicPlatform;
        private readonly bool _probe;
        private readonly bool _gameTracking;
        private static readonly FakeClients.NetworkProfile ZoneNetwork = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _soConfig = new ConfigOverride();
        private MyEntity _zone;
        private readonly bool _detach;
        private readonly int _heavyClusters = 1, _rigsPerCluster = 1, _lightClusters;

        public WelderPerfScenario() : this(ScenarioName, false, false)
        {
        }

        public WelderPerfScenario(string name, bool inZone, bool dynamicPlatform, bool probe = true, bool gameTracking = false, bool detach = false)
        {
            _detach = detach;
            if (detach)
            {
                _heavyClusters = 3;
                _rigsPerCluster = 3;
                _lightClusters = DetachLightClusters;
            }
            _probe = probe;
            _gameTracking = gameTracking;
            _name = name;
            _inZone = inZone;
            _dynamicPlatform = dynamicPlatform;
        }

        public override string Name => _name;
        public override int TimeoutSeconds => MaxWeldSeconds + 300;

        /// <summary>One platform with its hologram and its six welder ships.</summary>
        private sealed class Rig
        {
            public Vector3D Origin;
            public MyCubeGrid Platform;
            public Sandbox.Game.EntityComponents.MyResourceDistributorComponent Distributor;
            public MyProjectorBase Projector;
            public readonly List<MyCubeGrid> Ships = new List<MyCubeGrid>();
            public readonly List<SpaceWelder> Welders = new List<SpaceWelder>();
            public int Expected;
            public Vector3D Centre;
            public int BasePhysical, BaseFinished, Built;
            // the platform's own blocks as it was made, before any welder worked on it
            public int PlatformBlocks, PlatformFinished;
            public readonly Dictionary<long, (string Name, int Blocks)> BaseGrids = new Dictionary<long, (string, int)>();
            public bool Done;
        }

        private const double FixtureRadius = 400.0;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused("welder_perf start");
            _initialFreezerEnabled = RuntimePluginControls.FreezerEnabled;
            _initialWelderMultiplier = RuntimePluginControls.WelderRadiusMultiplier;
            _initialOwnAllDlcs = Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS;
            _captured = true;

            // The ship carries a few DLC blocks, and the welders belong to a test identity with no
            // Steam account behind it, which owns no DLC: the projector would never build those
            // blocks, and the ones behind them could never be reached. On a live server the
            // players' own DLCs decide that; here every DLC counts as owned for the run.
            Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS = true;

            RuntimePluginControls.SetFreezerEnabled(false);
            RuntimePluginControls.SetWelderRadiusMultiplier(RadiusMultiplier);
            Note("Freezer disabled; live welder radius multiplier set to x" + RadiusMultiplier);

            // the detach load: clusters=N rigs=M in the run's focus
            var clusters = _heavyClusters;
            var rigsPerCluster = _rigsPerCluster;
            if (_detach)
            {
                var focus = TestRunner.Focus ?? "";
                var c = System.Text.RegularExpressions.Regex.Match(focus, @"clusters=(\d+)");
                var r = System.Text.RegularExpressions.Regex.Match(focus, @"rigs=(\d+)");
                if (c.Success) clusters = int.Parse(c.Groups[1].Value);
                if (r.Success) rigsPerCluster = int.Parse(r.Groups[1].Value);
            }

            var authoredPose = WorldApi.LoadAuthoredGrid(ProjectionResource, WorldApi.EntityPrefix + "perf-projection").PositionAndOrientation.Value;
            // Translate only the root grid so the fixture cannot overlap the operator's originals.
            // Keep it beyond PlayersSyncDistance during profiling so client rendering and
            // replication do not contaminate the simulation-thread welding measurements.
            var fixtureOrigin = TestRunner.RunOrigin ??
                new Vector3D(authoredPose.Position.X + 12000.0, authoredPose.Position.Y, authoredPose.Position.Z);
            var origins = new List<Vector3D>();
            for (var c = 0; c < clusters; c++)
                for (var r = 0; r < rigsPerCluster; r++)
                    // the heavy clusters 30 km apart along the world's X (the game parts clusters only along the world's
                    // axes), the rigs of one cluster 1 km apart along its Z: one cluster
                    origins.Add(Vector3D.Right * (c * ClustersApart) + Vector3D.Backward * (r * RigsApart));
            var lights = Enumerable.Range(clusters, _lightClusters).Select(c => Vector3D.Right * (c * ClustersApart)).ToList();
            if (_dynamicPlatform)
            {
                // a dynamic platform must not fall: out along the planet's up until no rig and no light cluster has gravity
                var planet = MyGamePruningStructure.GetClosestPlanet(fixtureOrigin);
                if (planet != null)
                {
                    var away = Vector3D.Normalize(fixtureOrigin - planet.PositionComp.GetPosition());
                    for (var d = 0.0; d <= 1000000 && origins.Concat(lights).Any(o =>
                             Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(fixtureOrigin + o).Length() > 0.001f); d += 20000)
                        fixtureOrigin += away * 20000;
                }
            }

            var rigs = new List<Rig>();
            foreach (var offset in origins)
            {
                var rig = new Rig { Origin = fixtureOrigin + offset };
                var build = BuildRig(rig, authoredPose);
                while (build.MoveNext()) yield return build.Current;
                rigs.Add(rig);
            }
            if (_detach)
            {
                // a player by every heavy cluster (its physics stepped: EnableSelectivePhysicsUpdates) and by every light one
                FakeClients.RemoveAll();
                var players = Enumerable.Range(0, clusters).Select(c => fixtureOrigin + origins[c * rigsPerCluster] + Vector3D.Up * 300)
                    .Concat(lights.Select(l => fixtureOrigin + l)).ToList();
                FakeClients.Add(players.Count, ZoneNetwork, i => (players[i], 0, 0), withCharacters: true);
                yield return WaitForTicks(300);
            }

            TickMetrics.Take(); // discard spawn/stocking/JIT frames; measure welding only
            FrameProbe.Take();
            // One coroutine step is the start barrier: all the tools are armed in one sim tick.
            foreach (var rig in rigs)
                foreach (var welder in rig.Welders)
                    if (!WorldApi.ToolIsActivated(welder)) WorldApi.ToolStartShooting(welder);

            foreach (var rig in rigs)
            {
                var physical = PhysicalFixtureGrids(rig.Centre, FixtureRadius, rig.Projector.ProjectedGrid);
                // the base: the fixture as it stands, less what the welders built on the platform already - the rigs are
                // made one after another, and the first ones' welders had welded hundreds of blocks before this barrier (9
                // rigs of welder_perf_detach: 485, 435, 380 ... blocks on the platform here, counted as the base and taken
                // off the ship at the end, which then read "unfinished" though the projection was done, 25.10.2026)
                var welded = WorldApi.CountBlocks(rig.Platform) - rig.PlatformBlocks;
                var weldedFinished = WorldApi.CountFinished(rig.Platform) - rig.PlatformFinished;
                rig.BasePhysical = physical.Sum(WorldApi.CountBlocks) - welded;
                rig.BaseFinished = physical.Sum(WorldApi.CountFinished) - weldedFinished;
                foreach (var g in physical) rig.BaseGrids[g.EntityId] = (g.DisplayName, WorldApi.CountBlocks(g));
            }
            var started = DateTime.UtcNow;
            var lastLog = DateTime.MinValue;
            Note("PROFILE WINDOW START: " + rigs.Count * WelderCount + " welders armed in one tick (" + rigs.Count + " rigs" +
                 (_detach ? " in " + clusters + " clusters at " + string.Join(", ", Enumerable.Range(0, clusters).Select(c => Km(fixtureOrigin + origins[c * rigsPerCluster]))) +
                            "; light clusters at " + string.Join(", ", lights.Select(l => Km(fixtureOrigin + l))) : "") +
                 "); detector r=" + WorldApi.SensorSphere(rigs[0].Welders[0]).Radius.ToString("F1") + " m; probe sum=" +
                 rigs.Sum(rig => rig.Welders.Sum(WorldApi.ProbeProjectedBlocks)));
            TickMetrics.Take(); // the start probe is a full vanilla scan; keep it out of the window
            FrameProbe.Take();
            if (_inZone && _probe) Note("safe zone probe: " + SafeZoneProbe.Start());

            var harnessFrame = 0;
            while ((DateTime.UtcNow - started).TotalSeconds < MaxWeldSeconds)
            {
                // The harness runs inside the measured frame: keep its own work sparse so the
                // metrics describe welding, not the test bookkeeping.
                if (harnessFrame++ % 30 != 0)
                {
                    yield return null;
                    continue;
                }
                foreach (var rig in rigs.Where(rig => !rig.Done))
                {
                    WorldApi.ChargeBatteries(rig.Platform);
                    foreach (var ship in rig.Ships) WorldApi.ChargeBatteries(ship);
                    rig.Distributor.MarkForUpdate();
                    rig.Distributor.UpdateBeforeSimulation();
                    var physical = PhysicalFixtureGrids(rig.Centre, FixtureRadius, rig.Projector.ProjectedGrid);
                    rig.Built = physical.Sum(WorldApi.CountBlocks) - rig.BasePhysical;
                    var finished = physical.Sum(WorldApi.CountFinished) - rig.BaseFinished;
                    rig.Done = rig.Projector.ProjectedGrid == null && rig.Built >= rig.Expected && finished == rig.Built;
                }
                if (rigs.All(rig => rig.Done)) break;
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 10)
                {
                    lastLog = DateTime.UtcNow;
                    Note("profiling: elapsed=" + (DateTime.UtcNow - started).TotalSeconds.ToString("F0") + "s, built=" +
                         string.Join(" ", rigs.Select(rig => rig.Built + "/" + rig.Expected)) +
                         ", welders waiting for a projection build (by player) " + (RuntimePluginControls.ProjectionBuildsWaiting ?? "?") + " | " +
                         MixedWeldScenario.PluginCounters());
                }
                yield return null;
            }

            var weldingMetrics = TickMetrics.Take();
            var simWork = FrameProbe.Take();
            var first = rigs[0];
            if (_inZone)
            {
                var contained = typeof(MySafeZone)
                    .GetField("m_containedEntities", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(_zone);
                var count = contained?.GetType().GetProperty("Count")?.GetValue(contained);
                Check(((MySafeZone)_zone).GetGridsInside().Contains(first.Platform), "the zone does not hold the platform after the weld");
                Note("SAFE ZONE while welding: " + (_probe ? SafeZoneProbe.Format() : "probe off") + "; the zone holds " + (count?.ToString() ?? "?") +
                     " entities, platform " + (first.Platform.IsStatic ? "static" : "dynamic") + " " +
                     Vector3D.Distance(first.Platform.PositionComp.GetPosition(), _zone.PositionComp.GetPosition()).ToString("F0") +
                     " m from the zone's centre at " + (first.Platform.Physics?.LinearVelocity.Length() ?? 0).ToString("F1") + " m/s");
                SafeZoneProbe.Stop();
            }
            var unfinished = new List<string>();
            foreach (var rig in rigs)
            {
                var physical = PhysicalFixtureGrids(rig.Centre, FixtureRadius, rig.Projector.ProjectedGrid);
                rig.Built = physical.Sum(WorldApi.CountBlocks) - rig.BasePhysical;
                var finished = physical.Sum(WorldApi.CountFinished) - rig.BaseFinished;
                if (!(rig.Projector.ProjectedGrid == null && rig.Built >= rig.Expected && finished == rig.Built))
                    unfinished.Add("runtime blocks=" + rig.Built + ", raw preview cells=" + rig.Expected + ", finished=" + finished +
                                   ", physical-grids=" + physical.Count + ", probe=" + rig.Welders.Sum(WorldApi.ProbeProjectedBlocks) +
                                   "; " + Leftover(rig.Projector, physical) + "; counted then and now: " + string.Join(", ", rig.BaseGrids.Select(p => p.Value.Name + " " + p.Value.Blocks + "->" +
                                       (physical.FirstOrDefault(g => g.EntityId == p.Key) is MyCubeGrid now ? WorldApi.CountBlocks(now).ToString() : "gone"))) +
                                   ", new " + string.Join(", ", physical.Where(g => !rig.BaseGrids.ContainsKey(g.EntityId)).Select(g => g.DisplayName + " " + WorldApi.CountBlocks(g))) +
                                   "; projector's grid " + rig.Projector.CubeGrid.BlocksCount + " blocks, " +
                                   Vector3D.Distance(rig.Projector.PositionComp.GetPosition(), rig.Centre).ToString("F0") + " m from the rig's centre, " +
                                   (rig.Projector.CubeGrid.Physics?.LinearVelocity.Length() ?? 0).ToString("F1") + " m/s, counted " + physical.Contains(rig.Projector.CubeGrid) + "; " + Strays(rig.Centre, physical));
            }
            Check(unfinished.Count == 0, "real welder did not finish projection in " + MaxWeldSeconds + "s: " + string.Join(" | ", unfinished));
            Note("PROFILE WINDOW END: projection complete; runtime blocks=" + string.Join(" ", rigs.Select(rig => rig.Built)) +
                 ", raw preview cells=" + first.Expected + " in " +
                 (DateTime.UtcNow - started).TotalSeconds.ToString("F1") + "s | " +
                 weldingMetrics.Format() + " | " + simWork);
        }

        /// <summary>What of a projection is left and why: by preview grid, the projector's answer for its first blocks; mechanisms without a head.</summary>
        private static string Leftover(MyProjectorBase projector, List<MyCubeGrid> physical)
        {
            try
            {
                var clipboard = typeof(MyProjectorBase).GetField("m_clipboard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(projector);
                var previews = clipboard?.GetType().GetProperty("PreviewGrids")?.GetValue(clipboard) as System.Collections.IEnumerable;
                var parts = new List<string> { "hologram " + (projector.ProjectedGrid == null ? "gone" : "up") + ", projector " + (projector.IsWorking ? "working" : "not working") };
                if (previews != null)
                    foreach (MyCubeGrid preview in previews)
                    {
                        var blocks = preview.CubeBlocks.ToList();
                        if (blocks.Count == 0) continue;
                        var answers = blocks.Take(200).GroupBy(b => projector.CanBuild(b, true).ToString()).Select(g => g.Key + " " + g.Count());
                        var types = blocks.GroupBy(b => b.BlockDefinition.Id.SubtypeName).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key + " " + g.Count());
                        parts.Add("preview " + (preview == projector.ProjectedGrid ? "main" : "sub") + " " + blocks.Count + " left (" + string.Join(", ", answers) + "; " + string.Join(", ", types) + ")");
                    }
                var headless = physical.SelectMany(g => g.GetFatBlocks().OfType<Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase>()).Where(m => m.TopGrid == null).ToList();
                parts.Add("mechanisms without a head " + headless.Count + (headless.Count > 0 ? " (" + string.Join(", ", headless.Take(4).Select(m => m.BlockDefinition.Id.SubtypeName)) + ")" : ""));
                return string.Join("; ", parts);
            }
            catch (Exception e)
            {
                return "leftover: " + e.GetType().Name + " " + e.Message;
            }
        }

        /// <summary>The grids near a rig that its count leaves out (outside the fixture's radius, split off): how many blocks and how far.</summary>
        private static string Strays(Vector3D centre, List<MyCubeGrid> counted)
        {
            var set = new HashSet<MyCubeGrid>(counted);
            var strays = MyEntities.GetEntities().OfType<MyCubeGrid>()
                .Where(g => !g.MarkedForClose && g.Projector == null && !set.Contains(g) && Vector3D.Distance(g.PositionComp.GetPosition(), centre) < 3000)
                .OrderBy(g => Vector3D.Distance(g.PositionComp.GetPosition(), centre)).ToList();
            return "grids near not counted " + strays.Count + (strays.Count == 0 ? "" : " (" + string.Join(", ", strays.Take(5).Select(g =>
                g.BlocksCount + " blocks at " + Vector3D.Distance(g.PositionComp.GetPosition(), centre).ToString("F0") + " m, " +
                (g.IsStatic ? "static" : (g.Physics?.LinearVelocity.Length() ?? 0).ToString("F1") + " m/s"))) + ")");
        }

        private static string Km(Vector3D at) => (at.X / 1000).ToString("F0") + " " + (at.Y / 1000).ToString("F0") + " " + (at.Z / 1000).ToString("F0");

        /// <summary>A platform with the hologram up at the rig's origin, six welder ships round it stocked and their welders working.</summary>
        private IEnumerator BuildRig(Rig rig, MyPositionAndOrientation authoredPose)
        {
            var fixtureOrigin = rig.Origin;
            var platformOb = WorldApi.LoadAuthoredGrid(ProjectionResource, WorldApi.EntityPrefix + "perf-projection");
            platformOb.PositionAndOrientation = new MyPositionAndOrientation(
                fixtureOrigin, authoredPose.Forward, authoredPose.Up);
            platformOb.IsStatic = !_dynamicPlatform;

            var source = WorldApi.LoadTemplateXml(BlueprintResource);
            Check(source.GridSizeEnum == MyCubeSize.Large, BlueprintName + " is not a large grid, the platform is");
            Note(UseAsBlueprint(platformOb, source));
            if (_inZone && _zone == null)
            {
                // the zone is there first, as on a server: the grids come into it
                _soConfig.Set("SafeZoneGridTracking", !_gameTracking);
                // a zone over the whole site that lets every grid and every action in
                _zone = (MyEntity)MySessionComponentSafeZones.CrateSafeZone(MatrixD.CreateWorld(fixtureOrigin),
                    MySafeZoneShape.Sphere, MySafeZoneAccess.Blacklist, null, null, ZoneRadius, enable: true, isVisible: true);
                Check(_zone != null, "no safe zone");
                var zone = (MySafeZone)_zone;
                zone.AccessTypeGrids = MySafeZoneAccess.Blacklist;
                zone.AccessTypeFactions = MySafeZoneAccess.Blacklist;
                zone.AccessTypeFloatingObjects = MySafeZoneAccess.Blacklist;
                // a zone as players have them: no damage, everything else (welding, building projections...) allowed
                zone.AllowedActions = MySafeZoneAction.All & ~MySafeZoneAction.Damage;
                Note("safe zone of radius " + ZoneRadius + " m over the site, every grid welcome, damage off; platform " +
                     (_dynamicPlatform ? "DYNAMIC" : "static") + "; the zone's phantom on layer " +
                     (_zone.Physics?.RigidBody?.Layer.ToString() ?? "?") + (_gameTracking ? " (the game's tracking)" : " (the plugin's tracking)"));
            }
            var platform = WorldApi.SpawnGrid(platformOb);
            if (_inZone)
            {
                // A player by the site keeps its physics cluster stepped (EnableSelectivePhysicsUpdates): the game's
                // own zone tracking learns of a grid only from its phantom's contacts in the step, and with nobody
                // there it never saw the platform ("the zone does not hold the platform after the weld", 30.09.2026;
                // on 22.09 some character left on the site had kept the cluster stepped). Both trackings get the same.
                var upAtSite = platformOb.PositionAndOrientation.Value.Up;
                FakeClients.Add(1, ZoneNetwork, i => (fixtureOrigin + (Vector3D)(Vector3)upAtSite * 60, 0, 0), withCharacters: true);
            }
            Track(platform);
            rig.Platform = platform;
            rig.PlatformBlocks = WorldApi.CountBlocks(platform);
            rig.PlatformFinished = WorldApi.CountFinished(platform);
            if (!_fixturePosition.HasValue) _fixturePosition = WorldApi.PositionOf(platform);
            yield return WaitForTicks(60);

            var platformDistributor = WorldApi.EnsureDistributor(platform);
            rig.Distributor = platformDistributor;
            Check(WorldApi.ChargeBatteries(platform) > 0f,
                "the authored projection platform has no chargeable batteries");
            var projector = WorldApi.FindFunctional<MyProjectorBase>(platform);
            Check(projector != null, "the authored platform has no projector");
            rig.Projector = projector;
            projector.Enabled = true;

            var waitProjection = Wait(() =>
            {
                WorldApi.ChargeBatteries(platform);
                platformDistributor.MarkForUpdate();
                platformDistributor.UpdateBeforeSimulation();
                return projector.IsWorking && projector.ProjectedGrid != null;
            }, "authored projection active (" + WorldApi.DescribePower(platform) + ")", 90);
            while (waitProjection.MoveNext()) yield return waitProjection.Current;

            var preview = projector.ProjectedGrid;
            var expected = preview.CubeBlocks.Count;
            rig.Expected = expected;
            var buildable = preview.CubeBlocks.Cast<MySlimBlock>()
                .Count(block => projector.CanBuild(block, true) == BuildCheckResult.OK);
            Check(buildable > 0, "the " + BlueprintName + " hologram has no buildable contact block");
            Note(BlueprintName + " hologram up: " + expected + " blocks, " + buildable + " buildable now");

            var projectionBounds = preview.PositionComp.WorldAABB;
            rig.Centre = projectionBounds.Center;
            var shipPositions = new[]
            {
                new Vector3D(projectionBounds.Max.X + 20.0, projectionBounds.Center.Y, projectionBounds.Center.Z),
                new Vector3D(projectionBounds.Min.X - 20.0, projectionBounds.Center.Y, projectionBounds.Center.Z),
                new Vector3D(projectionBounds.Center.X, projectionBounds.Max.Y + 20.0, projectionBounds.Center.Z),
                new Vector3D(projectionBounds.Center.X, projectionBounds.Min.Y - 20.0, projectionBounds.Center.Z),
                new Vector3D(projectionBounds.Center.X, projectionBounds.Center.Y, projectionBounds.Max.Z + 20.0),
                new Vector3D(projectionBounds.Center.X, projectionBounds.Center.Y, projectionBounds.Min.Z - 20.0),
            };
            var ships = rig.Ships;
            for (var index = 0; index < WelderCount; index++)
            {
                var shipOb = WorldApi.LoadAuthoredGrid(WelderResource,
                    WorldApi.EntityPrefix + "perf-welder-" + index);
                var shipPose = shipOb.PositionAndOrientation.Value;
                shipOb.PositionAndOrientation = new MyPositionAndOrientation(
                    shipPositions[index], shipPose.Forward, shipPose.Up);
                shipOb.IsStatic = true;
                var spawnedShip = WorldApi.SpawnGrid(shipOb);
                ships.Add(spawnedShip);
                Track(spawnedShip);
            }
            yield return WaitForTicks(120);

            // Use the materialized preview definitions, not object-builder SubtypeName. Forty-two
            // turret-style cells in this authored blueprint encode their identity only in xsi:type;
            // grouping the XML builders by SubtypeName silently omits all of their components and
            // strands the connected cells behind them. Double stock also covers conveyor staging.
            var needs = RuntimeComponentsNeeded(preview.CubeBlocks, 2);
            var welders = rig.Welders;
            foreach (var ship in ships)
            {
                WorldApi.EnsureDistributor(ship);
                Check(WorldApi.ChargeBatteries(ship) > 0f, "an authored welder ship has no charged battery");
                var welder = WorldApi.FindFunctional<SpaceWelder>(ship);
                var container = WorldApi.FindFunctional<MyCargoContainer>(ship);
                Check(welder != null, "an authored ship has no welder");
                Check(container != null, "an authored ship has no cargo container");
                Note("stocking authored cargo for " + needs.Count + " component types: " +
                     WorldApi.StockComponents(container.GetInventory(), needs));
                var missingAfterStock = needs
                    .Where(kvp => WorldApi.CountComponent(container.GetInventory(), kvp.Key) < kvp.Value)
                    .Select(kvp => kvp.Key + "=" + WorldApi.CountComponent(container.GetInventory(), kvp.Key) +
                                   "/" + kvp.Value)
                    .ToList();
                Check(missingAfterStock.Count == 0,
                    "cargo did not accept the complete projection kit: " + string.Join(", ", missingAfterStock));
                welders.Add(welder);
            }

            var waitRadius = Wait(() =>
            {
                var ready = true;
                foreach (var ship in ships) WorldApi.ChargeBatteries(ship);
                foreach (var welder in welders)
                    ready &= Math.Abs(WorldApi.SensorSphere(welder).Radius - welder.DetectorSphere.Radius) < 0.01 &&
                             WorldApi.SensorSphere(welder).Radius > 200.0;
                return ready;
            }, "x100 detector radius applied to all authored welders", 30);
            while (waitRadius.MoveNext()) yield return waitRadius.Current;

            foreach (var welder in welders)
            {
                var sensor = WorldApi.SensorSphere(welder);
                var farthest = FarthestCornerDistance(sensor.Center, projectionBounds);
                Check(sensor.Radius >= farthest,
                    "welder sphere does not cover hologram: radius=" + sensor.Radius.ToString("F1") +
                    " farthest=" + farthest.ToString("F1"));
                welder.Enabled = true;
            }
            var waitWorking = Wait(() =>
            {
                foreach (var ship in ships) WorldApi.ChargeBatteries(ship);
                return welders.All(w => w.IsWorking);
            }, "all authored welders working", 60);
            while (waitWorking.MoveNext()) yield return waitWorking.Current;
        }

        /// <summary>
        /// Puts a copy of <paramref name="source"/> into the platform's projector, in place of the
        /// blueprint the platform was authored with, and picks the offset that makes it touch.
        ///
        /// The projector lays the blueprint out in its own axes with the first block of the blueprint
        /// at its own cell less the offset. So an armor block of the ship goes first, and the offset
        /// is chosen to put it next to an armor block of the platform - on a face of the ship open to
        /// the outside, and with no block of the platform ending up inside the ship.
        /// </summary>
        internal static string UseAsBlueprint(MyObjectBuilder_CubeGrid platformOb, MyObjectBuilder_CubeGrid source)
        {
            var projectorOb = platformOb.CubeBlocks.OfType<MyObjectBuilder_ProjectorBase>().FirstOrDefault();
            Check(projectorOb != null, "the authored platform has no projector");
            var at = (Vector3I)projectorOb.Min;
            Matrix toPlatform;
            new MyBlockOrientation(projectorOb.BlockOrientation.Forward, projectorOb.BlockOrientation.Up)
                .GetMatrix(out toPlatform);
            var toBlueprint = Matrix.Transpose(toPlatform);

            // The platform as the projector sees it: its cells relative to the projector, turned into
            // the blueprint's axes.
            var platformCells = new List<Vector3I>();
            var platformArmor = new List<Vector3I>();
            foreach (var block in platformOb.CubeBlocks)
            {
                var definition = MyDefinitionManager.Static.GetCubeBlockDefinition(block.GetId());
                var min = (Vector3I)block.Min;
                Vector3I max;
                MySlimBlock.ComputeMax(definition,
                    new MyBlockOrientation(block.BlockOrientation.Forward, block.BlockOrientation.Up), ref min, out max);
                foreach (var cell in Cells(min, max))
                    platformCells.Add(Vector3I.Round(Vector3.TransformNormal((Vector3)(cell - at), toBlueprint)));
                if (min == max && IsArmorCube(definition))
                    platformArmor.Add(Vector3I.Round(Vector3.TransformNormal((Vector3)(min - at), toBlueprint)));
            }
            Check(platformArmor.Count > 0, "the authored platform has no armor block to weld from");

            // the ship's cells, from its blocks and their definitions
            var shipCells = new HashSet<Vector3I>();
            var shipBlocks = new List<(MyObjectBuilder_CubeBlock Block, Vector3I Min, Vector3I Max, MyCubeBlockDefinition Definition)>();
            var shipMin = new Vector3I(int.MaxValue);
            var shipMax = new Vector3I(int.MinValue);
            foreach (var block in source.CubeBlocks)
            {
                var definition = MyDefinitionManager.Static.GetCubeBlockDefinition(block.GetId());
                if (definition == null) continue;
                var min = (Vector3I)block.Min;
                Vector3I max;
                MySlimBlock.ComputeMax(definition,
                    new MyBlockOrientation(block.BlockOrientation.Forward, block.BlockOrientation.Up), ref min, out max);
                shipBlocks.Add((block, min, max, definition));
                shipMin = Vector3I.Min(shipMin, min);
                shipMax = Vector3I.Max(shipMax, max);
                foreach (var cell in Cells(min, max))
                    shipCells.Add(cell);
            }

            // seed: an armor block of the ship, first in the blueprint; offset: where that puts it
            Vector3I? seed = null;
            var offset = Vector3I.Zero;
            foreach (var block in shipBlocks.Where(b => b.Min == b.Max && IsArmorCube(b.Definition))
                         .OrderBy(b => b.Min.X).ThenBy(b => b.Min.Y).ThenBy(b => b.Min.Z))
            {
                foreach (var outward in Base6Directions.IntDirections)
                {
                    if (!OpenToOutside(block.Min, outward, shipCells, shipMin, shipMax)) continue;
                    foreach (var armor in platformArmor)
                    {
                        // the platform shifted so that this armor block of it lands next to the seed
                        var shift = block.Min + outward - armor;
                        if (platformCells.Any(cell => shipCells.Contains(cell + shift))) continue;
                        seed = block.Min;
                        offset = shift - block.Min;
                        break;
                    }
                    if (seed.HasValue) break;
                }
                if (seed.HasValue) break;
            }
            Check(seed.HasValue, "found no armor block of " + BlueprintName + " the platform can be put against");
            Check(Math.Abs(offset.X) <= 50 && Math.Abs(offset.Y) <= 50 && Math.Abs(offset.Z) <= 50,
                "the projection offset " + offset + " is beyond what a projector takes");

            var blueprint = (MyObjectBuilder_CubeGrid)source.Clone();
            var first = blueprint.CubeBlocks.First(b => (Vector3I)b.Min == seed.Value);
            blueprint.CubeBlocks.Remove(first);
            blueprint.CubeBlocks.Insert(0, first);
            // what the projector does to a blueprint handed to it through the API
            blueprint.IsStatic = false;
            blueprint.DestructibleBlocks = false;
            foreach (var block in blueprint.CubeBlocks)
            {
                block.Owner = 0;
                block.ShareMode = MyOwnershipShareModeEnum.None;
                block.EntityId = 0;
                var functional = block as MyObjectBuilder_FunctionalBlock;
                if (functional != null) functional.Enabled = false;
            }
            MyEntities.RemapObjectBuilder(blueprint);

            projectorOb.ProjectedGrid = null;
            projectorOb.ProjectedGrids = new List<MyObjectBuilder_CubeGrid> { blueprint };
            projectorOb.ProjectionOffset = offset;
            projectorOb.ProjectionRotation = Vector3I.Zero;
            return BlueprintName + " (" + source.CubeBlocks.Count + " blocks, from the plugin) goes into the projector, starting from the armor block at " +
                   seed.Value + ", projection offset " + offset;
        }

        private static bool IsArmorCube(MyCubeBlockDefinition definition)
        {
            var subtype = definition?.Id.SubtypeName ?? "";
            return definition != null && definition.Size == Vector3I.One &&
                   (subtype == "LargeBlockArmorBlock" || subtype == "LargeHeavyBlockArmorBlock");
        }

        /// <summary>Whether nothing of the ship stands between this cell and the outside in that direction.</summary>
        private static bool OpenToOutside(Vector3I cell, Vector3I direction, HashSet<Vector3I> ship, Vector3I min, Vector3I max)
        {
            for (var next = cell + direction;
                 next.X >= min.X && next.Y >= min.Y && next.Z >= min.Z && next.X <= max.X && next.Y <= max.Y && next.Z <= max.Z;
                 next += direction)
                if (ship.Contains(next)) return false;
            return true;
        }

        private static IEnumerable<Vector3I> Cells(Vector3I a, Vector3I b)
        {
            var min = Vector3I.Min(a, b);
            var max = Vector3I.Max(a, b);
            for (var x = min.X; x <= max.X; x++)
                for (var y = min.Y; y <= max.Y; y++)
                    for (var z = min.Z; z <= max.Z; z++)
                        yield return new Vector3I(x, y, z);
        }

        internal static double FarthestCornerDistance(Vector3D center, BoundingBoxD box)
        {
            var dx = Math.Max(Math.Abs(center.X - box.Min.X), Math.Abs(center.X - box.Max.X));
            var dy = Math.Max(Math.Abs(center.Y - box.Min.Y), Math.Abs(center.Y - box.Max.Y));
            var dz = Math.Max(Math.Abs(center.Z - box.Min.Z), Math.Abs(center.Z - box.Max.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        internal static Dictionary<string, int> RuntimeComponentsNeeded(
            IEnumerable<MySlimBlock> blocks, int multiplier)
        {
            var needs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in blocks)
            {
                var components = block.BlockDefinition?.Components;
                if (components == null) continue;
                foreach (var component in components)
                {
                    var subtype = component.Definition?.Id.SubtypeName;
                    if (string.IsNullOrEmpty(subtype)) continue;
                    int previous;
                    needs.TryGetValue(subtype, out previous);
                    needs[subtype] = previous + component.Count * Math.Max(1, multiplier);
                }
            }
            return needs;
        }

        internal static List<MyCubeGrid> PhysicalFixtureGrids(Vector3D center, double radius, MyCubeGrid preview)
        {
            return WorldApi.GridsInSphere(center, radius)
                .Where(grid => grid != null && grid != preview && !grid.MarkedForClose && !grid.Closed)
                .ToList();
        }

        public override void Cleanup()
        {
            try
            {
                RestoreRuntimeConfig();
                _soConfig.Restore();
                SafeZoneProbe.Stop();
                if (_inZone || _detach) FakeClients.RemoveAll();
                if (_zone != null && !_zone.Closed) _zone.Close();
            }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                RestoreRuntimeConfig();
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    var namedTestGrid = (grid.Name ?? "").StartsWith(WorldApi.EntityPrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                        (grid.DisplayName ?? "").StartsWith(WorldApi.EntityPrefix,
                            StringComparison.OrdinalIgnoreCase);
                    var nearbyDebris = _fixturePosition.HasValue && grid.BlocksCount <= 4 &&
                        Vector3D.DistanceSquared(grid.PositionComp.GetPosition(), _fixturePosition.Value) <= 150 * 150;
                    if (namedTestGrid || nearbyDebris)
                    {
                        Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                        grid.Close();
                    }
                }
            }
            finally { base.CleanupLeftovers(); }
        }

        private void RestoreRuntimeConfig()
        {
            if (!_captured) return;
            RuntimePluginControls.SetWelderRadiusMultiplier(_initialWelderMultiplier);
            RuntimePluginControls.SetFreezerEnabled(_initialFreezerEnabled);
            Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS = _initialOwnAllDlcs;
            _captured = false;
        }
    }
}
