using System;
using System.Collections;
using System.Reflection;
using Sandbox.Game.Entities;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The space ball (<c>MySpaceBall</c>): a virtual mass the player sets in its terminal. A free ship with a space ball
    /// in a gravity generator's field: its virtual mass at nothing, it stays; set to its most, the ship falls along the
    /// field; the friction and the bounce the terminal sets stay as set.
    /// </summary>
    public sealed class SpaceBallCheck : RegressionCheck
    {
        public override string Name => Prefix + "space_ball";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -30));
            var station = Build("ball-field", Site.Origin, true,
                new Part("GravityGenerator/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var generator = Block<SpaceEngineers.Game.Entities.Blocks.MyGravityGenerator>(station);
            yield return Until(() => generator.IsWorking ? 1 : 0, v => v > 0, "the gravity generator working", 10, () => State(generator));
            var ship = Build("ball-ship", station.GridIntegerToWorld(Vector3I.Zero) + Site.Up * 15, false,
                new Part("SpaceBall/SpaceBallLarge", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var ball = Block<SpaceEngineers.Game.Entities.Blocks.MySpaceBall>(ship);
            var settings = (SpaceEngineers.Game.ModAPI.Ingame.IMySpaceBall)ball;
            settings.VirtualMass = 0;
            settings.Friction = 0.3f;
            settings.Restitution = 0.7f;
            yield return Until(() => ball.IsWorking ? 1 : 0, v => v > 0, "the space ball working", 10, () => State(ball));
            ship.Physics.LinearVelocity = Vector3.Zero;
            yield return Seconds(2);
            var still = Motion.Speed(ship);
            Check(still < 0.3, "the ship with its space ball's virtual mass at nothing moves at " + F(still) + " m/s in the field");
            settings.VirtualMass = ((Sandbox.Definitions.MySpaceBallDefinition)ball.BlockDefinition).MaxVirtualMass;
            yield return Until(() => Motion.Speed(ship), v => v > 1, "the ship with the space ball's virtual mass falling in the field", 10,
                () => State(ball) + ", virtual mass " + F(settings.VirtualMass) + ", gravity " + F(generator.GetGravityMultiplier(ship.PositionComp.GetPosition())));
            Check(Math.Abs(settings.Friction - 0.3f) < 0.01f && Math.Abs(settings.Restitution - 0.7f) < 0.01f,
                "the space ball's friction is " + F(settings.Friction) + " and its bounce " + F(settings.Restitution) + ", set 0.3 and 0.7");
            Result("virtual mass 0: " + F(still) + " m/s; " + F(settings.VirtualMass) + " kg: the ship falls in the field (" + F(Motion.Speed(ship)) + " m/s); friction and bounce as set");
        }
    }

    /// <summary>
    /// A safe zone's upkeep (<c>MySafeZoneComponent</c>): a working zone takes zone chips from its block's inventory - one
    /// as it comes up and one more each upkeep period (an hour and more in the game; the check ends the period early);
    /// with no chip left at the end of a period the zone goes down.
    /// </summary>
    public sealed class SafeZoneUpkeepCheck : RegressionCheck
    {
        public override string Name => Prefix + "safe_zone_upkeep";
        public override int TimeoutSeconds => 90;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -12));
            var grid = Build("safe-zone-upkeep", Site.Origin, true,
                new Part("SafeZoneBlock/SafeZoneBlock", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 1, 0).With(b => Motion.Charged(b)));
            var block = Block<SpaceEngineers.Game.Entities.Blocks.SafeZone.MySafeZoneBlock>(grid);
            var definition = (SpaceEngineers.Game.Definitions.SafeZone.MySafeZoneBlockDefinition)block.BlockDefinition;
            SkipUnless(definition.SafeZoneUpkeep > 0, "the safe zone block takes no upkeep in this game");
            var chips = (int)definition.SafeZoneUpkeep * 2;
            Put(Inventory(block), "Component/ZoneChip", chips);
            var zone = block.Components.Get<SpaceEngineers.Game.EntityComponents.Blocks.MySafeZoneComponent>();
            yield return Until(() => block.IsWorking ? 1 : 0, v => v > 0, "the safe zone block working", 10, () => State(block));
            var type = zone.GetType();
            var activation = type.GetField("m_safeZoneActivationTimeMS", Any);
            var upkeep = type.GetField("m_upkeepTime", Any);
            Check(activation != null && upkeep != null, "the safe zone component keeps no activation or upkeep time in this game");
            double Left() => Amount(Inventory(block), "Component/ZoneChip");

            Request(zone, "OnSafezoneCreateRemove", true);
            // the countdown ended early
            activation.SetValue(zone, (long)Sandbox.MySandboxGame.TotalGamePlayTimeInMilliseconds);
            yield return Until(() => zone.IsSafeZoneEnabled() ? 1 : 0, v => v > 0, "the safe zone up", 20, () => "chips " + F(Left()));
            var afterUp = Left();
            Check(afterUp == chips - definition.SafeZoneUpkeep, "coming up, the zone left " + F(afterUp) + " of " + chips + " zone chips");
            void EndPeriod() => upkeep.SetValue(zone, TimeSpan.FromMilliseconds(Sandbox.MySandboxGame.TotalGamePlayTimeInMilliseconds - 1000));

            EndPeriod();
            yield return Until(() => Left() < afterUp ? 1 : 0, v => v > 0, "the zone taking its upkeep at the end of the period", 15, () => "chips " + F(Left()));
            Check(zone.IsSafeZoneEnabled(), "the zone went down with its upkeep paid");
            var afterPeriod = Left();
            EndPeriod();
            yield return Until(() => zone.IsSafeZoneEnabled() ? 0 : 1, v => v > 0, "the zone down with no chip for the next period", 15, () => "chips " + F(Left()));
            Result("up: " + F(afterUp) + " of " + chips + " chips left; a period over: " + F(afterPeriod) + "; the next with none: the zone went down");
        }
    }
}
