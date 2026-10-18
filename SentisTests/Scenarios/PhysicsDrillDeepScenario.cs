using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.World;
using Sandbox.Engine.Voxels;
using VRage.Voxels;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A drill ship digging a tunnel into an asteroid, deeper and deeper: what the physics costs and how fast it drills, by
    /// depth. Reported on the stand (17.10.2026): the drill ship "Бур Физика", once buried in the asteroid it drills,
    /// loads the physics terribly, the more the deeper the tunnel; with its drills stopped for a while the load goes down.
    /// And on the rock's face the drills keep up with 0.46 m/s, deep in the tunnel not even with 0.1-0.2.
    ///
    /// A copy of the live ship (<see cref="PhysicsDrillLiveScenario.ShipName"/>, or the grid named in the test call's
    /// focus) with its subgrids is brought into empty space with a fake player by it. When the live ship is in an
    /// asteroid, that asteroid is copied too, its tunnels and all (its storage), and the ship put where it stands in it;
    /// otherwise (and always for <see cref="FreshName"/>) a fresh asteroid of the game's (<see cref="AsteroidPrefab"/>),
    /// the drills' heads a metre short of the rock on the line to its middle. It is driven the
    /// way a pilot drives it: a force along its drills of at most <see cref="MaxAccel"/> m/s² that holds
    /// <see cref="Speed"/> m/s (and stops it sideways) - it goes on only as fast as the drills clear the rock. Its cargo is
    /// emptied of ore at every sample, so a full hold never stops the drills. For <see cref="DrillSeconds"/> s every
    /// <see cref="SampleSeconds"/> s: the depth of the heads under the face, the speed it made, its physics cluster's step
    /// alone (timed every frame), the contact points of the ship with the rock and with anything else, the asteroid's
    /// collision cells, the drill cut-outs' cells (SentisOptimisations DrillCutPhysics), the ore drilled. Then the drills
    /// off and no force for <see cref="RestSeconds"/> s, sampled the same way, and the drills on again for
    /// <see cref="ResumeSeconds"/> s. The numbers are for reading; passes when it got into the rock.
    ///
    /// What it found (17.10.2026): the copy in the live ship's own tunnel of a 1 km asteroid cost its cluster 4.4-4.9 ms a
    /// frame drilling and 3.7-4.1 ms awake and still (the probe), 0.3 ms 300 m off the rock and 0.23 ms asleep, with no
    /// contact with the rock at all: Havok works out the distance to the triangles of the rock in its collision pieces'
    /// boxes every frame. The drills were not what it cost. SentisOptimisations' VoxelCoarseNearShips (voxel_coarse) takes
    /// it to 2.9-3.5 ms drilling.
    /// </summary>
    public sealed class PhysicsDrillDeepScenario : TestScenario
    {
        public const string ScenarioName = "physics_drill_deep";
        /// <summary>
        /// The same ship in the same place, its drills off and its body kept awake, <see cref="ProbeSeconds"/> s in each of:
        /// still where it stands, going back out along its tunnel at <see cref="Speed"/>, still 300 m off the asteroid,
        /// and still back where it stood - which part of the cost is the ship awake in the rock, and which is the drilling.
        /// </summary>
        public const string ProbeName = "physics_drill_deep_probe";
        private const double ProbeSeconds = 15;
        private readonly bool _probe;

        /// <summary>A fresh asteroid of the game's (AsteroidPrefab) even when the live ship is in one: a tunnel begun from the rock's face.</summary>
        public const string FreshName = "physics_drill_deep_fresh";
        /// <summary>The probe on a fresh asteroid, after a tunnel of 90 s.</summary>
        public const string ProbeFreshName = "physics_drill_deep_probe_fresh";
        private readonly bool _fresh;

        /// <summary>The drilling with VoxelCoarseNearShips off, for the comparison.</summary>
        public const string OffName = "physics_drill_deep_off";
        private readonly bool _coarseOff;

        /// <summary>
        /// A fresh tunnel (a fresh asteroid) drilled 300 s with the drills' radius at <see cref="_drillRadius"/> times the
        /// game's (SentisGameplayImprovements DrillRadiusMultiplier; the stand's 1.5) and VoxelCoarseNearShips off: how
        /// much a wider tunnel takes off the ship's cost.
        /// </summary>
        public const string RadiusName = "physics_drill_deep_radius";
        private readonly float? _drillRadius;
        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);

        public PhysicsDrillDeepScenario(bool probe = false, bool fresh = false, bool coarseOff = false, float? drillRadius = null)
        {
            _drillRadius = drillRadius;
            if (drillRadius.HasValue)
            {
                fresh = true;
                coarseOff = true;
            }
            _coarseOff = coarseOff;
            _probe = probe;
            _fresh = fresh;
        }

        private const string Prefix = "pdeep-";
        private const string AsteroidPrefab = "Asteroid256-001";
        private const double Speed = 0.3, MaxAccel = 1.5;
        private const double RestSeconds = 30, ResumeSeconds = 40, SampleSeconds = 5;
        private double DrillSeconds => _drillRadius.HasValue ? 300 : 150;

        private readonly ConfigOverride _config = new ConfigOverride();
        private Type _cost;
        private Action<HkWorld, double> _handler;
        private HkWorld _world;
        private readonly List<double> _alone = new List<double>();
        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private List<MyShipDrill> _drills = new List<MyShipDrill>();
        private MyCubeGrid _ship;
        private MyVoxelBase _asteroid;
        private Vector3D _into, _face;
        private double _ore;
        // contact points this frame window: with the rock, with anything else; the deepest penetration with the rock
        private int _rockPoints, _otherPoints;
        private double _maxPush;
        // where on the ship the rock touches it: contact points by the ship's cell, this sample
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Vector3I, int> _touchCells = new System.Collections.Concurrent.ConcurrentDictionary<Vector3I, int>();
        // SentisGameplayImprovements' count of grid-voxel deformations (what makes it "unstick" a grid), taken and cleared every frame
        private System.Collections.IDictionary _sgiContacts;
        private FieldInfo _sgiCount;
        private long _sgiTaken;

        public override string Name => _drillRadius.HasValue ? RadiusName + "_" + (_drillRadius.Value * 100).ToString("0")
            : _coarseOff ? OffName : _probe ? (_fresh ? ProbeFreshName : ProbeName) : _fresh ? FreshName : ScenarioName;
        public override int TimeoutSeconds => (int)(DrillSeconds + RestSeconds + ResumeSeconds) + 240;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _cost = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("Optimizer.Optimizations.PhysicsCost");
            SkipUnless(_cost?.GetField("Sampled", any) != null && _cost.GetField("GapFrames", any) != null, "SentisOptimisations has no cluster timing for tests");
            _config.Set("FreezerEnabled", false);
            _config.Set("EnablePhysicsGuard", false);
            if (_drillRadius.HasValue)
            {
                _gameplay.Set("DrillRadiusMultiplier", _drillRadius.Value);
                Note($"the drills' radius {_drillRadius.Value} times the game's for the ship spawned now");
            }
            // VoxelCoarseNearShips on (unless _off) with the threshold at 0: what is measured is the mechanism. The ship's
            // own load as the setting counts it (PhysicsCost.Share: what its cluster adds to the step of all, the clusters
            // stepped side by side) is noted with every sample - on the stand, beside the bots' clusters, 0.1-0.3 ms for a
            // cluster of 4.4 ms alone (18.10.2026)
            if (_cost.Assembly.GetType("Optimizer.Optimizations.VoxelCoarseNearShips") != null)
            {
                _config.Set("VoxelCoarseNearShips", !_coarseOff);
                _config.Set("VoxelCoarseMinGridMs", 0f);
            }
            // ------------------------------------------------------------- the live ship
            var name = TestRunner.Focus ?? PhysicsDrillLiveScenario.ShipName;
            var live = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.IsStatic && !g.MarkedForClose &&
                    !(g.Name ?? "").StartsWith(WorldApi.EntityPrefix) && (g.DisplayName ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(g => g.BlocksCount).FirstOrDefault();
            Check(live != null, "no dynamic grid named " + name);
            var obs = WorldApi.CopyLiveGroup(live, WorldApi.EntityPrefix + Prefix + "ship");

            // ------------------------------------------------------------- a place in empty space, the asteroid
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            var others = MyEntities.GetEntities().Where(e => e is MyCubeGrid || e is MyVoxelBase && !(e is MyPlanet))
                .Select(e => e.PositionComp.GetPosition()).ToList();
            Vector3D? spot = null;
            for (var i = 0; i < 400 && spot == null; i++)
            {
                var probe = new Vector3D(-760000 + (i % 20) * 79000, 590000 - (i / 20) * 59000, -240000 + (i % 7) * 80000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() > 1e-6) continue;
                if (planets.Any(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) < p.MaximumRadius + 20000)) continue;
                if (others.Any(o => Vector3D.DistanceSquared(o, probe) < 15000.0 * 15000.0)) continue;
                spot = probe;
            }
            Check(spot.HasValue, "no place in empty space");
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, _ => (spot.Value + Vector3D.Up * 300, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(8, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            // The asteroid the live ship is in, copied with its tunnel (its storage, edits and all) and the ship put where it is
            // in it; with none by the ship, a fresh asteroid of the game's (AsteroidPrefab) and the ship a metre short of it.
            var source = MyEntities.GetEntities().OfType<MyVoxelBase>()
                .Where(v => !(v is MyPlanet) && v.RootVoxel == v && v.Storage != null && !(v.StorageName ?? "").StartsWith(WorldApi.EntityPrefix))
                .Select(v => (v, d: v.PositionComp.WorldAABB.Distance(live.PositionComp.WorldAABB.Center)))
                .Where(x => x.d < 100).OrderBy(x => x.d).Select(x => x.v).FirstOrDefault();
            Vector3D heads;
            double? rock;
            if (_fresh) source = null;
            if (source != null)
            {
                var shift = spot.Value - source.PositionComp.WorldAABB.Center;
                _asteroid = MyWorldGenerator.AddVoxelMap(WorldApi.EntityPrefix + Prefix + "asteroid", (Sandbox.Engine.Voxels.MyStorageBase)source.Storage.Copy(),
                    source.PositionLeftBottomCorner + shift);
                Check(_asteroid != null, "the asteroid " + source.StorageName + " was not copied");
                Track(_asteroid);
                var size0 = source.PositionComp.WorldAABB.Size;
                Note($"ASTEROID copy of {source.StorageName} ({source.GetType().Name}): {size0.X:0} x {size0.Y:0} x {size0.Z:0} m, storage {source.Storage.Size}, " +
                     $"the copy off by {Vector3D.Distance(_asteroid.PositionLeftBottomCorner - shift, source.PositionLeftBottomCorner):0.00} m, " +
                     $"turned {(Vector3D.Dot(_asteroid.WorldMatrix.Forward, source.WorldMatrix.Forward) > 0.9999 && Vector3D.Dot(_asteroid.WorldMatrix.Up, source.WorldMatrix.Up) > 0.9999 ? "not" : "WRONG")}");
                yield return WaitForTicks(120);
                foreach (var ob in obs)
                {
                    var m = ob.PositionAndOrientation.Value.GetMatrix();
                    m.Translation += shift;
                    ob.PositionAndOrientation = new VRage.MyPositionAndOrientation(m);
                    ob.DampenersEnabled = false;
                    ob.LinearVelocity = default;
                    ob.AngularVelocity = default;
                }
                foreach (var ob in obs)
                {
                    var grid = WorldApi.SpawnGrid(ob);
                    Track(grid);
                    _grids.Add(grid);
                }
                _ship = _grids[0];
                yield return WaitForTicks(30);
                _drills = _grids.SelectMany(g => WorldApi.FindFunctionals<MyShipDrill>(g)).ToList();
                Check(_drills.Count > 0, "the copy has no drills");
                foreach (var d in _drills) d.Enabled = false;
                foreach (var g in _grids)
                {
                    WorldApi.EnsureDistributor(g);
                    WorldApi.ChargeBatteries(g);
                }
                _into = _drills[0].WorldMatrix.Forward;
                heads = Heads();
                rock = Rock(heads - _into * 3, _into, 300);
                Note($"in its own tunnel: the rock {(rock.HasValue ? (rock.Value - 3).ToString("0.0") + " m" : "not")} ahead of the heads");
                rock = 1;
                _face = heads;
            }
            else
            {
                var prefab = MyDefinitionManager.Static.GetVoxelMapStorageDefinitions().Select(d => d.Id.SubtypeName)
                    .FirstOrDefault(n => n == AsteroidPrefab) ?? MyDefinitionManager.Static.GetVoxelMapStorageDefinitions()
                    .Select(d => d.Id.SubtypeName).FirstOrDefault(n => n.StartsWith("Asteroid256"));
                Check(prefab != null, "no asteroid prefab of 256");
                _asteroid = MyWorldGenerator.AddAsteroidPrefab(prefab, spot.Value, WorldApi.EntityPrefix + Prefix + "asteroid");
                Check(_asteroid != null, "the asteroid " + prefab + " was not made");
                Track(_asteroid);
                yield return WaitForTicks(120);
                var middle = _asteroid.PositionComp.WorldAABB.Center;

                // ------------------------------------------------------------- the copy, 600 m out, turned so its drills face the
                // asteroid's middle (in the object builders: Teleport moves a group and keeps its rotation), then up to the rock
                var original = obs[0].PositionAndOrientation.Value.GetMatrix();
                var drillOb = obs.SelectMany(o => o.CubeBlocks.OfType<MyObjectBuilder_Drill>().Select(b => (o, b))).FirstOrDefault();
                Check(drillOb.b != null, "the copy has no drills");
                var drillGrid = drillOb.o.PositionAndOrientation.Value.GetMatrix();
                var drillsAlong = Vector3D.TransformNormal(Base6Directions.GetVector(drillOb.b.BlockOrientation.Forward), drillGrid.GetOrientation());
                var away = middle + Vector3D.Normalize(new Vector3D(0.3, 1, 0.2)) * 600;
                _into = Vector3D.Normalize(middle - away);
                var axis = Vector3D.Cross(drillsAlong, _into);
                var turn = axis.LengthSquared() < 1e-9
                    ? (Vector3D.Dot(drillsAlong, _into) > 0 ? MatrixD.Identity : MatrixD.CreateFromAxisAngle(Vector3D.CalculatePerpendicularVector(_into), Math.PI))
                    : MatrixD.CreateFromAxisAngle(Vector3D.Normalize(axis), Math.Acos(MathHelper.Clamp(Vector3D.Dot(drillsAlong, _into), -1, 1)));
                foreach (var ob in obs)
                {
                    var m = ob.PositionAndOrientation.Value.GetMatrix();
                    var placed = MatrixD.CreateWorld(away + Vector3D.TransformNormal(m.Translation - original.Translation, turn),
                        Vector3D.TransformNormal(m.Forward, turn), Vector3D.TransformNormal(m.Up, turn));
                    ob.PositionAndOrientation = new VRage.MyPositionAndOrientation(placed);
                    ob.DampenersEnabled = false;
                    ob.LinearVelocity = default;
                    ob.AngularVelocity = default;
                }
                foreach (var ob in obs)
                {
                    var grid = WorldApi.SpawnGrid(ob);
                    Track(grid);
                    _grids.Add(grid);
                }
                _ship = _grids[0];
                yield return WaitForTicks(30);
                _drills = _grids.SelectMany(g => WorldApi.FindFunctionals<MyShipDrill>(g)).ToList();
                Check(_drills.Count > 0, "the copy has no drills");
                foreach (var d in _drills) d.Enabled = false;
                foreach (var g in _grids)
                {
                    WorldApi.EnsureDistributor(g);
                    WorldApi.ChargeBatteries(g);
                }
                Note("drills along the line to the middle: " + Vector3D.Dot((Vector3D)_drills[0].WorldMatrix.Forward, _into).ToString("0.000"));
                // the heads put on the line through the middle, then a metre short of the rock
                heads = Heads();
                var offLine = heads - away;
                offLine -= _into * Vector3D.Dot(offLine, _into);
                Shift(-offLine);
                yield return WaitForTicks(10);
                heads = Heads();
                rock = Rock(heads, _into, 1000);
                Check(rock.HasValue, "no rock on the line to the middle of the asteroid");
                Shift(_into * (rock.Value - 1));
                yield return WaitForTicks(10);
                var check = Rock(Heads(), _into, 1000);
                Note($"the heads {(check ?? -1):0.00} m short of the rock (asked 1), {Vector3D.Distance(Heads(), middle):0.0} m from the middle");
                foreach (var g in _grids)
                    if (g.Physics != null)
                    {
                        g.Physics.LinearVelocity = Vector3.Zero;
                        g.Physics.AngularVelocity = Vector3.Zero;
                    }
                heads = Heads();
                rock = 1;
                _face = heads + _into * rock.Value;
            }
            var size = _ship.PositionComp.LocalAABB.Size;
            Note($"SHIP copy of '{live.DisplayName}' ({live.EntityId}): {_grids.Count} grids, {_grids.Sum(g => g.BlocksCount)} blocks, " +
                 $"{_drills.Count} drills ({string.Join(",", _drills.Select(d => d.BlockDefinition.Id.SubtypeName).Distinct())}), mass {_ship.Physics?.Mass:0} kg, " +
                 $"box {size.X:0.0} x {size.Y:0.0} x {size.Z:0.0} m; the face {rock.Value - 1:0.0} m on, ship radius on the drill axis {Across():0.0} m, " +
                 $"the drills' cut across {DrilledAcross():0.0} m");

            BindSgi();

            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, _ => (_face - _into * 80 + Vector3D.CalculatePerpendicularVector(_into) * 30, 0, 0), withCharacters: true);
            var nearFrom = DateTime.UtcNow;
            while ((DateTime.UtcNow - nearFrom).TotalSeconds < 6)
            {
                TakeSgi();
                yield return null;
            }
            EmptyOre();
            _ore = 0;

            // ------------------------------------------------------------- timing and touches
            _handler = (w, ms) => { if (w == _world) _alone.Add(ms); };
            _cost.GetField("Sampled", any).SetValue(null, _handler);
            _cost.GetField("GapFrames", any).SetValue(null, 1);
            foreach (var g in _grids) Listen(g);

            if (_probe && _fresh)
            {
                foreach (var d in _drills) d.Enabled = true;
                var first = Phase("probe: drilling a tunnel first", 90, drive: true);
                while (first.MoveNext()) yield return first.Current;
                foreach (var d in _drills) d.Enabled = false;
            }
            if (_probe)
            {
                var start = _ship.WorldMatrix;
                if (_ship.Physics?.RigidBody != null) _ship.Physics.RigidBody.EnableDeactivation = false;
                var still = Phase("probe: awake, still where it stands", ProbeSeconds, drive: false);
                while (still.MoveNext()) yield return still.Current;
                _into = -_into;
                var back = Phase("probe: awake, going back out along its tunnel", ProbeSeconds, drive: true);
                while (back.MoveNext()) yield return back.Current;
                _into = -_into;
                var outside = _ship.WorldMatrix;
                outside.Translation = _asteroid.PositionComp.WorldAABB.Center + Vector3D.Normalize(start.Translation - _asteroid.PositionComp.WorldAABB.Center) *
                    (_asteroid.PositionComp.WorldAABB.HalfExtents.Length() + 300);
                _ship.Teleport(outside);
                yield return WaitForTicks(30);
                var off = Phase("probe: awake, still 300 m off the asteroid", ProbeSeconds, drive: false);
                while (off.MoveNext()) yield return off.Current;
                _ship.Teleport(start);
                yield return WaitForTicks(30);
                var again = Phase("probe: awake, still back where it stood", ProbeSeconds, drive: false);
                while (again.MoveNext()) yield return again.Current;

                yield break;
            }

            foreach (var d in _drills) d.Enabled = true;
            var drilling = Phase("drilling", DrillSeconds, drive: true);
            while (drilling.MoveNext()) yield return drilling.Current;
            foreach (var d in _drills) d.Enabled = false;
            var rest = Phase("drills off, no force", RestSeconds, drive: false);
            while (rest.MoveNext()) yield return rest.Current;
            foreach (var d in _drills) d.Enabled = true;
            var resume = Phase("drilling again", ResumeSeconds, drive: true);
            while (resume.MoveNext()) yield return resume.Current;
            foreach (var d in _drills) d.Enabled = false;

            Note($"DEEP RESULT | depth {Depth():0.0} m, ore {_ore / 1000:0.0} t, " + Cells());
            Check(Depth() > 2, "the ship did not get into the rock: depth " + Depth().ToString("0.0") + " m");
        }

        private IEnumerator Phase(string label, double seconds, bool drive)
        {
            var began = DateTime.UtcNow;
            var sampleFrom = DateTime.UtcNow;
            var depthFrom = Depth();
            int frames = 0, rock = 0, other = 0;
            long hits = 0;
            double push = 0;
            _alone.Clear();
            var cuts = Cuts();
            while ((DateTime.UtcNow - began).TotalSeconds < seconds)
            {
                _world = _ship.Physics?.HavokWorld;
                if (drive) Drive(); else Hold();
                _rockPoints = _otherPoints = 0;
                yield return null;
                TakeSgi();
                if (_probe) hits += Hits();
                frames++;
                rock += _rockPoints;
                other += _otherPoints;
                push = Math.Max(push, _maxPush);
                _maxPush = 0;
                if ((DateTime.UtcNow - sampleFrom).TotalSeconds < SampleSeconds) continue;
                var dt = (DateTime.UtcNow - sampleFrom).TotalSeconds;
                var depth = Depth();
                var ore = EmptyOre();
                _ore += ore;
                var now = Cuts();
                var alone = _alone.OrderBy(v => v).ToList();
                var sgi = _sgiTaken;
                _sgiTaken = 0;
                var touched = Touched();
                Note($"{label} {(DateTime.UtcNow - began).TotalSeconds:0}s | depth {depth:0.0} m, made {(depth - depthFrom) / dt:0.000} m/s (asked {(drive ? Speed : 0):0.00}), " +
                     $"cluster alone median {(alone.Count == 0 ? 0 : alone[alone.Count / 2]):0.00} ms, max {(alone.Count == 0 ? 0 : alone[alone.Count - 1]):0.00} ms ({alone.Count}/{frames} frames timed); " +
                     $"contact points a frame: rock {rock / (double)frames:0.0}, other {other / (double)frames:0.0}, deepest push {push:0.00} m/s; " +
                     $"SGI voxel deformations {sgi / dt:0}/s; touched at {touched}; ore {ore / dt:0.0} kg/s; drill cut-outs: {Delta(cuts, now)}; {Cells()}; awake bodies of the ship {Awake()}; " +
                     $"ship speed {_ship.Physics?.LinearVelocity.Length():0.00}, spin {_ship.Physics?.AngularVelocity.Length():0.000}; {Coarse()}");
                sampleFrom = DateTime.UtcNow;
                depthFrom = depth;
                if (_probe) Note($"{label} {(DateTime.UtcNow - began).TotalSeconds:0}s | VOXEL rock queries {hits / (double)frames:0} a frame; {CellStats()}");
                hits = 0;
                frames = rock = other = 0;
                push = 0;
                _alone.Clear();
                cuts = now;
            }
        }

        /// <summary>A pilot holding "forward" with the dampeners on: <see cref="Speed"/> along the drills, nothing sideways.</summary>
        private void Drive()
        {
            var physics = _ship.Physics;
            if (physics == null) return;
            var mass = _grids.Sum(g => g.Physics?.Mass ?? 0);
            var v = (Vector3D)physics.LinearVelocity;
            var along = Vector3D.Dot(v, _into);
            var side = v - _into * along;
            var a = _into * MathHelper.Clamp((Speed - along) * 20, -MaxAccel, MaxAccel) - side * 20;
            if (a.Length() > MaxAccel) a = a / a.Length() * MaxAccel;
            physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, (Vector3)(a * mass), null, null);
        }

        private void Hold()
        {
            var physics = _ship.Physics;
            if (physics == null) return;
            var mass = _grids.Sum(g => g.Physics?.Mass ?? 0);
            var a = -(Vector3D)physics.LinearVelocity * 3;
            if (a.Length() > MaxAccel) a = a / a.Length() * MaxAccel;
            if (a.LengthSquared() > 1e-6) physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, (Vector3)(a * mass), null, null);
        }

        private void Listen(MyCubeGrid grid)
        {
            var body = grid.Physics?.RigidBody;
            if (body == null) return;
            var asteroid = _asteroid;
            body.ContactPointCallback += (ref HkContactPointEvent e) =>
            {
                var a = e.Base.BodyA?.UserObject as MyPhysicsBody;
                var b = e.Base.BodyB?.UserObject as MyPhysicsBody;
                if (a?.Entity == asteroid || b?.Entity == asteroid)
                {
                    _rockPoints++;
                    if (_touchCells.Count < 4000)
                    {
                        var cell = _ship.WorldToGridInteger(_ship.Physics.ClusterToWorld(e.ContactPoint.Position));
                        _touchCells.AddOrUpdate(cell, 1, (_, n) => n + 1);
                    }
                    var v = Math.Abs(e.SeparatingVelocity);
                    if (v > _maxPush) _maxPush = v;
                }
                else _otherPoints++;
            };
            body.ContactPointCallbackEnabled = true;
        }

        private void BindSgi()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisGameplayImprovements.DamagePatch")).FirstOrDefault(t => t != null);
            var field = type?.GetField("contactInfo", BindingFlags.Static | BindingFlags.Public);
            if (field == null)
            {
                Note("SentisGameplayImprovements' voxel contact count not found: the copy may be taken for stuck");
                return;
            }
            _sgiField = field;
            _sgiCount = field.FieldType.GetGenericArguments()[1].GetField("Count");
        }

        private FieldInfo _sgiField;

        /// <summary>
        /// The copy's grid-voxel deformations SentisGameplayImprovements counted since the last frame, taken out of its count:
        /// left there, a ship drilling a tunnel is "stuck" after 5 s of 500 a second and is moved 1 km away (Voxels.Unstick).
        /// </summary>
        private void TakeSgi()
        {
            if (_sgiField == null || _sgiCount == null) return;
            _sgiContacts = _sgiField.GetValue(null) as System.Collections.IDictionary;
            if (_sgiContacts == null) return;
            foreach (var g in _grids)
            {
                if (!_sgiContacts.Contains(g.EntityId)) continue;
                var info = _sgiContacts[g.EntityId];
                _sgiTaken += info == null ? 0 : (long)_sgiCount.GetValue(info);
                _sgiContacts.Remove(g.EntityId);
            }
        }

        /// <summary>The cells of the ship the rock touched most this sample: along the drills from their heads, out from their axis.</summary>
        private string Touched()
        {
            if (_touchCells.IsEmpty) return "nothing";
            var heads = Heads();
            var total = _touchCells.Values.Sum();
            var top = _touchCells.OrderByDescending(p => p.Value).Take(4).Select(p =>
            {
                var at = _ship.GridIntegerToWorld(p.Key) - heads;
                var along = Vector3D.Dot(at, _into);
                var r = (at - _into * along).Length();
                var block = _ship.GetCubeBlock(p.Key);
                return $"{100.0 * p.Value / total:0}% {-along:0.0} m behind the heads, {r:0.0} m out ({block?.BlockDefinition.Id.SubtypeName ?? "-"})";
            }).ToList();
            var cells = _touchCells.Count;
            _touchCells.Clear();
            return cells + " cells: " + string.Join("; ", top);
        }

        /// <summary>Every grid of the copy moved by the same offset (Teleport left the ship where it was here).</summary>
        private void Shift(Vector3D by)
        {
            foreach (var g in _grids)
            {
                var m = g.WorldMatrix;
                m.Translation += by;
                g.PositionComp.SetWorldMatrix(ref m);
            }
        }

        private Vector3D Heads() => _drills.Select(d => d.PositionComp.WorldAABB.Center).Aggregate((x, y) => x + y) / _drills.Count + (Vector3D)_drills[0].WorldMatrix.Forward * 2.5;

        private double Depth() => Vector3D.Dot(Heads() - _face, _into);

        /// <summary>How far the ship's grids reach from the drill axis (the tunnel must be as wide).</summary>
        private double Across()
        {
            var heads = Heads();
            double max = 0;
            foreach (var g in _grids)
            foreach (var b in g.CubeBlocks)
            {
                var p = g.GridIntegerToWorld(b.Position) - heads;
                var r = (p - _into * Vector3D.Dot(p, _into)).Length() + g.GridSize / 2;
                if (r > max) max = r;
            }
            return max;
        }

        /// <summary>How far from the drill axis the drills cut: the farthest drill head and its cut-out sphere.</summary>
        private double DrilledAcross()
        {
            var heads = Heads();
            double max = 0;
            foreach (var d in _drills)
            {
                var p = d.PositionComp.WorldAABB.Center - heads;
                var r = (p - _into * Vector3D.Dot(p, _into)).Length() + (d.CubeGrid.GridSizeEnum == MyCubeSize.Large ? 2.5 : 0.75);
                if (r > max) max = r;
            }
            return max;
        }

        /// <summary>The rock's queries counter (HkUniformGridShape.GetHitsAndClear): cell look-ups since the last read.</summary>
        private long Hits()
        {
            var physics = _asteroid?.Physics as MyPhysicsBody;
            if (physics?.RigidBody == null) return 0;
            return ((HkUniformGridShape)physics.RigidBody.GetShape()).GetHitsAndClear();
        }

        /// <summary>
        /// The rock's cells with a collision mesh in the ship's box (as the physics sees it: its world box and the collision
        /// tolerance), their triangles, the box itself, and the ship's own shapes.
        /// </summary>
        private string CellStats()
        {
            var physics = _asteroid?.Physics as MyPhysicsBody;
            if (physics?.RigidBody == null) return "no rock body";
            var shape = (HkUniformGridShape)physics.RigidBody.GetShape();
            var box = _ship.PositionComp.WorldAABB;
            box.Inflate(0.1);
            var local = box.TransformFast(_asteroid.PositionComp.WorldMatrixInvScaled);
            local.Translate(_asteroid.SizeInMetresHalf);
            Vector3 lmin = local.Min, lmax = local.Max;
            MyVoxelCoordSystems.LocalPositionToVoxelCoord(ref lmin, out Vector3I vmin);
            MyVoxelCoordSystems.LocalPositionToVoxelCoord(ref lmax, out Vector3I vmax);
            _asteroid.Storage.ClampVoxelCoord(ref vmin);
            _asteroid.Storage.ClampVoxelCoord(ref vmax);
            MyVoxelCoordSystems.VoxelCoordToGeometryCellCoord(ref vmin, out var cmin);
            MyVoxelCoordSystems.VoxelCoordToGeometryCellCoord(ref vmax, out var cmax);
            int inBox = 0, meshed = 0, missing = 0;
            long triangles = 0;
            int biggest = 0;
            for (var x = cmin.X; x <= cmax.X; x++)
            for (var y = cmin.Y; y <= cmax.Y; y++)
            for (var z = cmin.Z; z <= cmax.Z; z++)
            {
                inBox++;
                if (!shape.GetChild(x, y, z, out var mesh)) { missing++; continue; }
                if (mesh.IsZero) continue;
                meshed++;
                var geometry = new HkGeometry();
                mesh.GetGeometry(geometry);
                triangles += geometry.TriangleCount;
                biggest = Math.Max(biggest, geometry.TriangleCount);
                geometry.Dispose();
            }
            var size = box.Size;
            var gridShape = (_ship.Physics as MyGridPhysics)?.Shape;
            var lod1 = physics.RigidBody2 == null ? "" : $"; LOD1 body: {((HkUniformGridShape)physics.RigidBody2.GetShape()).ShapeCount} cells";
            return $"ship box {size.X:0} x {size.Y:0} x {size.Z:0} m{lod1}: {inBox} rock cells in it, {meshed} with a mesh, {missing} not made; " +
                   $"{triangles} triangles (the biggest cell {biggest}); the ship's shapes {gridShape?.ShapeCount}";
        }

        /// <summary>SentisOptimisations' VoxelCoarseNearShips so far: coarse cells now, made, rejected (too near the hull), reverted.</summary>
        private long[] _coarseStart;

        private string Coarse()
        {
            var coarse = _cost?.Assembly.GetType("Optimizer.Optimizations.VoxelCoarseNearShips");
            var now = CoarseCost.Take(coarse);
            _coarseStart = _coarseStart ?? now;
            var load = coarse?.GetMethod("LoadOf", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, new object[] { _ship }) is double l ? l : -1;
            return CoarseCellsNow() + $", the ship's load {load:0.000} ms; " + CoarseCost.Describe(_coarseStart, now);
        }

        private string CoarseCellsNow()
        {
            var type = _cost?.Assembly.GetType("Optimizer.Optimizations.VoxelCoarseNearShips");
            if (type == null) return "no coarse rock";
            long F(string name) => (long)type.GetField(name, BindingFlags.Static | BindingFlags.Public).GetValue(null);
            return $"coarse rock cells {F("CoarseCells")}";
        }

        private string Awake() => _grids.Count(g => g.Physics?.IsActive == true) + "/" + _grids.Count;

        private string Cells()
        {
            var physics = _asteroid?.Physics as MyPhysicsBody;
            if (physics?.RigidBody == null) return "rock cells -";
            return "rock cells " + ((HkUniformGridShape)physics.RigidBody.GetShape()).ShapeCount;
        }

        private static readonly string[] CutFields = { "KeptCells", "InvalidatedCells", "UnchangedEmptyCells", "UntouchedCells", "HeldMeshCount", "ShapeUpdatesApplied" };

        private long[] Cuts()
        {
            var type = _cost?.Assembly.GetType("Optimizer.Optimizations.DrillCutPhysics");
            return CutFields.Select(f => type?.GetField(f, BindingFlags.Static | BindingFlags.Public)?.GetValue(null) is long v ? v : -1).ToArray();
        }

        private static string Delta(long[] from, long[] to) =>
            string.Join(", ", CutFields.Select((f, i) => f.Replace("Cells", "").Replace("Count", "") + " " + (to[i] < 0 ? "?" : (to[i] - from[i]).ToString())));

        /// <summary>Takes the ore out of the ship's inventories; how much it was, kg.</summary>
        private double EmptyOre()
        {
            double kg = 0;
            foreach (var g in _grids)
            foreach (var block in g.GetFatBlocks())
                for (var i = 0; i < block.InventoryCount; i++)
                {
                    if (!(block.GetInventoryBase(i) is Sandbox.Game.MyInventory inv)) continue;
                    foreach (var item in inv.GetItems().ToList())
                        if (item.Content is MyObjectBuilder_Ore)
                        {
                            kg += (double)item.Amount;
                            inv.RemoveItems(item.ItemId);
                        }
                }
            return kg;
        }

        private static double? Rock(Vector3D from, Vector3D dir, double length)
        {
            var hits = new List<MyPhysics.HitInfo>();
            MyPhysics.CastRay(from, from + dir * length, hits, MyPhysics.CollisionLayers.CollideWithStaticLayer);
            foreach (var hit in hits)
                if (hit.HkHitInfo.GetHitEntity() is MyVoxelBase) return Vector3D.Distance(from, hit.Position);
            return null;
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                if (_cost != null)
                {
                    var sampled = _cost.GetField("Sampled", any);
                    if (sampled != null && (Delegate)sampled.GetValue(null) == (Delegate)_handler) sampled.SetValue(null, null);
                    _cost.GetField("GapFrames", any)?.SetValue(null, _cost.GetField("MeasureGapFrames", any)?.GetRawConstantValue() ?? 30);
                }
                _config.Restore();
                _gameplay.Restore();
            }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                _config.Restore();
                _gameplay.Restore();
            }
            finally { base.CleanupLeftovers(); }
        }
    }
}
