using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What a grid's physics really costs, for SentisOptimisations' share of the step (PhysicsCost: by cluster measured,
    /// within a cluster by work). Every case stands alone in a cluster of its own with a fake player by it, so the
    /// cluster's step timed alone is the case's cost: a cube of 1000 armor blocks at rest, spinning and drifting in
    /// space; a stick of 152 (2 x 2 x 38) spinning in space, fast, tumbling, sweeping by a station and hitting it; twenty
    /// small cubes spinning; two cubes touching; the piston stack; on a planet with an atmosphere an empty spot, the cube
    /// resting, kept awake and dropped, a slab kept awake, the stick toppling from upright, lying and kept awake. PhysicsCost times a cluster every
    /// frame for the run (<c>GapFrames</c> 1); each measurement goes with what the case's bodies do at the time - their
    /// blocks, speeds, sizes, whether they touch the ground - into <see cref="CsvName"/> by the reports, and each case's
    /// average into the log. The scenario passes when every case was measured; what the numbers say is for reading.
    /// </summary>
    public sealed class PhysicsCalibrationScenario : TestScenario
    {
        public const string ScenarioName = "physics_calibration";
        public const string CsvName = "physics_calibration.csv";
        private const string Prefix = "pcal-";
        private const string Armor = "LargeBlockArmorBlock";
        private const int RunSeconds = 60;
        private const double SpaceApart = 60000, PlanetApart = 45000, NoGridsWithin = 15000;

        private sealed class Case
        {
            public string Name;
            public bool OnPlanet;
            public Vector3D Spot, Up;
            public Func<Case, MyObjectBuilder_CubeGrid> Build;
            public Action<MyCubeGrid, Case> Start;
            public int CycleSeconds;
            public bool Group, Awake, Touching;
            /// <summary>A static slab of 20 x 2 x 20 this far from the spot (0: none), for a spinning case to sweep by or hit.</summary>
            public double StationAt;
            /// <summary>So many grids of the case (built alike, spread 30 m apart).</summary>
            public int Copies = 1;
            public int Contacts, LastContacts, Pairs;
            public MyCubeGrid Station;
            public bool OnSlab;
            public readonly List<MyCubeGrid> Grids = new List<MyCubeGrid>();
            public DateTime SpawnedAt;
            public Features Now;
            public readonly List<Sample> Samples = new List<Sample>();
        }

        private struct Features
        {
            public int Grids, Blocks;
            public double Work, V, W, WExtent, VExtent, Extent;
            public bool Ground;
            public int Contacts, Pairs, Shapes;
        }

        private struct Sample
        {
            public ulong Frame;
            public double Ms, SinceSpawn;
            public int Bodies, Foreign;
            public double Predicted;
            public Features F;
        }

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<Case> _cases = new List<Case>();
        private readonly Dictionary<HkWorld, Case> _byWorld = new Dictionary<HkWorld, Case>();
        private Type _cost;
        private Action<HkWorld, double> _handler;
        private MyPlanet _planet;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 420;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _cost = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("Optimizer.Optimizations.PhysicsCost");
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            SkipUnless(_cost?.GetField("Sampled", any) != null && _cost.GetField("GapFrames", any) != null, "SentisOptimisations has no timing of the physics clusters for tests");
            _config.Set("FreezerEnabled", false);
            _config.Set("EnablePhysicsGuard", false);

            // ------------------------------------------------------------- places: in space and on a planet, nobody near
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            var others = MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.PositionComp.GetPosition()).ToList();
            bool Lonely(Vector3D p) => others.All(o => Vector3D.DistanceSquared(o, p) > NoGridsWithin * NoGridsWithin);
            var space = new List<Vector3D>();
            for (var i = 0; i < 400 && space.Count < 20; i++)
            {
                var probe = new Vector3D(-900000 + (i % 20) * 90000, 700000 - (i / 20) * 70000, 400000 - (i % 7) * 120000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() > 1e-6) continue;
                if (planets.Any(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) < p.MaximumRadius + 20000)) continue;
                if (!Lonely(probe) || space.Any(s => Vector3D.Distance(s, probe) < SpaceApart)) continue;
                space.Add(probe);
            }
            Check(space.Count == 20, $"only {space.Count} of 20 places in space");

            _planet = planets.Where(p => p.HasAtmosphere).OrderByDescending(p => p.MaximumRadius).FirstOrDefault();
            SkipUnless(_planet != null, "no planet with an atmosphere");
            var centre = _planet.PositionComp.GetPosition();
            var ground = new List<Vector3D>();
            const int n = 1600;
            for (var i = 0; i < n && ground.Count < 14; i++)
            {
                // the points of a Fibonacci sphere: evenly over the planet
                var y = 1 - 2.0 * (i + 0.5) / n;
                var r = Math.Sqrt(1 - y * y);
                var a = i * Math.PI * (3 - Math.Sqrt(5));
                var dir = new Vector3D(Math.Cos(a) * r, y, Math.Sin(a) * r);
                var g = Surface(centre + dir * _planet.MaximumRadius);
                if (!Lonely(g) || ground.Any(s => Vector3D.Distance(s, g) < PlanetApart) || !Flat(g, dir)) continue;
                ground.Add(g);
            }
            Check(ground.Count == 14, $"only {ground.Count} of 14 flat places on {_planet.StorageName} without grids within {NoGridsWithin / 1000:0} km");

            // ------------------------------------------------------------- the cases
            MyObjectBuilder_CubeGrid Cube(Case c) => Box(c, 10, 10, 10, 0);
            MyObjectBuilder_CubeGrid StickUp(Case c) => Box(c, 2, 38, 2, 0);
            MyObjectBuilder_CubeGrid StickDown(Case c) => Box(c, 38, 2, 2, 0);
            void Spin(MyCubeGrid g, Case c) => g.Physics.AngularVelocity = Vector3.Normalize(new Vector3(0.3f, 1f, 0.6f)) * 0.5f;
            void Drift(MyCubeGrid g, Case c) => g.Physics.LinearVelocity = Vector3.Forward * 50;
            void Tip(MyCubeGrid g, Case c) => g.Physics.AngularVelocity = (Vector3)(Vector3D.CalculatePerpendicularVector(c.Up) * 0.15);
            var k = 0;
            void SpaceCase(string name, Func<Case, MyObjectBuilder_CubeGrid> build, Action<MyCubeGrid, Case> start = null, bool group = false, bool touching = false,
                double station = 0, int copies = 1, int cycle = 0, bool awake = false) =>
                _cases.Add(new Case { Name = name, Spot = space[k++], Up = Vector3D.Up, Build = build, Start = start, Group = group, Touching = touching, Awake = touching || awake,
                    StationAt = station, Copies = copies, CycleSeconds = cycle });
            Action<MyCubeGrid, Case> SpinAt(double w) => (g, c) => g.Physics.AngularVelocity = Vector3.Normalize(new Vector3(0.3f, 1f, 0.6f)) * (float)w;
            void Tumble(MyCubeGrid g, Case c)
            {
                g.Physics.AngularVelocity = new Vector3(1f, 0.7f, 1.2f);
                g.Physics.LinearVelocity = Vector3.Forward * 100;
            }
            // a stick turning about the slab's normal, in the slab's plane: its ends sweep a circle of 47 m
            void Sweep(MyCubeGrid g, Case c) => g.Physics.AngularVelocity = Vector3.Forward * 1f;
            SpaceCase("space_empty", null);
            SpaceCase("space_cube_rest", Cube);
            SpaceCase("space_cube_spin", Cube, Spin);
            SpaceCase("space_cube_drift", Cube, Drift);
            SpaceCase("space_stick_spin", StickUp, Spin);
            SpaceCase("space_piston_stack", null, null, group: true);
            SpaceCase("space_cubes_touching", null, null, touching: true);
            SpaceCase("space_cube_spin_fast", Cube, SpinAt(3));
            SpaceCase("space_stick_spin_fast", StickUp, SpinAt(3));
            SpaceCase("space_stick_tumble", StickUp, Tumble);
            SpaceCase("space_stick_sweeps_station", StickUp, Sweep, station: 70);
            SpaceCase("space_stick_hits_station", StickUp, Sweep, station: 35, cycle: 10);
            SpaceCase("space_small_cubes_spin", c => Box(c, 3, 3, 3, 0), SpinAt(1), copies: 20);
            // the same box hollow (its walls only), and a lattice of beams a block apart: more shapes, the armour not merged
            SpaceCase("space_hollow_box_spin", c => Box(c, 10, 10, 10, 0, (i, j, l) => i == 0 || i == 9 || j == 0 || j == 9 || l == 0 || l == 9), SpinAt(0.5));
            SpaceCase("space_lattice_spin", c => Box(c, 10, 10, 10, 0, (i, j, l) => (i % 2 == 0 ? 1 : 0) + (j % 2 == 0 ? 1 : 0) + (l % 2 == 0 ? 1 : 0) >= 2), SpinAt(0.5));
            SpaceCase("space_lattice_big_spin", c => Box(c, 20, 20, 20, 0, (i, j, l) => (i % 2 == 0 ? 1 : 0) + (j % 2 == 0 ? 1 : 0) + (l % 2 == 0 ? 1 : 0) >= 2), SpinAt(0.5));
            // the same kept awake standing still; a hollow tube 285 m long; a box of slopes (a convex shape each, none merged)
            SpaceCase("space_lattice_big_awake", c => Box(c, 20, 20, 20, 0, (i, j, l) => (i % 2 == 0 ? 1 : 0) + (j % 2 == 0 ? 1 : 0) + (l % 2 == 0 ? 1 : 0) >= 2), awake: true);
            SpaceCase("space_tube_long_awake", c => Box(c, 20, 114, 20, 0, (i, j, l) => i == 0 || i == 19 || l == 0 || l == 19), awake: true);
            SpaceCase("space_slopes_box_spin", c => Box(c, 10, 10, 10, 0, null, "LargeBlockArmorSlope"), SpinAt(0.5));
            SpaceCase("space_slopes_lattice_awake", c => Box(c, 20, 20, 20, 0, (i, j, l) => (i % 2 == 0 ? 1 : 0) + (j % 2 == 0 ? 1 : 0) + (l % 2 == 0 ? 1 : 0) >= 2, "LargeBlockArmorSlope"), awake: true);
            k = 0;
            void PlanetCase(string name, Func<Case, MyObjectBuilder_CubeGrid> build, Action<MyCubeGrid, Case> start = null, int cycle = 0, bool awake = false, bool onSlab = false) =>
                _cases.Add(new Case { Name = name, OnPlanet = true, Spot = ground[k], Up = Vector3D.Normalize(ground[k++] - centre), Build = build, Start = start, CycleSeconds = cycle, Awake = awake,
                    OnSlab = onSlab });
            PlanetCase("planet_empty", null);
            PlanetCase("planet_cube_rest", Cube);
            PlanetCase("planet_cube_drop", c => Box(c, 10, 10, 10, 25), null, 15);
            PlanetCase("planet_stick_topple", StickUp, Tip, 15);
            PlanetCase("planet_stick_lying", StickDown);
            PlanetCase("planet_cube_awake", Cube, awake: true);
            PlanetCase("planet_slab_awake", c => Box(c, 20, 2, 20, 0), awake: true);
            PlanetCase("planet_stick_awake", StickDown, awake: true);
            // small things on the ground kept awake: one block, 3 x 3, 5 x 5, and a small grid's plate of 10 x 10
            PlanetCase("planet_block_awake", c => Box(c, 1, 1, 1, 0), awake: true);
            PlanetCase("planet_plate3_awake", c => Box(c, 3, 1, 3, 0), awake: true);
            PlanetCase("planet_plate5_awake", c => Box(c, 5, 1, 5, 0), awake: true);
            PlanetCase("planet_small_plate10_awake", c => Box(c, 10, 1, 10, 0, null, "SmallBlockArmorBlock", small: true), awake: true);
            // on a static slab of 20 x 2 x 20 lying on the ground: a grid's contact with a grid instead of the voxels
            PlanetCase("planet_cube_awake_on_station", c => Box(c, 10, 10, 10, 5), awake: true, onSlab: true);
            // high in the air, turning as it falls, nothing touched
            PlanetCase("planet_stick_spin_air", c => Box(c, 2, 38, 2, 400), SpinAt(1), cycle: 8);

            // a player by each, 150 m to the side
            FakeClients.RemoveAll();
            FakeClients.Add(_cases.Count, new FakeClients.NetworkProfile { RttMs = 30 }, i =>
            {
                var c = _cases[i];
                var side = Vector3D.CalculatePerpendicularVector(c.Up);
                var at = c.Spot + side * 150;
                return (c.OnPlanet ? Surface(at + c.Up * 50) + c.Up * 3 : at, 0, 0);
            }, withCharacters: true);
            var arrive = WaitForSeconds(8, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;
            foreach (var c in _cases) Spawn(c);
            var settle = WaitForSeconds(5, "the cases are in place");
            while (settle.MoveNext()) yield return settle.Current;

            // ------------------------------------------------------------- a cluster timed every frame
            _byWorld.Clear();
            _handler = OnSampled;
            _cost.GetField("Sampled", any).SetValue(null, _handler);
            _cost.GetField("GapFrames", any).SetValue(null, 1);
            var started = DateTime.UtcNow;
            var lastNote = started;
            while ((DateTime.UtcNow - started).TotalSeconds < RunSeconds)
            {
                Observe();
                foreach (var c in _cases)
                    if (c.CycleSeconds > 0 && (DateTime.UtcNow - c.SpawnedAt).TotalSeconds >= c.CycleSeconds) Spawn(c);
                if ((DateTime.UtcNow - lastNote).TotalSeconds >= 10)
                {
                    lastNote = DateTime.UtcNow;
                    Note($"measuring ({(lastNote - started).TotalSeconds:0}/{RunSeconds} s): " + string.Join(", ", _cases.Select(c => $"{c.Name} {c.Samples.Count}")));
                }
                yield return null;
            }
            StopSampling();

            // ------------------------------------------------------------- what came out
            var csv = new List<string> { "case,frame,since_spawn_s,ms,predicted,bodies,foreign,grids,blocks,work,v,w,w_extent,v_extent,extent,ground,contacts,pairs,shapes" };
            var ic = CultureInfo.InvariantCulture;
            foreach (var c in _cases)
                foreach (var s in c.Samples)
                    csv.Add(string.Join(",", c.Name, s.Frame.ToString(ic), s.SinceSpawn.ToString("0.00", ic), s.Ms.ToString("0.0000", ic), s.Predicted.ToString("0.0000", ic), s.Bodies, s.Foreign,
                        s.F.Grids, s.F.Blocks, s.F.Work.ToString("0.00", ic), s.F.V.ToString("0.00", ic), s.F.W.ToString("0.000", ic),
                        s.F.WExtent.ToString("0.00", ic), s.F.VExtent.ToString("0.00", ic), s.F.Extent.ToString("0.0", ic), s.F.Ground ? 1 : 0, s.F.Contacts, s.F.Pairs, s.F.Shapes));
            var dir2 = TestRunner.ReportDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            Directory.CreateDirectory(dir2);
            var file = Path.Combine(dir2, CsvName);
            File.WriteAllLines(file, csv);
            foreach (var c in _cases)
            {
                var ms = c.Samples.Select(s => s.Ms).OrderBy(x => x).ToList();
                if (ms.Count == 0) { Note($"CALIBRATION {c.Name} | not measured"); continue; }
                Note($"CALIBRATION {c.Name} | {ms.Count} steps: median {ms[ms.Count / 2]:0.000} ms, p90 {ms[(int)(ms.Count * 0.9)]:0.000}, max {ms.Last():0.000}; " +
                     $"bodies up to {c.Samples.Max(s => s.Bodies)}, foreign up to {c.Samples.Max(s => s.Foreign)}, blocks {c.Samples.Max(s => s.F.Blocks)}, " +
                     $"v up to {c.Samples.Max(s => s.F.V):0.0} m/s, w up to {c.Samples.Max(s => s.F.W):0.00} rad/s, on the ground {c.Samples.Count(s => s.F.Ground)}, " +
                     $"contact points a frame median {c.Samples.Select(s => s.F.Contacts).OrderBy(x => x).ElementAt(ms.Count / 2)}, shapes {c.Samples.Max(s => s.F.Shapes)}");
            }
            Note($"CALIBRATION | {csv.Count - 1} measurements in {file}");
            var shared = _cases.Where(c => c.Grids.Count > 0).GroupBy(c => c.Grids[0].Physics?.HavokWorld).Where(g => g.Key != null && g.Count() > 1).ToList();
            Check(shared.Count == 0, "cases share a cluster: " + string.Join("; ", shared.Select(g => string.Join("+", g.Select(c => c.Name)))));
            var unmeasured = _cases.Where(c => c.Samples.Count < 20).Select(c => c.Name).ToList();
            Check(unmeasured.Count == 0, "measured fewer than 20 times: " + string.Join(", ", unmeasured));
        }

        /// <summary>A box of armor blocks <paramref name="x"/> by <paramref name="y"/> (up) by <paramref name="z"/>, its bottom <paramref name="lift"/> m over the spot.</summary>
        private static MyObjectBuilder_CubeGrid Box(Case c, int x, int y, int z, double lift, Func<int, int, int, bool> cell = null, string subtype = Armor, bool small = false)
        {
            var blocks = new List<BlockSpec>();
            for (var i = 0; i < x; i++)
                for (var j = 0; j < y; j++)
                    for (var l = 0; l < z; l++)
                        if (cell == null || cell(i, j, l))
                            blocks.Add(new BlockSpec(subtype, new Vector3I(i - x / 2, j, l - z / 2)));
            var forward = Vector3D.CalculatePerpendicularVector(c.Up);
            var at = c.Spot + c.Up * (c.OnPlanet ? (small ? 0.25 : 1.25) + 0.5 + lift : 0);
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + c.Name, small ? MyCubeSize.Small : MyCubeSize.Large, false, at, blocks, (Vector3)forward, (Vector3)c.Up);
            ob.DampenersEnabled = false;
            ob.DestructibleBlocks = false;
            return ob;
        }

        private void Spawn(Case c)
        {
            foreach (var g in c.Grids)
                if (!g.Closed) g.Close();
            c.Grids.Clear();
            c.SpawnedAt = DateTime.UtcNow;
            System.Threading.Interlocked.Exchange(ref c.Pairs, 0);
            if (c.Group)
            {
                var group = WorldApi.LoadAuthoredGroup(PistonStackScenario.ResourceName, WorldApi.EntityPrefix + Prefix + c.Name);
                var move = MatrixD.CreateTranslation(c.Spot - group[0].PositionAndOrientation.Value.GetMatrix().Translation);
                foreach (var ob in group)
                    ob.PositionAndOrientation = new MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
                group[0].IsStatic = false;
                foreach (var grid in group.Select(WorldApi.SpawnGrid))
                {
                    Track(grid);
                    c.Grids.Add(grid);
                    Count(grid, c);
                }
                return;
            }
            if (c.Touching)
            {
                // two cubes of 5 x 5 x 5, the second pressed against the first at half a metre a second
                for (var i = 0; i < 2; i++)
                {
                    var ob = Box(c, 5, 5, 5, 0);
                    ob.Name = ob.DisplayName = ob.Name + "-" + i;
                    ob.PositionAndOrientation = new MyPositionAndOrientation(c.Spot + Vector3D.Right * (i * 12.55), Vector3.Forward, Vector3.Up);
                    var cube = WorldApi.SpawnGrid(ob);
                    Track(cube);
                    c.Grids.Add(cube);
                    Count(cube, c);
                    if (i == 1 && cube.Physics != null) cube.Physics.LinearVelocity = Vector3.Left * 0.5f;
                }
                return;
            }
            if (c.OnSlab && c.Station == null)
            {
                var slab = Box(c, 20, 2, 20, 0);
                slab.IsStatic = true;
                slab.Name = slab.DisplayName = slab.Name + "-station";
                c.Station = WorldApi.SpawnGrid(slab);
                Track(c.Station);
            }
            if (c.StationAt > 0 && c.Station == null)
            {
                // a static slab beside the spot, its face towards the spot's turning stick
                var slab = Box(c, 20, 2, 20, 0);
                slab.IsStatic = true;
                slab.Name = slab.DisplayName = slab.Name + "-station";
                slab.PositionAndOrientation = new MyPositionAndOrientation(c.Spot + Vector3D.Right * c.StationAt + Vector3D.Forward * 4, Vector3.Forward, Vector3.Right);
                c.Station = WorldApi.SpawnGrid(slab);
                Track(c.Station);
            }
            if (c.Build == null) return;
            for (var i = 0; i < c.Copies; i++)
            {
                var ob = c.Build(c);
                if (c.Copies > 1)
                {
                    ob.Name = ob.DisplayName = ob.Name + "-" + i;
                    ob.PositionAndOrientation = new MyPositionAndOrientation(c.Spot + new Vector3D(i % 5 * 30, i / 5 * 30, 0), Vector3.Forward, Vector3.Up);
                }
                var spawned = WorldApi.SpawnGrid(ob);
                Track(spawned);
                c.Grids.Add(spawned);
                Count(spawned, c);
                if (c.Start != null && spawned.Physics != null) c.Start(spawned, c);
            }
        }

        /// <summary>The contact points and the collision pairs of the grid's body, counted for its case (on Havok's threads); a case kept awake is.</summary>
        private static void Count(MyCubeGrid grid, Case c)
        {
            var body = grid.Physics?.RigidBody;
            if (body == null) return;
            if (c.Awake) body.EnableDeactivation = false;
            body.ContactPointCallback += (ref HkContactPointEvent e) => System.Threading.Interlocked.Increment(ref c.Contacts);
            body.CollisionAddedCallback += (ref HkCollisionEvent e) => System.Threading.Interlocked.Increment(ref c.Pairs);
            body.CollisionRemovedCallback += (ref HkCollisionEvent e) => System.Threading.Interlocked.Decrement(ref c.Pairs);
        }

        private Vector3D Surface(Vector3D point) => _planet.GetClosestSurfacePointGlobal(ref point);

        /// <summary>Flat enough for the cube not to slide: the ground 30 m around within 2 m of the spot's height.</summary>
        private bool Flat(Vector3D spot, Vector3D up)
        {
            var centre = _planet.PositionComp.GetPosition();
            var height = Vector3D.Distance(spot, centre);
            var a = Vector3D.CalculatePerpendicularVector(up);
            var b = Vector3D.Cross(up, a);
            foreach (var d in new[] { a, -a, b, -b })
            {
                var around = Surface(spot + d * 30 + up * 50);
                if (Math.Abs(Vector3D.Distance(around, centre) - height) > 2) return false;
            }
            return true;
        }

        /// <summary>Each frame: which world each case is in, and what its bodies do.</summary>
        private void Observe()
        {
            _byWorld.Clear();
            foreach (var c in _cases)
            {
                var f = new Features();
                HkWorld world = null;
                foreach (var g in c.Grids)
                {
                    if (g.Closed || g.Physics == null) continue;
                    world = world ?? g.Physics.HavokWorld;
                    var body = g.Physics.RigidBody;
                    if (body == null || !body.IsActive) continue;
                    var extent = g.PositionComp.LocalAABB.Size.Length();
                    var v = g.Physics.LinearVelocity.Length();
                    var w = g.Physics.AngularVelocity.Length();
                    f.Grids++;
                    f.Blocks += g.BlocksCount;
                    f.Shapes += (g.Physics as Sandbox.Game.Entities.Cube.MyGridPhysics)?.Shape?.ShapeCount ?? 0;
                    f.Work += 1 + g.BlocksCount / 50.0;
                    f.V = Math.Max(f.V, v);
                    f.W = Math.Max(f.W, w);
                    f.WExtent += w * extent / 2;
                    f.VExtent += v * extent;
                    f.Extent = Math.Max(f.Extent, extent);
                    if (c.OnPlanet)
                    {
                        var box = g.PositionComp.WorldAABB;
                        var middle = box.Center;
                        var below = Surface(middle);
                        // the grid's box reaches down to the ground (with a block to spare)
                        if (Vector3D.Distance(middle, below) < box.HalfExtents.Length() + 2.5) f.Ground = true;
                    }
                }
                if (world == null)
                {
                    // the empty cases: the world of their player
                    var character = FakeClients.Character(_cases.IndexOf(c));
                    world = character?.Physics?.HavokWorld;
                }
                var contacts = System.Threading.Volatile.Read(ref c.Contacts);
                f.Contacts = contacts - c.LastContacts;
                c.LastContacts = contacts;
                f.Pairs = System.Threading.Volatile.Read(ref c.Pairs);
                c.Now = f;
                if (world != null && !_byWorld.ContainsKey(world)) _byWorld[world] = c;
            }
        }

        private MethodInfo _work;

        /// <summary>PhysicsCost's work of the world but the cluster's own: what it predicts the measured step over an empty cluster.</summary>
        private double Predicted(HkWorld world)
        {
            if (_work == null) _work = _cost.GetMethod("Work", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(HkWorld) }, null);
            var own = Convert.ToDouble(_cost.GetField("ClusterOwnWork", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue());
            return _work == null ? 0 : (double)_work.Invoke(null, new object[] { world }) - own;
        }

        private void OnSampled(HkWorld world, double ms)
        {
            if (!_byWorld.TryGetValue(world, out var c)) return;
            var ours = new HashSet<long>(c.Grids.Select(g => g.EntityId));
            int bodies = 0, foreign = 0;
            foreach (var body in world.ActiveRigidBodies)
            {
                bodies++;
                var entity = (body.UserObject as MyPhysicsBody)?.Entity;
                if (entity is MyCubeGrid grid && (ours.Contains(grid.EntityId) || grid == c.Station)) continue;
                if (entity is Sandbox.Game.Entities.Character.MyCharacter) continue;
                foreign++;
            }
            c.Samples.Add(new Sample
            {
                Frame = Sandbox.MySandboxGame.Static.SimulationFrameCounter,
                Ms = ms,
                SinceSpawn = (DateTime.UtcNow - c.SpawnedAt).TotalSeconds,
                Bodies = bodies,
                Foreign = foreign,
                Predicted = Predicted(world),
                F = c.Now,
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
                    foreach (var g in c.Grids)
                        if (!g.Closed) g.Close();
                    if (c.Station != null && !c.Station.Closed) c.Station.Close();
                }
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
