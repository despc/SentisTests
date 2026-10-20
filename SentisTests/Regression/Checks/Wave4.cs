using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A small ship with a parachute (and canvas for it) falling through a planet's atmosphere: open, the parachute
    /// slows it from a fall of tens of metres a second to a few.
    /// </summary>
    public sealed class ParachuteCheck : RegressionCheck
    {
        public override string Name => Prefix + "parachute";
        public override SiteKind Kind => SiteKind.Planet;
        public override int TimeoutSeconds => 120;

        protected override IEnumerator Check()
        {
            Check(Site.Planet.HasAtmosphere, "the planet of the planet sites has no atmosphere");
            var ground = Site.Ground(Site.Origin);
            yield return Arrive(ground + Site.Up * 500 + Site.Forward * 20);
            FakeClients.SetFree(Me, true);
            var ship = Build("parachute", ground + Site.Up * 600, false, new[]
            {
                // the small battery is 3 x 2 x 3 cells: the parachute on top of it, in the middle
                new Part("BatteryBlock/SmallBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("Parachute/SmParachute", 1, 2, 1),
                new Part("CubeBlock/SmallBlockArmorBlock", 1, -1, 1),
            }, MyCubeSize.Small);
            var chute = ship.GetFatBlocks().FirstOrDefault(b => b.BlockDefinition.Id.SubtypeName == "SmParachute");
            Check(chute is SpaceEngineers.Game.ModAPI.Ingame.IMyParachute, "the parachute is not on the ship: " +
                                                                            string.Join(", ", ship.GetBlocks().Select(b => b.BlockDefinition.Id.SubtypeName + " " + (b.FatBlock?.GetType().Name ?? "no block"))));
            // the parachute holds just what one opening takes
            Put(Inventory(chute), "Component/Canvas", 1);
            var ingame = (SpaceEngineers.Game.ModAPI.Ingame.IMyParachute)chute;
            ingame.AutoDeploy = false;
            // falling free first
            var fall = 0.0;
            for (var f = Frames(4); f > 0; f--)
            {
                Keys(Vector3.Zero, jetpack: true, dampeners: true);
                fall = Math.Max(fall, Motion.Speed(ship));
                yield return null;
            }
            Check(fall > 20, "the ship fell at only " + F(fall) + " m/s in 4 s");
            ingame.OpenDoor();
            yield return Until(() => ingame.Status == Sandbox.ModAPI.Ingame.DoorStatus.Open ? 1 : 0, v => v > 0, "the parachute open", 10,
                () => ingame.Status + ", atmosphere " + F(ingame.Atmosphere) + ", canvas " + F(Amount(Inventory(chute), "Component/Canvas")));
            yield return Until(() => { Keys(Vector3.Zero, jetpack: true, dampeners: true); return Motion.Speed(ship); }, v => v < 12, "the ship slowed by the parachute, m/s", 20,
                () => ingame.Status + ", " + F(Vector3D.Dot(ship.PositionComp.GetPosition() - ground, Site.Up)) + " m over the ground");
            Result("falling at " + F(fall) + " m/s, the parachute opened and slowed the ship to " + F(Motion.Speed(ship)) + " m/s (canvas left " +
                   F(Amount(Inventory(chute), "Component/Canvas")) + ")");
        }
    }

    /// <summary>
    /// An oxygen farm in open space, turned to the sun, makes oxygen at most of what it can: its panels see the sun (the
    /// game looks at the sun from its points one at a time, so the share grows over some seconds).
    /// </summary>
    public sealed class OxygenFarmCheck : RegressionCheck
    {
        public override string Name => Prefix + "oxygen_farm";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var sun = (Vector3D)MySector.DirectionToSunNormalized;
            Check(sun.LengthSquared() > 0.5, "the world has no sun");
            // the farm's panels are on one face of its block: a farm each way it can be turned to the sun, 15 m apart
            var side = Vector3D.CalculatePerpendicularVector(sun);
            var farms = new[] { (sun, side), (-sun, side), (side, sun), (side, -sun) }.Select((o, i) =>
            {
                var grid = Build("oxygen-farm-" + i, Site.Origin + Site.Right * (i * 15), true, new[]
                {
                    new Part("OxygenFarm/LargeBlockOxygenFarm", 0, 0, 0),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 1).With(b => Motion.Charged(b)),
                }, forward: o.Item1, up: o.Item2);
                return (SpaceEngineers.Game.ModAPI.Ingame.IMyOxygenFarm)Block<SpaceEngineers.Game.Entities.Blocks.MyOxygenFarm>(grid);
            }).ToList();
            yield return Until(() => farms.Max(f => (double)f.GetOutput()), v => v > 0.5, "the farm turned to the sun making oxygen, share", 30,
                () => string.Join(", ", farms.Select(f => F(f.GetOutput() * 100) + " %")));
            Result("oxygen farm in the sun at " + F(farms.Max(f => (double)f.GetOutput()) * 100) + " % of what it can make (each way: " +
                   string.Join(", ", farms.Select(f => F(f.GetOutput() * 100) + " %")) + ")");
        }
    }
}

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The flames of thrusters at full burn damage the blocks in front of their nozzles (the world's thruster damage
    /// on): a ship of two thrusters back to back - their pushes cancel, it stays where it is - between two armor walls.
    /// </summary>
    public sealed class ThrusterFlameCheck : RegressionCheck
    {
        public override string Name => Prefix + "thruster_flame";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.ThrusterDamage, "thruster damage is off in this world");
            yield return Arrive(Site.At(10, 6, 0));
            var ship = Build("flame-ship", Site.Origin, false, new[]
            {
                new Part("Thrust/LargeBlockSmallThrust", 0, 0, 0).Facing(VRageMath.Base6Directions.Direction.Forward),
                new Part("Thrust/LargeBlockSmallThrust", 0, 0, 2).Facing(VRageMath.Base6Directions.Direction.Backward),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 1).With(b => Motion.Charged(b)),
            });
            // a wall of 3 x 3 armor blocks 4 m off each end of the pair, across the line of the flames
            System.Collections.Generic.List<Part> Wall()
            {
                var parts = new System.Collections.Generic.List<Part>();
                for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                    parts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, 0));
                return parts;
            }
            var ends = ship.GetBlocks().Where(b => b.FatBlock is Sandbox.Game.Entities.MyThrust).Select(b => b.FatBlock.PositionComp.GetPosition()).ToList();
            var line = Vector3D.Normalize(ends[1] - ends[0]);
            var walls = new[]
            {
                Build("flame-wall-a", ends[0] - line * 4.5, true, Wall(), forward: line, up: Vector3D.CalculatePerpendicularVector(line)),
                Build("flame-wall-b", ends[1] + line * 4.5, true, Wall(), forward: line, up: Vector3D.CalculatePerpendicularVector(line)),
            };
            Func<double> integrity = () => walls.Sum(w => w.MarkedForClose ? 0 : w.GetBlocks().Sum(b => (double)b.Integrity));
            var whole = integrity();
            foreach (var thruster in Blocks<Sandbox.Game.Entities.MyThrust>(ship))
                ((Sandbox.ModAPI.Ingame.IMyThrust)thruster).ThrustOverridePercentage = 1f;
            var from = ship.PositionComp.GetPosition();
            yield return Until(() => whole - integrity(), v => v > 0, "the flames damaging the walls", 30,
                () => string.Join("; ", Blocks<Sandbox.Game.Entities.MyThrust>(ship).Select(t => State(t) + ", thrust " + F(t.CurrentStrength))) + "; the ship moved " +
                      F(Vector3D.Distance(from, ship.PositionComp.GetPosition())) + " m");
            foreach (var thruster in Blocks<Sandbox.Game.Entities.MyThrust>(ship))
                ((Sandbox.ModAPI.Ingame.IMyThrust)thruster).ThrustOverridePercentage = 0f;
            Result("the thrusters' flames at full burn took " + F(whole - integrity()) + " integrity off the walls in front of them");
        }
    }
}
