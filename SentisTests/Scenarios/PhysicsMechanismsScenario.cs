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
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
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
    /// What moving mechanisms and long falling sticks cost the physics, for PhysicsCost (as
    /// <see cref="PhysicsCalibrationScenario"/>: every case alone in a cluster of its own with a fake player by it, the
    /// cluster timed every frame). In space: the piston stack standing and moving (out and back), a rotor bare at 30 RPM,
    /// with a plate of 5 x 5 at 30 and at 5 RPM. On a planet with an atmosphere: a small rover parked and driving a circle,
    /// a piston pressing its head into the ground; sticks of 500 armour blocks toppling from upright (1 x 1 x 500,
    /// 2 x 2 x 125, 5 x 5 x 20) and one of 1 x 1 x 500 kept awake lying. Each measurement with what the case's bodies do
    /// goes into <see cref="CsvName"/> by the reports, each case's average into the log.
    /// </summary>
    public sealed class PhysicsMechanismsScenario : TestScenario
    {
        public const string ScenarioName = "physics_mechanisms";
        public const string SticksScenarioName = "physics_sticks";
        private readonly bool _sticks;

        public PhysicsMechanismsScenario(bool sticks = false) => _sticks = sticks;

        private string CsvName => _sticks ? "physics_sticks.csv" : "physics_mechanisms.csv";
        private const string Prefix = "pmech-";
        private const string Armor = "LargeBlockArmorBlock";
        /// <summary>Measured until every case has this many steps, or <see cref="MaxRunSeconds"/>.</summary>
        private const int StepsWanted = 80, MinRunSeconds = 60, MaxRunSeconds = 200;

        private static readonly MethodInfo CreateTopPart = typeof(MyMechanicalConnectionBlockBase)
            .GetMethod("CreateTopPartAndAttach", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly object NormalTopSize = CreateTopPart == null ? null : Enum.Parse(CreateTopPart.GetParameters()[1].ParameterType, "Normal");

        private sealed class Case
        {
            public string Name;
            public bool OnPlanet;
            public Vector3D Spot, Up;
            public Func<Case, IEnumerator> Build;
            public Action<Case, double> Drive;
            public readonly List<MyCubeGrid> Grids = new List<MyCubeGrid>();
            public int Contacts, LastContacts;
            public string State = "";
            public Row Now;
            public readonly List<Row> Rows = new List<Row>();
        }

        private struct Row
        {
            public double Ms, T;
            public int Bodies, Foreign, Awake, Shapes, Links, Contacts;
            public double Predicted;
            public double V, W;
            public string State;
        }

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<Case> _cases = new List<Case>();
        private readonly Dictionary<HkWorld, Case> _byWorld = new Dictionary<HkWorld, Case>();
        private Type _cost;
        private Action<HkWorld, double> _handler;
        private MyPlanet _planet;
        private DateTime _started;

        public override string Name => _sticks ? SticksScenarioName : ScenarioName;
        public override int TimeoutSeconds => 420;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _cost = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations")
                ?.GetType("Optimizer.Optimizations.PhysicsCost");
            SkipUnless(_cost?.GetField("Sampled", any) != null, "SentisOptimisations has no timing of the physics clusters for tests");
            Check(CreateTopPart != null, "no CreateTopPartAndAttach");
            _config.Set("FreezerEnabled", false);
            _config.Set("EnablePhysicsGuard", false);

            // ------------------------------------------------------------- places
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            var others = MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.PositionComp.GetPosition()).ToList();
            bool Lonely(Vector3D p) => others.All(o => Vector3D.DistanceSquared(o, p) > 15000.0 * 15000.0);
            var space = new List<Vector3D>();
            var spaceWanted = _sticks ? 1 : 6;
            for (var i = 0; i < 400 && space.Count < spaceWanted; i++)
            {
                var probe = new Vector3D(-850000 + (i % 20) * 85000, 650000 - (i / 20) * 65000, 450000 - (i % 7) * 115000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() > 1e-6) continue;
                if (planets.Any(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) < p.MaximumRadius + 20000)) continue;
                if (!Lonely(probe) || space.Any(s => Vector3D.Distance(s, probe) < 60000)) continue;
                space.Add(probe);
            }
            Check(space.Count == spaceWanted, $"only {space.Count} of {spaceWanted} places in space");
            _planet = planets.Where(p => p.HasAtmosphere).OrderByDescending(p => p.MaximumRadius).FirstOrDefault();
            SkipUnless(_planet != null, "no planet with an atmosphere");
            var centre = _planet.PositionComp.GetPosition();
            var ground = new List<Vector3D>();
            const int n = 1600;
            var groundWanted = _sticks ? 14 : 4;
            for (var i = 0; i < n && ground.Count < groundWanted; i++)
            {
                var y = 1 - 2.0 * (i + 0.5) / n;
                var r = Math.Sqrt(1 - y * y);
                var a = i * Math.PI * (3 - Math.Sqrt(5));
                var dir = new Vector3D(Math.Cos(a) * r, y, Math.Sin(a) * r);
                var g = Surface(centre + dir * _planet.MaximumRadius);
                if (!Lonely(g) || ground.Any(s => Vector3D.Distance(s, g) < 45000) || !Flat(g, dir)) continue;
                ground.Add(g);
            }
            Check(ground.Count == groundWanted, $"only {ground.Count} of {groundWanted} flat places on {_planet.StorageName}");

            var k = 0;
            void SpaceCase(string name, Func<Case, IEnumerator> build, Action<Case, double> drive = null) =>
                _cases.Add(new Case { Name = name, Spot = space[k++], Up = Vector3D.Up, Build = build, Drive = drive });
            SpaceCase("space_empty", null);
            if (!_sticks)
            {
                SpaceCase("space_pistons_still", c => PistonStack(c, false), null);
                SpaceCase("space_pistons_moving", c => PistonStack(c, true), PistonsBackAndForth);
                SpaceCase("space_rotor_bare_30rpm", c => Rotor(c, false, 30));
                SpaceCase("space_rotor_plate_30rpm", c => Rotor(c, true, 30));
                SpaceCase("space_rotor_plate_5rpm", c => Rotor(c, true, 5));
            }
            k = 0;
            void PlanetCase(string name, Func<Case, IEnumerator> build, Action<Case, double> drive = null) =>
                _cases.Add(new Case { Name = name, OnPlanet = true, Spot = ground[k], Up = Vector3D.Normalize(ground[k++] - centre), Build = build, Drive = drive });
            PlanetCase("planet_empty", null);
            if (!_sticks)
            {
                PlanetCase("planet_rover_parked", c => Rover(c), null);
                PlanetCase("planet_rover_driving", c => Rover(c), RoverCircle);
                PlanetCase("planet_piston_press", c => PistonPress(c));
            }
            else
            {
                // standing kept awake, by length; lying kept awake, by length; 500 blocks toppling three ways
                foreach (var length in new[] { 20, 50, 125, 250, 500 })
                    PlanetCase($"planet_stick_1x{length}_standing", c => Stick(c, 1, length, 1, false, 0));
                foreach (var length in new[] { 20, 50, 125, 250 })
                    PlanetCase($"planet_stick_1x{length}_lying", c => Stick(c, length, 1, 1, false, 0));
                PlanetCase("planet_stick_1x500_topple", c => Stick(c, 1, 500, 1, true, 0.01));
                PlanetCase("planet_stick_2x125_topple", c => Stick(c, 2, 125, 2, true, 0.02));
                PlanetCase("planet_stick_5x20_topple", c => Stick(c, 5, 20, 5, true, 0.1));
                PlanetCase("planet_stick_1x500_lying", c => Stick(c, 500, 1, 1, false, 0));
            }

            FakeClients.RemoveAll();
            FakeClients.Add(_cases.Count, new FakeClients.NetworkProfile { RttMs = 30 }, i =>
            {
                var c = _cases[i];
                var side = Vector3D.CalculatePerpendicularVector(c.Up);
                // along the axis a stick topples about: out of the way of its fall
                var at = c.Spot + side * 200;
                return (c.OnPlanet ? Surface(at + c.Up * 50) + c.Up * 3 : at, 0, 0);
            }, withCharacters: true);
            var arrive = WaitForSeconds(8, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;
            foreach (var c in _cases)
            {
                if (c.Build == null) continue;
                var build = c.Build(c);
                while (build.MoveNext()) yield return build.Current;
                foreach (var g in c.Grids) Count(g, c);
            }
            var settle = WaitForSeconds(3, "the cases are in place");
            while (settle.MoveNext()) yield return settle.Current;

            // ------------------------------------------------------------- timed every frame
            _handler = OnSampled;
            _cost.GetField("Sampled", any).SetValue(null, _handler);
            _cost.GetField("GapFrames", any).SetValue(null, 1);
            _started = DateTime.UtcNow;
            var lastNote = _started;
            while (true)
            {
                var t = (DateTime.UtcNow - _started).TotalSeconds;
                if (t > MaxRunSeconds || t > MinRunSeconds && _cases.All(c => c.Rows.Count >= StepsWanted)) break;
                foreach (var c in _cases) c.Drive?.Invoke(c, t);
                Observe(t);
                if ((DateTime.UtcNow - lastNote).TotalSeconds >= 15)
                {
                    lastNote = DateTime.UtcNow;
                    Note($"measuring ({t:0} s): " + string.Join(", ", _cases.Select(c => $"{c.Name} {c.Rows.Count}")));
                }
                yield return null;
            }
            StopSampling();

            // ------------------------------------------------------------- what came out
            var ic = CultureInfo.InvariantCulture;
            var csv = new List<string> { "case,t,ms,predicted,bodies,foreign,awake_grids,shapes,links,contacts,v,w,state" };
            foreach (var c in _cases)
                foreach (var row in c.Rows)
                    csv.Add(string.Join(",", c.Name, row.T.ToString("0.00", ic), row.Ms.ToString("0.0000", ic), row.Predicted.ToString("0.0000", ic), row.Bodies, row.Foreign, row.Awake, row.Shapes, row.Links,
                        row.Contacts, row.V.ToString("0.00", ic), row.W.ToString("0.000", ic), row.State));
            var dir2 = TestRunner.ReportDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            Directory.CreateDirectory(dir2);
            var file = Path.Combine(dir2, CsvName);
            File.WriteAllLines(file, csv);
            double Median(IEnumerable<double> xs) { var l = xs.OrderBy(x => x).ToList(); return l.Count == 0 ? 0 : l[l.Count / 2]; }
            var spaceBase = Median(_cases[0].Rows.Select(x => x.Ms));
            var planetBase = Median(_cases.First(c => c.Name == "planet_empty").Rows.Select(x => x.Ms));
            foreach (var c in _cases)
            {
                var awake = c.Rows.Where(x => x.Bodies > 0).ToList();
                var b = c.OnPlanet ? planetBase : spaceBase;
                Note($"MECHANISM {c.Name} | {c.Rows.Count} steps, awake in {awake.Count}: over the empty {Median(awake.Select(x => x.Ms)) - b:+0.000;-0.000} ms median, " +
                     $"p90 {(awake.Count > 0 ? awake.Select(x => x.Ms).OrderBy(x => x).ElementAt((int)(awake.Count * 0.9)) - b : 0):+0.000;-0.000}, max {(c.Rows.Count > 0 ? c.Rows.Max(x => x.Ms) - b : 0):+0.000}; " +
                     $"bodies up to {(c.Rows.Count > 0 ? c.Rows.Max(x => x.Bodies) : 0)}, foreign {(c.Rows.Count > 0 ? c.Rows.Max(x => x.Foreign) : 0)}, shapes {(c.Rows.Count > 0 ? c.Rows.Max(x => x.Shapes) : 0)}, links {(c.Rows.Count > 0 ? c.Rows.Max(x => x.Links) : 0)}, " +
                     $"contacts a frame median {Median(awake.Select(x => (double)x.Contacts))}, v up to {(c.Rows.Count > 0 ? c.Rows.Max(x => x.V) : 0):0.0}, w up to {(c.Rows.Count > 0 ? c.Rows.Max(x => x.W) : 0):0.00}");
            }
            Note($"MECHANISMS | {csv.Count - 1} measurements in {file}");
            var unmeasured = _cases.Where(c => c.Rows.Count < 20).Select(c => c.Name).ToList();
            Check(unmeasured.Count == 0, "measured fewer than 20 times: " + string.Join(", ", unmeasured));
        }

        // ================================================================= the cases

        private MyObjectBuilder_CubeBlock Block(List<MyObjectBuilder_CubeBlock> blocks, string subtype, int x, int y, int z,
            Base6Directions.Direction forward = Base6Directions.Direction.Forward, Base6Directions.Direction up = Base6Directions.Direction.Up)
        {
            var owner = WorldApi.PlayerIdentityId();
            var block = WorldApi.MakeBlockOb(subtype);
            block.Min = new SerializableVector3I(x, y, z);
            block.BlockOrientation = new SerializableBlockOrientation(forward, up);
            block.Owner = owner;
            block.BuiltBy = owner;
            block.ShareMode = MyOwnershipShareModeEnum.Faction;
            blocks.Add(block);
            return block;
        }

        private MyObjectBuilder_CubeGrid GridOf(Case c, string name, MyCubeSize size, bool isStatic, MatrixD world, List<MyObjectBuilder_CubeBlock> blocks) => new MyObjectBuilder_CubeGrid
        {
            Name = WorldApi.EntityPrefix + Prefix + c.Name + "-" + name,
            DisplayName = WorldApi.EntityPrefix + Prefix + c.Name + "-" + name,
            GridSizeEnum = size,
            IsStatic = isStatic,
            PositionAndOrientation = new MyPositionAndOrientation(world),
            PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
            CubeBlocks = blocks,
            DestructibleBlocks = false,
        };

        private MyCubeGrid Spawn(Case c, MyObjectBuilder_CubeGrid ob)
        {
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            c.Grids.Add(grid);
            return grid;
        }

        private void Attach(Case c, MyMechanicalConnectionBlockBase block)
        {
            CreateTopPart.Invoke(block, new[] { (object)WorldApi.PlayerIdentityId(), NormalTopSize, true });
            if (block.TopGrid is MyCubeGrid top && !c.Grids.Contains(top))
            {
                Track(top);
                c.Grids.Add(top);
            }
        }

        /// <summary>The piston stack of <see cref="PistonStackScenario"/>, its base dynamic; moving or not.</summary>
        private IEnumerator PistonStack(Case c, bool moving)
        {
            var group = WorldApi.LoadAuthoredGroup(PistonStackScenario.ResourceName, WorldApi.EntityPrefix + Prefix + c.Name);
            var move = MatrixD.CreateTranslation(c.Spot - group[0].PositionAndOrientation.Value.GetMatrix().Translation);
            foreach (var ob in group)
                ob.PositionAndOrientation = new MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
            group[0].IsStatic = false;
            foreach (var ob in group) Spawn(c, ob);
            yield return null;
            foreach (var piston in c.Grids.SelectMany(g => g.GetFatBlocks()).OfType<Sandbox.ModAPI.IMyPistonBase>())
                piston.Velocity = 0;
        }

        private static void PistonsBackAndForth(Case c, double t)
        {
            // out for 6 s, back for 6 s
            var velocity = (int)(t / 6) % 2 == 0 ? 0.5f : -0.5f;
            foreach (var piston in c.Grids.SelectMany(g => g.GetFatBlocks()).OfType<Sandbox.ModAPI.IMyPistonBase>())
                if (Math.Abs(piston.Velocity - velocity) > 1e-3f) piston.Velocity = velocity;
            c.State = velocity > 0 ? "out" : "back";
        }

        /// <summary>A base of 3 x 1 x 3, an advanced rotor on it, its head bare or with a plate of 5 x 5, turning at <paramref name="rpm"/>.</summary>
        private IEnumerator Rotor(Case c, bool plate, float rpm)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 3; x++)
                for (var z = 0; z < 3; z++)
                    Block(blocks, Armor, x, 0, z);
            Block(blocks, "LargeAdvancedStator", 1, 1, 1);
            Block(blocks, "LargeBlockBatteryBlock", 0, 1, 0);
            var grid = Spawn(c, GridOf(c, "base", MyCubeSize.Large, false, MatrixD.CreateWorld(c.Spot, Vector3D.Forward, Vector3D.Up), blocks));
            WorldApi.ChargeBatteries(grid);
            yield return null;
            var stator = grid.GetFatBlocks().OfType<MyMotorStator>().First();
            Attach(c, stator);
            yield return null;
            Note($"{c.Name}: the rotor's head {(stator.TopGrid == null ? "not attached" : "attached")}");
            if (plate && stator.TopGrid is MyCubeGrid head)
            {
                // the plate on the side of the head away from the rotor
                var away = Vector3D.TransformNormal(stator.WorldMatrix.Up, MatrixD.Transpose(head.WorldMatrix));
                var axis = Base6Directions.GetIntVector(Base6Directions.GetClosestDirection((Vector3)away));
                var origin = stator.TopBlock.Position + axis;
                for (var i = -2; i <= 2; i++)
                    for (var j = -2; j <= 2; j++)
                    {
                        var offset = axis.X != 0 ? new Vector3I(0, i, j) : axis.Y != 0 ? new Vector3I(i, 0, j) : new Vector3I(i, j, 0);
                        try { WorldApi.AddRealBlock(head, Armor, origin + offset); }
                        catch (Exception e) { Note($"{c.Name}: a plate block not added: {e.Message}"); }
                    }
            }
            ((Sandbox.ModAPI.IMyMotorStator)stator).TargetVelocityRPM = rpm;
            ((Sandbox.ModAPI.IMyMotorStator)stator).Torque = 1e7f;
            c.State = rpm + "rpm";
        }

        /// <summary>A small rover of 12 x 6 on four 3 x 3 wheels, a metre over the ground (WheelPerfScenario's).</summary>
        private IEnumerator Rover(Case c)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 12; x++)
                for (var z = 0; z < 6; z++)
                    Block(blocks, "SmallBlockArmorBlock", x, 0, z);
            for (var x = 4; x < 8; x++)
                Block(blocks, "SmallBlockSmallBatteryBlock", x, 1, 2);
            foreach (var x in new[] { 1, 10 })
            {
                var left = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_MotorSuspension)Block(blocks, "SmallSuspension3x3mirrored", x, 0, -2, Base6Directions.Direction.Down, Base6Directions.Direction.Forward);
                var right = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_MotorSuspension)Block(blocks, "SmallSuspension3x3", x, 0, 6, Base6Directions.Direction.Down, Base6Directions.Direction.Backward);
                foreach (var s in new[] { left, right })
                {
                    s.SpeedLimit = 1000f;
                    s.Height = -0.32f;
                    s.PropulsionOverride = 0;
                    s.SteeringOverride = 0;
                }
            }
            var forward = Vector3D.CalculatePerpendicularVector(c.Up);
            var rover = Spawn(c, GridOf(c, "rover", MyCubeSize.Small, false, MatrixD.CreateWorld(c.Spot + c.Up * 2, forward, c.Up), blocks));
            WorldApi.ChargeBatteries(rover);
            yield return null;
            foreach (var suspension in rover.GetFatBlocks().OfType<MyMotorSuspension>().ToList())
                Attach(c, suspension);
        }

        private static void RoverCircle(Case c, double t)
        {
            foreach (var suspension in c.Grids.SelectMany(g => g.GetFatBlocks()).OfType<MyMotorSuspension>())
            {
                if (Math.Abs(suspension.PropulsionOverride - 1f) > 1e-3f) suspension.PropulsionOverride = 1f;
                if (Math.Abs(suspension.SteeringOverride - 0.3f) > 1e-3f) suspension.SteeringOverride = 0.3f;
            }
            c.State = "driving";
        }

        /// <summary>A static plate of 3 x 1 x 3 standing 9 m over the ground, a piston under it pushing its head down into the ground.</summary>
        private IEnumerator PistonPress(Case c)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            // the grid upside down: the plate (y = 0) on top, the piston (y = 1) under it pointing down, a battery on the plate
            for (var x = 0; x < 3; x++)
                for (var z = 0; z < 3; z++)
                    Block(blocks, Armor, x, 0, z);
            Block(blocks, "LargePistonBase", 1, 1, 1);
            Block(blocks, "LargeBlockBatteryBlock", 0, -1, 0);
            var forward = Vector3D.CalculatePerpendicularVector(c.Up);
            // the piston's base block 6 m over the ground: its head, 2.5 m under it, has 3.5 m to go
            var grid = Spawn(c, GridOf(c, "press", MyCubeSize.Large, true, MatrixD.CreateWorld(c.Spot + c.Up * (6 + 1.25 + 2.5), forward, -c.Up), blocks));
            WorldApi.ChargeBatteries(grid);
            yield return null;
            var piston = grid.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>().FirstOrDefault();
            if (piston == null) { Note(c.Name + ": no piston among " + string.Join(", ", grid.GetFatBlocks().Select(b => b.GetType().Name))); yield break; }
            Attach(c, piston);
            if (piston is Sandbox.ModAPI.IMyPistonBase p) p.Velocity = 1f;
            Note($"{c.Name}: the piston's head {(piston.TopGrid == null ? "not attached" : "attached")}");
            c.State = "pressing";
        }

        /// <summary>A stick of armour <paramref name="x"/> by <paramref name="y"/> (up) by <paramref name="z"/> on the ground; tipped to topple, or kept awake.</summary>
        private IEnumerator Stick(Case c, int x, int y, int z, bool topple, double tip)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var i = 0; i < x; i++)
                for (var j = 0; j < y; j++)
                    for (var l = 0; l < z; l++)
                        Block(blocks, Armor, i - x / 2, j, l - z / 2);
            var forward = Vector3D.CalculatePerpendicularVector(c.Up);
            var right = Vector3D.Cross(forward, c.Up);
            var at = Surface(c.Spot + c.Up * 50) + c.Up * 1.75;
            if (x > 1)
            {
                // lying along `right`: put over the highest ground under its length, a metre to spare, to come down on it
                var centre = _planet.PositionComp.GetPosition();
                var top = 0.0;
                for (var d = -x / 2 * 2.5; d <= x / 2 * 2.5; d += 5)
                    top = Math.Max(top, Vector3D.Distance(Surface(c.Spot + right * d + c.Up * 300), centre));
                at = centre + c.Up * (top + 1.25 + 1);
            }
            var ob = GridOf(c, "stick", MyCubeSize.Large, false, MatrixD.CreateWorld(at, forward, c.Up), blocks);
            ob.DampenersEnabled = false;
            var stick = Spawn(c, ob);
            yield return null;
            if (stick.Physics?.RigidBody == null) yield break;
            stick.Physics.RigidBody.EnableDeactivation = false;
            if (topple) stick.Physics.AngularVelocity = (Vector3)(forward * tip);
            c.State = topple ? "toppling" : "lying";
        }

        // ================================================================= measuring

        private Vector3D Surface(Vector3D point) => _planet.GetClosestSurfacePointGlobal(ref point);

        private bool Flat(Vector3D spot, Vector3D up)
        {
            var centre = _planet.PositionComp.GetPosition();
            var height = Vector3D.Distance(spot, centre);
            var a = Vector3D.CalculatePerpendicularVector(up);
            var b = Vector3D.Cross(up, a);
            return new[] { a, -a, b, -b }.All(d => Math.Abs(Vector3D.Distance(Surface(spot + d * 30 + up * 50), centre) - height) < 2);
        }

        private static void Count(MyCubeGrid grid, Case c)
        {
            var body = grid.Physics?.RigidBody;
            if (body == null) return;
            body.ContactPointCallback += (ref HkContactPointEvent e) => System.Threading.Interlocked.Increment(ref c.Contacts);
        }

        private void Observe(double t)
        {
            _byWorld.Clear();
            foreach (var c in _cases)
            {
                var row = new Row { T = t, State = c.State };
                HkWorld world = null;
                foreach (var g in c.Grids)
                {
                    if (g.Closed || g.Physics == null) continue;
                    world = world ?? g.Physics.HavokWorld;
                    if (!g.Physics.IsActive) continue;
                    row.Awake++;
                    row.Shapes += (g.Physics as MyGridPhysics)?.Shape?.ShapeCount ?? 0;
                    row.V = Math.Max(row.V, g.Physics.LinearVelocity.Length());
                    row.W = Math.Max(row.W, g.Physics.AngularVelocity.Length());
                }
                row.Links = c.Grids.Count(g => !g.Closed) > 0 ? Math.Max(0, c.Grids.Count(g => !g.Closed) - 1) : 0;
                var contacts = System.Threading.Volatile.Read(ref c.Contacts);
                row.Contacts = contacts - c.LastContacts;
                c.LastContacts = contacts;
                if (world == null) world = FakeClients.Character(_cases.IndexOf(c))?.Physics?.HavokWorld;
                c.Now = row;
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
            var row = c.Now;
            row.Ms = ms;
            row.Predicted = Predicted(world);
            foreach (var body in world.ActiveRigidBodies)
            {
                row.Bodies++;
                var entity = (body.UserObject as MyPhysicsBody)?.Entity;
                if (entity == null) { row.Foreign++; continue; }
                if (entity is Sandbox.Game.Entities.Character.MyCharacter) continue;
                if (entity.GetTopMostParent() is MyCubeGrid grid && ours.Contains(grid.EntityId)) continue;
                row.Foreign++;
            }
            c.Rows.Add(row);
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
                    foreach (var g in c.Grids)
                        if (!g.Closed) g.Close();
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
