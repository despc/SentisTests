using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What a real ship of the world costs the physics alone, and why. The ship (<see cref="ShipName"/> in its display
    /// name, else the dynamic grid of the most blocks) is copied with everything joined to it (its physical group:
    /// subgrids, ships docked by connector or landing gear) into empty space, its programmable blocks off, a fake player
    /// 150 m off; its cluster is timed every frame for <see cref="RunSeconds"/> (PhysicsCost, <c>GapFrames</c> 1). Noted:
    /// the cluster's step against an empty one, what is awake in it (by entity) and how fast it moves, the contacts of its
    /// grids, and what PhysicsCost.Share lays to it. The copy is removed after. Passes when the copy's cluster was
    /// measured; the numbers are for reading.
    /// </summary>
    public sealed class PhysicsShipProbeScenario : TestScenario
    {
        public const string ScenarioName = "physics_ship_probe";
        public const string ShipName = "Shokaku";
        private const string Prefix = "pship-";
        private const int RunSeconds = 15;

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<MyCubeGrid> _copy = new List<MyCubeGrid>();
        private readonly List<double> _ms = new List<double>(), _emptyMs = new List<double>();
        private readonly Dictionary<string, int> _awake = new Dictionary<string, int>();
        private HkWorld _world, _emptyWorld;
        private int _contacts, _samples, _bodies;
        private readonly Dictionary<string, int> _kinds = new Dictionary<string, int>();
        private Type _cost;
        private Action<HkWorld, double> _handler;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _cost = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("Optimizer.Optimizations.PhysicsCost");
            SkipUnless(_cost?.GetField("Sampled", any) != null, "SentisOptimisations has no timing of the physics clusters for tests");
            var monitor = _cost.Assembly.GetType("Optimizer.Optimizations.PhysicsLoadMonitor");
            var share = _cost.GetMethod("Share", any);
            _config.Set("FreezerEnabled", false);
            _config.Set("EnablePhysicsGuard", false);

            // ------------------------------------------------------------- the ship and all joined to it
            var grids = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose).ToList();
            var name = TestRunner.Focus ?? ShipName;
            var ship = grids.Where(g => (g.DisplayName ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).OrderByDescending(g => g.BlocksCount).FirstOrDefault()
                       ?? grids.Where(g => !g.IsStatic).OrderByDescending(g => g.BlocksCount).FirstOrDefault();
            Check(ship != null, "no ship");
            var group = MyCubeGridGroups.Static.GetGroups(GridLinkTypeEnum.Physical).GetGroupNodes(ship) ?? new List<MyCubeGrid> { ship };
            var gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(ship.PositionComp.GetPosition()).Length();
            Note($"SHIP | '{ship.DisplayName}' {ship.BlocksCount} blocks, static {ship.IsStatic}, physics {(ship.Physics == null ? "none" : ship.Physics.IsActive ? "awake" : "asleep")}, " +
                 $"gravity {gravity:0.00} m/s2; its group {group.Count} grids, {group.Sum(g => g.BlocksCount)} blocks: " +
                 string.Join(", ", group.OrderByDescending(g => g.BlocksCount).Take(12).Select(g => $"{g.DisplayName} {g.BlocksCount}{(g.IsStatic ? " static" : "")}")));
            var kinds = group.SelectMany(g => g.GetFatBlocks()).GroupBy(b => b.BlockDefinition.Id.TypeId.ToString().Replace("MyObjectBuilder_", ""))
                .OrderByDescending(k => k.Count()).Take(15).Select(k => $"{k.Key} {k.Count()}");
            Note("SHIP BLOCKS | " + string.Join(", ", kinds));

            // ------------------------------------------------------------- two places in empty space: the copy's and an empty one
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            var positions = grids.Select(g => g.PositionComp.GetPosition()).ToList();
            var spots = new List<Vector3D>();
            for (var i = 0; i < 400 && spots.Count < 2; i++)
            {
                var probe = new Vector3D(800000 - (i % 20) * 80000, -600000 + (i / 20) * 60000, -500000 + (i % 7) * 110000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() > 1e-6) continue;
                if (planets.Any(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) < p.MaximumRadius + 20000)) continue;
                if (positions.Any(o => Vector3D.DistanceSquared(o, probe) < 20000.0 * 20000.0) || spots.Any(s => Vector3D.Distance(s, probe) < 60000)) continue;
                spots.Add(probe);
            }
            Check(spots.Count == 2, "no two empty places in space");

            var bySize = group.OrderByDescending(g => g.BlocksCount).ToList();
            List<MyObjectBuilder_CubeGrid> Copy(Func<MyObjectBuilder_CubeBlock, bool> drop, Func<MyCubeGrid, bool> keep, bool? destructible)
            {
                var obs = group.Where(g => keep == null || keep(g)).Select(g => (MyObjectBuilder_CubeGrid)g.GetObjectBuilder(true)).ToList();
                var move = spots[0] - ship.PositionComp.GetPosition();
                foreach (var ob in obs)
                {
                    var m = ob.PositionAndOrientation.Value.GetMatrix();
                    m.Translation += move;
                    ob.PositionAndOrientation = new MyPositionAndOrientation(m);
                    ob.Name = WorldApi.EntityPrefix + Prefix + ob.EntityId;
                    ob.DisplayName = WorldApi.EntityPrefix + Prefix + ob.DisplayName;
                    ob.LinearVelocity = default;
                    ob.AngularVelocity = default;
                    if (drop != null) ob.CubeBlocks.RemoveAll(block => drop(block));
                    if (destructible.HasValue) ob.DestructibleBlocks = destructible.Value;
                    foreach (var block in ob.CubeBlocks)
                        if (block is Sandbox.Common.ObjectBuilders.MyObjectBuilder_MyProgrammableBlock pb) pb.Enabled = false;
                }
                obs.RemoveAll(ob => ob.CubeBlocks.Count == 0);
                Sandbox.ModAPI.MyAPIGateway.Entities.RemapObjectBuilderCollection(obs);
                return obs;
            }

            FakeClients.RemoveAll();
            var off = new Vector3D(0, 150 + ship.PositionComp.LocalVolume.Radius, 0);
            FakeClients.Add(3, new FakeClients.NetworkProfile { RttMs = 30 }, i => (i < 2 ? spots[i] + off : ship.PositionComp.GetPosition() + off, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(6, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;

            // ------------------------------------------------------------- where it is, a player by it: is it awake, what does its cluster cost
            if (ship.Physics?.HavokWorld != null)
            {
                _world = ship.Physics.HavokWorld;
                _emptyWorld = FakeClients.Character(1)?.Physics?.HavokWorld;
                _handler = OnSampled;
                _cost.GetField("Sampled", any).SetValue(null, _handler);
                _cost.GetField("GapFrames", any).SetValue(null, 1);
                var awakeFrames = 0;
                var whereFrames = 0;
                var t0 = DateTime.UtcNow;
                while ((DateTime.UtcNow - t0).TotalSeconds < 20)
                {
                    yield return null;
                    whereFrames++;
                    if (ship.Physics != null && ship.Physics.IsActive) awakeFrames++;
                }
                StopSampling();
                Note($"SHIP WHERE IT IS | awake {awakeFrames * 100 / Math.Max(1, whereFrames)}% of {whereFrames} frames; its cluster {Median(_ms):0.000} ms a step ({_ms.Count} steps, p90 {P90(_ms):0.000}), " +
                     $"awake bodies in it up to {_bodies}: " + string.Join(", ", _kinds.OrderByDescending(p => p.Value).Take(12).Select(p => $"{p.Key} {p.Value}")));
                Note("SHIP WHERE IT IS, AWAKE | " + string.Join(", ", _awake.OrderByDescending(p => p.Value).Take(12).Select(p => $"{p.Key} {p.Value * 100 / Math.Max(1, _samples)}%")));
                _ms.Clear(); _emptyMs.Clear(); _awake.Clear(); _kinds.Clear(); _samples = 0; _bodies = 0;
            }
            else Note("SHIP WHERE IT IS | no physics (frozen?)");
            // ------------------------------------------------------------- the copy kept awake (as something keeps waking it in the world), whole and without some kinds
            bool Is(MyObjectBuilder_CubeBlock block, string type) => block.TypeId.ToString().EndsWith("_" + type);
            var variants = new List<(string Name, Func<MyObjectBuilder_CubeBlock, bool> Drop, Func<MyCubeGrid, bool> Keep, bool? Destructible)>
            {
                ("whole", null, null, null),
                ("the biggest grid alone", null, g => g == bySize[0], null),
                ("the second alone", null, g => bySize.Count > 1 && g == bySize[1], null),
                ("the biggest grid alone, armour only", b => !(b.TypeId.ToString().EndsWith("_CubeBlock")), g => g == bySize[0], null),
                ("the biggest grid alone, armour only, asleep", b => !(b.TypeId.ToString().EndsWith("_CubeBlock")), g => g == bySize[0], null),
            };
            // a ship of one grid has no second
            if (bySize.Count < 2) variants.RemoveAll(v => v.Name == "the second alone");
            var measuredAll = true;
            foreach (var variant in variants)
            {
                foreach (var ob in Copy(variant.Drop, variant.Keep, variant.Destructible))
                {
                    var grid = WorldApi.SpawnGrid(ob);
                    Track(grid);
                    _copy.Add(grid);
                }
                var settle = WaitForSeconds(3, "the copy settles: " + variant.Name);
                while (settle.MoveNext()) yield return settle.Current;
                var main = _copy.OrderByDescending(g => g.BlocksCount).First();
                Check(main.Physics != null, "the copy has no physics");
                _world = main.Physics.HavokWorld;
                _emptyWorld = FakeClients.Character(1)?.Physics?.HavokWorld;
                Check(_world != null && _emptyWorld != null && _world != _emptyWorld, "the copy and the empty place are not in two clusters");
                _contacts = 0;
                foreach (var g in _copy)
                    if (g.Physics?.RigidBody != null)
                    {
                        g.Physics.RigidBody.ContactPointCallback += (ref HkContactPointEvent e) => System.Threading.Interlocked.Increment(ref _contacts);
                        if (variant.Name.EndsWith("asleep")) continue;
                        g.Physics.RigidBody.EnableDeactivation = false;
                        g.Physics.RigidBody.Activate();
                        if (variant.Name.EndsWith("turning")) g.Physics.AngularVelocity = Vector3.Normalize(new Vector3(0.3f, 1f, 0.6f)) * 0.3f;
                    }
                _handler = OnSampled;
                _cost.GetField("Sampled", any).SetValue(null, _handler);
                _cost.GetField("GapFrames", any).SetValue(null, 1);
                var shares = new Dictionary<long, double>();
                double shareSum = 0, stepSum = 0;
                var frames = 0;
                var started = DateTime.UtcNow;
                while ((DateTime.UtcNow - started).TotalSeconds < RunSeconds)
                {
                    yield return null;
                    var stepMs = Convert.ToDouble(monitor.GetProperty("LastMs", any).GetValue(null));
                    share.Invoke(null, new object[] { stepMs, shares });
                    frames++;
                    stepSum += stepMs;
                    shareSum += _copy.Sum(g => shares.TryGetValue(g.EntityId, out var x) ? x : 0);
                }
                StopSampling();
                var copyBox = _copy.Select(g => g.PositionComp.WorldAABB).Aggregate((a, b) => a.Include(b));
                var near = MyEntities.GetEntities().Where(e => !(e is MyCubeGrid) && e.PositionComp != null && e.PositionComp.WorldAABB.Intersects(copyBox.GetInflated(50))).ToList();
                Note($"SHIP NEAR {variant.Name} | the copy's box {copyBox.Size.ToString("F0")}; overlapping it: " +
                     (near.Count == 0 ? "nothing" : string.Join(", ", near.GroupBy(e => e.GetType().Name).Select(k => $"{k.Key} {k.Count()}"))));
                Note($"SHIP COST {variant.Name} | {_copy.Sum(g => g.BlocksCount)} blocks, {_copy.Sum(g => (g.Physics as Sandbox.Game.Entities.Cube.MyGridPhysics)?.Shape?.ShapeCount ?? 0)} shapes, " +
                     $"box {string.Join(" x ", _copy.OrderByDescending(g => g.BlocksCount).Take(1).Select(g => (g.PositionComp.LocalAABB.Size).ToString("F0")))}; the copy kept awake: its cluster {Median(_ms):0.000} ms a step (p90 {P90(_ms):0.000}, {_ms.Count} steps), " +
                     $"an empty one {Median(_emptyMs):0.000} ms ({_emptyMs.Count}); awake bodies up to {_bodies}: " +
                     string.Join(", ", _kinds.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key} {x.Value}")) +
                     $"; contact points of its grids {_contacts} in {frames} frames; PhysicsCost laid to it {shareSum / Math.Max(1, frames):0.000} ms a frame of a {stepSum / Math.Max(1, frames):0.000} ms step");
                measuredAll &= _ms.Count >= 30 && _emptyMs.Count >= 20;
                foreach (var g in _copy)
                    if (!g.Closed) g.Close();
                _copy.Clear();
                _ms.Clear(); _emptyMs.Clear(); _awake.Clear(); _kinds.Clear(); _samples = 0; _bodies = 0;
                var gone = WaitForSeconds(2, "the copy removed");
                while (gone.MoveNext()) yield return gone.Current;
            }
            Check(measuredAll, "a copy's cluster was not measured enough");
        }

        private static double Median(List<double> xs) => xs.Count == 0 ? 0 : xs.OrderBy(x => x).ElementAt(xs.Count / 2);
        private static double P90(List<double> xs) => xs.Count == 0 ? 0 : xs.OrderBy(x => x).ElementAt((int)(xs.Count * 0.9));

        private void OnSampled(HkWorld world, double ms)
        {
            if (world == _emptyWorld) { _emptyMs.Add(ms); return; }
            if (world != _world) return;
            _ms.Add(ms);
            _samples++;
            var bodies = 0;
            var seen = new HashSet<string>();
            var kinds = new Dictionary<string, int>();
            foreach (var body in world.ActiveRigidBodies)
            {
                bodies++;
                var entity = (body.UserObject as MyPhysicsBody)?.Entity;
                var what = entity == null ? "no entity"
                    : entity is MyCubeGrid g ? "grid " + (g.DisplayName ?? "").Replace(WorldApi.EntityPrefix + Prefix, "") + (g.IsStatic ? " (static)" : "")
                    : entity.GetType().Name + (entity.Parent is MyCubeBlock block ? " of " + block.BlockDefinition.Id.SubtypeName : "");
                if (seen.Add(what)) _awake[what] = (_awake.TryGetValue(what, out var n) ? n : 0) + 1;
                var kind = entity == null ? "no entity" : entity is MyCubeGrid ? "grid" : entity.GetType().Name + (entity.Parent is MyCubeBlock b2 ? " of " + b2.BlockDefinition.Id.TypeId.ToString().Replace("MyObjectBuilder_", "") : "");
                kinds[kind] = (kinds.TryGetValue(kind, out var k) ? k : 0) + 1;
            }
            if (bodies > _bodies)
            {
                _bodies = bodies;
                _kinds.Clear();
                foreach (var pair in kinds) _kinds[pair.Key] = pair.Value;
            }
        }

        private void StopSampling()
        {
            if (_cost == null) return;
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var sampled = _cost.GetField("Sampled", any);
            if (sampled != null && ReferenceEquals(sampled.GetValue(null), _handler)) sampled.SetValue(null, null);
            _cost.GetField("GapFrames", any)?.SetValue(null, _cost.GetField("MeasureGapFrames", any)?.GetRawConstantValue() ?? 30);
        }

        public override void Cleanup()
        {
            try
            {
                StopSampling();
                foreach (var g in _copy)
                    if (!g.Closed) g.Close();
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
