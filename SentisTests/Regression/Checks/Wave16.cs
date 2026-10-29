using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>A rotor locked while still holds at the speed set; unlocked, it turns.</summary>
    public sealed class RotorLockCheck : RegressionCheck
    {
        public override string Name => Prefix + "rotor_lock";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -8));
            var grid = Build("rotor-lock", Site.Origin, true,
                new Part("MotorStator/LargeStator", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var rotor = Block<MyMotorStator>(grid);
            yield return AddHead(rotor);
            var ingame = (Sandbox.ModAPI.Ingame.IMyMotorStator)rotor;
            // the lock takes hold only on a rotor nearly still (under 5 degrees a second), as in the game: stopped, locked, then set going
            ingame.TargetVelocityRPM = 0;
            yield return Seconds(0.5);
            ingame.RotorLock = true;
            yield return Seconds(1);
            ingame.TargetVelocityRPM = 10;
            yield return Seconds(0.5);
            var angle = ingame.Angle;
            yield return Seconds(2);
            var locked = Math.Abs(ingame.Angle - angle);
            Check(locked < 0.01, "the locked rotor turned " + F(MathHelper.ToDegrees(locked)) + "°");
            ingame.RotorLock = false;
            yield return Until(() => Math.Abs(ingame.Angle - angle), v => v > 0.2, "the unlocked rotor turning", 10, () => State(rotor));
            Result("locked at 10 rpm the rotor held (" + F(MathHelper.ToDegrees(locked)) + "° in 2 s); unlocked it turned");
        }
    }

    /// <summary>A ship tool working on a player in its reach hurts them: the grinder cuts, the drill grinds.</summary>
    public sealed class ToolHurtsPlayerCheck : RegressionCheck
    {
        private readonly string _tool;

        public ToolHurtsPlayerCheck(string tool) { _tool = tool; }

        public override string Name => Prefix + (_tool.StartsWith("ShipGrinder") ? "grinder" : "drill") + "_hurts_player";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, 8));
            var at = Site.Origin;
            var rig = ToolRig("tool-player", _tool, at, Site.Forward);
            var tool = (MyCubeBlock)rig.GetCubeBlock(Vector3I.Zero).FatBlock;
            var sphere = WorldApi.SensorSphere(tool);
            Switch(tool, true);
            yield return Seconds(0.5);
            if (tool is MyShipDrill)
            {
                // a drill cuts (and hurts) in its cut-out sphere at the head, not the whole sensor
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                var drillBase = typeof(MyShipDrill).GetField("m_drillBase", flags)?.GetValue(tool);
                var cutOut = drillBase?.GetType().GetField("m_cutOut", flags)?.GetValue(drillBase);
                if (cutOut?.GetType().GetProperty("Sphere")?.GetValue(cutOut) is BoundingSphereD cut) sphere = cut;
            }
            StandAt(sphere.Center - Site.Up * 1.25, sphere.Center + Site.Forward * 5);
            Body.JetpackComp?.TurnOnJetpack(true);
            Body.StatComp.Health.Value = Body.StatComp.Health.MaxValue;
            var health = Body.StatComp.Health.Value;
            Switch(tool, true);
            yield return Until(() =>
            {
                StandAt(sphere.Center - Site.Up * 1.25, sphere.Center + Site.Forward * 5);
                return health - (Body.IsDead ? 0 : Body.StatComp.Health.Value);
            }, v => v > 0, "the player hurt by the " + tool.BlockDefinition.Id.SubtypeName, 15, () => State(tool));
            Switch(tool, false);
            Result(tool.BlockDefinition.Id.SubtypeName + " took " + F(health - (Body.IsDead ? 0 : Body.StatComp.Health.Value)) + " of the player's health" + (Body.IsDead ? " (dead)" : ""));
        }
    }

    /// <summary>A full hydrogen tank destroyed blows up: the armor round it is hurt.</summary>
    public sealed class HydrogenTankBlastCheck : RegressionCheck
    {
        public override string Name => Prefix + "hydrogen_tank_blast";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 40, -40));
            var tank = new Part("OxygenTank/LargeHydrogenTankSmall", 0, 0, 0).With(b => ((MyObjectBuilder_GasTank)b).FilledRatio = 1f);
            var taken = Cells(tank);
            var around = taken.SelectMany(c => Base6Directions.IntDirections.Select(d => c + (Vector3I)d)).Where(c => !taken.Contains(c)).Distinct().Take(6).ToList();
            var parts = new List<Part> { tank };
            parts.AddRange(around.Select(c => new Part("CubeBlock/LargeHeavyBlockArmorBlock", c.X, c.Y, c.Z)));
            var grid = Build("h2-blast", Site.Origin, true, parts);
            yield return Seconds(1);
            var gas = (Sandbox.ModAPI.Ingame.IMyGasTank)grid.GetCubeBlock(Vector3I.Zero).FatBlock;
            Check(gas.FilledRatio > 0.99, "the tank is at " + F(gas.FilledRatio * 100) + " %");
            Func<double> integrity = () => around.Sum(c => (double)(grid.GetCubeBlock(c)?.Integrity ?? 0));
            var whole = integrity();
            var block = grid.GetCubeBlock(Vector3I.Zero);
            block.DoDamage(block.MaxIntegrity * 10, MyDamageType.Bullet, true, null, 0);
            yield return Until(() => whole - integrity(), v => v > 0, "the armor round the tank hurt by its blast", 10);
            Result("the full hydrogen tank destroyed blew up: " + F(whole - integrity()) + " integrity taken from the heavy armor round it");
        }
    }

    /// <summary>A solar panel in the shadow of a wall between it and the sun gives little; in the open it gives its share.</summary>
    public sealed class SolarShadowCheck : RegressionCheck
    {
        public override string Name => Prefix + "solar_shadow";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var sun = (Vector3D)MySector.DirectionToSunNormalized;
            Check(sun.LengthSquared() > 0.5, "the world has no sun");
            // the face that takes the sun: both ways tried
            MyCubeGrid grid = null;
            SpaceEngineers.Game.Entities.Blocks.MySolarPanel panel = null;
            var open = 0.0;
            foreach (var facing in new[] { sun, -sun })
            {
                grid = Build("solar-shadow", Site.Origin, true, new[] { new Part("SolarPanel/LargeBlockSolarPanel", 0, 0, 0) }, forward: facing, up: Vector3D.CalculatePerpendicularVector(facing));
                panel = Block<SpaceEngineers.Game.Entities.Blocks.MySolarPanel>(grid);
                for (var f = Frames(10); f > 0 && open <= 0.01; f--)
                {
                    open = ((Sandbox.ModAPI.Ingame.IMyPowerProducer)panel).MaxOutput;
                    yield return null;
                }
                if (open > 0.01) break;
                grid.Close();
            }
            Check(open > 0.01, "no power from the panel turned to the sun either way");
            // a wall of armor 10 m sunwards, wide enough to shade the whole panel
            var wall = new List<Part>();
            for (var x = -3; x <= 3; x++)
            for (var y = -3; y <= 3; y++)
                wall.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, 0));
            Build("solar-shade", grid.PositionComp.WorldAABB.Center + sun * 10, true, wall, forward: sun, up: Vector3D.CalculatePerpendicularVector(sun));
            yield return Until(() => ((Sandbox.ModAPI.Ingame.IMyPowerProducer)panel).MaxOutput, v => v < open * 0.3, "the shaded panel's output dropping, MW", 60);
            Result("solar panel " + F(open * 1000) + " kW in the open, " + F(((Sandbox.ModAPI.Ingame.IMyPowerProducer)panel).MaxOutput * 1000) + " kW in a wall's shadow");
        }
    }

    /// <summary>The world's speed limit: a ship pushed at 300 m/s is held to the most its size may go.</summary>
    public sealed class SpeedLimitCheck : RegressionCheck
    {
        private readonly MyCubeSize _size;

        public SpeedLimitCheck(MyCubeSize size) { _size = size; }

        public override string Name => Prefix + "speed_limit_" + (_size == MyCubeSize.Large ? "large" : "small");

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 20, -20));
            var limit = _size == MyCubeSize.Large ? Sandbox.Game.Entities.Cube.MyGridPhysics.LargeShipMaxLinearVelocity() : Sandbox.Game.Entities.Cube.MyGridPhysics.SmallShipMaxLinearVelocity();
            var ship = Build("speed-limit", Site.Origin, false, Bodies.Plate(_size), _size);
            yield return null;
            ship.Physics.LinearVelocity = (Vector3)(Site.Forward * 300);
            var top = 0.0;
            for (var f = 0; f < 30; f++)
            {
                yield return null;
                if (f > 2) top = Math.Max(top, Motion.Speed(ship));
            }
            var scriptedAtEnd = Game.Mods.ScriptSpeedLimit(ship);
            ship.Close();
            if (scriptedAtEnd.HasValue && scriptedAtEnd.Value > limit) limit = (float)scriptedAtEnd.Value;
            // Havok's cap is a little over the world's figure (100 m/s comes out at 104.4)
            // a script of the world's holding each grid to a limit of its own (the modded V5's speed restrictions)
            var scripted = Game.Mods.ScriptSpeedLimit(ship);
            if (scripted.HasValue && scripted.Value > limit) limit = (float)scripted.Value;
            // Havok's cap over the world's figure: 104.4 at 100, 190 at 180 (the modded V5's small ships): 6 % at most
            var overshoot = 1.06;
            Check(top <= limit * overshoot + 0.2, "a " + _size + " ship pushed at 300 m/s went " + F(top) + " m/s, the limit is " + F(limit));
            Check(top >= limit - 5, "a " + _size + " ship pushed at 300 m/s went only " + F(top) + " m/s, the limit is " + F(limit));
            Result(_size + " ship pushed at 300 m/s held to " + F(top) + " m/s (the limit " + F(limit) + ")");
        }
    }
}
