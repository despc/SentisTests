using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using Sandbox.Game.EntityComponents;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A player comes to an untouched procedural asteroid in space and drills it at once. Reported on the live
    /// server (10.10.2026): the drill does nothing at first, then after a couple of seconds the client shows the
    /// asteroid streaming and it drills as it should.
    ///
    /// A client makes the procedural asteroids round it itself, from the seeds; the server makes them for the
    /// characters it tracks (MyProceduralWorldGenerator, within the view distance). Here a fake client with a
    /// one-drill ship comes to a spot nobody has been to, next to the nearest asteroid seed, and the times are
    /// measured from its arrival: until the server has that asteroid, and until the drill's first cut changes it.
    /// A measure, not a check: it fails only when there is nothing to measure.
    /// </summary>
    public sealed class AsteroidDrillScenario : TestScenario
    {
        public const string ScenarioName = "asteroid_drill";
        private const string Prefix = "astdrill-";
        private const double WaitAsteroidSeconds = 60;
        private const double WaitCutSeconds = 30;
        private static readonly Vector3D Start = new Vector3D(-3000000, 2500000, 1500000);
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        public override string Name => ScenarioName;

        // ---- what the replication does with the asteroid: MyVoxelReplicable.Serialize and InvalidateClientCache, by Harmony
        private static HarmonyLib.Harmony _harmony;
        private static long _watchedVoxel;
        private static Stopwatch _clock;
        private static readonly List<string> Events = new List<string>();
        private static System.Reflection.PropertyInfo _instance;

        private static void SerializePrefix(object __instance, HashSet<string> cachedData, VRage.Network.Endpoint forClient)
        {
            try
            {
                var voxel = (_instance ?? (_instance = __instance.GetType().GetProperty("Instance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)))
                    ?.GetValue(__instance) as MyVoxelBase;
                if (voxel == null || voxel.EntityId != _watchedVoxel) return;
                var changed = voxel.ContentChanged || voxel.BeforeContentChanged;
                var cached = cachedData != null && cachedData.Contains(voxel.StorageName);
                var content = (!cached && changed) || voxel.AsteroidName == null;
                lock (Events) Events.Add(_clock.Elapsed.TotalSeconds.ToString("F2") + "s Serialize for client " + forClient.Id.Value % 1000 +
                                         (content ? " WITH the whole storage" : " by the seed only") + " (changed " + changed + ", client cache " + cached + ")");
            }
            catch (Exception e) { lock (Events) Events.Add("Serialize probe: " + e.Message); }
        }

        private static void InvalidatePrefix(object replicable, string storageName)
        {
            try
            {
                var voxel = (_instance ?? (_instance = replicable?.GetType().GetProperty("Instance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)))
                    ?.GetValue(replicable) as MyVoxelBase;
                if (voxel == null || voxel.EntityId != _watchedVoxel) return;
                lock (Events) Events.Add(_clock.Elapsed.TotalSeconds.ToString("F2") + "s InvalidateClientCache " + storageName);
            }
            catch { }
        }

        private static void Hook()
        {
            if (_harmony != null) return;
            _harmony = new HarmonyLib.Harmony("SentisTests.AsteroidDrill");
            var replicable = typeof(MyVoxelBase).Assembly.GetType("Sandbox.Game.Replication.MyVoxelReplicable");
            _harmony.Patch(replicable.GetMethod("Serialize"), prefix: new HarmonyLib.HarmonyMethod(typeof(AsteroidDrillScenario).GetMethod(nameof(SerializePrefix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            var server = typeof(VRage.Network.MyReplicationServer).GetMethod("InvalidateClientCache");
            _harmony.Patch(server, prefix: new HarmonyLib.HarmonyMethod(typeof(AsteroidDrillScenario).GetMethod(nameof(InvalidatePrefix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
        }

        private static void Unhook()
        {
            _harmony?.UnpatchAll("SentisTests.AsteroidDrill");
            _harmony = null;
            _watchedVoxel = 0;
        }
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var generator = MyProceduralWorldGenerator.Static;
            Check(generator != null && generator.Enabled && MySession.Static.Settings.ProceduralDensity > 0, "procedural asteroids are off in this world");
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            // space nobody has been to, and the nearest asteroid seed there - what a client already sees
            var rng = new Random();
            var spot = Start + Vector3D.Normalize(new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5)) * rng.Next(0, 1000000);
            var seeds = new List<MyObjectSeed>();
            generator.OverlapAllAsteroidSeedsInSphere(new BoundingSphereD(spot, 20000), seeds);
            var seed = seeds.Where(s => s.Params.Type == MyObjectSeedType.Asteroid && !s.Params.Generated && s.Size > 150)
                .OrderBy(s => Vector3D.DistanceSquared(s.BoundingVolume.Center, spot)).FirstOrDefault();
            Check(seed != null, "no asteroid seed within 20 km of " + spot);
            var centre = seed.BoundingVolume.Center;
            var radius = seed.Size / 2;
            var towards = Vector3D.Normalize(centre - spot);
            var arrive = centre - towards * (radius + 60);
            Note("asteroid seed " + seed.Params.Seed + " of " + seed.Size.ToString("F0") + " m at " + Vector3D.Round(centre, 0) + ", " +
                 seeds.Count(s => s.Params.Type == MyObjectSeedType.Asteroid) + " asteroid seeds within 20 km");

            var before = new HashSet<long>(MySession.Static.VoxelMaps.Instances.Select(v => v.EntityId));
            var watch = Stopwatch.StartNew();
            FakeClients.Add(1, Network, p => (arrive - towards * 20, 0, 0), withCharacters: true);
            var steamId = FakeClients.PlayerOf(0).Id.SteamId;
            MySession.Static.RemoteAdminSettings[steamId] = AdminSettingsEnum.Invulnerable | AdminSettingsEnum.Untargetable;
            var ship = SpawnDrillShip(arrive, towards);
            Track(ship);
            var drill = WorldApi.FindFunctionals<MyShipDrill>(ship).FirstOrDefault();
            Check(drill != null, "the drill ship has no drill");
            var queued = QueuedAsteroids();
            Note("arrived; asteroids waiting in SentisOptimisations' generation queue: " + (queued < 0 ? "no such queue (vanilla)" : queued.ToString()));

            // until the server has the asteroid
            MyVoxelBase asteroid = null;
            var newMaps = 0;
            while (watch.Elapsed.TotalSeconds < WaitAsteroidSeconds)
            {
                var fresh = MySession.Static.VoxelMaps.Instances.Where(v => !before.Contains(v.EntityId)).ToList();
                newMaps = fresh.Count;
                asteroid = fresh.FirstOrDefault(v => !(v is MyPlanet) && v.PositionComp.WorldAABB.Contains(centre) != ContainmentType.Disjoint);
                if (asteroid != null) break;
                yield return null;
            }
            var exist = watch.Elapsed.TotalSeconds;
            Check(asteroid != null, "the server made no asteroid at the seed in " + WaitAsteroidSeconds + " s (" + newMaps + " voxel maps made meanwhile)");
            var mapsAtExist = newMaps;
            Note("the server has the asteroid after " + exist.ToString("F2") + " s (" + mapsAtExist + " voxel maps made by then)");
            lock (Events) Events.Clear();
            _clock = watch;
            _watchedVoxel = asteroid.EntityId;
            Hook();
            var snapshots = new List<string>();
            void Snap(string when) => snapshots.Add(when + ": " + FakeClients.Arrivals(new[] { (VRage.Game.Entity.MyEntity)asteroid }).Trim());

            // before any drilling: is the asteroid given to the client (SentisOptimisations' AsteroidNearReplication)?
            var early = new List<string>();
            string seen = null;
            var waitEarly = Stopwatch.StartNew();
            while (waitEarly.Elapsed.TotalSeconds < 20)
            {
                var arrival = Arrival(asteroid);
                if (arrival != seen)
                {
                    early.Add(watch.Elapsed.TotalSeconds.ToString("F1") + "s " + arrival);
                    seen = arrival;
                }
                if (arrival == "ready") break;
                yield return null;
            }
            Note("BEFORE DRILLING | the asteroid for the client: " + string.Join(" -> ", early) + " | " + NearCounters());

            // the drill put on its surface, as the player holds it where the client shows the rock, and switched on
            var cutAt = -1.0;
            MyVoxelBase.StorageChanged changed = (v, min, max, data) =>
            {
                if (cutAt < 0) cutAt = watch.Elapsed.TotalSeconds;
            };
            asteroid.RangeChanged += changed;
            try
            {
                var surface = Surface(arrive, towards, radius * 2 + 120);
                Check(surface.HasValue, "no rock of the asteroid along the drill's line");
                PlaceDrillOn(ship, drill, surface.Value, towards);
                Snap("before the drill " + watch.Elapsed.TotalSeconds.ToString("F2") + "s");
                drill.Enabled = true;
                yield return WaitForTicks(10);
                var state = "drill working " + drill.IsWorking + ", functional " + drill.IsFunctional + ", powered " + drill.ResourceSink?.IsPoweredByType(MyResourceDistributorComponent.ElectricityId) +
                            ", blocks " + ship.BlocksCount + ", rock " + (Surface(drill.PositionComp.WorldAABB.Center - towards * 30, towards, 60) is Vector3D r
                                ? (Vector3D.Dot(r - drill.PositionComp.WorldAABB.Center, towards)).ToString("F1") + " m ahead of the drill's centre" : "not found");
                Note(state);
                var on = watch.Elapsed.TotalSeconds;
                while (watch.Elapsed.TotalSeconds - on < WaitCutSeconds && cutAt < 0)
                {
                    HoldOn(ship, towards);
                    yield return null;
                }
                var hold = Stopwatch.StartNew();
                var snapAt = new[] { 0.5, 2, 5 };
                var next = 0;
                while (hold.Elapsed.TotalSeconds < 5.2)
                {
                    HoldOn(ship, towards);
                    if (cutAt >= 0 && next < snapAt.Length && hold.Elapsed.TotalSeconds >= snapAt[next]) Snap("cut+" + snapAt[next++] + "s");
                    yield return null;
                }
                // until the fake client has the asteroid: when it is put in its replicables (pending) and fully replicated
                var states = new List<string>();
                string last = null;
                var follow = Stopwatch.StartNew();
                while (follow.Elapsed.TotalSeconds < 60)
                {
                    var arrival = Arrival(asteroid);
                    if (arrival != last)
                    {
                        states.Add(watch.Elapsed.TotalSeconds.ToString("F1") + "s " + arrival + " (voxel maps for the client: " + VoxelCounts() + ")");
                        last = arrival;
                    }
                    if (arrival == "ready") break;
                    yield return null;
                }
                Note("ASTEROID REACHES THE CLIENT | " + string.Join(" -> ", states));
                string events;
                lock (Events) events = string.Join("; ", Events.Take(30));
                Note("REPLICATION | cut at " + (cutAt < 0 ? "never" : cutAt.ToString("F2") + "s") + " | " + events + " | " + string.Join(" | ", snapshots));
                var ore = (double)(drill.GetInventory(0)?.CurrentMass ?? 0);
                Note("ASTEROID DRILL RESULT | asteroid on the server " + exist.ToString("F2") + " s after the player came (" + mapsAtExist +
                     " voxel maps made by then, " + (queued < 0 ? "vanilla generation" : queued + " queued at arrival") + ") | the drill's first cut " +
                     (cutAt < 0 ? "never in " + WaitCutSeconds + " s" : (cutAt - on).ToString("F2") + " s after it was put on the rock") +
                     " | " + ore.ToString("F0") + " kg of ore 5 s later | " + state);
            }
            finally
            {
                asteroid.RangeChanged -= changed;
            }
        }

        /// <summary>SentisOptimisations' AsteroidNearReplication counters, or that it is not there.</summary>
        private static string NearCounters()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisationsPlugin.AsteroidNearReplication")).FirstOrDefault(t => t != null);
            if (type == null) return "no AsteroidNearReplication";
            long Get(string name) => (long)(type.GetField(name)?.GetValue(null) ?? -1L);
            return "AsteroidNearReplication: given near " + Get("GivenNear") + ", boxes kept " + Get("BoxesKept");
        }

        /// <summary>"missing", "pending" or "ready": the asteroid in the fake client's replicables on the server.</summary>
        private static string Arrival(VRage.Game.Entity.MyEntity entity)
        {
            var text = FakeClients.Arrivals(new[] { entity });
            return text.Contains("ready 1") ? "ready" : text.Contains("pending 1") ? "pending" : "missing";
        }

        /// <summary>The voxel maps made since the player came: how many the client has, waits for, lacks.</summary>
        private static string VoxelCounts()
        {
            var maps = MySession.Static.VoxelMaps.Instances.Where(v => !(v is MyPlanet)).Cast<VRage.Game.Entity.MyEntity>().ToList();
            int ready = 0, pending = 0;
            foreach (var m in maps)
            {
                var state = Arrival(m);
                if (state == "ready") ready++;
                else if (state == "pending") pending++;
            }
            return ready + " ready, " + pending + " pending, " + (maps.Count - ready - pending) + " not yet";
        }

        /// <summary>The asteroids SentisOptimisations' AsteroidGenerationBudget has queued, or -1 when it is not there.</summary>
        private static int QueuedAsteroids()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => (a.GetType("SentisOptimisationsPlugin.AsteroidGenerationBudget") ?? a.GetType("Optimizer.Optimizations.AsteroidGenerationBudget"))).FirstOrDefault(t => t != null);
            var queue = type?.GetField("Queue", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null) as ICollection;
            return queue?.Count ?? -1;
        }

        private MyCubeGrid SpawnDrillShip(Vector3D at, Vector3D forward)
        {
            // the drill points at the grid's forward (-Z); the battery right behind it, by the blocks' real sizes
            var drillSize = Sandbox.Definitions.MyDefinitionManager.Static.GetCubeBlockDefinition(
                new MyDefinitionId(typeof(Sandbox.Common.ObjectBuilders.MyObjectBuilder_Drill), "LargeBlockDrill")).Size;
            var blocks = new List<BlockSpec>
            {
                new BlockSpec("LargeBlockDrill", new Vector3I(0, 0, 0)),
                new BlockSpec("LargeBlockBatteryBlock", new Vector3I(0, 0, drillSize.Z)),
            };
            var up = Vector3D.CalculatePerpendicularVector(forward);
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "ship", MyCubeSize.Large, false, at, blocks, (Vector3)forward, (Vector3)up);
            var grid = WorldApi.SpawnGrid(ob);
            WorldApi.ChargeBatteries(grid);
            foreach (var d in WorldApi.FindFunctionals<MyShipDrill>(grid)) d.Enabled = false;
            return grid;
        }

        /// <summary>The first voxel along the line, from outside the asteroid inwards.</summary>
        private static Vector3D? Surface(Vector3D from, Vector3D dir, double length)
        {
            var hits = new List<MyPhysics.HitInfo>();
            MyPhysics.CastRay(from, from + dir * length, hits, MyPhysics.CollisionLayers.CollideWithStaticLayer);
            foreach (var hit in hits)
                if (hit.HkHitInfo.GetHitEntity() is MyVoxelBase || hit.HkHitInfo.GetHitEntity()?.GetType().Name.Contains("Voxel") == true)
                    return hit.Position;
            return null;
        }

        /// <summary>The ship moved so that the drill's head is a metre into the rock.</summary>
        private static void PlaceDrillOn(MyCubeGrid ship, MyShipDrill drill, Vector3D surface, Vector3D forward)
        {
            var head = drill.PositionComp.WorldAABB.Center + forward * (drill.PositionComp.LocalAABB.HalfExtents.Z);
            var shift = surface + forward * 1.0 - head;
            var m = ship.WorldMatrix;
            m.Translation += shift;
            ship.Teleport(m);
            if (ship.Physics != null) ship.Physics.LinearVelocity = Vector3.Zero;
        }

        /// <summary>Pressed gently against the rock, as a pilot holds it there.</summary>
        private static void HoldOn(MyCubeGrid ship, Vector3D forward)
        {
            if (ship.Physics == null) return;
            ship.Physics.LinearVelocity = (Vector3)(forward * 0.5);
            ship.Physics.AngularVelocity = Vector3.Zero;
        }

        public override void Cleanup()
        {
            try
            {
                Unhook();
                if (FakeClients.Count > 0) MySession.Static?.RemoteAdminSettings.Remove(FakeClients.PlayerOf(0).Id.SteamId);
                FakeClients.RemoveAll();
            }
            catch { }
            finally { base.Cleanup(); }
        }
    }
}
