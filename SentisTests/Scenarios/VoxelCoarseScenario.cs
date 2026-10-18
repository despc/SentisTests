using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Voxels;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisOptimisations' VoxelCoarseNearShips does not change the rock for anyone but the big ship it is round. On an
    /// asteroid of its own in empty space (<see cref="AsteroidPrefab"/>), the stand's drill ship
    /// (<see cref="PhysicsToolsScenario.ShipResource"/>):
    ///
    ///  0. hangs awake 4 m over the rock with the threshold (VoxelCoarseMinGridMs) over its load: no coarse cell;
    ///  1. the threshold 0: coarse cells come round it;
    ///  2. a character comes from 60 m to beside it, over the rock: no coarse cell ever within <see cref="CharacterClear"/>
    ///     m of the character, and the character ends out of the rock;
    ///  3. a second copy flies past it 10 m over the rock at 3 m/s: it never touches the rock and is never kicked
    ///     (a change of speed of more than <see cref="MaxKick"/> m/s in a frame);
    ///  4. a third copy comes down onto the rock 40 m off (across its short side) and lies pressed on it: no cell under it changes while it
    ///     lies there (its own change of speed is noted: a ship pressed on rock jitters by itself);
    ///  4b. the freezer takes the first, its physics too (nobody within 100 m): no cell under it changes while it is
    ///     frozen, and it is not kicked when it thaws;
    ///  5. the first flies off 300 m: every coarse cell goes back to the game's.
    /// </summary>
    public sealed class VoxelCoarseScenario : TestScenario
    {
        public const string ScenarioName = "voxel_coarse";
        /// <summary>The same with VoxelCoarseNearShips off (the setting): what the ships do by themselves.</summary>
        public const string OffName = "voxel_coarse_off";
        private readonly bool _off;

        public VoxelCoarseScenario(bool off = false) => _off = off;
        private const string Prefix = "vcoarse-";
        private const string AsteroidPrefab = "Asteroid256-001";
        private const double CharacterClear = 8, MaxKick = 0.5, Hover = 4;

        private readonly ConfigOverride _config = new ConfigOverride();
        private Type _coarse;
        private MyVoxelBase _asteroid;
        private readonly List<string> _problems = new List<string>();
        private readonly Dictionary<MyCubeGrid, int> _rockTouches = new Dictionary<MyCubeGrid, int>();

        public override string Name => _off ? OffName : ScenarioName;
        public override int TimeoutSeconds => 420;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _coarse = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("Optimizer.Optimizations.VoxelCoarseNearShips");
            SkipUnless(_coarse != null, "SentisOptimisations has no VoxelCoarseNearShips");
            _config.Set("VoxelCoarseNearShips", !_off);
            // the threshold over the ship's load first (stage 0), then 0: every big ship qualifies
            _config.Set("VoxelCoarseMinGridMs", 1000f);
            _config.Set("FreezerEnabled", false);
            _config.Set("EnablePhysicsGuard", false);
            _start = CoarseCost.Take(_coarse);

            // ------------------------------------------------------------- empty space, an asteroid, an observer 150 m off
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            var others = MyEntities.GetEntities().Where(e => e is MyCubeGrid || e is MyVoxelBase && !(e is MyPlanet)).Select(e => e.PositionComp.GetPosition()).ToList();
            Vector3D? spot = null;
            for (var i = 0; i < 400 && spot == null; i++)
            {
                var probe = new Vector3D(700000 - (i % 20) * 71000, -560000 + (i / 20) * 57000, 220000 - (i % 7) * 70000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() > 1e-6) continue;
                if (planets.Any(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) < p.MaximumRadius + 20000)) continue;
                if (others.Any(o => Vector3D.DistanceSquared(o, probe) < 15000.0 * 15000.0)) continue;
                spot = probe;
            }
            Check(spot.HasValue, "no place in empty space");
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, _ => (spot.Value + Vector3D.Up * 400, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(8, "the observer arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            var prefab = MyDefinitionManager.Static.GetVoxelMapStorageDefinitions().Select(d => d.Id.SubtypeName).FirstOrDefault(n => n == AsteroidPrefab)
                         ?? MyDefinitionManager.Static.GetVoxelMapStorageDefinitions().Select(d => d.Id.SubtypeName).FirstOrDefault(n => n.StartsWith("Asteroid256"));
            Check(prefab != null, "no asteroid prefab of 256");
            _asteroid = MyWorldGenerator.AddAsteroidPrefab(prefab, spot.Value, WorldApi.EntityPrefix + Prefix + "asteroid");
            Check(_asteroid != null, "the asteroid was not made");
            Track(_asteroid);
            yield return WaitForTicks(240);
            var middle = _asteroid.PositionComp.WorldAABB.Center;

            // three places on the rock: the drill ship's, the flight past it, the pressed ship's
            var up = Vector3D.Normalize(new Vector3D(0.2, 1, 0.1));
            var side = Vector3D.Normalize(Vector3D.Cross(up, Vector3D.Forward));
            var along = Vector3D.Cross(side, up);
            Vector3D SurfaceUnder(Vector3D over)
            {
                var hits = new List<MyPhysics.HitInfo>();
                MyPhysics.CastRay(over, over - up * 600, hits, MyPhysics.CollisionLayers.CollideWithStaticLayer);
                foreach (var h in hits)
                    if (h.HkHitInfo.GetHitEntity() is MyVoxelBase) return h.Position;
                throw new ScenarioFailedException($"no rock under {over}: {hits.Count} hits ({string.Join(", ", hits.Select(h => h.HkHitInfo.GetHitEntity()?.GetType().Name ?? "?"))}), the asteroid {_asteroid.PositionComp.WorldAABB.Center} {_asteroid.PositionComp.WorldAABB.Size}, physics {_asteroid.Physics != null}");
            }
            var siteA = SurfaceUnder(middle + up * 300);
            var siteR = SurfaceUnder(middle + up * 300 - along * 40);
            var characterEnd = SurfaceUnder(middle + up * 300 + along * 22) + up * 1.2;
            var characterStart = characterEnd + along * 60 + up * 8;

            // ------------------------------------------------------------- 0. the drill ship hangs awake over the rock, under the threshold
            var ship = Place("ship", siteA, up, along, Hover);
            yield return WaitForTicks(30);
            Listen(ship);
            var belowFrom = DateTime.UtcNow;
            var underMax = 0;
            while ((DateTime.UtcNow - belowFrom).TotalSeconds < 12)
            {
                Hold(ship);
                underMax = Math.Max(underMax, Coarse().Count);
                yield return null;
            }
            Note($"0. the threshold over its load (1000 ms, its load {LoadOf(ship):0.000} ms): coarse cells at most {underMax}");
            if (!_off && underMax > 0) _problems.Add($"{underMax} coarse cells round a ship under the threshold");

            // ------------------------------------------------------------- 1. the threshold 0: coarse cells come round it
            _config.Set("VoxelCoarseMinGridMs", 0f);
            var wait = DateTime.UtcNow;
            while ((DateTime.UtcNow - wait).TotalSeconds < 25 && Coarse().Count < 10)
            {
                Hold(ship);
                yield return null;
            }
            Note($"1. coarse cells round the hanging ship: {Coarse().Count}, its load {LoadOf(ship):0.000} ms ({Counters()})");
            if (Coarse().Count < 10) _problems.Add("no coarse cells came round the ship hanging over the rock");

            // ------------------------------------------------------------- 2. a character comes up beside it
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, _ => (characterStart, 0, 0), withCharacters: true);
            var walker = FakeClients.Count - 1;
            yield return WaitForTicks(60);
            var startSeconds = FakeClients.Seconds;
            FakeClients.SetPath(walker, t => Vector3D.Lerp(characterStart, characterEnd, MathHelper.Clamp((t - startSeconds) / 25, 0, 1)));
            var closest = double.MaxValue;
            var walk = DateTime.UtcNow;
            while ((DateTime.UtcNow - walk).TotalSeconds < 32)
            {
                Hold(ship);
                var character = FakeClients.Character(walker);
                if (character != null) closest = Math.Min(closest, NearestCoarse(character.PositionComp.WorldAABB));
                yield return null;
            }
            var walkerChar = FakeClients.Character(walker);
            var inRock = walkerChar != null && SmoothContent(walkerChar.PositionComp.WorldAABB.Center) >= 128;
            Note($"2. the character came to {(walkerChar == null ? "-" : Vector3D.Distance(walkerChar.PositionComp.GetPosition(), characterEnd).ToString("0.0"))} m of its end; " +
                 $"the nearest coarse cell to it on the way {closest:0.0} m; in the rock: {inRock}; coarse cells now {Coarse().Count} ({Counters()})");
            if (walkerChar == null) _problems.Add("the character is gone");
            if (closest < CharacterClear) _problems.Add($"a coarse cell {closest:0.0} m from the character");
            if (inRock) _problems.Add("the character ended in the rock");

            // ------------------------------------------------------------- 3. a second ship flies past, 4 m over the rock
            var pastStart = SurfaceUnder(siteA + up * 200 + along * 70 + side * 18);
            const double flyHover = 10;
            var flyer = Place("flyer", pastStart, up, along, flyHover);
            yield return WaitForTicks(30);
            Listen(flyer);
            double flyKick = 0;
            var lastV = flyer.Physics.LinearVelocity;
            var fly = DateTime.UtcNow;
            var climb = 0.0;
            var frames = 0;
            while ((DateTime.UtcNow - fly).TotalSeconds < 45)
            {
                Hold(ship);
                // the height over the rock under its middle, by a ray now and then: climb or sink to Hover
                if (frames++ % 10 == 0)
                {
                    var c = flyer.PositionComp.WorldAABB.Center;
                    var low = flyer.PositionComp.WorldAABB.GetCorners().Min(x => Vector3D.Dot(x - c, up));
                    var hits = new List<MyPhysics.HitInfo>();
                    MyPhysics.CastRay(c + up * 50, c - up * 200, hits, MyPhysics.CollisionLayers.CollideWithStaticLayer);
                    foreach (var h in hits)
                        if (h.HkHitInfo.GetHitEntity() is MyVoxelBase)
                        {
                            var gap = Vector3D.Dot(c - h.Position, up) + low;
                            climb = MathHelper.Clamp((flyHover - gap) * 0.5, -2, 2);
                            break;
                        }
                }
                Drive(flyer, -along * 3 + up * climb);
                if ((DateTime.UtcNow - fly).TotalSeconds < 2) _rockTouches[flyer] = 0;
                yield return null;
                if (flyer.Physics == null) break;
                if ((DateTime.UtcNow - fly).TotalSeconds > 2) flyKick = Math.Max(flyKick, (flyer.Physics.LinearVelocity - lastV).Length());
                lastV = flyer.Physics.LinearVelocity;
            }
            Note($"3. kinds: {Kinds()} (ship {ship.EntityId}, flyer {flyer.EntityId})");
            Note($"3. the ship flying past: {Touches(flyer)} rock touches, the biggest change of speed in a frame {flyKick:0.000} m/s ({Counters()})");
            if (Touches(flyer) > 0) _problems.Add($"the ship flying past touched the rock {Touches(flyer)} times");
            if (flyKick > MaxKick) _problems.Add($"the ship flying past was kicked {flyKick:0.00} m/s in a frame");

            // ------------------------------------------------------------- 4. a third ship pressed on the rock
            var pressed = Place("pressed", siteR, up, along, 2);
            yield return WaitForTicks(30);
            Listen(pressed);
            var down = DateTime.UtcNow;
            while ((DateTime.UtcNow - down).TotalSeconds < 40 && Touches(pressed) == 0)
            {
                Hold(ship);
                Drive(pressed, -up * 1);
                yield return null;
            }
            if (Touches(pressed) == 0) _problems.Add("the pressed ship never reached the rock");
            double pressKick = 0;
            var pressedV = pressed.Physics.LinearVelocity;
            // the cells under it: those within 2 m of its box, as VoxelCoarseNearShips holds them (Held)
            var underBox = pressed.PositionComp.WorldAABB;
            underBox.Inflate(2);
            var under = new HashSet<Vector3I>(Coarse().Where(c => underBox.Intersects(CellBox(c))));
            var underStart = under.Count;
            var underChanges = 0;
            int underAdded = 0, underRemoved = 0;
            var kindsAtTouch = Kinds();
            var press = DateTime.UtcNow;
            while ((DateTime.UtcNow - press).TotalSeconds < 25)
            {
                Hold(ship);
                Push(pressed, -up, 0.3);
                yield return null;
                pressKick = Math.Max(pressKick, (pressed.Physics.LinearVelocity - pressedV).Length());
                pressedV = pressed.Physics.LinearVelocity;
                var now = new HashSet<Vector3I>(Coarse().Where(c => underBox.Intersects(CellBox(c))));
                if (!now.SetEquals(under))
                {
                    underAdded += now.Count(c => !under.Contains(c));
                    underRemoved += under.Count(c => !now.Contains(c));
                    underChanges = underAdded + underRemoved;
                    under = now;
                }
            }
            Note($"4. kinds at the end: {Kinds()} (pressed {pressed.EntityId}); it came down in {(press - down).TotalSeconds:0.0} s");
            Note($"4. the pressed ship: {Touches(pressed)} rock touches, coarse cells under it {underStart} when it touched, {underChanges} changed while it lay there ({underAdded} made coarse, {underRemoved} given back), kinds at the touch [{kindsAtTouch}], " +
                 $"the biggest change of speed in a frame {pressKick:0.000} m/s");
            if (underChanges > 0) _problems.Add($"{underChanges} cells under the ship pressed on the rock changed while it lay there");


            // ------------------------------------------------------------- 4b. the freezer takes the hanging ship, physics and all
            flyer.Close();
            pressed.Close();
            // only the observer 400 m off: nobody within the freeze distance
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, _ => (spot.Value + Vector3D.Up * 400, 0, 0), withCharacters: true);
            yield return WaitForTicks(120);
            if (ship.Physics?.RigidBody != null) ship.Physics.RigidBody.EnableDeactivation = true;
            _config.Set("FreezeDistanceDynamic", 100);
            _config.Set("FreezeDistanceStatic", 100);
            _config.Set("FreezePhysics", true);
            _config.Set("FreezerEnabled", true);
            var shipBox = ship.PositionComp.WorldAABB;
            shipBox.Inflate(2);
            var freezeWait = DateTime.UtcNow;
            while ((DateTime.UtcNow - freezeWait).TotalSeconds < 40 && !FreezerState.IsPhysicsFrozen(ship)) yield return null;
            var frozen = FreezerState.IsPhysicsFrozen(ship);
            var underFrozen = new HashSet<Vector3I>(Coarse().Where(c => shipBox.Intersects(CellBox(c))));
            var frozenChanges = 0;
            var hold = DateTime.UtcNow;
            while ((DateTime.UtcNow - hold).TotalSeconds < 20)
            {
                yield return null;
                var now = new HashSet<Vector3I>(Coarse().Where(c => shipBox.Intersects(CellBox(c))));
                if (!now.SetEquals(underFrozen)) { frozenChanges += now.Count(c => !underFrozen.Contains(c)) + underFrozen.Count(c => !now.Contains(c)); underFrozen = now; }
            }
            _config.Set("FreezeDistanceDynamic", 5000);
            _config.Set("FreezeDistanceStatic", 5000);
            var thawWait = DateTime.UtcNow;
            while ((DateTime.UtcNow - thawWait).TotalSeconds < 40 && FreezerState.IsFrozen(ship)) yield return null;
            _rockTouches[ship] = 0;
            double thawKick = 0;
            var thawV = ship.Physics?.LinearVelocity ?? Vector3.Zero;
            var watch = DateTime.UtcNow;
            while ((DateTime.UtcNow - watch).TotalSeconds < 6)
            {
                yield return null;
                if (ship.Physics == null) break;
                thawKick = Math.Max(thawKick, (ship.Physics.LinearVelocity - thawV).Length());
                thawV = ship.Physics.LinearVelocity;
            }
            _config.Set("FreezerEnabled", false);
            if (ship.Physics?.RigidBody != null) ship.Physics.RigidBody.EnableDeactivation = false;
            Note($"4b. the freezer: physics-frozen {frozen}, coarse cells under it {underFrozen.Count}, {frozenChanges} changed while it was frozen; " +
                 $"thawed {!FreezerState.IsFrozen(ship)}, after the thaw {Touches(ship)} rock touches, the biggest change of speed in a frame {thawKick:0.000} m/s");
            if (!frozen) _problems.Add("the freezer did not take the hanging ship");
            if (frozenChanges > 0) _problems.Add($"{frozenChanges} cells under the frozen ship changed");
            if (thawKick > MaxKick) _problems.Add($"the ship was kicked {thawKick:0.00} m/s in a frame after the thaw");

            // ------------------------------------------------------------- 5. the drill ship flies off
            var away = DateTime.UtcNow;
            while ((DateTime.UtcNow - away).TotalSeconds < 60)
            {
                Drive(ship, up * 15);
                yield return null;
                if (Vector3D.Distance(ship.PositionComp.GetPosition(), siteA) > 400 && Coarse().Count == 0) break;
            }
            Note($"5. the ship {Vector3D.Distance(ship.PositionComp.GetPosition(), siteA):0} m off; coarse cells left {Coarse().Count} " +
                 $"[{string.Join("; ", Coarse().Select(c => Vector3D.Distance(CellBox(c).Center, ship.PositionComp.WorldAABB.Center).ToString("0") + " m from the ship"))}], kinds [{Kinds()}] ({Counters()})");
            if (Coarse().Count > 0) _problems.Add($"{Coarse().Count} coarse cells left after the ship went");

            Note("VOXEL COARSE RESULT | " + (_problems.Count == 0 ? "no problems" : string.Join("; ", _problems)) + " | " + Counters());
            Check(_problems.Count == 0, string.Join("; ", _problems));
        }

        private MyCubeGrid Place(string name, Vector3D surface, Vector3D up, Vector3D forward, double gap, bool surfaceAt = false)
        {
            // lying flat: its long side (the template's up) across the rock
            var across = Vector3D.Normalize(Vector3D.Cross(up, forward));
            var ob = WorldApi.LoadGridTemplate(PhysicsToolsScenario.ShipResource, WorldApi.EntityPrefix + Prefix + name, surface + up * 200, (Vector3)forward, (Vector3)across);
            ob.DampenersEnabled = false;
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            // its lowest corner along up put gap metres over the rock
            var box = grid.PositionComp.WorldAABB;
            var corners = box.GetCorners();
            var low = corners.Min(c => Vector3D.Dot(c - surface, up));
            var m = grid.WorldMatrix;
            var centre = box.Center;
            m.Translation += (surface - centre) - up * Vector3D.Dot(surface - centre, up) + up * (gap - low);
            grid.Teleport(m);
            if (grid.Physics != null)
            {
                grid.Physics.LinearVelocity = Vector3.Zero;
                grid.Physics.AngularVelocity = Vector3.Zero;
                if (grid.Physics.RigidBody != null) grid.Physics.RigidBody.EnableDeactivation = false;
            }
            return grid;
        }

        private static void Hold(MyCubeGrid grid) => Drive(grid, Vector3D.Zero);

        /// <summary>A force holding the grid at <paramref name="velocity"/>, at most 1.5 m/s².</summary>
        private static void Drive(MyCubeGrid grid, Vector3D velocity)
        {
            var physics = grid?.Physics;
            if (physics == null) return;
            var a = (velocity - physics.LinearVelocity) * 5;
            if (a.Length() > 1.5) a = a / a.Length() * 1.5;
            physics.AngularVelocity = Vector3.Zero;
            if (a.LengthSquared() > 1e-8) physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, (Vector3)(a * physics.Mass), null, null);
        }

        private static void Push(MyCubeGrid grid, Vector3D direction, double accel)
        {
            var physics = grid?.Physics;
            if (physics == null) return;
            physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, (Vector3)(direction * accel * physics.Mass), null, null);
        }

        private void Listen(MyCubeGrid grid)
        {
            var body = grid.Physics?.RigidBody;
            if (body == null) return;
            _rockTouches[grid] = 0;
            var asteroid = _asteroid;
            body.ContactPointCallback += (ref HkContactPointEvent e) =>
            {
                var a = e.Base.BodyA?.UserObject as MyPhysicsBody;
                var b = e.Base.BodyB?.UserObject as MyPhysicsBody;
                if (a?.Entity == asteroid || b?.Entity == asteroid) _rockTouches[grid]++;
            };
            body.ContactPointCallbackEnabled = true;
        }

        private int Touches(MyCubeGrid grid) => _rockTouches.TryGetValue(grid, out var n) ? n : 0;

        private List<Vector3I> Coarse() =>
            (List<Vector3I>)_coarse.GetMethod("CoarseCellsOf", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { _asteroid });

        private BoundingBoxD CellBox(Vector3I cell)
        {
            var coord = new MyCellCoord(0, cell);
            MyVoxelCoordSystems.GeometryCellCoordToWorldAABB(_asteroid.PositionLeftBottomCorner, ref coord, out var box);
            return box;
        }

        /// <summary>How far the nearest coarse cell is from a box, metres (0 when they overlap).</summary>
        private double NearestCoarse(BoundingBoxD box)
        {
            var best = double.MaxValue;
            foreach (var cell in Coarse())
            {
                var c = CellBox(cell);
                var dx = Math.Max(0, Math.Max(c.Min.X - box.Max.X, box.Min.X - c.Max.X));
                var dy = Math.Max(0, Math.Max(c.Min.Y - box.Max.Y, box.Min.Y - c.Max.Y));
                var dz = Math.Max(0, Math.Max(c.Min.Z - box.Max.Z, box.Min.Z - c.Max.Z));
                best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
            return best;
        }

        /// <summary>The rock content at a point, between the voxels round it (128 is the surface).</summary>
        private double SmoothContent(Vector3D point)
        {
            var data = new VRage.Voxels.MyStorageData(VRage.Voxels.MyStorageDataTypeFlags.Content);
            var p = point - _asteroid.PositionLeftBottomCorner;
            var cell = Vector3I.Floor(p);
            var f = p - new Vector3D(cell.X, cell.Y, cell.Z);
            var min = cell + _asteroid.StorageMin;
            data.Resize(new Vector3I(2));
            _asteroid.Storage.ReadRange(data, VRage.Voxels.MyStorageDataTypeFlags.Content, 0, min, min + 1);
            double C(int x, int y, int z) { var v = new Vector3I(x, y, z); return data.Content(ref v); }
            double L(double a, double b, double t) => a + (b - a) * t;
            return L(L(L(C(0, 0, 0), C(1, 0, 0), f.X), L(C(0, 1, 0), C(1, 1, 0), f.X), f.Y),
                     L(L(C(0, 0, 1), C(1, 0, 1), f.X), L(C(0, 1, 1), C(1, 1, 1), f.X), f.Y), f.Z);
        }

        private double LoadOf(MyCubeGrid grid) =>
            (double)_coarse.GetMethod("LoadOf", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { grid });

        private string Kinds() => (string)_coarse.GetMethod("KindsOf", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { _asteroid });

        private long[] _start;

        private string Counters()
        {
            var now = CoarseCost.Take(_coarse);
            _start = _start ?? now;
            return CoarseCost.Describe(_start, now);
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
            try { _config.Restore(); }
            finally { base.CleanupLeftovers(); }
        }
    }

    /// <summary>VoxelCoarseNearShips' counters (and VoxelShapeHold's cells asked for in a step) between two moments.</summary>
    internal static class CoarseCost
    {
        private static readonly string[] Names = { "MadeCoarse", "Rejected", "Reverted", "WorkTicks", "PassTicks", "Passes", "ShapeUpdateTicks", "ShapeUpdates" };

        public static long[] Take(Type coarse)
        {
            var values = Names.Select(n => coarse?.GetField(n, BindingFlags.Static | BindingFlags.Public)?.GetValue(null) is long v ? v : 0).ToList();
            var hold = coarse?.Assembly.GetType("Optimizer.Optimizations.VoxelShapeHold");
            values.Add(hold?.GetField("AskedCells", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) is long a ? a : 0);
            values.Add(System.Diagnostics.Stopwatch.GetTimestamp());
            return values.ToArray();
        }

        public static string Describe(long[] from, long[] to)
        {
            double Ms(int i) => (to[i] - from[i]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var minutes = Math.Max(1e-6, Ms(9) / 60000);
            return $"made {to[0] - from[0]}, rejected {to[1] - from[1]}, reverted {to[2] - from[2]}; background {Ms(3):0} ms; game thread: passes {to[5] - from[5]} {Ms(4):0.0} ms, " +
                   $"voxel shape updates {to[7] - from[7]} {Ms(6):0.0} ms; cells made in a step {to[8] - from[8]}; over {minutes:0.0} min";
        }
    }
}
