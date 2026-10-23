using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A player's rover left on a planet - on a station's safe zone too - frozen by the freezer with its physics and woken
    /// for the compensation of its production (prod, 23.10.2026: a rover with a refinery by an NPC station, frozen when
    /// its owner left, was thrown far off and wrecked at a wake-up ten minutes later). Copies of the world's grid
    /// <see cref="SourceName"/> with its subgrids are set down at <see cref="Copies"/> places of its planet, some in the
    /// safe zones of stations there, each standing on the ground the way the original stands. Fake players by each let them
    /// settle; they go, and the freezer (physics freeze on, wake-ups every <see cref="WakeInterval"/> s) freezes the copies
    /// and wakes them for compensation with nobody near, for <see cref="WatchSeconds"/> s; then the players come back and
    /// thaw them once more. Every second each copy's grids are compared with where they settled: a grid faster than
    /// <see cref="MaxSpeed"/> or moved more than <see cref="MaxShift"/> m is a throw, noted with what the freezer had of the
    /// copy then (frozen, physics frozen, its Havok world stepped).
    /// </summary>
    public sealed class RoverThawScenario : TestScenario
    {
        public const string ScenarioName = "rover_thaw";
        public const string SourceName = "Ровер Физика";
        private const string Prefix = "rover-thaw-";
        private const int Copies = 64;
        private const int WakeInterval = 60;
        private const int FreezeDistance = 1000;
        private const double SettleSeconds = 60;
        private const double WatchSeconds = 420;
        private const double ReturnSeconds = 60;
        private const double MaxSpeed = 8;
        private const double MaxShift = 3;
        private const double StationShare = 0.5;
        /// <summary>
        /// From real players: past the sync distance (15 km on the stand), or the copies are replicated to them and their
        /// Havok world is stepped all the time - on prod a rover left alone is in a world nobody steps.
        /// </summary>
        private const double PlayerGap = 17000;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private sealed class Copy
        {
            public int Index;
            public string Where;
            public Vector3D Site;
            public List<MyCubeGrid> Grids = new List<MyCubeGrid>();
            public List<Vector3D> Settled;
            public double MaxSpeed, MaxShift;
            public string Worst;
            public int Thaws, Freezes, PhysicsFreezes;
            public bool WasFrozen;
            public List<string> Throws = new List<string>();
            /// <summary>Why the copy is left out: it did not stand settled and whole before the freezer came.</summary>
            public string Unsound;
            public bool GroundAtSettle, GroundLost;
            public int UnsteppedThaws;
        }

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<Copy> _copies = new List<Copy>();
        private readonly Dictionary<MyPlanet, PlanetGround> _grounds = new Dictionary<MyPlanet, PlanetGround>();

        /// <summary>The ground of the planet nearest the point.</summary>
        private PlanetGround G(Vector3D at)
        {
            var planet = MyGamePruningStructure.GetClosestPlanet(at);
            if (!_grounds.TryGetValue(planet, out var ground)) _grounds[planet] = ground = new PlanetGround(planet);
            return ground;
        }

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 1200;

        public override IEnumerator Run()
        {
            var source = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !g.MarkedForClose && g.DisplayName == SourceName);
            SkipUnless(source != null, "no grid \"" + SourceName + "\" in the world");
            var group = Sandbox.Game.Entities.MyCubeGridGroups.Static.Mechanical.GetGroupNodes(source).Where(g => !g.MarkedForClose).ToList();
            var planet = MyGamePruningStructure.GetClosestPlanet(source.PositionComp.GetPosition());
            Check(planet != null, "the rover is by no planet");
            var builders = group.Select(g => (MyObjectBuilder_CubeGrid)g.GetObjectBuilder(true)).ToList();
            var origin = source.PositionComp.GetPosition();
            var originUp = G(origin).Up(origin);
            var originFrame = Frame(origin, originUp, source.WorldMatrix.Forward);
            // each copy as high over its ground as the rover stands over its own, 0.2 m more (a physics ray down from it: its
            // blocks are 7-11 m over its grid's origin, and a height from the blocks' boxes put the copies 8-9 m up in the air)
            var height = RestHeight(source, originUp) + 0.2;
            Note("source " + source.DisplayName + ": " + group.Count + " grids, " + group.Sum(g => g.BlocksCount) + " blocks, " + height.ToString("F2") + " m over the ground; production blocks " +
                 group.Sum(g => g.GetFatBlocks().Count(b => b is Sandbox.Game.Entities.Cube.MyProductionBlock)).ToString() + "; speeds " +
                 string.Join(" ", group.Select(g => (g.Physics?.LinearVelocity.Length() ?? 0).ToString("F2"))) + ", suspensions attached " + Attached(group));

            // away from every character of the world - the players' and the bots': near one, the Havok world is stepped
            var players = MyEntities.GetEntities().OfType<Sandbox.Game.Entities.Character.MyCharacter>().Where(ch => !ch.MarkedForClose && !ch.IsDead)
                .Select(ch => ch.PositionComp.GetPosition()).ToList();
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().Where(x => !x.MarkedForClose).ToList();
            var sites = new List<(Vector3D Site, string Where)>();
            foreach (var each in planets)
                sites.AddRange(Sites(each, players, sites, (Copies - sites.Count) / Math.Max(1, planets.Count - planets.IndexOf(each))));
            Note("planets: " + string.Join(", ", planets.Select(x => x.StorageName + " " + sites.Count(t => MyGamePruningStructure.GetClosestPlanet(t.Site) == x))) +
                 "; characters kept away from: " + players.Count);
            Note("sites: " + sites.Count(s => s.Where != "open") + " in station safe zones, " + sites.Count(s => s.Where == "open") + " in the open");

            // the players first: the planet makes the physics of its ground only round something dynamic, and a rover set down
            // where there is none yet sinks into the ground before it is made and is thrown out of it
            Watchers(sites.Select(x => x.Site).ToList());
            var ready = WaitForSeconds(15, "the ground's physics made round the players");
            while (ready.MoveNext()) yield return ready.Current;

            for (var i = 0; i < sites.Count; i++)
            {
                var (site, where) = sites[i];
                // every other copy on its handbrake
                var brake = i % 2 == 0;
                where += brake ? ", handbrake" : ", no handbrake";
                var up = G(site).Up(site);
                var frame = Frame((RaySurface(site, up) ?? G(site).Ground(site)) + up * height, up, Vector3D.CalculatePerpendicularVector(up));
                var copy = new Copy { Index = i, Where = where, Site = site };
                var obs = WorldApi.CopyGroup(builders.Select(b => (MyObjectBuilder_CubeGrid)MyObjectBuilderSerializer.Clone(b)).ToList(),
                    WorldApi.EntityPrefix + Prefix + i, keepOwners: true);
                foreach (var ob in obs)
                {
                    var m = ob.PositionAndOrientation.Value.GetMatrix() * MatrixD.Invert(originFrame) * frame;
                    ob.PositionAndOrientation = new MyPositionAndOrientation(m);
                    ob.LinearVelocity = Vector3.Zero;
                    ob.AngularVelocity = Vector3.Zero;
                    ob.Handbrake = brake;
                    var grid = WorldApi.SpawnGrid(ob);
                    Track(grid);
                    copy.Grids.Add(grid);
                }
                foreach (var grid in copy.Grids) WorldApi.ChargeBatteries(grid);
                _copies.Add(copy);
                if (i % 8 == 7) yield return null;
            }

            _config.Set("FreezeDistanceDynamic", FreezeDistance);
            _config.Set("FreezeDistanceStatic", FreezeDistance);
            _config.Set("MinWakeUpIntervalInSec", WakeInterval);
            _config.Set("FreezePhysics", true);
            _config.Set("FreezerEnabled", true);
            var settle = WaitForSeconds(SettleSeconds, "the copies settle with players by them");
            while (settle.MoveNext()) yield return settle.Current;
            foreach (var c in _copies) c.Settled = c.Grids.Select(g => g.PositionComp.GetPosition()).ToList();
            // a copy that did not stand whole and still by now is the spawn's, not the freezer's: left out
            var wanted = Attached(group);
            foreach (var c in _copies)
            {
                var fast = c.Grids.Where(g => !g.Closed).Select(g => g.Physics?.LinearVelocity.Length() ?? 0).DefaultIfEmpty(0).Max();
                if (c.Grids.Any(g => g.Closed || g.MarkedForClose)) c.Unsound = "a grid gone";
                else if (Attached(c.Grids) != wanted) c.Unsound = "suspensions attached " + Attached(c.Grids) + " of " + wanted;
                else if (fast > 1) c.Unsound = "still moving at " + fast.ToString("F1") + " m/s";
                else if (G(c.Site).UnderGround(c.Grids[0])) c.Unsound = "under the ground";
            }
            var sound = _copies.Where(c => c.Unsound == null).ToList();
            Note("settled: " + sound.Count + " of " + _copies.Count + " whole and still (in station zones " + sound.Count(c => c.Where != "open") + "); left out: " +
                 string.Join(", ", _copies.Where(c => c.Unsound != null).Select(c => c.Index + " " + c.Unsound)));
            _copies.RemoveAll(c => c.Unsound != null);
            Check(_copies.Count >= 10, "only " + _copies.Count + " copies settled whole");
            foreach (var c in _copies) c.GroundAtSettle = GroundPhysics(c.Grids[0]);
            Note("ground physics under the copies at the settle: " + _copies.Count(c => c.GroundAtSettle) + " of " + _copies.Count);

            // the players go: the freezer freezes the copies, and wakes them for their production with nobody near
            Watchers(null);
            var started = DateTime.UtcNow;
            var told = 0.0;
            while ((DateTime.UtcNow - started).TotalSeconds < WatchSeconds)
            {
                Sample("alone", (DateTime.UtcNow - started).TotalSeconds);
                var t = (DateTime.UtcNow - started).TotalSeconds;
                if (t - told >= 30)
                {
                    told = t;
                    Note("alone " + t.ToString("F0") + " s: frozen " + _copies.Count(c => c.Grids.All(g => g.Closed || FreezerState.IsFrozen(g))) + "/" + _copies.Count +
                         ", physics frozen " + _copies.Count(c => c.Grids.Any(g => !g.Closed && FreezerState.IsPhysicsFrozen(g))) +
                         ", thaws so far " + _copies.Sum(c => c.Thaws) + ", thrown " + _copies.Count(c => c.Throws.Count > 0) +
                         ", ground physics under " + _copies.Count(c => GroundPhysics(c.Grids[0])) + "/" + _copies.Count);
                }
                for (var k = 0; k < 60; k++) yield return null;
            }

            // the players come back: one more thaw, theirs
            Watchers(_copies.Select(c => c.Grids[0].PositionComp.GetPosition()).ToList());
            started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started).TotalSeconds < ReturnSeconds)
            {
                Sample("players back", (DateTime.UtcNow - started).TotalSeconds);
                for (var k = 0; k < 60; k++) yield return null;
            }
            FakeClients.RemoveAll();

            foreach (var kind in new[] { "open, handbrake", "open, no handbrake", "zone, handbrake", "zone, no handbrake" })
            {
                var of = _copies.Where(c => c.Where.StartsWith(kind.Split(',')[0]) && c.Where.EndsWith("," + kind.Split(',')[1])).ToList();
                Note("KIND " + kind + ": " + of.Count + " copies, thrown " + of.Count(c => c.Throws.Count > 0) + ", thaws " + of.Sum(c => c.Thaws) + " (in a world not stepped " + of.Sum(c => c.UnsteppedThaws) + ")" +
                     ", the ground's physics gone under one while frozen " + of.Count(c => c.GroundLost) + ", fastest " + (of.Count == 0 ? 0 : of.Max(c => c.MaxSpeed)).ToString("F1") + " m/s");
            }
            Note("ROVER THAW RESULT | " + _copies.Count + " copies: thaws " + _copies.Sum(c => c.Thaws) + ", freezes " + _copies.Sum(c => c.Freezes) +
                 " (with physics " + _copies.Sum(c => c.PhysicsFreezes) + "), thrown " + _copies.Count(c => c.Throws.Count > 0) +
                 " (in station zones " + _copies.Count(c => c.Throws.Count > 0 && c.Where != "open") + " of " + _copies.Count(c => c.Where != "open") + ")");
            foreach (var c in _copies.Where(c => c.Throws.Count > 0))
                Note("THROWN " + c.Index + " (" + c.Where + "): " + string.Join(" | ", c.Throws.Take(4)));
            foreach (var c in _copies.OrderByDescending(c => c.MaxSpeed).Take(8))
                Note("fastest " + c.Index + " (" + c.Where + "): " + c.MaxSpeed.ToString("F1") + " m/s, moved up to " + c.MaxShift.ToString("F1") + " m, " + c.Worst +
                     "; thaws " + c.Thaws + ", freezes " + c.Freezes + " (physics " + c.PhysicsFreezes + ")");
            Check(_copies.All(c => c.Throws.Count == 0), _copies.Count(c => c.Throws.Count > 0) + " of " + _copies.Count + " copies thrown: " +
                  string.Join(" | ", _copies.Where(c => c.Throws.Count > 0).Take(5).Select(c => c.Index + " (" + c.Where + ") " + c.Throws[0])));
        }

        /// <summary>How high the grid's origin is over the ground under it, along up, by a physics ray through its blocks' middle.</summary>
        /// <summary>Where a physics ray down through the point meets the ground (its voxel physics), if it does.</summary>
        internal static Vector3D? RaySurface(Vector3D at, Vector3D up)
        {
            var hits = new List<Sandbox.Engine.Physics.MyPhysics.HitInfo>();
            Sandbox.Engine.Physics.MyPhysics.CastRay(at + up * 60, at - up * 60, hits);
            foreach (var hit in hits.OrderBy(h => h.HkHitInfo.HitFraction))
                if (Sandbox.Engine.Physics.MyPhysicsExtensions.GetHitEntity(hit.HkHitInfo) is MyVoxelBase) return hit.Position;
            return null;
        }

        internal static double RestHeight(MyCubeGrid grid, Vector3D up)
        {
            var origin = grid.PositionComp.GetPosition();
            var middle = grid.PositionComp.WorldAABB.Center;
            var hits = new List<Sandbox.Engine.Physics.MyPhysics.HitInfo>();
            Sandbox.Engine.Physics.MyPhysics.CastRay(middle + up * 30, middle - up * 60, hits);
            foreach (var hit in hits.OrderBy(h => h.HkHitInfo.HitFraction))
                if (Sandbox.Engine.Physics.MyPhysicsExtensions.GetHitEntity(hit.HkHitInfo) is MyVoxelBase)
                    return Vector3D.Dot(origin - hit.Position, up);
            throw new ScenarioFailedException("no ground under the rover " + grid.DisplayName);
        }

        private static int Attached(IEnumerable<MyCubeGrid> grids) =>
            grids.Where(g => !g.Closed).SelectMany(g => g.GetFatBlocks()).OfType<Sandbox.Game.Entities.Cube.MyMotorSuspension>().Count(s => s.TopBlock != null);

        private static MatrixD Frame(Vector3D at, Vector3D up, Vector3D forward)
        {
            var flat = forward - up * Vector3D.Dot(forward, up);
            if (flat.LengthSquared() < 1e-6) flat = Vector3D.CalculatePerpendicularVector(up);
            return MatrixD.CreateWorld(at, Vector3D.Normalize(flat), up);
        }

        /// <summary>A fake player 25 m from each of the places, or none.</summary>
        private void Watchers(List<Vector3D> at)
        {
            FakeClients.RemoveAll();
            if (at == null) return;
            FakeClients.Add(at.Count, Network, i =>
            {
                var up = G(at[i]).Up(at[i]);
                var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
                return (G(at[i]).Ground(at[i] + side * 25) + up * 2, 0, 0);
            }, withCharacters: true);
        }

        private static FieldInfo _shapes;

        /// <summary>Whether the planet has the physics of its ground (a MyVoxelPhysics sector of it) where the grid is.</summary>
        private bool GroundPhysics(MyCubeGrid grid)
        {
            _shapes = _shapes ?? typeof(MyPlanet).GetField("m_physicsShapes", BindingFlags.Instance | BindingFlags.NonPublic);
            var at = grid.PositionComp.GetPosition();
            if (!(_shapes?.GetValue(G(at).Planet) is System.Collections.IEnumerable shapes)) return false;
            foreach (var pair in shapes)
            {
                var value = pair.GetType().GetProperty("Value")?.GetValue(pair) as MyVoxelBase;
                if (value != null && !value.Closed && value.PositionComp.WorldAABB.Contains(at) != ContainmentType.Disjoint) return true;
            }
            return false;
        }

        private void Sample(string phase, double t)
        {
            foreach (var c in _copies)
            {
                var frozen = c.Grids.All(g => g.Closed || FreezerState.IsFrozen(g));
                var physics = c.Grids.Any(g => !g.Closed && FreezerState.IsPhysicsFrozen(g));
                if (frozen && !c.WasFrozen) { c.Freezes++; if (physics) c.PhysicsFreezes++; }
                if (!frozen && c.WasFrozen)
                {
                    c.Thaws++;
                    if (Stepped(c.Grids[0]) == "False") c.UnsteppedThaws++;
                }
                var ground = GroundPhysics(c.Grids[0]);
                if (frozen && !ground) c.GroundLost = true;
                c.WasFrozen = frozen;
                for (var i = 0; i < c.Grids.Count; i++)
                {
                    var g = c.Grids[i];
                    if (g.Closed || g.MarkedForClose)
                    {
                        if (c.Throws.All(x => !x.StartsWith("grid " + i + " gone")))
                            c.Throws.Add("grid " + i + " gone (" + phase + " " + t.ToString("F0") + " s)");
                        continue;
                    }
                    var speed = g.Physics?.LinearVelocity.Length() ?? 0;
                    var shift = Vector3D.Distance(g.PositionComp.GetPosition(), c.Settled[i]);
                    if (speed > c.MaxSpeed || shift > c.MaxShift)
                        c.Worst = "grid " + i + " " + g.DisplayName + " " + speed.ToString("F1") + " m/s, " + shift.ToString("F1") + " m at " + phase + " " + t.ToString("F0") + " s";
                    c.MaxSpeed = Math.Max(c.MaxSpeed, speed);
                    c.MaxShift = Math.Max(c.MaxShift, shift);
                    if ((speed > MaxSpeed || shift > MaxShift) && c.Throws.Count < 6)
                        c.Throws.Add(phase + " " + t.ToString("F0") + " s: grid " + i + " " + speed.ToString("F1") + " m/s, " + shift.ToString("F1") + " m off (frozen " + frozen +
                                     ", physics frozen " + physics + ", fixed body " + FreezerState.HasFixedBody(g) + ", stepped " + Stepped(g) + ", ground physics " + ground + ", thaws " + c.Thaws + ")");
                }
            }
        }

        private static FieldInfo _stepped;

        /// <summary>Whether the freezer's last look had the grid's Havok world stepped (null: every world is).</summary>
        private static string Stepped(MyCubeGrid g)
        {
            _stepped = _stepped ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisationsPlugin.Freezer.FreezeLogic")).FirstOrDefault(x => x != null)
                ?.GetField("_steppedWorlds", BindingFlags.Static | BindingFlags.NonPublic);
            var set = _stepped?.GetValue(null) as HashSet<Havok.HkWorld>;
            if (set == null) return "all";
            var world = g.Physics?.HavokWorld;
            return world == null ? "no world" : set.Contains(world).ToString();
        }

        /// <summary>
        /// Places for the copies: about <see cref="StationShare"/> of them in the safe zones of stations on this planet (a few
        /// per zone, off its middle), the rest spread over the planet, 3 km from real players and 200 m from any grid.
        /// </summary>
        private List<(Vector3D Site, string Where)> Sites(MyPlanet planet, List<Vector3D> players, List<(Vector3D Site, string Where)> taken, int budget)
        {
            var centre = planet.PositionComp.GetPosition();
            var _ground = G(centre + Vector3D.Up * planet.MaximumRadius);
            var sites = new List<(Vector3D, string)>();
            var random = new Random(23102026);
            bool Clear(Vector3D p, double gridGap) =>
                players.All(x => Vector3D.Distance(x, p) > PlayerGap) && sites.All(s => Vector3D.Distance(s.Item1, p) > 60) && taken.All(s => Vector3D.Distance(s.Site, p) > 60) &&
                MyEntities.GetEntities().OfType<MyCubeGrid>().All(g => g.MarkedForClose || g.PositionComp.WorldAABB.Distance(p) > gridGap);
            var zones = MyEntities.GetEntities().OfType<MySafeZone>().Where(z => !z.MarkedForClose && z.Enabled &&
                Vector3D.Distance(z.PositionComp.GetPosition(), centre) < planet.MaximumRadius + 1000).ToList();
            var perZone = zones.Count == 0 ? 0 : Math.Max(1, (int)(budget * StationShare) / zones.Count);
            foreach (var zone in zones)
            {
                var c = zone.PositionComp.GetPosition();
                var up = _ground.Up(c);
                var side = Vector3D.CalculatePerpendicularVector(up);
                var placed = 0;
                for (var k = 0; k < 40 && placed < perZone && sites.Count < budget * StationShare + 1; k++)
                {
                    var angle = random.NextDouble() * Math.PI * 2;
                    var r = zone.Radius * (0.3 + 0.5 * random.NextDouble());
                    var dir = Vector3D.Transform(side, MatrixD.CreateFromAxisAngle(up, angle));
                    var p = _ground.Ground(c + dir * r);
                    if (Vector3D.Distance(p, c) < zone.Radius - 15 && Clear(p, 15))
                    {
                        sites.Add((p, "zone " + planet.StorageName + " " + zone.DisplayName));
                        placed++;
                    }
                }
            }
            for (var k = 0; k < 5000 && sites.Count < budget; k++)
            {
                var dir = Vector3D.Normalize(new Vector3D(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1));
                var p = _ground.Ground(centre + dir * planet.MaximumRadius);
                var up = _ground.Up(p);
                var side = Vector3D.CalculatePerpendicularVector(up);
                var flat = new[] { side, -side, Vector3D.Cross(up, side), -Vector3D.Cross(up, side) }
                    .All(d => Math.Abs(Vector3D.Dot(_ground.Ground(p + d * 6) - p, up)) < 1.5);
                if (flat && Clear(p, 200)) sites.Add((p, "open " + planet.StorageName));
            }
            return sites;
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                _config.Restore();
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!(grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                    grid.Close();
                }
            }
            finally { base.CleanupLeftovers(); }
        }
    }
}
