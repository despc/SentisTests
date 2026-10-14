using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Grids left lying on a planet fall through it when the freezer lets them go. On the stand hundreds of small grids
    /// test runs had left behind (lone blocks, wheels, pieces of split grids) went through the ground in bursts - 274,
    /// 187, 178 at a time (09.10.2026) - each time a scenario switched the freezer off or its stress run ended, and
    /// fell to the planet's centre at 104 m/s.
    ///
    /// Here: <see cref="Junk"/> small grids are dropped onto the ground with a player by them and settle; the player goes
    /// <see cref="AwayM"/> off (beyond the freeze distance and the planet's physics sectors about it, in the same cluster of the physics, which stays stepped)
    /// and the freezer freezes them; after <see cref="FrozenSeconds"/> the freezer is switched off, the way freeze_production
    /// does, and every grid is watched for <see cref="WatchSeconds"/>: none may end up more than <see cref="SinkM"/> under
    /// where it lay.
    /// </summary>
    public sealed class ThawFallScenario : TestScenario
    {
        public const string ScenarioName = "thaw_fall";
        public const string FarScenarioName = "thaw_fall_far";
        public const string NoSectorScenarioName = "thaw_fall_nosector";
        public const string ToggleScenarioName = "thaw_fall_toggle";
        /// <summary>The player leaves for <see cref="FarM"/> while they are frozen - out of their cluster of the physics - and comes back after the thaw.</summary>
        private const double FarM = 15000;
        private const string Prefix = "thawfall-";
        private const int Junk = 24;
        private const double SpreadM = 120;
        private const int FreezeDistanceM = 300;
        private const double AwayM = 3000;
        private const double SettleSeconds = 15;
        private const double FreezeWaitSeconds = 90;
        private const double FrozenSeconds = 40;
        private const double WatchSeconds = 40;
        private const double SinkM = 5;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<ulong> _protected = new List<ulong>();

        private readonly bool _far;
        private readonly bool _noSector;
        private readonly bool _toggle;

        /// <param name="far">the player leaves while they are frozen, out of their cluster of the physics</param>
        /// <param name="noSector">
        /// the planet's physics under them is taken away while they are frozen, as a world loaded with them frozen
        /// has it: the planet makes it anew only when they are let go, with them already lying in it
        /// </param>
        /// <param name="toggle">
        /// frozen with FreezePhysics off (the stand's own setting), and FreezePhysics switched on only once they are
        /// frozen - the way freezer_stress finds the grids test runs left behind: their bodies made fixed by
        /// FreezeLogic.UpdateFreezePhysics, a few groups a frame
        /// </param>
        public ThawFallScenario(bool far = false, bool noSector = false, bool toggle = false) { _far = far; _noSector = noSector; _toggle = toggle; }

        public override string Name => _far ? FarScenarioName : _noSector ? NoSectorScenarioName : _toggle ? ToggleScenarioName : ScenarioName;
        public override int TimeoutSeconds => 600;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);
            _config.Set("FreezerEnabled", false);
            _config.Set("DelayBeforeFreezeSec", 5);
            _config.Set("FreezeDistanceStatic", FreezeDistanceM);
            _config.Set("FreezeDistanceDynamic", FreezeDistanceM);
            // as freezer_stress has it: the frozen ones get fixed bodies
            _config.Set("FreezePhysics", !_toggle);

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchor - centre);
            var east = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var north = Vector3D.Cross(up, east);
            // a patch of its own, away from the other scenarios' sites
            var site = planet.GetClosestSurfacePointGlobal(anchor + east * 2500 + north * 2500);

            Player(site + up * 5);
            var grids = new List<MyCubeGrid>();
            var random = new Random(4711);
            for (var i = 0; i < Junk; i++)
            {
                var at = site + east * ((random.NextDouble() * 2 - 1) * SpreadM) + north * ((random.NextDouble() * 2 - 1) * SpreadM);
                var ground = planet.GetClosestSurfacePointGlobal(ref at);
                var upAt = Vector3D.Normalize(ground - centre);
                var large = i % 3 != 0;
                var plate = i % 4 == 0 ? 3 : 1;
                var blocks = new List<MyObjectBuilder_CubeBlock>();
                for (var x = 0; x < plate; x++)
                for (var z = 0; z < plate; z++)
                {
                    var block = WorldApi.MakeBlockOb(large ? "LargeBlockArmorBlock" : "SmallBlockArmorBlock");
                    block.Min = new SerializableVector3I(x, 0, z);
                    blocks.Add(block);
                }
                var forward = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(upAt));
                var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
                {
                    Name = WorldApi.EntityPrefix + Prefix + i,
                    DisplayName = WorldApi.EntityPrefix + Prefix + i,
                    GridSizeEnum = large ? MyCubeSize.Large : MyCubeSize.Small,
                    IsStatic = false,
                    PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(ground + upAt * (large ? 4 : 2), forward, upAt)),
                    PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                    CubeBlocks = blocks,
                });
                Track(grid);
                grids.Add(grid);
                yield return null;
            }
            var settle = WaitForSeconds(SettleSeconds, "the grids settle on the ground");
            while (settle.MoveNext()) yield return settle.Current;
            double Over(MyCubeGrid g)
            {
                var p = g.PositionComp.WorldAABB.Center;
                return (p - centre).Length() - (planet.GetClosestSurfacePointGlobal(ref p) - centre).Length();
            }
            var lying = grids.Select(Over).ToList();
            Note("the grids lie " + string.Join("/", lying.Select(h => h.ToString("F1"))) + " m over the ground");

            // the player goes away: beyond the freeze distance, in the same cluster of the physics
            FakeClients.RemoveAll();
            Player(site + up * 5 + east * AwayM);
            _config.Set("FreezerEnabled", true);
            if (_far)
            {
                // frozen first with the player near enough to step their physics, then it goes
                var near = DateTime.UtcNow;
                while ((DateTime.UtcNow - near).TotalSeconds < FreezeWaitSeconds && grids.Any(g => !g.Closed && !RuntimePluginControls.IsGridFrozen(g.EntityId)))
                    yield return null;
                FakeClients.RemoveAll();
                Player(site + up * 5 + east * FarM);
            }
            var frozenAt = DateTime.UtcNow;
            while ((DateTime.UtcNow - frozenAt).TotalSeconds < FreezeWaitSeconds && grids.Any(g => !g.Closed && !RuntimePluginControls.IsGridFrozen(g.EntityId)))
                yield return null;
            var frozen = grids.Count(g => RuntimePluginControls.IsGridFrozen(g.EntityId));
            var physicsFrozen = grids.Count(FreezerState.IsPhysicsFrozen);
            Note("the planet's physics sector under them when frozen: " + (HasSector(planet, site) ? "there" : "gone"));
            Note(frozen + " of " + grids.Count + " frozen (" + physicsFrozen + " with fixed bodies) after " + (DateTime.UtcNow - frozenAt).TotalSeconds.ToString("F0") + " s");
            Check(frozen > 0, "the freezer froze none of them");
            if (_toggle)
            {
                var logic = WaitForSeconds(10, "frozen, FreezePhysics off");
                while (logic.MoveNext()) yield return logic.Current;
                _config.Set("FreezePhysics", true);
                yield return WaitForTicks(30);
                physicsFrozen = grids.Count(FreezerState.IsPhysicsFrozen);
                Note("FreezePhysics on: " + physicsFrozen + " of them with fixed bodies");
            }
            var hold = WaitForSeconds(FrozenSeconds, "frozen");
            while (hold.MoveNext()) yield return hold.Current;

            if (_noSector)
            {
                Note("the planet's physics sectors about them taken away: " + DropSectors(planet, site, SpreadM + 100));
                yield return WaitForTicks(2);
            }
            Note("the planet's physics sector under them before the thaw: " + (HasSector(planet, site) ? "there" : "gone"));

            Note("their worlds of the physics when frozen: " + Worlds(planet, site, grids));

            // the freezer off: every one of them is let go at once
            _config.Set("FreezerEnabled", false);
            if (_far)
            {
                var off = WaitForSeconds(5, "thawed with nobody near");
                while (off.MoveNext()) yield return off.Current;
                Note("their worlds of the physics thawed, nobody near: " + Worlds(planet, site, grids));
                FakeClients.RemoveAll();
                Player(site + up * 5);
                var back = WaitForSeconds(3, "the player is back");
                while (back.MoveNext()) yield return back.Current;
                Note("their worlds of the physics with the player back: " + Worlds(planet, site, grids));
            }
            var deepest = new double[grids.Count];
            var fastest = new double[grids.Count];
            for (var i = 0; i < grids.Count; i++) deepest[i] = double.MaxValue;
            var watch = DateTime.UtcNow;
            while ((DateTime.UtcNow - watch).TotalSeconds < WatchSeconds)
            {
                for (var i = 0; i < grids.Count; i++)
                {
                    var g = grids[i];
                    if (g.Closed) continue;
                    deepest[i] = Math.Min(deepest[i], Over(g));
                    fastest[i] = Math.Max(fastest[i], g.Physics?.LinearVelocity.Length() ?? 0);
                }
                yield return null;
            }
            Note("their worlds of the physics at the end: " + Worlds(planet, site, grids));
            var fell = Enumerable.Range(0, grids.Count).Where(i => deepest[i] < lying[i] - SinkM).ToList();
            Note("THAW FALL RESULT | " + grids.Count + " grids, " + frozen + " frozen, " + physicsFrozen + " fixed, thawed by the freezer off: fell " + fell.Count + " | " +
                 string.Join("; ", Enumerable.Range(0, grids.Count).Select(i => grids[i].DisplayName.Substring(grids[i].DisplayName.LastIndexOf('-') + 1) + " lay " +
                     lying[i].ToString("F1") + " deepest " + (deepest[i] == double.MaxValue ? "?" : deepest[i].ToString("F1")) + " fastest " + fastest[i].ToString("F1"))));
            Check(fell.Count == 0, fell.Count + " grids fell through the ground after the thaw");
        }

        /// <summary>
        /// Which world of the physics the grids' bodies are in, against the world of the planet's sector under them:
        /// "same", "other" (a grid in a world the ground is not in), "none" (a body out of every world).
        /// </summary>
        private static string Worlds(MyPlanet planet, Vector3D site, List<MyCubeGrid> grids)
        {
            var sector = Sector(planet, site);
            var ground = (sector?.Physics as Sandbox.Engine.Physics.MyPhysicsBody)?.HavokWorld;
            var counts = new Dictionary<string, int>();
            foreach (var g in grids)
            {
                if (g.Closed) continue;
                var world = g.Physics?.HavokWorld;
                var what = world == null || g.Physics?.RigidBody?.InWorld != true ? "none" : ground == null ? "no ground" : world == ground ? "same" : "other";
                if (g.Physics?.RigidBody?.IsFixed == true) what += "/fixed";
                counts[what] = counts.TryGetValue(what, out var n) ? n + 1 : 1;
            }
            return "sector " + (sector == null ? "gone" : ground == null ? "without a world" : "in world " + ground.GetHashCode()) + ", clusters " +
                   Sandbox.Engine.Physics.MyPhysics.Clusters.GetList().Count + ": " + string.Join(", ", counts.Select(p => p.Key + " " + p.Value));
        }

        private static VRage.Game.Entity.MyEntity Sector(MyPlanet planet, Vector3D at)
        {
            // a MyConcurrentDictionary<Vector3I, MyVoxelPhysics>: not an IDictionary, its TryGetValue by reflection
            var shapes = typeof(MyPlanet).GetField("m_physicsShapes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(planet);
            if (shapes == null) return null;
            VRage.Voxels.MyVoxelCoordSystems.WorldPositionToVoxelCoord(planet.PositionLeftBottomCorner, ref at, out var voxel);
            var args = new object[] { voxel / 1024, null };
            return (bool)shapes.GetType().GetMethod("TryGetValue").Invoke(shapes, args) ? args[1] as VRage.Game.Entity.MyEntity : null;
        }

        /// <summary>Closes the planet's physics sectors within <paramref name="radius"/> of <paramref name="at"/>, the way MyPlanet.UpdateAfterSimulation100 drops them.</summary>
        private static int DropSectors(MyPlanet planet, Vector3D at, double radius)
        {
            var shapes = typeof(MyPlanet).GetField("m_physicsShapes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(planet);
            if (shapes == null) return 0;
            var box = new BoundingBoxD(at - radius, at + radius);
            VRage.Voxels.MyVoxelCoordSystems.WorldPositionToVoxelCoord(planet.PositionLeftBottomCorner, ref box.Min, out var min);
            VRage.Voxels.MyVoxelCoordSystems.WorldPositionToVoxelCoord(planet.PositionLeftBottomCorner, ref box.Max, out var max);
            min /= 1024;
            max /= 1024;
            var tryGet = shapes.GetType().GetMethod("TryGetValue");
            var remove = shapes.GetType().GetMethod("Remove", new[] { typeof(Vector3I) });
            var dropped = 0;
            for (var it = new Vector3I_RangeIterator(ref min, ref max); it.IsValid(); it.MoveNext())
            {
                var args = new object[] { it.Current, null };
                if (!(bool)tryGet.Invoke(shapes, args)) continue;
                (args[1] as VRage.Game.Entity.MyEntity)?.Close();
                remove.Invoke(shapes, new object[] { it.Current });
                dropped++;
            }
            return dropped;
        }

        /// <summary>Whether the planet has the physics of the 1024 m sector at <paramref name="at"/> (MyPlanet.m_physicsShapes).</summary>
        private static bool HasSector(MyPlanet planet, Vector3D at) => Sector(planet, at) != null;

        private void Player(Vector3D at)
        {
            FakeClients.Add(1, Network, p => (at, 0, 0), withCharacters: true);
            var steamId = FakeClients.PlayerOf(0).Id.SteamId;
            _protected.Add(steamId);
            Sandbox.Game.World.MySession.Static.RemoteAdminSettings[steamId] =
                Sandbox.Game.World.AdminSettingsEnum.Invulnerable | Sandbox.Game.World.AdminSettingsEnum.Untargetable;
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
                foreach (var steamId in _protected) Sandbox.Game.World.MySession.Static?.RemoteAdminSettings.Remove(steamId);
                _protected.Clear();
            }
            finally { base.Cleanup(); }
        }
    }
}
