using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Ships thrown into a planet's ground at speed stop on it, all of them. 16 ships of four heavy armor blocks, 50 m
    /// apart, thrown down at 100 m/s from 120 m at once (reg_collision_ground throws one: under the load of the whole
    /// suite one in about seven went through the planet, 3 km under its ground). Each that came to rest is looked at:
    /// on the ground or under it.
    /// </summary>
    public sealed class PlanetTunnelCheck : RegressionCheck
    {
        public const string MassName = "planet_tunnel_mass";

        private readonly int Ships;
        private readonly int Row;

        /// <param name="ships">how many; the suite throws 16, <see cref="MassName"/> 100 at once (the load of its own)</param>
        public PlanetTunnelCheck(int ships = 16)
        {
            Ships = ships;
            Row = (int)Math.Ceiling(Math.Sqrt(ships));
        }

        public override string Name => Ships == 16 ? Prefix + "planet_tunnel" : MassName;
        public override SiteKind Kind => SiteKind.Planet;
        public override int TimeoutSeconds => 240;
        protected override bool NeedsDamage => true;
        protected override bool NeedsFallRestore => true;

        protected override IEnumerator Check()
        {
            var ground = Highest(Site.Origin, 20);
            yield return Arrive(ground + Site.Up * 80);
            var ships = new List<MyCubeGrid>();
            for (var i = 0; i < Ships; i++)
            {
                var spot = Site.Origin + Site.Right * ((i % Row - (Row - 1) / 2.0) * 40) + Site.Forward * ((i / Row - (Row - 1) / 2.0) * 40);
                var under = Site.Ground(spot);
                ships.Add(Build("tunnel-" + i, under + Site.Up * 120, false, new[]
                {
                    new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeHeavyBlockArmorBlock", 1, 0, 0),
                    new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 1), new Part("CubeBlock/LargeHeavyBlockArmorBlock", 1, 0, 1),
                }));
            }
            yield return null;
            foreach (var ship in ships) ship.Physics.LinearVelocity = (Vector3)(-Site.Up * 100);
            Func<MyCubeGrid, double> over = s => Vector3D.Dot(s.PositionComp.WorldAABB.Center - Site.Ground(s.PositionComp.WorldAABB.Center), Site.Up);
            // each at rest (or broken up), or well under the ground and still going
            yield return Until(() => ships.Count(s => s.MarkedForClose || Motion.Speed(s) < 0.5 || over(s) < -50), v => v >= Ships, "the ships stopped or gone under", 60,
                () => string.Join("; ", ships.Where(s => !s.MarkedForClose && Motion.Speed(s) >= 0.5).Select(s => s.DisplayName + " " + F(Motion.Speed(s)) + " m/s at " + F(over(s)) + " m")));
            // a ship that went through where the planet had no physics yet (the first fast fall on a place, vanilla too)
            // is brought back by SentisGameplayImprovements (FallInVoxelDetector, twice a second): given 20 s of game time
            var fell = ships.Where(s => !s.MarkedForClose && over(s) < -3).ToList();
            for (var f = Frames(20); f > 0 && fell.Any(s => !s.MarkedForClose && over(s) < -3); f--)
                yield return null;
            var back = fell.Count(s => !s.MarkedForClose && over(s) >= -3);
            var through = ships.Where(s => !s.MarkedForClose && over(s) < -3).ToList();
            // where they went through, against the planet's physics cells (it builds its voxel physics in cells around what
            // moves near it): the spot in its cell of 1024 m, and whether a physics body of the planet is there now
            var planet = Site.Planet;
            string Where(MyCubeGrid s)
            {
                var at = s.PositionComp.WorldAABB.Center;
                var inCell = at - planet.PositionLeftBottomCorner;
                var cell = new Vector3D(Math.Floor(inCell.X / 1024), Math.Floor(inCell.Y / 1024), Math.Floor(inCell.Z / 1024));
                var bodies = MyEntities.GetEntities().Count(e => e.GetType().Name == "MyVoxelPhysics" && e.PositionComp.WorldAABB.Contains(at) != ContainmentType.Disjoint);
                return "cell " + cell + " at " + F(inCell.X - cell.X * 1024) + "/" + F(inCell.Y - cell.Y * 1024) + "/" + F(inCell.Z - cell.Z * 1024) + ", " + bodies + " planet physics bodies there";
            }
            Check(through.Count == 0, through.Count + " of " + Ships + " ships went into the planet at 100 m/s and stayed there 20 s (" + fell.Count + " went in, " + back + " brought back): " +
                                      string.Join("; ", through.Take(8).Select(s => s.DisplayName + " " + F(over(s)) + " m under the ground at " + F(Motion.Speed(s)) + " m/s, " + Where(s) + ", " + Bodies.Physics(s))));
            Result(Ships + " ships thrown down at 100 m/s, all on the ground (" + ships.Count(s => s.MarkedForClose) + " broke up on it" +
                   (fell.Count > 0 ? ", " + fell.Count + " went into it and were brought back" : "") + ")");
        }

    }
}
