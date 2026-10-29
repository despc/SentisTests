using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;
using SpaceWelder = SpaceEngineers.Game.Entities.Blocks.MyShipWelder;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisClusters' detached clusters going and coming back while welding and grinding go on in them: what breaks
    /// at the moment a cluster's ticks move to a thread of its own, at the moment they come back to the game's, and
    /// in between.
    ///
    /// One heap: <see cref="Sites"/> welder_perf sites (a projector holding the Spitfire, six welder ships round it), and
    /// between them <see cref="Pairs"/> grinder_perf pairs (a grinder ship pressed onto a station with every block off).
    /// By every target two players with hand tools: one grinds the station from its far side, one welds the same spot
    /// back - the stockpile's sync list both use is what broke under a player's hand grinder (the stand, 11.10.2026).
    /// SentisClusters' DetachMs is <see cref="DetachMs"/> so that the heap is detached whenever it may be; for
    /// <see cref="ChurnSeconds"/> it is then let go and taken back at random, either way the plugin has: Detach off
    /// (every cluster attached at once) or a budget out of reach (attached once it has been light for a while).
    ///
    /// Fails when a detached cluster's tick threw, when a cluster's thread threw, or when the churn did not happen
    /// (fewer than <see cref="MinTurns"/> detaches and attaches). A crash of the server is the run's own verdict.
    /// </summary>
    public sealed class DetachChurnScenario : TestScenario
    {
        public const string ScenarioName = "detach_churn";
        private const int Sites = 12;
        private const int PerRow = 4;
        private const double Step = 450;
        private const int Pairs = 6;
        private const float DetachMs = 1f;
        private const double ChurnSeconds = 300;
        private const double MinHold = 4, MaxHold = 15;
        private const int MinTurns = 4;
        private const double HandReach = 1.6;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private static readonly MyDefinitionId GrinderItem = new MyDefinitionId(typeof(MyObjectBuilder_PhysicalGunObject), "AngleGrinderItem");
        private static readonly MyDefinitionId WelderItem = new MyDefinitionId(typeof(MyObjectBuilder_PhysicalGunObject), "WelderItem");

        private readonly List<ulong> _protected = new List<ulong>();
        private readonly ConfigOverride _soConfig = new ConfigOverride();
        private readonly ConfigOverride _clusters = new ConfigOverride(ConfigOverride.Clusters);
        private bool _captured;
        private float _initialWelderMultiplier;
        private bool _initialOwnAllDlcs;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 1200;

        private sealed class Site
        {
            public MyCubeGrid Platform;
            public MyProjectorBase Projector;
            public readonly List<MyCubeGrid> Ships = new List<MyCubeGrid>();
            public readonly List<SpaceWelder> Welders = new List<SpaceWelder>();
        }

        private sealed class Pair
        {
            public MyCubeGrid Grinder, Target;
            public List<MyShipGrinder> Grinders;
            public Vector3 Dir;
            public int StartBlocks;
            // where the hand tools work: a block of the target's far face, and the players' places by it
            public Vector3D Spot, Grinding, Welding;
        }

        private sealed class Hand
        {
            public int Client;
            public MyDefinitionId Tool;
            public Vector3D At, Aim;
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            Check(Type("SentisClusters.Run.Detacher") != null, "SentisClusters is not loaded");
            _initialWelderMultiplier = RuntimePluginControls.WelderRadiusMultiplier;
            _initialOwnAllDlcs = Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS;
            _captured = true;
            Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS = true;
            RuntimePluginControls.SetWelderRadiusMultiplier(WelderPerfScenario.RadiusMultiplier);
            _soConfig.Set("ProjectionBuildsPerFrame", Sites * WelderPerfScenario.WelderCount);
            _clusters.Set("Detach", true);
            _clusters.Set("DetachMs", DetachMs);
            // detached by its load whatever the server's frame (SentisClusters detaches otherwise only a busy server's clusters)
            _clusters.Set("DetachFrameMs", 0f);
            FakeClients.RemoveAll();

            var authored = WorldApi.LoadAuthoredGrid(WelderPerfScenario.ProjectionResource, WorldApi.EntityPrefix + "perf-projection");
            var pose = authored.PositionAndOrientation.Value;
            var origin = TestRunner.RunOrigin ?? new Vector3D(pose.Position.X + 12000.0, pose.Position.Y, pose.Position.Z);
            var up = (Vector3D)(Vector3)pose.Up;
            var siteAt = Enumerable.Range(0, Sites).Select(i => origin + new Vector3D(i % PerRow * Step, 0, i / PerRow * Step)).ToList();

            // the grinder pairs between the sites, 200 m above their row
            var pairs = new List<Pair>();
            for (var i = 0; i < Pairs; i++)
            {
                pairs.Add(SpawnPair(i, siteAt[i] + new Vector3D(Step / 2, 200, Step / 2)));
                yield return null;
            }

            // the players: one over every site, two with hand tools by every target, all invulnerable
            var hands = new List<Hand>();
            var spots = siteAt.Select(s => s + up * 150).ToList();
            foreach (var pair in pairs)
            {
                hands.Add(new Hand { Client = spots.Count, Tool = GrinderItem, At = pair.Grinding, Aim = pair.Spot });
                spots.Add(pair.Grinding);
                hands.Add(new Hand { Client = spots.Count, Tool = WelderItem, At = pair.Welding, Aim = pair.Spot });
                spots.Add(pair.Welding);
            }
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

            var sites = new List<Site>();
            var source = WorldApi.LoadTemplateXml(WelderPerfScenario.BlueprintResource);
            for (var i = 0; i < Sites; i++)
            {
                var site = new Site();
                var platformOb = WorldApi.LoadAuthoredGrid(WelderPerfScenario.ProjectionResource, WorldApi.EntityPrefix + "churn-projection-" + i);
                platformOb.PositionAndOrientation = new MyPositionAndOrientation(siteAt[i], pose.Forward, pose.Up);
                platformOb.IsStatic = true;
                WelderPerfScenario.UseAsBlueprint(platformOb, source);
                site.Platform = WorldApi.SpawnGrid(platformOb);
                Track(site.Platform);
                yield return WaitForTicks(30);
                WorldApi.EnsureDistributor(site.Platform);
                Check(WorldApi.ChargeBatteries(site.Platform) > 0f, "a platform has no chargeable batteries");
                site.Projector = WorldApi.FindFunctional<MyProjectorBase>(site.Platform);
                Check(site.Projector != null, "a platform has no projector");
                site.Projector.Enabled = true;
                sites.Add(site);
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

            for (var i = 0; i < Sites; i++)
            {
                var site = sites[i];
                var bounds = site.Projector.ProjectedGrid.PositionComp.WorldAABB;
                var places = new[]
                {
                    new Vector3D(bounds.Max.X + 20.0, bounds.Center.Y, bounds.Center.Z),
                    new Vector3D(bounds.Min.X - 20.0, bounds.Center.Y, bounds.Center.Z),
                    new Vector3D(bounds.Center.X, bounds.Max.Y + 20.0, bounds.Center.Z),
                    new Vector3D(bounds.Center.X, bounds.Min.Y - 20.0, bounds.Center.Z),
                    new Vector3D(bounds.Center.X, bounds.Center.Y, bounds.Max.Z + 20.0),
                    new Vector3D(bounds.Center.X, bounds.Center.Y, bounds.Min.Z - 20.0),
                };
                var needs = WelderPerfScenario.RuntimeComponentsNeeded(site.Projector.ProjectedGrid.CubeBlocks, 2);
                for (var w = 0; w < WelderPerfScenario.WelderCount; w++)
                {
                    var shipOb = WorldApi.LoadAuthoredGrid(WelderPerfScenario.WelderResource, WorldApi.EntityPrefix + "churn-welder-" + i + "-" + w);
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
                    WorldApi.ChargeBatteries(ship);
                    var welder = WorldApi.FindFunctional<SpaceWelder>(ship);
                    var container = WorldApi.FindFunctional<MyCargoContainer>(ship);
                    Check(welder != null && container != null, "a welder ship has no welder or no cargo container");
                    WorldApi.StockComponents(container.GetInventory(), needs);
                    site.Welders.Add(welder);
                }
                yield return null;
            }
            var welders = sites.SelectMany(s => s.Welders).ToList();
            var waitRadius = Wait(() =>
            {
                foreach (var ship in sites.SelectMany(s => s.Ships)) WorldApi.ChargeBatteries(ship);
                return welders.All(w => WorldApi.SensorSphere(w).Radius > 200.0);
            }, "the welders' detector radius", 60);
            while (waitRadius.MoveNext()) yield return waitRadius.Current;
            foreach (var welder in welders) welder.Enabled = true;
            foreach (var welder in welders)
                if (!WorldApi.ToolIsActivated(welder)) WorldApi.ToolStartShooting(welder);

            // the hand tools: a grinder or a welder in the hand, components for the welder
            var equipped = 0;
            foreach (var hand in hands)
                if (Equip(hand)) equipped++;
            foreach (var pair in pairs)
            {
                pair.StartBlocks = pair.Target.CubeBlocks.Count;
                foreach (var g in pair.Grinders) g.Enabled = true;
            }

            var detacher = Type("SentisClusters.Run.Detacher");
            long Counter(string name) => Convert.ToInt64(detacher.GetField(name).GetValue(null));
            long RunnerExceptions() => Convert.ToInt64(Type("SentisClusters.Run.ClusterRunner").GetProperty("Exceptions").GetValue(null));
            var detachedBefore = Counter("DetachedTotal");
            var attachedBefore = Counter("AttachedTotal");
            var threwBefore = Counter("ExceptionsTotal");
            var runnerBefore = RunnerExceptions();
            var remainingBefore = sites.Sum(s => Remaining(s.Projector));
            Note("DETACH CHURN | " + Sites + " welding sites (" + welders.Count + " welders), " + Pairs + " grinder pairs (" +
                 pairs.Sum(p => p.Grinders.Count) + " grinders), " + hands.Count + " players with hand tools (" + equipped + " equipped), DetachMs " + DetachMs);

            // the churn: the heap let go and taken back at random
            var rng = new Random(20261011);
            var watch = Stopwatch.StartNew();
            var turnAt = 0.0;
            var turns = new List<string>();
            var detach = true;
            var nextLog = 30.0;
            while (watch.Elapsed.TotalSeconds < ChurnSeconds)
            {
                var now = watch.Elapsed.TotalSeconds;
                if (now >= turnAt)
                {
                    turnAt = now + MinHold + rng.NextDouble() * (MaxHold - MinHold);
                    if (detach)
                    {
                        // taken back: at once (Detach off) or once it has been light a while (a budget out of reach)
                        if (rng.Next(2) == 0) { _clusters.Set("Detach", false); turns.Add("off"); }
                        else { _clusters.Set("DetachMs", 1000f); turns.Add("high"); }
                    }
                    else
                    {
                        _clusters.Set("Detach", true);
                        _clusters.Set("DetachMs", DetachMs);
                        turns.Add("on");
                    }
                    detach = !detach;
                }
                foreach (var pair in pairs) GrinderPerfScenario.Push(pair.Grinder, pair.Dir);
                foreach (var hand in hands) Work(hand);
                foreach (var ship in sites.SelectMany(s => s.Ships)) WorldApi.ChargeBatteries(ship);
                if (now >= nextLog)
                {
                    nextLog += 30;
                    Note("churn " + now.ToString("F0") + " s: detached " + (Counter("DetachedTotal") - detachedBefore) + ", attached " + (Counter("AttachedTotal") - attachedBefore) +
                         ", threw " + (Counter("ExceptionsTotal") - threwBefore) + "/" + (RunnerExceptions() - runnerBefore) +
                         ", welded " + (remainingBefore - sites.Sum(s => Remaining(s.Projector))) + ", ground off " + pairs.Sum(p => p.StartBlocks - Left(p)) +
                         ", hand tools out " + hands.Count(h => Holding(h)));
                }
                yield return null;
            }
            _clusters.Set("Detach", true);
            _clusters.Set("DetachMs", DetachMs);
            foreach (var g in pairs.SelectMany(p => p.Grinders)) g.Enabled = false;

            var detached = Counter("DetachedTotal") - detachedBefore;
            var attached = Counter("AttachedTotal") - attachedBefore;
            var threw = Counter("ExceptionsTotal") - threwBefore;
            var runnerThrew = RunnerExceptions() - runnerBefore;
            Note("DETACH CHURN RESULT | " + turns.Count + " turns (" + string.Join(" ", turns) + "), detached " + detached + ", attached " + attached +
                 " | threw: detached ticks " + threw + ", clusters' threads " + runnerThrew +
                 " | welded " + (remainingBefore - sites.Sum(s => Remaining(s.Projector))) + " blocks, ground off " + pairs.Sum(p => p.StartBlocks - Left(p)) +
                 " blocks, hand tools out " + hands.Count(h => Holding(h)) + "/" + hands.Count);
            Check(threw == 0, threw + " exceptions in the detached clusters' ticks (see the log)");
            Check(runnerThrew == 0, runnerThrew + " exceptions in the clusters' threads (see the log)");
            Check(detached >= MinTurns && attached >= MinTurns, "the heap was detached " + detached + " and attached " + attached + " times: not a churn");
        }

        private static Type Type(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);

        private static int Remaining(MyProjectorBase projector) =>
            projector.ProjectedGrid != null ? ((Sandbox.ModAPI.Ingame.IMyProjector)projector).RemainingBlocks : 0;

        private static int Left(Pair pair) => pair.Target.Closed ? 0 : pair.Target.CubeBlocks.Count;

        /// <summary>A grinder pair from grinder_perf, its grinder <see cref="GrinderPerfScenario"/>'s way, put at <paramref name="at"/>.</summary>
        private Pair SpawnPair(int index, Vector3D at)
        {
            var pair = new Pair();
            var grinderObs = WorldApi.LoadAuthoredGroup(GrinderPerfScenario.GrinderResource, WorldApi.EntityPrefix + "churn-grinder-" + index).ToList();
            var targetObs = WorldApi.LoadAuthoredGroup(GrinderPerfScenario.TargetResource, WorldApi.EntityPrefix + "churn-target-" + index).ToList();
            var offset = at - (Vector3D)grinderObs[0].PositionAndOrientation.Value.Position;
            foreach (var ob in grinderObs)
            {
                GrinderPerfScenario.Shift(ob, offset);
                pair.Grinder = WorldApi.SpawnGrid(ob);
                Track(pair.Grinder);
            }
            foreach (var ob in targetObs)
            {
                GrinderPerfScenario.Shift(ob, offset);
                pair.Target = WorldApi.SpawnGrid(ob);
                Track(pair.Target);
                pair.Target.ConvertToStatic();
                foreach (var block in pair.Target.GetFatBlocks().OfType<MyFunctionalBlock>()) block.Enabled = false;
            }
            Check(pair.Grinder != null && pair.Target != null, "grinder pair grids not loaded");
            pair.Grinders = pair.Grinder.GetFatBlocks().OfType<MyShipGrinder>().ToList();
            Check(pair.Grinders.Count > 0, "no grinders on " + pair.Grinder.DisplayName);
            pair.Dir = Vector3.Normalize(pair.Grinders.Aggregate(Vector3D.Zero, (a, g) => a + g.WorldMatrix.Forward));
            var gap = GrinderPerfScenario.Gap(pair.Grinders, pair.Target, pair.Dir);
            if (gap > 1.5 && gap < 100)
                pair.Grinder.PositionComp.SetPosition(pair.Grinder.PositionComp.GetPosition() + (Vector3D)pair.Dir * (gap - 1.5));

            // the hand tools' spot: the target's block farthest along the grind axis, near the axis; the players
            // stand off its far face, side by side
            var centre = pair.Target.PositionComp.WorldAABB.Center;
            var dir = (Vector3D)pair.Dir;
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(dir));
            var spot = pair.Target.CubeBlocks
                .Select(b => pair.Target.GridIntegerToWorld(b.Position))
                .Where(p => (p - centre - dir * Vector3D.Dot(p - centre, dir)).Length() < 6)
                .OrderByDescending(p => Vector3D.Dot(p - centre, dir))
                .First();
            var half = pair.Target.GridSize / 2;
            pair.Spot = spot;
            pair.Grinding = spot + dir * (half + HandReach) + side * 0.6;
            pair.Welding = spot + dir * (half + HandReach) - side * 0.6;
            return pair;
        }

        /// <summary>The tool in the player's hand, and components in his pockets for a welder. Game thread.</summary>
        private static bool Equip(Hand hand)
        {
            var character = FakeClients.Character(hand.Client);
            if (character == null) return false;
            var inventory = character.GetInventory();
            if (inventory == null) return false;
            var gun = (MyObjectBuilder_PhysicalGunObject)MyObjectBuilderSerializer.CreateNewObject(hand.Tool);
            inventory.AddItems(1, gun);
            if (hand.Tool == WelderItem)
                WorldApi.StockComponents(inventory, new Dictionary<string, int> { { "SteelPlate", 200 }, { "InteriorPlate", 100 }, { "Construction", 100 }, { "SmallTube", 50 }, { "MetalGrid", 50 }, { "Girder", 50 } });
            character.SwitchToWeapon(hand.Tool, false);
            return Holding(hand);
        }

        private static bool Holding(Hand hand)
        {
            var character = FakeClients.Character(hand.Client);
            return character?.CurrentWeapon is MyEngineerToolBase;
        }

        /// <summary>The player faces the spot and keeps the tool going. Game thread.</summary>
        private static void Work(Hand hand)
        {
            var character = FakeClients.Character(hand.Client);
            if (character == null || character.IsDead) return;
            if (!Holding(hand))
            {
                character.SwitchToWeapon(hand.Tool, false);
                if (!Holding(hand)) return;
            }
            var at = character.PositionComp.GetPosition();
            var forward = Vector3D.Normalize(hand.Aim - at);
            var up = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(forward));
            character.PositionComp.SetWorldMatrix(MatrixD.CreateWorld(at, forward, up));
            if (!character.IsShooting(MyShootActionEnum.PrimaryAction))
                character.BeginShootSync(forward, MyShootActionEnum.PrimaryAction);
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
                _clusters.Restore();
                FakeClients.RemoveAll();
                foreach (var steamId in _protected) Sandbox.Game.World.MySession.Static?.RemoteAdminSettings.Remove(steamId);
                _protected.Clear();
            }
            finally { base.Cleanup(); }
        }
    }
}
