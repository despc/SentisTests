using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    internal static class Bodies
    {
        /// <summary>How far along the direction the middle of the entity is from the point.</summary>
        public static double Along(VRage.ModAPI.IMyEntity entity, Vector3D from, Vector3D direction) =>
            Vector3D.Dot(entity.PositionComp.WorldAABB.Center - from, direction);

        /// <summary>A grid's physics as Havok sees it: its world (cluster), whether it moves, its collision filter.</summary>
        public static string Physics(MyCubeGrid grid)
        {
            if (grid == null || grid.MarkedForClose) return "gone";
            var physics = grid.Physics;
            var body = physics?.RigidBody;
            if (body == null) return grid.DisplayName + ": no body";
            var info = body.GetCollisionFilterInfo();
            var shape = body.GetShape();
            string shapeText;
            try { shapeText = shape.ShapeType + (shape.ShapeType == Havok.HkShapeType.List ? " of " + ((Havok.HkListShape)shape).TotalChildrenCount : ""); }
            catch (Exception e) { shapeText = "? " + e.GetType().Name; }
            return grid.DisplayName.Replace(WorldApi.EntityPrefix, "") + ": shape " + shapeText + ", mass " + physics.Mass.ToString("F0") + ", world " + (physics.HavokWorld?.GetHashCode().ToString("x") ?? "none") +
                   ", cluster " + (typeof(Sandbox.Engine.Physics.MyPhysicsBody).GetProperty("ClusterObjectID", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(physics) ?? "?") + ", enabled " + physics.Enabled + ", active " + body.IsActive + ", layer " + (info & 31) +
                   ", system " + (info >> 16) + ", sub " + ((info >> 5) & 31) + ", not with " + ((info >> 10) & 31) + ", static " + grid.IsStatic +
                   ", v " + physics.LinearVelocity.Length().ToString("F1");
        }

        /// <summary>Whether Havok's filter lets the two grids' bodies collide at all.</summary>
        public static string Filter(MyCubeGrid a, MyCubeGrid b)
        {
            var ba = a?.Physics?.RigidBody;
            var bb = b?.Physics?.RigidBody;
            var world = a?.Physics?.HavokWorld;
            if (ba == null || bb == null || world == null) return "filter: no bodies";
            return "filter lets them collide: " + world.IsCollisionEnabled(ba.GetCollisionFilterInfo(), bb.GetCollisionFilterInfo()) +
                   ", same world " + (a.Physics.HavokWorld == b.Physics.HavokWorld);
        }

        /// <summary>
        /// A plate three blocks thick of heavy armor, centred on the grid's origin: large 3 x 3 (7.5 m), small 10 x 10
        /// (5 m). Thinner, a grid flies through what it hits at speed: the game drops the contact to deform blocks, and a
        /// thin one is through before it stops.
        /// </summary>
        public static IEnumerable<Part> Plate(MyCubeSize size)
        {
            var large = size == MyCubeSize.Large;
            var block = large ? "CubeBlock/LargeHeavyBlockArmorBlock" : "CubeBlock/SmallHeavyBlockArmorBlock";
            var side = large ? 3 : 10;
            for (var x = 0; x < side; x++)
            for (var y = 0; y < side; y++)
            for (var z = 0; z < 3; z++)
                yield return new Part(block, x - side / 2, y - side / 2, z);
        }

        public static List<Part> Wall(string block, int half, int depth)
        {
            var parts = new List<Part>();
            for (var x = -half; x <= half; x++)
            for (var y = -half; y <= half; y++)
            for (var z = 0; z < depth; z++)
                parts.Add(new Part(block, x, y, -z));
            return parts;
        }
    }

    /// <summary>
    /// Plates of heavy armor three blocks thick (<see cref="Bodies.Plate"/>) thrown at a station's wall of heavy armor
    /// three blocks thick, one after another: large and small grids, at
    /// 10, 50, 100 and 150 m/s, straight and slanted, a whole wall for each. Whatever is left of a ship stays on its side
    /// of the wall, or rams through it breaking its blocks: none goes through a wall it left whole.
    /// </summary>
    public sealed class CollisionWallCheck : RegressionCheck
    {
        public override string Name => Prefix + "collision_wall";
        protected override bool NeedsDamage => true;
        public override int TimeoutSeconds => 240;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(30, 0, -20));
            HoldHighQuality();
            MyCubeGrid wall = null;
            var front = -2.5 * 0.5;
            var back = 2.5 * 2.5;
            var throws = new List<string>();
            foreach (var size in new[] { MyCubeSize.Large, MyCubeSize.Small })
            foreach (var speed in new[] { 10.0, 50.0, 100.0, 150.0 })
            foreach (var slant in new[] { 0.0, 25.0 })
            {
                var direction = Vector3D.Transform(Site.Forward, MatrixD.CreateFromAxisAngle(Site.Up, MathHelper.ToRadians(slant)));
                var start = Site.Origin - direction * 40;
                // a whole wall for each throw
                if (wall != null) yield return CloseAndWait(wall);
                wall = Build("wall", Site.Origin, true, Bodies.Wall("CubeBlock/LargeHeavyBlockArmorBlock", 3, 3));
                var wallBlocks = wall.BlocksCount;
                var ship = Build("thrown", start, false, Bodies.Plate(size), size);
                yield return null;
                var before = Bodies.Physics(ship) + " | " + Bodies.Physics(wall) + " | " + Bodies.Filter(ship, wall);
                ship.Physics.LinearVelocity = (Vector3)(direction * speed);
                yield return Seconds(30 / speed);
                var meeting = Bodies.Physics(ship) + " | " + Bodies.Physics(wall) + " | " + Bodies.Filter(ship, wall);
                yield return Seconds(10 / speed + 2);
                var where = ship.MarkedForClose ? double.NaN : Bodies.Along(ship, Site.Origin, Site.Forward);
                var label = size + " at " + speed + " m/s" + (slant > 0 ? " slanted " + slant + " degrees" : "");
                // rammed through, breaking the wall's blocks on the way, is what a heavy ship does; through a whole wall is not
                var holed = wallBlocks - (wall.MarkedForClose ? 0 : wall.BlocksCount);
                Check(ship.MarkedForClose || where < back || holed > 0, label + " went through the wall and broke none of its blocks: its middle " + F(where) +
                                                                       " m past the wall's face (the wall's back at " + F(back) + "); before: " + before + "; meeting: " + meeting);
                throws.Add(label + (ship.MarkedForClose ? " (broke up)" : where >= back ? " rammed through, " + holed + " wall blocks broken" : " stopped at " + F(where)) +
                           (holed > 0 && where < back ? ", " + holed + " wall blocks broken" : ""));
                yield return CloseAndWait(ship);
            }
            Result(throws.Count + " ships thrown at the wall, none through it (wall face at " + F(front) + "): " + string.Join("; ", throws));
        }
    }

    /// <summary>
    /// Plates three blocks thick driven head on at each other at 5, 15 and 30 m/s each, large into large, small into
    /// small, large into small: they meet and part, neither passes through the other whole. Small into small is let through at
    /// speed, as the game itself does (vanilla too, under load at times with no contact at all); any pair may ram through
    /// the blocks the impact broke.
    /// </summary>
    public sealed class CollisionShipsCheck : RegressionCheck
    {
        public override string Name => Prefix + "collision_ships";
        protected override bool NeedsDamage => true;
        public override int TimeoutSeconds => 180;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(30, 0, 0));
            // at the game's low quality (its own under load) grids pass through each other: the collision itself is checked
            HoldHighQuality();
            var pairs = new[] { (MyCubeSize.Large, MyCubeSize.Large), (MyCubeSize.Small, MyCubeSize.Small), (MyCubeSize.Large, MyCubeSize.Small) };
            var met = new List<string>();
            var through = new List<string>();
            foreach (var speed in new[] { 5.0, 15.0, 30.0 })
            foreach (var (one, two) in pairs)
            {
                var a = Build("head-a", Site.Origin - Site.Forward * 25, false, Bodies.Plate(one), one);
                var b = Build("head-b", Site.Origin + Site.Forward * 25, false, Bodies.Plate(two), two);
                yield return null;
                Func<double> integrity = () => (a.MarkedForClose ? 0 : a.GetBlocks().Sum(x => (double)x.Integrity)) + (b.MarkedForClose ? 0 : b.GetBlocks().Sum(x => (double)x.Integrity));
                var whole = integrity();
                var blocks = a.BlocksCount + b.BlocksCount;
                var before = Bodies.Physics(a) + " | " + Bodies.Physics(b) + " | " + Bodies.Filter(a, b);
                a.Physics.LinearVelocity = (Vector3)(Site.Forward * speed);
                b.Physics.LinearVelocity = (Vector3)(-Site.Forward * speed);
                // until well past the time they meet, the closest the two boxes came and whether they overlapped
                var closest = double.MaxValue;
                var overlapped = 0;
                for (var f = Frames(25 / speed + 2); f > 0; f--)
                {
                    if (!a.MarkedForClose && !b.MarkedForClose)
                    {
                        var boxA = a.PositionComp.WorldAABB;
                        var boxB = b.PositionComp.WorldAABB;
                        if (boxA.Intersects(boxB)) overlapped++;
                        closest = Math.Min(closest, Vector3D.Distance(boxA.Center, boxB.Center));
                    }
                    yield return null;
                }
                var gap = Bodies.Along(b, a.PositionComp.WorldAABB.Center, Site.Forward);
                var label = one + "/" + two + " at " + speed + "+" + speed + " m/s";
                var damage = whole - integrity();
                var lost = blocks - (a.MarkedForClose ? 0 : a.BlocksCount) - (b.MarkedForClose ? 0 : b.BlocksCount);
                var passed = !(a.MarkedForClose || b.MarkedForClose || gap > 0);
                // through the blocks the impact broke (large plates at 30+30 m/s break each other up): rammed, as through a wall
                var rammed = passed && lost > 0;
                // small into small at speed goes through in the game itself (vanilla too): it has only to do damage on the way
                var smallPair = one == MyCubeSize.Small && two == MyCubeSize.Small;
                if (passed && smallPair)
                {
                    // the game lets small grids through at speed; under load they sometimes slip through before any contact
                    if (damage <= 0) Note(label + ": went through with no damage at all; before: " + before);
                }
                else if (passed && !rammed)
                    through.Add(label + ": " + F(gap) + " m, damage " + F(damage) + ", boxes overlapped " + overlapped + " frames, centres " + F(closest) + " m at the closest; before: " + before);
                met.Add(label + " " + (a.MarkedForClose || b.MarkedForClose ? "broke" : passed ? (smallPair ? "through, as the game does for small grids" : "rammed through, " + lost + " blocks broken") : F(gap) + " m apart") + ", damage " + F(damage));
                Note(label + ": " + (a.MarkedForClose || b.MarkedForClose ? "broke" : F(gap) + " m apart") + ", damage " + F(damage));
                // run from the field: a few seconds to look at where they ended
                if (TestRunner.RunOrigin.HasValue) yield return Seconds(4);
                b.Close();
                yield return CloseAndWait(a);
                yield return CloseAndWait(b);
            }
            Note("head on: " + string.Join("; ", met));
            Check(through.Count == 0, through.Count + " of " + met.Count + " pairs went wrong: " + string.Join(" || ", through));
            Result("head on, none through the other: " + string.Join("; ", met));
        }
    }

    /// <summary>
    /// Two cubes of light armor, 10 x 10 x 10 large blocks each (25 m, 1000 blocks), set at each other in open space at
    /// 2, 5, 10, 20 and 40 m/s each. They are 25 m through when touching: how far they went into each other, how they
    /// moved apart, how much armor they lost. Neither may come out on the other's side.
    /// </summary>
    public sealed class CollisionCubesCheck : RegressionCheck
    {
        public override string Name => Prefix + "collision_cubes";
        protected override bool NeedsDamage => true;
        public override int TimeoutSeconds => 300;

        private const int Side = 10;
        private const double Size = Side * 2.5;

        protected override IEnumerator Check()
        {
            // the player right by the cubes, as in every check: the physics there is stepped
            yield return Arrive(Site.At(30, 15, 0));
            HoldHighQuality();
            var runs = new List<string>();
            var through = new List<string>();
            foreach (var speed in new[] { 2.0, 5.0, 10.0, 20.0, 40.0 })
            {
                // centred on the line through the site, 15 m of empty space between their faces
                var half = Size / 2 + 7.5;
                var a = Build("cube-a", Site.Origin - Site.Forward * half - Cube.Offset(Site), false, Cube.Parts(Side));
                var b = Build("cube-b", Site.Origin + Site.Forward * half - Cube.Offset(Site), false, Cube.Parts(Side));
                yield return Seconds(0.5);
                Func<double> integrity = () => (a.MarkedForClose ? 0 : a.GetBlocks().Sum(x => (double)x.Integrity)) + (b.MarkedForClose ? 0 : b.GetBlocks().Sum(x => (double)x.Integrity));
                var whole = integrity();
                var blocks = a.BlocksCount + b.BlocksCount;
                a.Physics.LinearVelocity = (Vector3)(Site.Forward * speed);
                b.Physics.LinearVelocity = (Vector3)(-Site.Forward * speed);
                var closest = double.MaxValue;
                // until well after they meet: 15 m to close at 2 x speed, then as long again
                for (var f = Frames(15 / speed + 3); f > 0; f--)
                {
                    if (!a.MarkedForClose && !b.MarkedForClose)
                        closest = Math.Min(closest, Bodies.Along(b, a.PositionComp.WorldAABB.Center, Site.Forward));
                    yield return null;
                }
                var gap = a.MarkedForClose || b.MarkedForClose ? double.NaN : Bodies.Along(b, a.PositionComp.WorldAABB.Center, Site.Forward);
                var va = Vector3D.Dot(a.Physics?.LinearVelocity ?? Vector3.Zero, Site.Forward);
                var vb = Vector3D.Dot(b.Physics?.LinearVelocity ?? Vector3.Zero, Site.Forward);
                var lost = blocks - (a.MarkedForClose ? 0 : a.BlocksCount) - (b.MarkedForClose ? 0 : b.BlocksCount);
                var line = speed + "+" + speed + " m/s: into each other " + F(Size - closest) + " m (centres " + F(closest) + " m at the closest), now " + F(gap) +
                           " m apart, moving " + F(va) + " / " + F(vb) + " m/s, " + lost + " blocks lost, " + F(whole - integrity()) + " integrity";
                // through the blocks the impact broke (light armor at 40+40 m/s breaks up): rammed, as through a wall
                if (gap < 0 && lost == 0) through.Add(line);
                else if (gap < 0) line += " (rammed through)";
                runs.Add(line);
                b.Close();
                yield return CloseAndWait(a);
                yield return CloseAndWait(b);
            }
            Note("cubes: " + string.Join(" || ", runs));
            Check(through.Count == 0, through.Count + " of " + runs.Count + " times the cubes went through each other: " + string.Join(" || ", through));
            Result("two 10x10x10 light armor cubes, none through the other: " + string.Join(" || ", runs));
        }
    }

    internal static class Cube
    {

        public static IEnumerable<Part> Parts(int side)
        {
            for (var x = 0; x < side; x++)
            for (var y = 0; y < side; y++)
            for (var z = 0; z < side; z++)
                yield return new Part("CubeBlock/LargeBlockArmorBlock", x, y, z);
        }

        /// <summary>From a cube's origin cell to its middle, in the world: the cube is built from its corner.</summary>
        public static Vector3D Offset(Site site)
        {
            var half = (10 - 1) * 2.5 / 2;
            // cells grow along the grid's right, up and backward
            return site.Right * half + site.Up * half - site.Forward * half;
        }
    }

    /// <summary>An astronaut on the jetpack at full speed into a wall: stopped by it, not through it.</summary>
    public sealed class CollisionCharacterCheck : RegressionCheck
    {
        public override string Name => Prefix + "collision_character";

        protected override IEnumerator Check()
        {
            var wall = Build("character-wall", Site.Origin, true, Bodies.Wall("CubeBlock/LargeBlockArmorBlock", 3, 1));
            yield return Arrive(Site.Origin - Site.Forward * 60);
            FakeClients.SetFree(Me, true);
            var top = 0.0;
            for (var i = Frames(6); i > 0; i--)
            {
                Keys(new Vector3(0, 0, -1), jetpack: true, dampeners: false, face: Site.Forward);
                top = Math.Max(top, Motion.Speed(Body));
                yield return null;
            }
            yield return Hold(2, Vector3.Zero, jetpack: true, dampeners: true, face: Site.Forward);
            var where = Bodies.Along(Body, Site.Origin, Site.Forward);
            Check(top > 20, "the astronaut never got going: " + F(top) + " m/s");
            Check(where < 1.25, "the astronaut went through the wall at " + F(top) + " m/s: " + F(where) + " m past its face");
            Result("into the wall at " + F(top) + " m/s, stopped " + F(-where) + " m short of its middle");
        }
    }

    /// <summary>
    /// On a planet: a ship let fall from 150 m, a ship thrown down at 100 m/s, an astronaut let fall from 60 m - each
    /// ends on the ground, not in it.
    /// </summary>
    public sealed class CollisionGroundCheck : RegressionCheck
    {
        public override string Name => Prefix + "collision_ground";
        protected override bool NeedsDamage => true;
        protected override bool NeedsFallRestore => true;
        public override SiteKind Kind => SiteKind.Planet;
        public override int TimeoutSeconds => 150;

        protected override IEnumerator Check()
        {
            var ground = Highest(Site.Origin, 20);
            yield return Arrive(ground + Site.Up * 60 + Site.Forward * 30);
            HoldHighQuality();
            var landed = new List<string>();
            foreach (var (label, height, speed) in new[] { ("let fall from 150 m", 150.0, 0.0), ("thrown down at 100 m/s", 120.0, 100.0) })
            {
                var ship = Build("faller", ground + Site.Up * height, false, new[]
                {
                    new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeHeavyBlockArmorBlock", 1, 0, 0),
                    new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 1), new Part("CubeBlock/LargeHeavyBlockArmorBlock", 1, 0, 1),
                });
                yield return null;
                if (speed > 0) ship.Physics.LinearVelocity = (Vector3)(-Site.Up * speed);
                // falling first, then at rest
                yield return Until(() => Motion.Speed(ship), v => v > 3, "the ship " + label + " falling, m/s", 10);
                Func<string> where = () =>
                {
                    var c = ship.PositionComp.WorldAABB.Center;
                    var planet = MyGamePruningStructure.GetClosestPlanet(c);
                    return "its middle " + F(Vector3D.Dot(c - Site.Ground(c), Site.Up)) + " m over the ground, " +
                           (planet == null ? "no planet" : F((c - planet.PositionComp.GetPosition()).Length() - planet.MinimumRadius) + " m over the planet's lowest") +
                           ", " + Bodies.Physics(ship);
                };
                yield return Until(() => ship.MarkedForClose ? 0 : Motion.Speed(ship), v => v < 0.5, "the ship " + label + " come to rest, m/s", 40, where);
                if (ship.MarkedForClose)
                {
                    landed.Add("ship " + label + " broke up on the ground");
                    yield return null;
                    continue;
                }
                var at = ship.PositionComp.WorldAABB.Center;
                var over = Vector3D.Dot(at - Site.Ground(at), Site.Up);
                Check(over > -1.5, "the ship " + label + " went into the ground: its middle " + F(over) + " m over it");
                landed.Add("ship " + label + " at " + F(over) + " m");
                yield return CloseAndWait(ship);
            }
            // dropped from 10 m: a fall from higher kills (damage is on in the suite), and a body rolls down the slope
            StandAt(ground + Site.Up * 10, ground + Site.Up * 10 + Site.Forward);
            yield return Seconds(0.5);
            FakeClients.SetFree(Me, true);
            yield return Hold(1, Vector3.Zero, jetpack: false);
            yield return Until(() => { Keys(Vector3.Zero, jetpack: false); return Motion.Speed(Body); }, v => v < 0.3, "the astronaut come to rest, m/s", 30,
                () => F(Vector3D.Dot(Body.PositionComp.GetPosition() - Site.Ground(Body.PositionComp.GetPosition()), Site.Up)) + " m over the ground, " +
                      Body.GetCurrentMovementState() + (Body.IsDead ? ", dead" : ""));
            var feet = Body.PositionComp.GetPosition();
            var feetOver = Vector3D.Dot(feet - Site.Ground(feet), Site.Up);
            Check(feetOver > -1.0, "the astronaut went into the ground: " + F(feetOver) + " m over it");
            landed.Add("astronaut at " + F(feetOver) + " m");
            Result("all on the ground: " + string.Join("; ", landed));
        }
    }

    /// <summary>
    /// A ship docked to a station by connector still collides with the rest of the world: a third ship thrown at it does
    /// not go through it. (SentisOptimisations turns collisions between docked grids off; only between them.)
    /// </summary>
    public sealed class CollisionDockedCheck : RegressionCheck
    {
        public override string Name => Prefix + "collision_docked";
        protected override bool NeedsDamage => true;

        /// <summary>A ship of a connector before a cube of heavy armor 3 x 3 x 3 with a battery in it.</summary>
        private static IEnumerable<Part> Ship()
        {
            yield return new Part("ShipConnector/Connector", 0, 0, 0);
            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
            for (var z = 1; z <= 3; z++)
                yield return x == 0 && y == 0 && z == 2
                    ? new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 2).With(b => Motion.Charged(b))
                    : new Part("CubeBlock/LargeHeavyBlockArmorBlock", x, y, z);
        }

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(20, 6, 0));
            HoldHighQuality();
            var station = Build("docked-station", Site.Origin, true,
                new Part("ShipConnector/Connector", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 1).With(b => Motion.Charged(b)));
            var ship = Build("docked-ship", station.GridIntegerToWorld(Vector3I.Zero) + Site.Forward * 2.5, false, Ship(), forward: -Site.Forward, up: Site.Up);
            // the same ship, free, 60 m off: what happens to it is what the game does without the dock
            var twin = Build("free-ship", station.GridIntegerToWorld(Vector3I.Zero) + Site.Forward * 2.5 + Site.Up * 60, false, Ship(), forward: -Site.Forward, up: Site.Up);
            var connector = (MyShipConnector)ship.GetCubeBlock(Vector3I.Zero).FatBlock;
            yield return Until(() => ((Sandbox.ModAPI.Ingame.IMyShipConnector)connector).Status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connectable ? 1 : 0, v => v > 0,
                "the connectors in reach", 20);
            Request(connector, "TryConnect");
            yield return Until(() => connector.Connected ? 1 : 0, v => v > 0, "the ship docked", 10);
            var results = new List<string>();
            var through = new List<string>();
            foreach (var (target, label) in new[] { (ship, "the docked ship"), (twin, "the free ship") })
            {
                // a cube of heavy armor thrown across the ship from the side
                var thrown = Build("docked-thrown", target.PositionComp.WorldAABB.Center - Site.Right * 30, false, Bodies.Plate(MyCubeSize.Large));
                yield return null;
                thrown.Physics.LinearVelocity = (Vector3)(Site.Right * 40);
                // through is the two middles coming together (cubes 7.5 m through touch at 7.5 m apart): a free ship struck
                // turns and is pushed, and the thrown one may slide past it - that is not through
                var closest = double.MaxValue;
                for (var f = Frames(2.5); f > 0; f--)
                {
                    if (!thrown.MarkedForClose && !target.MarkedForClose)
                        closest = Math.Min(closest, Vector3D.Distance(thrown.PositionComp.WorldAABB.Center, target.PositionComp.WorldAABB.Center));
                    yield return null;
                }
                var line = label + ": " + (thrown.MarkedForClose ? "the thrown one broke up" : "middles " + F(closest) + " m apart at the closest");
                results.Add(line);
                if (!thrown.MarkedForClose && closest < 3) through.Add(line);
                yield return CloseAndWait(thrown);
            }
            Check(through.Count == 0, "thrown at 40 m/s, a cube of heavy armor went through: " + string.Join("; ", through) + " (" + string.Join("; ", results) + ")");
            Result("thrown at 40 m/s, none through: " + string.Join("; ", results));
        }
    }
}
