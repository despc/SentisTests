using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A steady load for tuning SentisClusters' detaching (DetachMs, DetachFrameMs): one heavy cluster and the rest of the
    /// server's frame, each set apart, held while <c>tools/detach_tune.py</c> switches the thresholds and the heavy
    /// cluster's state live and reads the server's frame and the clusters' speeds from SentisClusters' web view.
    ///
    /// The heavy cluster: <c>heavy</c> copies of RefineryPerfScenario's grid (101 refineries each: updates, evenly spread
    /// over the frames) and <c>ships</c> Spitfires (welder_perf's ship, about 43 turrets, no ammunition) 40 m apart,
    /// turning (<c>spin</c>, rad/s) and drawn to their middle every 2 s: they keep hitting each other - the physics' load,
    /// like a heap of big ships a player brings together. The rest: <c>groups</c> clusters of <c>grids</c> refinery grids
    /// each, 20 km apart along the world's X axis (the game parts clusters only along the axes). A fake player by
    /// every cluster (SentisOptimisations steps only the clusters players are in), the freezer off. The load stays <c>hold</c> s (or until <c>!test stop</c>) and goes.
    /// </summary>
    internal sealed class DetachTuneScenario : TestScenario
    {
        public const string ScenarioName = "detach_tune";
        private const string Prefix = "detach-tune-";
        private const string Spitfire = "SentisTests.Resources.Spitfire.xml";
        private const double ClustersApart = 20000;
        private const double GridStep = 250;
        private const int GridsPerRow = 6;
        private const double ShipSpacing = 40;
        private const int ClosePerFrame = 10;

        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private readonly List<ulong> _protected = new List<ulong>();
        private bool _captured;
        private bool _initialFreezerEnabled;
        private uint? _autoSave;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 7200;

        private static int Arg(string name, int otherwise)
        {
            var m = Regex.Match(TestRunner.Focus ?? "", @"\b" + name + @"=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : otherwise;
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var heavy = Arg("heavy", 4);
            var ships = Arg("ships", 0);
            var spin = Arg("spin", 1);
            var groups = Arg("groups", 8);
            var perGroup = Arg("grids", 2);
            var holdSeconds = Arg("hold", 1800);
            Check(heavy + ships > 0, "no heavy cluster: heavy=0 ships=0");

            _initialFreezerEnabled = RuntimePluginControls.FreezerEnabled;
            _autoSave = MySession.Static.Settings.AutoSaveInMinutes;
            _captured = true;
            MySession.Static.Settings.AutoSaveInMinutes = 0;
            RuntimePluginControls.SetFreezerEnabled(false);

            var template = WorldApi.LoadAuthoredGrid(RefineryPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "template");
            var pose = template.PositionAndOrientation.Value;
            var origin = TestRunner.RunOrigin ?? new Vector3D(pose.Position.X + 12000.0, pose.Position.Y, pose.Position.Z);
            // the heavy cluster at the origin, the others along X; the ships over the heavy refineries
            var spots = new List<(Vector3D At, int Cluster)>();
            for (var i = 0; i < heavy; i++) spots.Add((origin + Row(i), 0));
            for (var g = 0; g < groups; g++)
            for (var i = 0; i < perGroup; i++)
                spots.Add((origin + Vector3D.Right * ((g + 1) * ClustersApart) + Row(i), g + 1));
            var side = (int)Math.Ceiling(Math.Sqrt(Math.Max(1, ships)));
            var fleetCentre = origin + Vector3D.Up * 400;
            var shipSpots = Enumerable.Range(0, ships).Select(k => fleetCentre + Vector3D.Right * ((k % side - (side - 1) / 2.0) * ShipSpacing) +
                                                                  Vector3D.Backward * ((k / side - (side - 1) / 2.0) * ShipSpacing)).ToList();
            var shift = HeavyNeighbourScenario.OutOfPlanets(spots.Select(s => s.At).Concat(shipSpots).ToList());
            if (shift.LengthSquared() > 0) Note("the load moved " + shift.Length().ToString("F0") + " m out of a planet and its gravity");
            origin += shift;
            fleetCentre += shift;

            var refineryGrids = new List<MyCubeGrid>();
            foreach (var spot in spots)
            {
                var ob = WorldApi.LoadAuthoredGrid(RefineryPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "c" + spot.Cluster + "-" + refineryGrids.Count.ToString("D3"));
                ob.PositionAndOrientation = new MyPositionAndOrientation(spot.At + shift, pose.Forward, pose.Up);
                ob.IsStatic = true;
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                _grids.Add(grid);
                refineryGrids.Add(grid);
                yield return null;
            }
            var fleet = new List<MyCubeGrid>();
            foreach (var at in shipSpots)
            {
                var ob = WorldApi.LoadGridTemplate(Spitfire, WorldApi.EntityPrefix + Prefix + "ship-" + fleet.Count, at + shift, Vector3.Forward, Vector3.Up);
                var ship = WorldApi.SpawnGrid(ob);
                Track(ship);
                _grids.Add(ship);
                fleet.Add(ship);
                yield return null;
            }
            yield return WaitForTicks(120);
            for (var i = 0; i < fleet.Count; i++)
            {
                fleet[i].DestructibleBlocks = false;
                if (spin > 0 && fleet[i].Physics != null)
                    fleet[i].Physics.AngularVelocity = new Vector3(spin * (i % 3 == 0 ? 1 : -0.6f), spin * (i % 2 == 0 ? 0.7f : -0.8f), spin * 0.5f);
            }

            var refineries = 0;
            foreach (var grid in refineryGrids)
            {
                WorldApi.EnsureDistributor(grid);
                refineries += WorldApi.FindFunctionals<MyRefinery>(grid).Count;
            }
            yield return WaitForTicks(120);
            if (refineryGrids.Count > 0)
            {
                // ore for the whole hold at full speed, twice over: the refineries never run dry
                var ore = RefineryPerfScenario.OreLoad(refineries / refineryGrids.Count, 2 * holdSeconds);
                for (var g = 0; g < refineryGrids.Count; g++)
                {
                    RefineryPerfScenario.StockOre(WorldApi.FindFunctionals<MyCargoContainer>(refineryGrids[g]).Select(c => c.GetInventory(0)).Where(inv => inv != null).ToList(), ore);
                    if ((g + 1) % 8 == 0) yield return null;
                }
            }
            // a player by every cluster, invulnerable: SentisOptimisations steps only the clusters players are in
            FakeClients.RemoveAll();
            var players = new List<Vector3D> { fleetCentre + Vector3D.Up * 300 };
            for (var g = 0; g < groups; g++) players.Add(origin + Vector3D.Right * ((g + 1) * ClustersApart) + Vector3D.Up * 100);
            FakeClients.Add(players.Count, new FakeClients.NetworkProfile { RttMs = 50 }, i => (players[i], 0, 0), withCharacters: true);
            for (var i = 0; i < FakeClients.Count; i++)
            {
                var steamId = FakeClients.PlayerOf(i).Id.SteamId;
                _protected.Add(steamId);
                MySession.Static.RemoteAdminSettings[steamId] = AdminSettingsEnum.Invulnerable | AdminSettingsEnum.Untargetable;
            }
            var settle = WaitForSeconds(15, "the players arrive, the grids settle");
            while (settle.MoveNext()) yield return settle.Current;
            Note("DETACH TUNE READY | heavy at " + Point(origin + Vector3D.Up * 100) + ": " + heavy + " refinery grids, " + ships + " ships spin " + spin +
                 "; " + groups + " groups of " + perGroup + " refinery grids from " + Point(origin + Vector3D.Right * ClustersApart) + " every " +
                 ClustersApart / 1000 + " km along X; " + refineries + " refineries; holding " + holdSeconds + " s");

            var held = System.Diagnostics.Stopwatch.StartNew();
            var lastPull = 0.0;
            while (held.Elapsed.TotalSeconds < holdSeconds)
            {
                if (fleet.Count > 0 && held.Elapsed.TotalSeconds - lastPull >= 2)
                {
                    lastPull = held.Elapsed.TotalSeconds;
                    foreach (var ship in fleet)
                    {
                        if (ship.MarkedForClose || ship.Physics == null) continue;
                        var towards = fleetCentre - ship.PositionComp.GetPosition();
                        if (towards.LengthSquared() > 1) ship.Physics.LinearVelocity = (Vector3)(Vector3D.Normalize(towards) * Math.Min(10, towards.Length() / 5));
                    }
                }
                yield return null;
            }
            Note("DETACH TUNE DONE");
            FakeClients.RemoveAll();
            for (var i = 0; i < _grids.Count; i++)
            {
                if (!_grids[i].MarkedForClose) _grids[i].Close();
                if (i % ClosePerFrame == ClosePerFrame - 1) yield return null;
            }
            _grids.Clear();
        }

        private static Vector3D Row(int i) => new Vector3D(i % GridsPerRow * GridStep, 0, i / GridsPerRow * GridStep);

        private static string Point(Vector3D at) => at.X.ToString("F0") + " " + at.Y.ToString("F0") + " " + at.Z.ToString("F0");

        private void Restore()
        {
            if (!_captured) return;
            _captured = false;
            RuntimePluginControls.SetFreezerEnabled(_initialFreezerEnabled);
            FakeClients.RemoveAll();
            foreach (var steamId in _protected) MySession.Static?.RemoteAdminSettings.Remove(steamId);
            _protected.Clear();
            if (_autoSave.HasValue) MySession.Static.Settings.AutoSaveInMinutes = _autoSave.Value;
        }

        public override void Cleanup()
        {
            try { Restore(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                Restore();
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                    if (!grid.MarkedForClose && (grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + Prefix, StringComparison.OrdinalIgnoreCase))
                        grid.Close();
            }
            finally { base.CleanupLeftovers(); }
        }
    }
}
