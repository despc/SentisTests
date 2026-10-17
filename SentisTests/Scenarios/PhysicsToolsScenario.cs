using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;
using SpaceWelder = SpaceEngineers.Game.Entities.Blocks.MyShipWelder;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What a ship's tools cost the physics, against what SentisOptimisations lays to the ship (PhysicsCost). Reported on
    /// the stand (17.10.2026): a drill ship of 159 blocks drilling an asteroid was the dearest grid of the physics while
    /// the step as a whole hardly moved; and welding a dynamic grid seemed to count more than it costs.
    ///
    /// Every case stands alone in a cluster of its own in empty space with a fake player by it, and its cluster is timed
    /// every frame (<c>GapFrames</c> 1). The drill ship is the stand's own (<see cref="ShipResource"/>, from the save): in
    /// empty space kept awake; by an asteroid of its own (<see cref="AsteroidPrefab"/>) 5 m off its rock, pressed on it
    /// with the drills off, and drilling into it at <see cref="DrillSpeed"/>; the asteroid alone. The welding: a dynamic
    /// plate of 7 x 5 with ten welders on it builds a wall of light armour from its own projection, over and over (the
    /// wall razed once it is up), and the same plate with the welders on and nothing to build.
    ///
    /// Each case's step over the empty cluster of its kind (space or the asteroid) is what the case costs; PhysicsCost
    /// lays to it its cluster's step less the cluster's own, by its work in the cluster. Both are noted
    /// with what made the work: the ground under the ship, the touches, the shapes. Passes when every case was measured;
    /// the numbers are for reading.
    /// </summary>
    public sealed class PhysicsToolsScenario : TestScenario
    {
        public const string ScenarioName = "physics_tools";
        internal const string ShipResource = "SentisTests.Resources.PhysicsDrillShip.xml";
        private const string AsteroidPrefab = "Asteroid256-001";
        private const string Prefix = "ptool-";
        private const int RunSeconds = 60;
        private const double DrillSpeed = 0.5, NoGridsWithin = 15000, Apart = 60000;
        private const string Armor = "LargeBlockArmorBlock";

        private enum Kind { Empty, ShipAwake, AsteroidEmpty, ShipNear, ShipPressed, ShipDrilling, ShipTunnel, Welding, WeldersIdle, WeldShip, WeldShipIdle }

        private sealed class Case
        {
            public string Name;
            public Kind Kind;
            public Vector3D Spot;
            public MyVoxelBase Asteroid;
            public Vector3D Into;
            public MyCubeGrid Grid;
            /// <summary>The welder grid lying on the plate (the welding by a ship).</summary>
            public MyCubeGrid Second;
            public bool Touched;
            public List<MyShipDrill> Drills = new List<MyShipDrill>();
            public List<SpaceWelder> Welders = new List<SpaceWelder>();
            public MyProjectorBase Projector;
            public List<Vector3I> Wall = new List<Vector3I>();
            public int Walls, WallBlocks;
            public readonly List<Sample> Samples = new List<Sample>();
        }

        private struct Sample
        {
            public double Ms, Laid, Work, CaseWork;
            public bool Voxels, Grids;
            public int Shapes;
        }

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<Case> _cases = new List<Case>();
        private readonly Dictionary<HkWorld, Case> _byWorld = new Dictionary<HkWorld, Case>();
        private Type _cost;
        private MethodInfo _work, _gridWork, _describe;
        private double _own;
        private Action<HkWorld, double> _handler;
        private bool _ownedDlcs;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 420;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _cost = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("Optimizer.Optimizations.PhysicsCost");
            _work = _cost?.GetMethod("Work", any, null, new[] { typeof(HkWorld) }, null);
            _gridWork = _cost?.GetMethod("GridWork", any);
            _describe = _cost?.GetMethod("Describe", any);
            SkipUnless(_work != null && _gridWork != null && _describe != null && _cost.GetField("Sampled", any) != null,
                "SentisOptimisations has no timing of the physics clusters for tests");
            _own = Convert.ToDouble(_cost.GetField("ClusterOwnWork", any).GetRawConstantValue());
            _config.Set("FreezerEnabled", false);
            _config.Set("EnablePhysicsGuard", false);
            _ownedDlcs = Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS;
            Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS = true;

            // ------------------------------------------------------------- places in empty space, nobody near
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            var others = MyEntities.GetEntities().Where(e => e is MyCubeGrid || e is MyVoxelBase && !(e is MyPlanet))
                .Select(e => e.PositionComp.GetPosition()).ToList();
            var spots = new List<Vector3D>();
            for (var i = 0; i < 400 && spots.Count < 11; i++)
            {
                var probe = new Vector3D(-850000 + (i % 20) * 85000, -650000 + (i / 20) * 65000, -300000 + (i % 7) * 110000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() > 1e-6) continue;
                if (planets.Any(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) < p.MaximumRadius + 20000)) continue;
                if (others.Any(o => Vector3D.DistanceSquared(o, probe) < NoGridsWithin * NoGridsWithin)) continue;
                if (spots.Any(s => Vector3D.Distance(s, probe) < Apart)) continue;
                spots.Add(probe);
            }
            Check(spots.Count == 11, $"only {spots.Count} of 11 places in empty space");
            var k = 0;
            foreach (var (name, kind) in new[]
                     {
                         ("space_empty", Kind.Empty), ("space_ship_awake", Kind.ShipAwake), ("asteroid_empty", Kind.AsteroidEmpty),
                         ("asteroid_ship_near", Kind.ShipNear), ("asteroid_ship_pressed", Kind.ShipPressed), ("asteroid_ship_drilling", Kind.ShipDrilling),
                         ("asteroid_ship_tunnel", Kind.ShipTunnel),
                         ("weld_dynamic", Kind.Welding), ("weld_dynamic_idle", Kind.WeldersIdle),
                         ("weld_by_ship", Kind.WeldShip), ("weld_by_ship_idle", Kind.WeldShipIdle),
                     })
                _cases.Add(new Case { Name = name, Kind = kind, Spot = spots[k++] });

            FakeClients.RemoveAll();
            FakeClients.Add(_cases.Count, new FakeClients.NetworkProfile { RttMs = 30 }, i => (_cases[i].Spot + Vector3D.Up * 200, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(8, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;

            // ------------------------------------------------------------- the asteroids and the ships
            var prefab = MyDefinitionManager.Static.GetVoxelMapStorageDefinitions().Select(d => d.Id.SubtypeName)
                .FirstOrDefault(n => n == AsteroidPrefab) ?? MyDefinitionManager.Static.GetVoxelMapStorageDefinitions()
                .Select(d => d.Id.SubtypeName).FirstOrDefault(n => n.StartsWith("Asteroid256"));
            Check(prefab != null, "no asteroid prefab of 256");
            foreach (var c in _cases.Where(c => c.Kind >= Kind.AsteroidEmpty && c.Kind <= Kind.ShipTunnel))
            {
                c.Asteroid = MyWorldGenerator.AddAsteroidPrefab(prefab, c.Spot, WorldApi.EntityPrefix + Prefix + c.Name);
                Check(c.Asteroid != null, "the asteroid " + prefab + " was not made");
                Track(c.Asteroid);
            }
            yield return WaitForTicks(30);

            foreach (var c in _cases)
            {
                if (c.Kind == Kind.ShipAwake) c.Grid = SpawnShip(c, c.Spot, Vector3D.Forward);
                else if (c.Kind >= Kind.ShipNear && c.Kind <= Kind.ShipTunnel)
                {
                    // the drills towards the middle of the asteroid, the ship's front 60 m out of the rock
                    var middle = c.Asteroid.PositionComp.WorldAABB.Center;
                    c.Into = Vector3D.Normalize(new Vector3D(0.3, -1, 0.2));
                    var from = middle - c.Into * 300;
                    var rock = Surface(from, c.Into, 600);
                    Check(rock.HasValue, c.Name + ": no rock along the line to the middle of its asteroid");
                    c.Grid = SpawnShip(c, rock.Value - c.Into * 60, c.Into);
                }
                else if (Welds(c))
                {
                    c.Grid = WorldApi.SpawnGrid(WeldPlate(c));
                    Track(c.Grid);
                    if (c.Kind == Kind.WeldShip || c.Kind == Kind.WeldShipIdle)
                    {
                        c.Second = WorldApi.SpawnGrid(WelderBar(c));
                        Track(c.Second);
                    }
                }
            }
            yield return WaitForTicks(60);

            // ------------------------------------------------------------- the ship in a tunnel: 30 m into the rock, 3 m clear of it all round
            foreach (var c in _cases.Where(c => c.Kind == Kind.ShipTunnel))
            {
                var target = c.Grid.WorldMatrix;
                target.Translation += c.Into * 90;
                var box = (BoundingBoxD)c.Grid.PositionComp.LocalAABB;
                box.Inflate(3);
                box.Min.Y -= 60;
                Sandbox.Engine.Voxels.MyVoxelGenerator.CutOutShapeWithProperties(c.Asteroid, new Sandbox.Engine.Voxels.MyShapeBox { Boundaries = box, Transformation = target },
                    out var cut, out _, null, updateSync: true);
                Note($"{c.Name}: the tunnel cut, {cut:0.0}% of its box was rock");
                yield return WaitForTicks(60);
                c.Grid.Teleport(target);
                c.Grid.Physics.LinearVelocity = Vector3.Zero;
            }

            // ------------------------------------------------------------- the drill ships come to the rock
            foreach (var c in _cases.Where(c => c.Kind >= Kind.ShipNear && c.Kind <= Kind.ShipDrilling))
            {
                var body = c.Grid.Physics?.RigidBody;
                Check(body != null, c.Name + ": the ship has no body");
                var asteroid = c.Asteroid;
                var mine = c;
                body.ContactPointCallback += (ref HkContactPointEvent e) =>
                {
                    if (e.Base.BodyA?.UserObject is MyPhysicsBody a && a.Entity == asteroid || e.Base.BodyB?.UserObject is MyPhysicsBody b && b.Entity == asteroid)
                        mine.Touched = true;
                };
                body.ContactPointCallbackEnabled = true;
            }
            var coming = _cases.Where(c => c.Kind >= Kind.ShipNear && c.Kind <= Kind.ShipDrilling).ToList();
            var approach = DateTime.UtcNow;
            while (coming.Any(c => !c.Touched) && (DateTime.UtcNow - approach).TotalSeconds < 40)
            {
                foreach (var c in coming) Push(c, c.Touched ? 0 : 5);
                yield return null;
            }
            Check(coming.All(c => c.Touched), "the ships did not reach the rock: " + string.Join(", ", coming.Where(c => !c.Touched).Select(c => c.Name)));
            foreach (var c in coming)
            {
                if (c.Kind != Kind.ShipNear) continue;
                var m = c.Grid.WorldMatrix;
                m.Translation -= c.Into * 5;
                c.Grid.Teleport(m);
            }
            foreach (var c in _cases.Where(c => c.Grid != null && !Welds(c)))
            {
                c.Grid.Physics.LinearVelocity = Vector3.Zero;
                c.Grid.Physics.AngularVelocity = Vector3.Zero;
            }

            // ------------------------------------------------------------- the welders: power, steel, the projection
            foreach (var c in _cases.Where(Welds))
            {
                foreach (var g in new[] { c.Grid, c.Second }.Where(g => g != null))
                {
                    WorldApi.EnsureDistributor(g);
                    Check(WorldApi.ChargeBatteries(g) > 0f, c.Name + ": no battery charged on " + g.DisplayName);
                }
                c.Welders = WorldApi.FindFunctionals<SpaceWelder>(c.Second ?? c.Grid);
                Check(c.Welders.Count == 10, c.Name + ": " + c.Welders.Count + " welders");
                foreach (var w in c.Welders) Stock(w);
                if (Projects(c))
                {
                    c.Projector = WorldApi.FindFunctional<MyProjectorBase>(c.Grid);
                    Check(c.Projector != null, c.Name + ": no projector");
                    c.Projector.Enabled = true;
                }
            }
            var welding = _cases.First(c => c.Kind == Kind.Welding);
            var up = Wait(() =>
            {
                foreach (var c in _cases.Where(Projects)) WorldApi.ChargeBatteries(c.Grid);
                return _cases.Where(Projects).All(c => c.Projector.IsWorking && c.Projector.ProjectedGrid != null);
            }, "the projections up", 60);
            while (up.MoveNext()) yield return up.Current;
            var projected = welding.Projector.ProjectedGrid;
            var aligned = Vector3D.Distance(projected.PositionComp.GetPosition(), welding.Grid.PositionComp.GetPosition()) < 0.1 &&
                          Vector3D.Dot(projected.WorldMatrix.Forward, welding.Grid.WorldMatrix.Forward) > 0.999 && Vector3D.Dot(projected.WorldMatrix.Up, welding.Grid.WorldMatrix.Up) > 0.999;
            Check(aligned, "the projection is not laid over the plate: at " + projected.PositionComp.GetPosition() + ", the plate at " + welding.Grid.PositionComp.GetPosition());
            var buildable = projected.CubeBlocks.Count(b => welding.Projector.CanBuild(b, true) == Sandbox.ModAPI.BuildCheckResult.OK);
            Note($"the wall: {welding.Wall.Count} blocks projected, {buildable} buildable now; welders' reach {WorldApi.SensorSphere(welding.Welders[0]).Radius:0.0} m");
            Check(buildable > 0, "nothing of the wall is buildable");
            foreach (var c in _cases.Where(Welds))
                foreach (var w in c.Welders)
                {
                    w.Enabled = true;
                    if (!WorldApi.ToolIsActivated(w)) WorldApi.ToolStartShooting(w);
                }
            foreach (var g in _cases.SelectMany(c => new[] { c.Grid, c.Second }).Where(g => g?.Physics?.RigidBody != null)) g.Physics.RigidBody.EnableDeactivation = false;
            foreach (var c in _cases.Where(c => c.Kind == Kind.ShipDrilling))
                foreach (var d in c.Drills) d.Enabled = true;

            // ------------------------------------------------------------- a cluster timed every frame
            _handler = OnSampled;
            _cost.GetField("Sampled", any).SetValue(null, _handler);
            _cost.GetField("GapFrames", any).SetValue(null, 1);
            var started = DateTime.UtcNow;
            var lastNote = started;
            var frame = 0;
            while ((DateTime.UtcNow - started).TotalSeconds < RunSeconds)
            {
                _byWorld.Clear();
                foreach (var c in _cases)
                {
                    var world = c.Grid?.Physics?.HavokWorld ?? FakeClients.Character(_cases.IndexOf(c))?.Physics?.HavokWorld;
                    if (world != null && !_byWorld.ContainsKey(world)) _byWorld[world] = c;
                    if (c.Kind == Kind.ShipPressed || c.Kind == Kind.ShipDrilling) Push(c, c.Kind == Kind.ShipDrilling ? DrillSpeed : 0.2);
                    else if (c.Kind == Kind.ShipNear || c.Kind == Kind.ShipAwake || c.Kind == Kind.ShipTunnel) Push(c, 0);
                }
                if (frame++ % 30 == 0)
                    foreach (var c in _cases.Where(Welds))
                    {
                        WorldApi.ChargeBatteries(c.Grid);
                        if (c.Second != null) WorldApi.ChargeBatteries(c.Second);
                        foreach (var w in c.Welders) Stock(w);
                        if (Projects(c)) Reweld(c);
                    }
                if ((DateTime.UtcNow - lastNote).TotalSeconds >= 10)
                {
                    lastNote = DateTime.UtcNow;
                    Note($"measuring ({(lastNote - started).TotalSeconds:0}/{RunSeconds} s): " + string.Join(", ", _cases.Select(c => $"{c.Name} {c.Samples.Count}")) +
                         $"; walls built {welding.Walls}, wall blocks now {WallBuilt(welding)}/{welding.Wall.Count}");
                }
                yield return null;
            }
            StopSampling();
            foreach (var c in _cases)
                foreach (var d in c.Drills) d.Enabled = false;

            // ------------------------------------------------------------- what came out
            double Median(IEnumerable<double> xs)
            {
                var l = xs.OrderBy(x => x).ToList();
                return l.Count == 0 ? 0 : l[l.Count / 2];
            }
            var empty = Median(_cases.First(c => c.Kind == Kind.Empty).Samples.Select(s => s.Ms));
            var rockMs = Median(_cases.First(c => c.Kind == Kind.AsteroidEmpty).Samples.Select(s => s.Ms));
            foreach (var c in _cases)
            {
                var s = c.Samples;
                if (s.Count == 0) { Note($"TOOLS {c.Name} | not measured"); continue; }
                var baseline = c.Asteroid != null ? rockMs : empty;
                var ms = Median(s.Select(x => x.Ms));
                Note($"TOOLS {c.Name} | {s.Count} steps: median {ms:0.000} ms (mean {s.Average(x => x.Ms):0.000}, p90 {s.Select(x => x.Ms).OrderBy(x => x).ElementAt((int)(s.Count * 0.9)):0.000}), " +
                     $"over the empty cluster ({baseline:0.000}) {ms - baseline:0.000}; laid to the case: median {Median(s.Select(x => x.Laid)):0.000} ms (mean {s.Average(x => x.Laid):0.000}); " +
                     $"its work {Median(s.Select(x => x.CaseWork)):0.000} of the cluster's {Median(s.Select(x => x.Work)):0.000}; on the voxels {100.0 * s.Count(x => x.Voxels) / s.Count:0}% of the steps, " +
                     $"touching a grid {100.0 * s.Count(x => x.Grids) / s.Count:0}%, shapes {s.Min(x => x.Shapes)}-{s.Max(x => x.Shapes)}" +
                     (c.Grid != null && !c.Grid.Closed ? "; now: " + _describe.Invoke(null, new object[] { c.Grid }) : "") +
                     (c.Second != null && !c.Second.Closed ? "; the welder grid: " + _describe.Invoke(null, new object[] { c.Second }) : "") +
                     (Projects(c) ? $"; walls built {c.Walls}, blocks welded {c.WallBlocks}" : ""));
            }
            var idle = _cases.Where(Projects).Where(c => c.WallBlocks == 0).Select(c => c.Name).ToList();
            Check(idle.Count == 0, "the welders built nothing: " + string.Join(", ", idle));
            var unmeasured = _cases.Where(c => c.Samples.Count < 20).Select(c => c.Name).ToList();
            Check(unmeasured.Count == 0, "measured fewer than 20 times: " + string.Join(", ", unmeasured));
        }

        /// <summary>The stand's drill ship, its drills along <paramref name="into"/>, its front (the drills' heads) at <paramref name="front"/>; everything but the power off.</summary>
        private MyCubeGrid SpawnShip(Case c, Vector3D front, Vector3D into)
        {
            var side = Vector3D.CalculatePerpendicularVector(into);
            // the drills face the grid's up: the grid's up along the line, its forward across it
            var ob = WorldApi.LoadGridTemplate(ShipResource, WorldApi.EntityPrefix + Prefix + c.Name, Vector3D.Zero, (Vector3)side, (Vector3)into);
            ob.DampenersEnabled = false;
            ob.DestructibleBlocks = false;
            var heads = ob.CubeBlocks.OfType<MyObjectBuilder_Drill>().ToList();
            Check(heads.Count > 0, "the drill ship has no drills");
            foreach (var block in ob.CubeBlocks.OfType<MyObjectBuilder_FunctionalBlock>())
                if (!(block is MyObjectBuilder_BatteryBlock) && !(block is MyObjectBuilder_Reactor))
                    block.Enabled = false;
            var size = ob.GridSizeEnum == MyCubeSize.Large ? 2.5 : 0.5;
            var top = ob.CubeBlocks.Max(b => (int)b.Min.Y) + 1.5;
            var across = heads.Average(b => (double)b.Min.X);
            var along = heads.Average(b => (double)b.Min.Z);
            // the grid's local: x along its right, y up (into), z back
            var matrix = MatrixD.CreateWorld(Vector3D.Zero, side, into);
            var headLocal = new Vector3D(across, top, along) * size;
            var origin = front - Vector3D.TransformNormal(headLocal, matrix);
            ob.PositionAndOrientation = new MyPositionAndOrientation(origin, (Vector3)side, (Vector3)into);
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            WorldApi.EnsureDistributor(grid);
            WorldApi.ChargeBatteries(grid);
            c.Drills = WorldApi.FindFunctionals<MyShipDrill>(grid);
            foreach (var d in c.Drills) d.Enabled = false;
            return grid;
        }

        private static bool Welds(Case c) => c.Kind >= Kind.Welding;
        private static bool Projects(Case c) => c.Kind == Kind.Welding || c.Kind == Kind.WeldShip;

        /// <summary>
        /// The welders of the plate on a grid of their own lying on it: the two rows on columns under a bridge of armour
        /// over the wall, a battery on the bridge.
        /// </summary>
        private MyObjectBuilder_CubeGrid WelderBar(Case c)
        {
            var blocks = new List<BlockSpec>();
            for (var z = -2; z <= 2; z++)
            {
                blocks.Add(new BlockSpec("LargeShipWelder", new Vector3I(-3, 1, z), (int)Base6Directions.Direction.Right));
                blocks.Add(new BlockSpec("LargeShipWelder", new Vector3I(2, 1, z), (int)Base6Directions.Direction.Left));
                blocks.Add(new BlockSpec(Armor, new Vector3I(-3, 2, z)));
                blocks.Add(new BlockSpec(Armor, new Vector3I(3, 2, z)));
                for (var x = -3; x <= 3; x++) blocks.Add(new BlockSpec(Armor, new Vector3I(x, 3, z)));
            }
            blocks.Add(new BlockSpec("LargeBlockBatteryBlock", new Vector3I(-3, 4, -2)));
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + c.Name + "-welders", MyCubeSize.Large, false, c.Spot, blocks);
            ob.DampenersEnabled = false;
            ob.DestructibleBlocks = false;
            var owner = WorldApi.PlayerIdentityId();
            foreach (var block in ob.CubeBlocks)
            {
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
            }
            return ob;
        }

        /// <summary>The ship along its line at <paramref name="speed"/> (0: held still), not turning.</summary>
        private static void Push(Case c, double speed)
        {
            var physics = c.Grid?.Physics;
            if (physics == null || c.Grid.Closed) return;
            physics.LinearVelocity = (Vector3)(c.Into * speed);
            physics.AngularVelocity = Vector3.Zero;
        }

        /// <summary>
        /// A plate of 7 x 5 light armour, five welders on each long side facing in, a battery and a projector under it;
        /// for the welding case the wall between the welders, the cells their reach covers, goes into the projector (the
        /// plate's own blueprint with the wall, laid over the plate).
        /// </summary>
        private MyObjectBuilder_CubeGrid WeldPlate(Case c)
        {
            var blocks = new List<BlockSpec>();
            for (var x = -3; x <= 3; x++)
                for (var z = -2; z <= 2; z++)
                    blocks.Add(new BlockSpec(Armor, new Vector3I(x, 0, z)));
            if (c.Kind == Kind.Welding || c.Kind == Kind.WeldersIdle)
                for (var z = -2; z <= 2; z++)
                {
                    blocks.Add(new BlockSpec("LargeShipWelder", new Vector3I(-3, 1, z), (int)Base6Directions.Direction.Right));
                    blocks.Add(new BlockSpec("LargeShipWelder", new Vector3I(2, 1, z), (int)Base6Directions.Direction.Left));
                }
            blocks.Add(new BlockSpec("LargeProjector", new Vector3I(0, -1, 0)));
            var battery = MyDefinitionManager.Static.GetCubeBlockDefinition(new MyDefinitionId(typeof(MyObjectBuilder_BatteryBlock), "LargeBlockBatteryBlock"));
            Check(battery != null, "no large battery");
            blocks.Add(new BlockSpec("LargeBlockBatteryBlock", new Vector3I(-3, -battery.Size.Y, -2)));
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + c.Name, MyCubeSize.Large, false, c.Spot, blocks);
            ob.DampenersEnabled = false;
            ob.DestructibleBlocks = false;
            var owner = WorldApi.PlayerIdentityId();
            foreach (var block in ob.CubeBlocks)
            {
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
            }
            if (!Projects(c)) return ob;

            // the wall: the cells between the welders over the plate, one and two up
            for (var x = -1; x <= 1; x++)
                for (var y = 1; y <= 2; y++)
                    for (var z = -2; z <= 2; z++)
                        c.Wall.Add(new Vector3I(x, y, z));
            var blueprint = (MyObjectBuilder_CubeGrid)ob.Clone();
            foreach (var cell in c.Wall)
            {
                var block = WorldApi.MakeBlockOb(Armor);
                block.Min = cell;
                blueprint.CubeBlocks.Add(block);
            }
            // the projector first: the blueprint laid out from it, so with no offset over the plate itself
            var projector = blueprint.CubeBlocks.First(b => b.SubtypeName == "LargeProjector");
            blueprint.CubeBlocks.Remove(projector);
            blueprint.CubeBlocks.Insert(0, projector);
            blueprint.IsStatic = false;
            blueprint.DestructibleBlocks = false;
            foreach (var block in blueprint.CubeBlocks)
            {
                block.Owner = 0;
                block.ShareMode = MyOwnershipShareModeEnum.None;
                block.EntityId = 0;
                if (block is MyObjectBuilder_FunctionalBlock functional) functional.Enabled = false;
            }
            MyEntities.RemapObjectBuilder(blueprint);
            var projectorOb = (MyObjectBuilder_ProjectorBase)ob.CubeBlocks.First(b => b.SubtypeName == "LargeProjector");
            projectorOb.ProjectedGrid = null;
            projectorOb.ProjectedGrids = new List<MyObjectBuilder_CubeGrid> { blueprint };
            projectorOb.ProjectionOffset = Vector3I.Zero;
            projectorOb.ProjectionRotation = Vector3I.Zero;
            projectorOb.KeepProjection = true;
            projectorOb.Enabled = false;
            return ob;
        }

        private static void Stock(SpaceWelder welder)
        {
            var inventory = welder.GetInventory();
            var have = WorldApi.CountComponent(inventory, "SteelPlate");
            if (have < 200) WorldApi.StockComponents(inventory, new Dictionary<string, int> { { "SteelPlate", 300 - have } });
        }

        private static int WallBuilt(Case c) => c.Grid == null ? 0 : c.Wall.Count(cell => c.Grid.GetCubeBlock(cell) != null);

        /// <summary>The wall up and welded through: razed, for the welders to build it again from the projection.</summary>
        private static void Reweld(Case c)
        {
            var blocks = c.Wall.Select(cell => c.Grid.GetCubeBlock(cell)).ToList();
            var built = blocks.Count(b => b != null);
            if (built < c.Wall.Count || blocks.Any(b => !b.IsFullIntegrity)) return;
            c.Walls++;
            c.WallBlocks += built;
            c.Grid.RazeBlocks(new List<Vector3I>(c.Wall));
        }

        /// <summary>The first voxel along the line, from outside the asteroid inwards.</summary>
        private static Vector3D? Surface(Vector3D from, Vector3D dir, double length)
        {
            var hits = new List<MyPhysics.HitInfo>();
            MyPhysics.CastRay(from, from + dir * length, hits, MyPhysics.CollisionLayers.CollideWithStaticLayer);
            foreach (var hit in hits)
                if (hit.HkHitInfo.GetHitEntity() is MyVoxelBase) return hit.Position;
            return null;
        }

        private void OnSampled(HkWorld world, double ms)
        {
            if (!_byWorld.TryGetValue(world, out var c)) return;
            var frame = Sandbox.MySandboxGame.Static.SimulationFrameCounter;
            var work = (double)_work.Invoke(null, new object[] { world });
            var mine = 0.0;
            foreach (var body in world.ActiveRigidBodies)
            {
                if (!(body.UserObject is MyPhysicsBody physics) || physics.Entity == null) continue;
                var own = physics.Entity is MyCubeGrid;
                var grid = (own ? physics.Entity : physics.Entity.GetTopMostParent()) as MyCubeGrid;
                if (grid == null || grid != c.Grid && grid != c.Second) continue;
                mine += own ? (double)_gridWork.Invoke(null, new object[] { grid, frame }) : 0.003;
            }
            var text = c.Grid != null && !c.Grid.Closed ? (string)_describe.Invoke(null, new object[] { c.Grid }) : "";
            c.Samples.Add(new Sample
            {
                Ms = ms,
                Work = work,
                CaseWork = mine,
                // as PhysicsCost.Share lays a cluster stepped alone: its own off, the rest by the work
                Laid = work > _own ? Math.Max(0, ms - _own) * Math.Min(1, mine / (work - _own)) : 0,
                Voxels = text.Contains("on the voxels"),
                Grids = text.Contains("touching a grid"),
                Shapes = (c.Grid?.Physics as Sandbox.Game.Entities.Cube.MyGridPhysics)?.Shape?.ShapeCount ?? 0,
            });
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
                foreach (var c in _cases)
                {
                    if (c.Grid != null && !c.Grid.Closed) c.Grid.Close();
                    if (c.Asteroid != null && !c.Asteroid.Closed) c.Asteroid.Close();
                }
                FakeClients.RemoveAll();
                Sandbox.Engine.Utils.MyFakes.OWN_ALL_DLCS = _ownedDlcs;
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
