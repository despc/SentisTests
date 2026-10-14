using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NLog;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRageMath;

namespace SentisTests.Core
{
    /// <summary>
    /// Catches grids going through a planet as it happens, whatever runs: every half a second every moving grid is
    /// looked at, and one that is under the ground and still sinking gets one "FALL RECORD |" line with what its body,
    /// the planet's physics under it and the freezer had at that moment, and how long ago the freezer let it go.
    /// Hundreds of grids test runs had left on the stand went through the planet at a time when freezer_stress ran
    /// (09-10.10.2026), and no scenario made of fresh grids would do it.
    /// </summary>
    internal static class FallRecorder
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const int EveryTicks = 30;
        private const double MinSinkMps = 8;
        private const double MinDepthM = 3;
        private const double MaxDepthM = 400;
        private const int MaxPerMinute = 40;

        private sealed class Seen
        {
            public ulong LastFrozen, LastPhysicsFrozen, FirstSeen;
            public bool Reported;
        }

        private static readonly Dictionary<long, Seen> Grids = new Dictionary<long, Seen>();
        /// <summary>Grids recorded falling, looked at again a second and three seconds later.</summary>
        private static readonly List<(MyCubeGrid Grid, ulong At, int Step)> FollowUps = new List<(MyCubeGrid, ulong, int)>();
        private static ulong _tick;
        private static DateTime _minute;
        private static int _inMinute;

        private static object _frozen, _physicsFrozen;
        private static MethodInfo _contains;
        private static bool _bound;
        private static readonly FieldInfo Shapes = typeof(MyPlanet).GetField("m_physicsShapes", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Tick()
        {
            if (++_tick % EveryTicks != 0 || MySession.Static == null || MySandboxGame.Static == null) return;
            Bind();
            var frame = MySandboxGame.Static.SimulationFrameCounter;
            FollowUp(frame);
            var live = new HashSet<long>();
            foreach (var entity in MyEntities.GetEntities())
            {
                if (!(entity is MyCubeGrid grid) || grid.Closed || grid.MarkedForClose || grid.Physics == null) continue;
                live.Add(grid.EntityId);
                if (!Grids.TryGetValue(grid.EntityId, out var seen)) Grids[grid.EntityId] = seen = new Seen { FirstSeen = frame };
                if (Contains(_frozen, grid.EntityId)) seen.LastFrozen = frame;
                if (Contains(_physicsFrozen, grid.EntityId)) seen.LastPhysicsFrozen = frame;
                if (seen.Reported || grid.IsStatic) continue;
                var velocity = grid.Physics.LinearVelocity;
                if (velocity.Length() < MinSinkMps) continue;
                var at = grid.PositionComp.WorldAABB.Center;
                var planet = MyGamePruningStructure.GetClosestPlanet(at);
                if (planet == null) continue;
                var centre = planet.PositionComp.GetPosition();
                var down = Vector3D.Normalize(centre - at);
                if (Vector3D.Dot(velocity, down) < MinSinkMps * 0.7) continue;
                var depth = (planet.GetClosestSurfacePointGlobal(ref at) - centre).Length() - (at - centre).Length();
                if (depth < MinDepthM || depth > MaxDepthM) continue;
                seen.Reported = true;
                if (DateTime.UtcNow - _minute > TimeSpan.FromMinutes(1)) { _minute = DateTime.UtcNow; _inMinute = 0; }
                if (++_inMinute > MaxPerMinute) continue;
                FollowUps.Add((grid, frame, 0));
                try { Log.Warn("FALL RECORD | " + Describe(grid, planet, at, depth, frame, seen)); }
                catch (Exception e) { Log.Warn("FALL RECORD | " + grid.DisplayName + ": " + e.Message); }
            }
            if (Grids.Count > live.Count + 1000)
                foreach (var id in Grids.Keys.Where(id => !live.Contains(id)).ToList()) Grids.Remove(id);
        }

        private static string Describe(MyCubeGrid grid, MyPlanet planet, Vector3D at, double depth, ulong frame, Seen seen)
        {
            var physics = grid.Physics;
            var body = physics.RigidBody;
            var world = physics.HavokWorld;
            var sector = Sector(planet, at);
            var sectorBody = sector?.Physics as MyPhysicsBody;
            var sectorWorld = sectorBody?.HavokWorld;
            var nearest = MySession.Static.Players.GetOnlinePlayers()
                .Select(p => p.Character).Where(c => c != null && !c.IsDead)
                .Select(c => Vector3D.Distance(c.PositionComp.GetPosition(), at)).DefaultIfEmpty(double.MaxValue).Min();
            string Ago(ulong f) => f == 0 ? "never" : f >= frame - EveryTicks ? "now" : ((frame - f) / 60.0).ToString("F0") + " s ago";
            var group = MyCubeGridGroups.Static.Physical.GetGroup(grid);
            return grid.DisplayName + " " + grid.EntityId + " " + grid.GridSizeEnum + " " + grid.BlocksCount + " blocks | " +
                   depth.ToString("F0") + " m under the ground, " + physics.LinearVelocity.Length().ToString("F1") + " m/s | at " + Vector3D.Round(at, 0) +
                   " | body: " + (body == null ? "none" : body.GetMotionType() + " layer " + body.Layer + (body.InWorld ? "" : " NOT IN WORLD") +
                                                         (body.IsActive ? " active" : " asleep") + " quality " + body.Quality) +
                   ", physics static " + physics.IsStatic + ", world " + Id(world) +
                   " | pruning " + (grid.StaticForPruningStructure ? "STATIC tree" : "dynamic tree") + " proxy " + grid.TopMostPruningProxyId +
                   " | planet sectors " + SectorCount(planet) + ", here " + SectorState(planet, at) +
                   " | " + PlanetQuery(planet, grid) + " | " + RealGround(planet, at) +
                   " | ground sector: " + (sector == null ? "none" : "world " + Id(sectorWorld) + (sectorWorld == world ? " (same)" : " (OTHER)") +
                                                                    (sectorBody?.RigidBody?.InWorld == true ? "" : " body not in world")) +
                   " | physics worlds " + MyPhysics.Clusters.GetList().Count +
                   " | frozen " + Ago(seen.LastFrozen) + ", physics frozen " + Ago(seen.LastPhysicsFrozen) +
                   ", known for " + ((frame - seen.FirstSeen) / 60.0).ToString("F0") + " s" +
                   " | nearest character " + (nearest == double.MaxValue ? "none" : nearest.ToString("F0") + " m") +
                   " | physical group " + (group?.Nodes.Count ?? 1) + " grids" +
                   " | presence tier " + grid.PlayerPresenceTier + ", seconds since start " + (frame / 60);
        }

        private static void FollowUp(ulong frame)
        {
            for (var i = FollowUps.Count - 1; i >= 0; i--)
            {
                var (grid, at, step) = FollowUps[i];
                var due = at + (ulong)(step == 0 ? 60 : 180);
                if (frame < due) continue;
                FollowUps.RemoveAt(i);
                if (step == 0) FollowUps.Add((grid, at, 1));
                try
                {
                    if (grid.Closed || grid.Physics == null) { Log.Warn("FALL RECORD +" + (step == 0 ? 1 : 3) + "s | " + grid.EntityId + " closed"); continue; }
                    var p = grid.PositionComp.WorldAABB.Center;
                    var planet = MyGamePruningStructure.GetClosestPlanet(p);
                    var centre = planet.PositionComp.GetPosition();
                    var depth = (planet.GetClosestSurfacePointGlobal(ref p) - centre).Length() - (p - centre).Length();
                    Log.Warn("FALL RECORD +" + (step == 0 ? 1 : 3) + "s | " + grid.DisplayName + " " + grid.EntityId + " | " + depth.ToString("F0") + " m under, " +
                             grid.Physics.LinearVelocity.Length().ToString("F1") + " m/s | pruning " + (grid.StaticForPruningStructure ? "STATIC tree" : "dynamic tree") +
                             " | planet sectors " + SectorCount(planet) + ", here " + SectorState(planet, p) + " | body " + grid.Physics.RigidBody?.GetMotionType() +
                             (grid.Physics.RigidBody?.IsActive == true ? " active" : " asleep"));
                }
                catch (Exception e) { Log.Warn("FALL RECORD follow-up: " + e.Message); }
            }
        }

        /// <summary>The planet's own query for where to build (MyPlanet.UpdatePlanetPhysics), asked now: is the grid in any box of it.</summary>
        private static string PlanetQuery(MyPlanet planet, MyCubeGrid grid)
        {
            var box = planet.PositionComp.WorldAABB;
            box.Min -= 1024.0;
            box.Max += 1024.0;
            var clusters = new List<BoundingBoxD>();
            MyGamePruningStructure.GetAproximateDynamicClustersForSize(ref box, 512.0, clusters);
            var mine = grid.PositionComp.WorldAABB;
            var hit = clusters.Where(c => c.Intersects(mine)).ToList();
            return "planet " + planet.StorageName + " " + planet.EntityId + " updates " + planet.NeedsUpdate +
                   ", its query " + clusters.Count + " boxes, grid in " + hit.Count +
                   (hit.Count > 0 ? " (size " + hit[0].Size.Max().ToString("F0") + " m)" : "") +
                   ", pruning box " + MyGamePruningStructure.GetEntityAABB(grid).Size.Max().ToString("F0") + " m";
        }

        /// <summary>
        /// The real ground over a point, by a ray down from 400 m over the planet's height there: how deep under the
        /// planet's height (which knows no craters nor digging) the first voxel the ray meets is, or "no voxel hit".
        /// </summary>
        public static string RealGround(MyPlanet planet, Vector3D at)
        {
            var centre = planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(at - centre);
            var surface = planet.GetClosestSurfacePointGlobal(ref at);
            var surfaceR = (surface - centre).Length();
            var from = centre + up * (surfaceR + 400);
            var to = at - up * 2;
            var hits = new List<MyPhysics.HitInfo>();
            MyPhysics.CastRay(from, to, hits, MyPhysics.CollisionLayers.CollideWithStaticLayer);
            foreach (var hit in hits)
            {
                var entity = hit.HkHitInfo.GetHitEntity();
                if (!(entity is VRage.Game.Entity.MyEntity e) || !(e is MyVoxelBase || e.GetType().Name.Contains("Voxel"))) continue;
                var depth = surfaceR - (hit.Position - centre).Length();
                return "real ground " + depth.ToString("F0") + " m under the planet's height, the grid " + (surfaceR - (at - centre).Length() - depth).ToString("F0") + " m under it";
            }
            return "no voxel hit over it (" + hits.Count + " hits)";
        }

        private static int SectorCount(MyPlanet planet)
        {
            var shapes = Shapes?.GetValue(planet);
            return shapes == null ? 0 : (int)shapes.GetType().GetProperty("Count").GetValue(shapes);
        }

        /// <summary>"none" (no entry), "empty" (an entry the planet left without physics: no ground in its box), or the sector's state.</summary>
        private static string SectorState(MyPlanet planet, Vector3D at)
        {
            var shapes = Shapes?.GetValue(planet);
            if (shapes == null) return "no sectors at all";
            VRage.Voxels.MyVoxelCoordSystems.WorldPositionToVoxelCoord(planet.PositionLeftBottomCorner, ref at, out var voxel);
            var args = new object[] { voxel / 1024, null };
            if (!(bool)shapes.GetType().GetMethod("TryGetValue").Invoke(shapes, args)) return "none (key " + (voxel / 1024) + ")";
            if (!(args[1] is VRage.Game.Entity.MyEntity sector)) return "empty entry";
            var body = sector.Physics as MyPhysicsBody;
            return "sector " + (sector.Closed ? "closed" : "open") + ", body " + (body?.RigidBody == null ? "none" : body.RigidBody.InWorld ? "in world " + Id(body.HavokWorld) : "NOT in world");
        }

        private static string Id(object o) => o == null ? "none" : RuntimeHelpers.GetHashCode(o).ToString("x");

        private static VRage.Game.Entity.MyEntity Sector(MyPlanet planet, Vector3D at)
        {
            var shapes = Shapes?.GetValue(planet);
            if (shapes == null) return null;
            VRage.Voxels.MyVoxelCoordSystems.WorldPositionToVoxelCoord(planet.PositionLeftBottomCorner, ref at, out var voxel);
            var args = new object[] { voxel / 1024, null };
            return (bool)shapes.GetType().GetMethod("TryGetValue").Invoke(shapes, args) ? args[1] as VRage.Game.Entity.MyEntity : null;
        }

        private static void Bind()
        {
            if (_bound) return;
            _bound = true;
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("SentisOptimisationsPlugin.Freezer.FreezeLogic")).FirstOrDefault(t => t != null);
            if (type == null) return;
            _frozen = type.GetField("FrozenGrids", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            _physicsFrozen = type.GetField("FrozenPhysicsGrids", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            _contains = _frozen?.GetType().GetMethod("Contains");
        }

        private static bool Contains(object set, long id)
        {
            if (set == null || _contains == null) return false;
            try { return (bool)_contains.Invoke(set, new object[] { id }); }
            catch { return false; }
        }
    }
}
