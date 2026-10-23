using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Why copies of the world's rover (<see cref="RoverThawScenario.SourceName"/>) set down where nobody is were thrown
    /// about - their wheels at hundreds of metres a second - when copies set down by a player were not. Four sets of
    /// <see cref="PerSet"/> copies, each on fresh ground of a planet nobody is on: set down first and a player brought 10 s
    /// later, or a player first and the copies 15 s later; each once with the freezer off and once on with its physics
    /// freeze. Every frame for <see cref="WatchSeconds"/> s every grid of every copy is read: its speed, how deep its
    /// lowest point is in the ground, whether the planet has the physics of its ground there, whether the freezer holds
    /// it, whether its Havok world is stepped, and whether all its grids are in one Havok world. The first frame a grid
    /// goes faster than <see cref="Fast"/> m/s is written down with all of it, and the frames just before.
    /// </summary>
    public sealed class RoverSpawnProbeScenario : TestScenario
    {
        public const string ScenarioName = "rover_spawn_probe";
        private const string Prefix = "rover-probe-";
        private const int PerSet = 8;
        private const double WatchSeconds = 30;
        private const double Fast = 10;
        private const double PlayerGap = 17000;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private sealed class Copy
        {
            public string Set;
            public int Index;
            public Vector3D Site;
            public List<MyCubeGrid> Grids = new List<MyCubeGrid>();
            public int SpawnFrame;
            public Vector3D Start;
            public double Sunk;
            public string SunkEach;
            public string FirstFast;
            public double MaxSpeed;
            public double MaxDepth;
            public bool EverFrozen, EverPhysicsFrozen, EverNoGround, EverSplitWorlds, EverUnstepped;
            public readonly Queue<string> Recent = new Queue<string>();
        }

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly Dictionary<MyPlanet, PlanetGround> _grounds = new Dictionary<MyPlanet, PlanetGround>();
        private readonly List<Copy> _all = new List<Copy>();
        private List<MyObjectBuilder_CubeGrid> _builders;
        private MatrixD _originFrame;
        private double _height;
        private List<Vector3D> _characters;
        private readonly List<Vector3D> _used = new List<Vector3D>();
        private MyPlanet _planet;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        private PlanetGround G(Vector3D at)
        {
            var planet = MyGamePruningStructure.GetClosestPlanet(at);
            if (!_grounds.TryGetValue(planet, out var ground)) _grounds[planet] = ground = new PlanetGround(planet);
            return ground;
        }

        public override IEnumerator Run()
        {
            var source = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !g.MarkedForClose && g.DisplayName == RoverThawScenario.SourceName);
            SkipUnless(source != null, "no grid \"" + RoverThawScenario.SourceName + "\" in the world");
            var group = MyCubeGridGroups.Static.Mechanical.GetGroupNodes(source).Where(g => !g.MarkedForClose).ToList();
            _builders = group.Select(g => (MyObjectBuilder_CubeGrid)g.GetObjectBuilder(true)).ToList();
            var origin = source.PositionComp.GetPosition();
            var up0 = G(origin).Up(origin);
            _originFrame = Frame(origin, up0, source.WorldMatrix.Forward);
            // the rover's own height over the ground it stands on, by a physics ray (its blocks are 7-11 m over its grid's
            // origin: a height from the blocks' boxes put the copies 8-9 m up in the air, and they fell)
            _height = RoverThawScenario.RestHeight(source, up0) + 0.2;
            _characters = MyEntities.GetEntities().OfType<Sandbox.Game.Entities.Character.MyCharacter>().Where(c => !c.MarkedForClose && !c.IsDead)
                .Select(c => c.PositionComp.GetPosition()).ToList();
            // a planet with nobody on it, the biggest such
            _planet = MyEntities.GetEntities().OfType<MyPlanet>().Where(p => !p.MarkedForClose)
                .Where(p => _characters.All(c => Vector3D.Distance(c, p.PositionComp.GetPosition()) > p.MaximumRadius + PlayerGap))
                .OrderByDescending(p => p.MaximumRadius).FirstOrDefault()
                ?? MyEntities.GetEntities().OfType<MyPlanet>().OrderByDescending(p => p.MaximumRadius).First();
            foreach (var g in group)
                Note("source grid " + g.DisplayName + ": " + g.BlocksCount + " blocks, " + g.GridSizeEnum + ", size " + ((g.Max - g.Min + 1) * g.GridSize).ToString() +
                     ", static " + g.IsStatic + ", lowest block centre " + g.GetBlocks().Min(b => { b.ComputeWorldCenter(out var w); return Vector3D.Dot(w - origin, up0); }).ToString("F2") +
                     " m along up from the first grid; kinds " + string.Join(", ", g.GetBlocks().GroupBy(b => b.BlockDefinition.Id.SubtypeName).OrderByDescending(x => x.Count()).Take(6).Select(x => x.Key + " x" + x.Count())));
            Note("planet " + _planet.StorageName + ", rover " + group.Count + " grids; " + _characters.Count + " characters in the world kept " + PlayerGap + " m off");

            // where to set the rover down: on the top of the rock read from the voxel storage (as the earlier runs did), on the
            // surface a physics ray finds, or a metre into the rock under that surface
            // settled rovers frozen with their physics, pushed into the rock while frozen (or not), thawed by the players'
            // return: the freezer's ThawGuard has to lift them out and keep them still
            _config.Set("FreezePhysics", true);
            _config.Set("FreezeDistanceDynamic", 1000);
            _config.Set("FreezeDistanceStatic", 1000);
            // without power the planet drops the physics of the ground under a frozen rover; then woken the way the
            // compensation wakes a group (the freezer's wake-up time), with nobody near, and later thawed by the players
            foreach (var powered in new[] { true, false })
            {
                var wake = RunFrozen((powered ? "powered" : "no power, ground gone") + ", woken alone", 0, powered, wakeAlone: true);
                while (wake.MoveNext()) yield return wake.Current;
            }
            FakeClients.RemoveAll();

            foreach (var set in _all.Select(c => c.Set).Distinct())
            {
                var of = _all.Where(c => c.Set == set).ToList();
                Note("SET " + set + ": thrown " + of.Count(c => c.FirstFast != null) + " of " + of.Count + ", fastest " + of.Max(c => c.MaxSpeed).ToString("F1") +
                     " m/s, deepest in the ground " + of.Max(c => c.MaxDepth).ToString("F2") + " m; frozen at some point " + of.Count(c => c.EverFrozen) +
                     " (physics " + of.Count(c => c.EverPhysicsFrozen) + "), with no ground physics at some point " + of.Count(c => c.EverNoGround) +
                     ", grids in two Havok worlds at some point " + of.Count(c => c.EverSplitWorlds) + ", world not stepped at some point " + of.Count(c => c.EverUnstepped));
            }
            foreach (var c in _all.Where(c => c.FirstFast != null))
                Note("THROWN " + c.Set + " #" + c.Index + ": " + c.FirstFast);
            foreach (var c in _all.OrderByDescending(c => c.Sunk))
                Note("SUNK " + c.Set + " #" + c.Index + ": deepest wheel " + c.Sunk.ToString("F2") + " m in the ground as set down (each " + c.SunkEach + "), fastest after " +
                     c.MaxSpeed.ToString("F1") + " m/s" + (c.MaxSpeed > 30 ? " THROWN" : ""));
            Note("ROVER PROBE RESULT | " + string.Join(" | ", _all.Select(c => c.Set).Distinct().Select(s => s + ": " + _all.Count(c => c.Set == s && c.FirstFast != null) + "/" + _all.Count(c => c.Set == s))));
        }

        private IEnumerator RunFrozen(string set, double sink, bool powered = true, bool wakeAlone = false)
        {
            _config.Set("FreezerEnabled", false);
            var sites = Sites(PerSet);
            Watchers(sites);
            var wait = WaitForSeconds(15, set + ": players by the sites");
            while (wait.MoveNext()) yield return wait.Current;
            // dropped from 3 m: set down right onto it, a rover tilted as the original sinks a wheel into the rock
            var copies = sites.Select((site, i) => Spawn(set, i, (RaySurface(site) ?? G(site).Ground(site)) + G(site).Up(site) * 3)).ToList();
            _all.AddRange(copies);
            var frame0 = Frame();
            while (Frame() - frame0 < 20 * 60) { foreach (var c in copies) Sample(c, Frame() - frame0, Frame() - frame0); yield return null; }
            var settled = copies.Count(c => c.MaxSpeed < 30);
            foreach (var c in copies) { c.MaxSpeed = 0; c.FirstFast = null; c.EverFrozen = false; }

            if (!powered)
            {
                // every source of power off: the batteries, the reactor
                foreach (var b in copies.SelectMany(c => c.Grids).SelectMany(g => g.GetFatBlocks()).OfType<Sandbox.Game.Entities.Cube.MyFunctionalBlock>()
                             .Where(f => f.Components.Get<Sandbox.Game.EntityComponents.MyResourceSourceComponent>() != null))
                    b.Enabled = false;
                // a frozen grid's power stays what it was at the freeze: off before the freezer comes
                var dark = WaitForSeconds(5, set + ": the batteries off");
                while (dark.MoveNext()) yield return dark.Current;
                var g0 = copies[0].Grids[0];
                Note(set + ": copy 0 power now " + g0.GridSystems.ResourceDistributor.ResourceStateByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId) +
                     "; producers working: " + string.Join(", ", copies[0].Grids.SelectMany(g => g.GetFatBlocks()).OfType<Sandbox.Game.Entities.Cube.MyFunctionalBlock>()
                         .Where(f => f.Components.Get<Sandbox.Game.EntityComponents.MyResourceSourceComponent>() is var src && src != null && src.Enabled && src.CurrentOutputByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId) >= 0 && f.IsWorking)
                         .Select(f => f.BlockDefinition.Id.SubtypeName + " " + (f.Enabled ? "on" : "off"))) +
                     "; batteries " + string.Join(" ", copies[0].Grids.SelectMany(g => g.GetFatBlocks()).OfType<Sandbox.Game.Entities.MyBatteryBlock>().Select(b => b.Enabled ? "on" : "off")));
            }
            _config.Set("FreezerEnabled", true);
            FakeClients.RemoveAll();
            var frozen = Wait(() => copies.All(c => c.Grids.All(g => g.Closed || g.IsStatic || FreezerState.IsPhysicsFrozen(g))), set + ": every copy physics-frozen", 60);
            while (frozen.MoveNext()) yield return frozen.Current;
            if (sink > 0)
                foreach (var c in copies)
                {
                    var up = G(c.Start).Up(c.Start);
                    foreach (var g in c.Grids.Where(g => !g.Closed)) g.PositionComp.SetPosition(g.PositionComp.GetPosition() - up * sink);
                }
            // frozen long enough for the planet to drop the physics of its ground where nothing holds it (every 100 frames)
            var heldFrom = Frame();
            while (Frame() - heldFrom < 30 * 60)
            {
                foreach (var c in copies) c.EverNoGround |= !GroundPhysics(c.Grids[0]);
                yield return null;
            }
            {
                var c0 = copies[0];
                var g0 = c0.Grids[0];
                var box = new BoundingBoxD(g0.PositionComp.GetPosition() - 1500, g0.PositionComp.GetPosition() + 1500);
                var near = new List<VRage.Game.Entity.MyEntity>();
                MyGamePruningStructure.GetTopMostEntitiesInBox(ref box, near);
                Note(set + ": copy 0 power " + g0.GridSystems.ResourceDistributor.ResourceStateByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId) +
                     ", physics static " + g0.Physics?.IsStatic + "; within 1.5 km: " + string.Join(", ", near.Where(e => e.Physics != null)
                         .Select(e => e.GetType().Name + (e.Physics.IsStatic ? " static" : " dynamic") + (e is MyCubeGrid cg && cg.Physics.IsStatic ? " " + cg.GridSystems.ResourceDistributor.ResourceStateByType(Sandbox.Game.EntityComponents.MyResourceDistributorComponent.ElectricityId) : ""))
                         .GroupBy(x => x).Select(x => x.Key + " x" + x.Count())));
            }
            Note(set + ": the ground's physics gone under " + copies.Count(c => c.EverNoGround) + " of " + copies.Count + " while frozen; there at the thaw under " +
                 copies.Count(c => GroundPhysics(c.Grids[0])));
            if (wakeAlone)
            {
                // the wake-up the compensation does: the group's next wake-up time a few seconds ahead (FreezeLogic.WakeUpDatas,
                // by the group's smallest entity id); each copy woken twice, 40 s apart, nobody near
                var logic = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisationsPlugin.Freezer.FreezeLogic")).FirstOrDefault(t => t != null);
                var wakeUps = (Dictionary<long, DateTime>)logic.GetField("WakeUpDatas", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                var lockObject = logic.GetField("_wakeUpLock", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                foreach (var c in copies) { c.MaxSpeed = 0; c.FirstFast = null; }
                for (var round = 1; round <= 2; round++)
                {
                    lock (lockObject)
                        foreach (var c in copies) wakeUps[c.Grids.Min(g => g.EntityId)] = DateTime.Now.AddSeconds(5);
                    var thawed = new HashSet<Copy>();
                    var groundAtThaw = 0;
                    var steppedAtThaw = 0;
                    var fixedAtThaw = 0;
                    // every frame a copy is awake: a grid of it with a body Havok moves (not fixed), or one the freezer no
                    // longer has as physics-frozen
                    long awakeFrames = 0, dynamicFrames = 0, unlistedFrames = 0;
                    string firstDynamic = null;
                    var physicsList = logic.GetField("FrozenPhysicsGrids", BindingFlags.Static | BindingFlags.Public).GetValue(null);
                    var listed = physicsList.GetType().GetMethod("Contains");
                    var t0 = Frame();
                    while (Frame() - t0 < 40 * 60)
                    {
                        foreach (var c in copies)
                        {
                            if (!thawed.Contains(c) && c.Grids.All(g => g.Closed || !FreezerState.IsFrozen(g)))
                            {
                                thawed.Add(c);
                                if (GroundPhysics(c.Grids[0])) groundAtThaw++;
                                if (Stepped(c.Grids[0]) != "False") steppedAtThaw++;
                                if (c.Grids.All(g => g.Closed || g.IsStatic || FreezerState.HasFixedBody(g))) fixedAtThaw++;
                            }
                            if (!c.Grids.All(g => g.Closed || FreezerState.IsFrozen(g)))
                            {
                                awakeFrames++;
                                var moving = c.Grids.Where(g => !g.Closed && !g.IsStatic && g.Physics?.RigidBody != null &&
                                                               (!g.Physics.RigidBody.IsFixed || g.Physics.RigidBody.GetMotionType() != Havok.HkMotionType.Fixed)).ToList();
                                if (moving.Count > 0)
                                {
                                    dynamicFrames++;
                                    firstDynamic = firstDynamic ?? "copy " + c.Index + " frame " + (Frame() - t0) + ": " + string.Join(", ", moving.Select(g => g.DisplayName + " " + g.Physics.RigidBody.GetMotionType()));
                                }
                                if (c.Grids.Any(g => !g.Closed && !g.IsStatic && !(bool)listed.Invoke(physicsList, new object[] { g.EntityId }))) unlistedFrames++;
                            }
                            Sample(c, Frame() - t0, -1);
                        }
                        yield return null;
                    }
                    Note(set + ": wake-up " + round + " with nobody near: thawed " + thawed.Count + " of " + copies.Count + ", the ground's physics there at the thaw under " + groundAtThaw +
                         ", their world stepped " + steppedAtThaw + ", bodies kept fixed " + fixedAtThaw + " (awake copy-frames " + awakeFrames + ": with a dynamic body " + dynamicFrames + (firstDynamic != null ? ", first " + firstDynamic : "") +
                         ", with a grid not listed physics-frozen " + unlistedFrames + "); frozen again " + copies.Count(c => c.Grids.All(g => g.Closed || FreezerState.IsFrozen(g))) +
                         ", ground physics now under " + copies.Count(c => GroundPhysics(c.Grids[0])) + "; fastest " + copies.Max(c => c.MaxSpeed).ToString("F1") + " m/s, moved up to " +
                         copies.Max(c => Vector3D.Distance(c.Grids[0].PositionComp.GetPosition(), c.Start)).ToString("F1") + " m from where it was set down; each " +
                         string.Join(" ", copies.Select(c => c.MaxSpeed.ToString("F1"))));
                    foreach (var c in copies) c.MaxSpeed = 0;
                }
                // woken again, and the players come in the middle of the wake-up's seconds: the bodies are to be handed back
                // to the physics at once, the logic to stay as it is
                lock (lockObject)
                    foreach (var c in copies) wakeUps[c.Grids.Min(g => g.EntityId)] = DateTime.Now.AddSeconds(5);
                var awake = Wait(() => copies.All(c => c.Grids.All(g => g.Closed || !FreezerState.IsFrozen(g))), set + ": awake for the compensation", 15);
                while (awake.MoveNext()) yield return awake.Current;
                var stillFixed = copies.Count(c => c.Grids.Any(g => !g.Closed && !g.IsStatic && FreezerState.HasFixedBody(g)));
                Watchers(sites);
                var came = Frame();
                var dynamicAfter = new Dictionary<Copy, int>();
                var refrozen = 0;
                while (Frame() - came < 15 * 60)
                {
                    foreach (var c in copies)
                    {
                        if (!dynamicAfter.ContainsKey(c) && c.Grids.All(g => g.Closed || g.IsStatic || !FreezerState.HasFixedBody(g))) dynamicAfter[c] = Frame() - came;
                        if (c.Grids.Any(g => !g.Closed && FreezerState.IsFrozen(g))) refrozen++;
                    }
                    yield return null;
                }
                Note(set + ": players came while awake for the compensation (bodies fixed then " + stillFixed + " of " + copies.Count + "): bodies dynamic " + dynamicAfter.Count +
                     " of " + copies.Count + ", after frames " + string.Join(" ", copies.Select(c => dynamicAfter.TryGetValue(c, out var f) ? f.ToString() : "never")) +
                     "; copy-frames frozen again while the players were there " + refrozen + "; logic awake now " + copies.Count(c => c.Grids.All(g => g.Closed || !FreezerState.IsFrozen(g))) +
                     "; fastest " + copies.Max(c => c.MaxSpeed).ToString("F1") + " m/s");
                FakeClients.RemoveAll();
                var refreeze = Wait(() => copies.All(c => c.Grids.All(g => g.Closed || g.IsStatic || FreezerState.IsPhysicsFrozen(g))), set + ": frozen again after the players left", 90);
                while (refreeze.MoveNext()) yield return refreeze.Current;
            }
            Watchers(sites);
            frame0 = Frame();
            while (Frame() - frame0 < 20 * 60) { foreach (var c in copies) Sample(c, Frame() - frame0, Frame() - frame0); yield return null; }
            Note(set + ": " + settled + " of " + copies.Count + " settled whole before; after the thaw thrown (over 30 m/s) " + copies.Count(c => c.MaxSpeed > 30) +
                 ", fastest " + copies.Max(c => c.MaxSpeed).ToString("F1") + " m/s; each " + string.Join(" ", copies.Select(c => c.MaxSpeed.ToString("F1"))));
            FakeClients.RemoveAll();
            foreach (var c in copies)
                foreach (var g in c.Grids.Where(g => !g.MarkedForClose))
                {
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(g);
                    g.Close();
                }
            yield return null;
        }

        private IEnumerator RunSet(string set, bool playerFirst, string mode)
        {
            var sites = Sites(PerSet);
            Note(set + ": " + sites.Count + " sites");
            if (playerFirst)
            {
                Watchers(sites);
                var wait = WaitForSeconds(15, set + ": players by the sites");
                while (wait.MoveNext()) yield return wait.Current;
            }
            var copies = new List<Copy>();
            var offsets = new List<string>();
            for (var i = 0; i < sites.Count; i++)
            {
                var up = G(sites[i]).Up(sites[i]);
                var storage = G(sites[i]).Ground(sites[i]);
                var ray = RaySurface(sites[i]);
                offsets.Add(ray.HasValue ? Vector3D.Dot(storage - ray.Value, up).ToString("F2") : "no hit");
                var at = (ray ?? storage) + (mode == "dropped from 8 m" ? up * 8 : Vector3D.Zero);
                var c = Spawn(set, i, at);
                copies.Add(c);
                _all.Add(c);
            }
            Note(set + ": the storage's top over the ray's surface, m: " + string.Join(" ", offsets));
            var frame0 = Frame();
            var playersAt = playerFirst ? frame0 : -1;
            while (Frame() - frame0 < (WatchSeconds + (playerFirst ? 0 : 10)) * 60)
            {
                if (!playerFirst && playersAt < 0 && Frame() - frame0 >= 600)
                {
                    Watchers(sites);
                    playersAt = Frame();
                    Note(set + ": players brought at frame " + (playersAt - frame0));
                }
                foreach (var c in copies) Sample(c, Frame() - frame0, playersAt < 0 ? -1 : Frame() - playersAt);
                yield return null;
            }
            FakeClients.RemoveAll();
            foreach (var c in copies)
                foreach (var g in c.Grids.Where(g => !g.MarkedForClose))
                {
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(g);
                    g.Close();
                }
            yield return null;
        }

        /// <summary>Where a physics ray down through the point meets the planet's ground (its voxel physics).</summary>
        private Vector3D? RaySurface(Vector3D at)
        {
            var up = G(at).Up(at);
            var hits = new List<Sandbox.Engine.Physics.MyPhysics.HitInfo>();
            Sandbox.Engine.Physics.MyPhysics.CastRay(at + up * 60, at - up * 60, hits);
            foreach (var hit in hits.OrderBy(h => h.HkHitInfo.HitFraction))
                if (hit.HkHitInfo.GetHitEntity() is MyVoxelBase) return hit.Position;
            return null;
        }

        private static int Frame() => (int)Sandbox.MySandboxGame.Static.SimulationFrameCounter;

        private Copy Spawn(string set, int index, Vector3D site)
        {
            var up = G(site).Up(site);
            var frame = Frame(site + up * _height, up, Vector3D.CalculatePerpendicularVector(up));
            var copy = new Copy { Set = set, Index = index, Site = site, SpawnFrame = Frame() };
            var obs = WorldApi.CopyGroup(_builders.Select(b => (MyObjectBuilder_CubeGrid)MyObjectBuilderSerializer.Clone(b)).ToList(),
                WorldApi.EntityPrefix + Prefix + _all.Count, keepOwners: true);
            foreach (var ob in obs)
            {
                ob.PositionAndOrientation = new MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * MatrixD.Invert(_originFrame) * frame);
                ob.LinearVelocity = Vector3.Zero;
                ob.AngularVelocity = Vector3.Zero;
                ob.Handbrake = true;
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                copy.Grids.Add(grid);
            }
            foreach (var g in copy.Grids) WorldApi.ChargeBatteries(g);
            copy.Start = copy.Grids[0].PositionComp.GetPosition();
            // how deep each wheel is in the ground as set down: its model's radius less its centre's height over the ground
            // under it (a physics ray), 0 when it is clear of it
            var sunk = new List<double>();
            foreach (var wheel in copy.Grids.SelectMany(g => g.GetFatBlocks()).OfType<Sandbox.Game.Entities.Blocks.MyWheel>())
            {
                var centre = wheel.PositionComp.WorldAABB.Center;
                var radius = wheel.PositionComp.LocalAABB.Size.Max() / 2;
                var surface = RaySurface(centre);
                sunk.Add(surface.HasValue ? radius - Vector3D.Dot(centre - surface.Value, up) : double.NaN);
            }
            copy.Sunk = sunk.Where(x => !double.IsNaN(x)).DefaultIfEmpty(0).Max();
            copy.SunkEach = string.Join(" ", sunk.Select(x => x.ToString("F2")));
            return copy;
        }

        private void Sample(Copy c, int frame, int sincePlayers)
        {
            var parts = new List<string>();
            var worlds = new HashSet<Havok.HkWorld>();
            string fast = null;
            for (var i = 0; i < c.Grids.Count; i++)
            {
                var g = c.Grids[i];
                if (g.Closed || g.MarkedForClose) { parts.Add(i + ":gone"); continue; }
                var speed = g.Physics?.LinearVelocity.Length() ?? 0;
                var depth = Depth(g);
                var world = g.Physics?.HavokWorld;
                if (world != null) worlds.Add(world);
                var frozen = FreezerState.IsFrozen(g);
                var physics = FreezerState.IsPhysicsFrozen(g);
                c.EverFrozen |= frozen;
                c.EverPhysicsFrozen |= physics;
                c.MaxSpeed = Math.Max(c.MaxSpeed, speed);
                c.MaxDepth = Math.Max(c.MaxDepth, depth);
                parts.Add(i + ":" + speed.ToString("F1") + "m/s,d" + depth.ToString("F2") + (frozen ? ",F" : "") + (physics ? "P" : "") + (FreezerState.HasFixedBody(g) ? ",fixed" : ""));
                if (speed > Fast && fast == null) fast = "grid " + i + " " + g.DisplayName + " " + speed.ToString("F1") + " m/s";
            }
            var ground = GroundPhysics(c.Grids[0]);
            var stepped = Stepped(c.Grids[0]);
            c.EverNoGround |= !ground;
            c.EverSplitWorlds |= worlds.Count > 1;
            c.EverUnstepped |= stepped == "False";
            var line = "f" + frame + (sincePlayers >= 0 ? " (players +" + sincePlayers + ")" : " (no players)") + " ground " + ground + " [" + Sector(c.Grids[0]) + "]" + ", stepped " + stepped + ", worlds " + worlds.Count +
                       " | " + string.Join(" ", parts);
            if (c.FirstFast == null && fast != null)
                c.FirstFast = fast + " at " + line + " || before: " + string.Join(" || ", c.Recent.Reverse().Take(3));
            // the first copy of each set, every 10 frames: how far its chassis went along up and sideways, its velocity along up
            if (c.Index == 0 && (frame == 0 || frame == 300))
            {
                var up1 = G(c.Start).Up(c.Start);
                var surface = RaySurface(c.Start);
                Note("CLEARANCE " + c.Set + " f" + frame + ": lowest block centre over the ray's surface per grid: " + (surface.HasValue
                    ? string.Join(" ", c.Grids.Where(g => !g.Closed).Select((g, i) => i + ":" + g.GetBlocks().Min(b => { b.ComputeWorldCenter(out var w); return Vector3D.Dot(w - surface.Value, up1); }).ToString("F2")))
                    : "no surface"));
            }
            if (c.Index == 0 && frame % 10 == 0 && frame <= 900)
            {
                var g0 = c.Grids[0];
                var up = G(c.Start).Up(c.Start);
                var d = g0.PositionComp.GetPosition() - c.Start;
                var v = g0.Physics?.LinearVelocity ?? Vector3.Zero;
                Note("TRACE " + c.Set + " f" + frame + ": up " + Vector3D.Dot(d, up).ToString("F2") + " m, side " + (d - up * Vector3D.Dot(d, up)).Length().ToString("F2") +
                     " m, v up " + Vector3D.Dot(v, up).ToString("F2") + " side " + ((Vector3D)v - up * Vector3D.Dot(v, up)).Length().ToString("F2") +
                     ", rock top over the chassis' start " + Vector3D.Dot(G(c.Start).Ground(c.Start) - c.Start, up).ToString("F2") + " | " + line);
            }
            c.Recent.Enqueue(line);
            while (c.Recent.Count > 30) c.Recent.Dequeue();
        }

        /// <summary>How deep the grid's lowest point is under the top of the rock below it (0 when over it).</summary>
        private double Depth(MyCubeGrid g)
        {
            var box = g.PositionComp.WorldAABB;
            var at = box.Center;
            var ground = G(at);
            var up = ground.Up(at);
            var low = box.GetCorners().Min(x => Vector3D.Dot(x - at, up));
            var bottom = at + up * low;
            var top = ground.Ground(at);
            return Math.Max(0, Vector3D.Dot(top - bottom, up));
        }

        private static FieldInfo _shapes, _stepped;

        /// <summary>The planet's ground sector where the grid is: in the grid's Havok world, its bodies made (LOD0, LOD1).</summary>
        private string Sector(MyCubeGrid grid)
        {
            _shapes = _shapes ?? typeof(MyPlanet).GetField("m_physicsShapes", BindingFlags.Instance | BindingFlags.NonPublic);
            var at = grid.PositionComp.GetPosition();
            if (!(_shapes?.GetValue(G(at).Planet) is IEnumerable shapes)) return "none";
            foreach (var pair in shapes)
            {
                var value = pair.GetType().GetProperty("Value")?.GetValue(pair) as MyVoxelBase;
                if (value == null || value.Closed || value.PositionComp.WorldAABB.Contains(at) == ContainmentType.Disjoint) continue;
                var body = value.Physics as Sandbox.Engine.Voxels.MyVoxelPhysicsBody;
                if (body == null) return "no body";
                var world = body.HavokWorld;
                return (world == null ? "no world" : world == grid.Physics?.HavokWorld ? "same world" : "OTHER world") +
                       ", lod0 " + (body.RigidBody == null ? "none" : body.RigidBody.InWorld ? "in" : "out") +
                       ", lod1 " + (body.RigidBody2 == null ? "none" : body.RigidBody2.InWorld ? "in" : "out");
            }
            return "none";
        }

        private bool GroundPhysics(MyCubeGrid grid)
        {
            _shapes = _shapes ?? typeof(MyPlanet).GetField("m_physicsShapes", BindingFlags.Instance | BindingFlags.NonPublic);
            var at = grid.PositionComp.GetPosition();
            if (!(_shapes?.GetValue(G(at).Planet) is IEnumerable shapes)) return false;
            foreach (var pair in shapes)
            {
                var value = pair.GetType().GetProperty("Value")?.GetValue(pair) as MyVoxelBase;
                if (value != null && !value.Closed && value.PositionComp.WorldAABB.Contains(at) != ContainmentType.Disjoint) return true;
            }
            return false;
        }

        private static string Stepped(MyCubeGrid g)
        {
            _stepped = _stepped ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisationsPlugin.Freezer.FreezeLogic")).FirstOrDefault(x => x != null)
                ?.GetField("_steppedWorlds", BindingFlags.Static | BindingFlags.NonPublic);
            var set = _stepped?.GetValue(null) as HashSet<Havok.HkWorld>;
            if (set == null) return "all";
            var world = g.Physics?.HavokWorld;
            return world == null ? "no world" : set.Contains(world).ToString();
        }

        private void Watchers(List<Vector3D> at)
        {
            FakeClients.RemoveAll();
            FakeClients.Add(at.Count, Network, i =>
            {
                var up = G(at[i]).Up(at[i]);
                var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
                return (G(at[i]).Ground(at[i] + side * 25) + up * 2, 0, 0);
            }, withCharacters: true);
        }

        /// <summary>Flat places of the planet, far from every character and from each other, never used before in the run.</summary>
        private List<Vector3D> Sites(int count)
        {
            var centre = _planet.PositionComp.GetPosition();
            var random = new Random(_used.Count * 7919 + 23);
            var sites = new List<Vector3D>();
            for (var k = 0; k < 5000 && sites.Count < count; k++)
            {
                var dir = Vector3D.Normalize(new Vector3D(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1));
                var p = G(centre + dir * _planet.MaximumRadius).Ground(centre + dir * _planet.MaximumRadius);
                if (_characters.Any(c => Vector3D.Distance(c, p) < PlayerGap) || _used.Concat(sites).Any(s => Vector3D.Distance(s, p) < 3000)) continue;
                var up = G(p).Up(p);
                var side = Vector3D.CalculatePerpendicularVector(up);
                var flat = new[] { side, -side, Vector3D.Cross(up, side), -Vector3D.Cross(up, side) }
                    .All(d => Math.Abs(Vector3D.Dot(G(p).Ground(p + d * 6) - p, up)) < 1.0);
                if (!flat || MyEntities.GetEntities().OfType<MyCubeGrid>().Any(g => !g.MarkedForClose && g.PositionComp.WorldAABB.Distance(p) < 300)) continue;
                sites.Add(p);
            }
            _used.AddRange(sites);
            return sites;
        }

        private static MatrixD Frame(Vector3D at, Vector3D up, Vector3D forward)
        {
            var flat = forward - up * Vector3D.Dot(forward, up);
            if (flat.LengthSquared() < 1e-6) flat = Vector3D.CalculatePerpendicularVector(up);
            return MatrixD.CreateWorld(at, Vector3D.Normalize(flat), up);
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
