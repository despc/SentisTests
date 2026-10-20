using System;
using System.Collections;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;
using GearMode = SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode;

namespace SentisTests.Regression.Checks
{
    /// <summary>A piston on a station, its head added, extends out and retracts back at the speed set in its terminal.</summary>
    public sealed class PistonCheck : RegressionCheck
    {
        public override string Name => Prefix + "piston";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(6, 4, 0));
            var grid = Build("piston", Site.Origin, true,
                new Part("ExtendedPistonBase/LargePistonBase", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var piston = Block<MyPistonBase>(grid);
            yield return AddHead(piston);
            var ingame = (Sandbox.ModAPI.Ingame.IMyPistonBase)piston;
            ((Sandbox.ModAPI.IMyTerminalBlock)piston).SetValueFloat("Velocity", 2f);
            yield return Until(() => ingame.CurrentPosition, v => v > 3, "the piston extending, m", 15, () => State(piston));
            var head = Vector3D.Distance(piston.TopGrid.PositionComp.GetPosition(), piston.PositionComp.GetPosition());
            ((Sandbox.ModAPI.IMyTerminalBlock)piston).SetValueFloat("Velocity", -2f);
            yield return Until(() => ingame.CurrentPosition, v => v < 0.5, "the piston retracting, m", 15);
            Result("piston extended past 3 m (head " + F(head) + " m off) and retracted");
        }
    }

    /// <summary>A hinge on a station, its head added, swings one way and back within its limits.</summary>
    public sealed class HingeCheck : RegressionCheck
    {
        public override string Name => Prefix + "hinge";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(6, 4, 0));
            var grid = Build("hinge", Site.Origin, true,
                new Part("MotorAdvancedStator/LargeHinge", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var hinge = Block<MyMotorAdvancedStator>(grid);
            yield return AddHead(hinge);
            var ingame = (Sandbox.ModAPI.Ingame.IMyMotorStator)hinge;
            ((Sandbox.ModAPI.IMyTerminalBlock)hinge).SetValueFloat("Velocity", 5f);
            yield return Until(() => MathHelper.ToDegrees(ingame.Angle), v => v > 30, "the hinge swinging, degrees", 15, () => State(hinge));
            ((Sandbox.ModAPI.IMyTerminalBlock)hinge).SetValueFloat("Velocity", -5f);
            yield return Until(() => MathHelper.ToDegrees(ingame.Angle), v => v < -30, "the hinge swinging back, degrees", 20);
            Result("hinge swung past 30 degrees both ways");
        }
    }

    /// <summary>
    /// A landing gear under a small ship over a floor on a planet locks onto the floor as the player locks it: the ship
    /// holds there; unlocked, it lets go.
    /// </summary>
    public sealed class LandingGearCheck : RegressionCheck
    {
        public override string Name => Prefix + "landing_gear";
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            var pad = Pad("gear-pad", Site.Origin, 3);
            var floor = pad.PositionComp.GetPosition() + Site.Up * 1.25;
            yield return Arrive(floor + Site.Up * 2 + Site.Forward * 8);
            // the gear's locking face is in its model: each way it can face is tried, the gear's cells just over the floor
            foreach (var face in new[] { Direction.Down, Direction.Forward, Direction.Backward, Direction.Up, Direction.Left, Direction.Right })
            {
                var up = Base6Directions.GetPerpendicular(face);
                var at = floor + Site.Up * (1.25 + 0.3);
                var ship = Build("gear", at, false, new[] { new Part("LandingGear/LargeBlockLandingGear", 0, 0, 0).Facing(face, up) });
                var gear = Block<SpaceEngineers.Game.Entities.Blocks.MyLandingGear>(ship);
                var ingame = (SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear)gear;
                var started = DateTime.UtcNow;
                while ((DateTime.UtcNow - started).TotalSeconds < 4 && ingame.LockMode == GearMode.Unlocked)
                    yield return null;
                if (ingame.LockMode == GearMode.Unlocked)
                {
                    ship.Close();
                    yield return null;
                    continue;
                }
                ingame.Lock();
                yield return Until(() => ingame.IsLocked ? 1 : 0, v => v > 0, "the gear locked", 5);
                var held = ship.PositionComp.GetPosition();
                yield return Seconds(2);
                var moved = Vector3D.Distance(held, ship.PositionComp.GetPosition());
                Check(moved < 0.1, "a locked gear let the ship go " + F(moved) + " m");
                ingame.Unlock();
                yield return Until(() => ingame.IsLocked ? 0 : 1, v => v > 0, "the gear unlocked", 5);
                Result("gear facing " + face + " locked on the floor (moved " + F(moved) + " m in 2 s) and unlocked");
                yield break;
            }
            Check(false, "the landing gear came ready to lock on the floor no way it faced");
        }
    }

    /// <summary>
    /// A gravity generator on a station in open space makes gravity in its field (9.81 m/s2 as set), none far outside it;
    /// an astronaut in it with the jetpack off falls.
    /// </summary>
    public sealed class GravityGeneratorCheck : RegressionCheck
    {
        public override string Name => Prefix + "gravity_generator";

        protected override IEnumerator Check()
        {
            // the astronaut well off the generator's block, with room under them to fall
            var inside = Site.At(20, 30, 20);
            yield return Arrive(inside);
            var grid = Build("gravity", Site.Origin, true,
                new Part("GravityGenerator/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var generator = Block<SpaceEngineers.Game.Entities.Blocks.MyGravityGenerator>(grid);
            yield return Until(() => MyGravityProviderSystem.CalculateArtificialGravityInPoint(inside).Length(), v => v > 9, "gravity in the field, m/s2", 10,
                () => State(generator));
            var g = MyGravityProviderSystem.CalculateArtificialGravityInPoint(inside);
            var outside = MyGravityProviderSystem.CalculateArtificialGravityInPoint(Site.At(0, 500, 0)).Length();
            Check(outside < 0.01, "gravity 500 m off the generator: " + F(outside));
            FakeClients.SetFree(Me, true);
            var fall = 0.0;
            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started).TotalSeconds < 5 && fall <= 3)
            {
                Keys(Vector3.Zero, jetpack: false);
                fall = Math.Max(fall, Vector3D.Dot(Body.Physics.LinearVelocity, Vector3D.Normalize(g)));
                yield return null;
            }
            var took = (DateTime.UtcNow - started).TotalSeconds;
            Check(fall > 3, "an astronaut in the field with the jetpack off falls at most " + F(fall) + " m/s in 5 s (" + Body.GetCurrentMovementState() + ", jetpack " + Body.JetpackComp?.TurnedOn + ")");
            Result("gravity " + F(g.Length()) + " m/s2 in the field, none outside; the astronaut fell at " + F(fall) + " m/s");
        }
    }
}
