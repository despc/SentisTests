using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A gatling turret with ammunition, on a powered station, fires at an enemy station 150 m off on its own: the
    /// enemy's blocks take damage and the turret's ammunition goes down.
    /// </summary>
    public sealed class TurretCheck : RegressionCheck
    {
        public override string Name => Prefix + "turret";
        public override int TimeoutSeconds => 150;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, 20));
            var grid = Build("turret", Site.Origin, true,
                new Part("LargeGatlingTurret/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 2).With(b => Motion.Charged(b)));
            var turret = Block<SpaceEngineers.Game.Entities.Weapons.MyLargeGatlingTurret>(grid);
            Put(Inventory(turret), "AmmoMagazine/NATO_25x184mm", 10);
            // the enemy: a station of the test identity, which is no friend of the player's
            var enemyAt = Site.At(0, 0, 150);
            var enemy = BuildFor(WorldApi.TestIdentityId(), "turret-target", enemyAt,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", -1, 0, 0));
            var health = enemy.GetBlocks().Sum(b => (double)b.Integrity);
            var ammo = Amount(Inventory(turret), "AmmoMagazine/NATO_25x184mm");
            yield return Until(() => health - (enemy.MarkedForClose ? 0 : enemy.GetBlocks().Sum(b => (double)b.Integrity)), v => v > 0,
                "damage to the enemy from the turret", 90,
                () => State(turret) + ", target " + (((Sandbox.ModAPI.Ingame.IMyLargeTurretBase)turret).HasTarget ? "locked" : "none") +
                      ", ammo " + Amount(Inventory(turret), "AmmoMagazine/NATO_25x184mm"));
            yield return Seconds(2);
            var spent = ammo - Amount(Inventory(turret), "AmmoMagazine/NATO_25x184mm");
            Result("turret fired on its own: the enemy lost " + F(health - (enemy.MarkedForClose ? 0 : enemy.GetBlocks().Sum(b => (double)b.Integrity))) +
                   " integrity, " + spent + " magazines used");
        }

    }

    /// <summary>A warhead, armed and detonated as a player detonates it, blows up and takes the blocks next to it with it.</summary>
    public sealed class WarheadCheck : RegressionCheck
    {
        public override string Name => Prefix + "warhead";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 40, -40));
            var grid = Build("warhead", Site.Origin, true,
                new Part("Warhead/LargeWarhead", 0, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 2, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 1, 0));
            var warhead = Block<MyWarhead>(grid);
            var blocks = grid.BlocksCount;
            ((Sandbox.ModAPI.IMyTerminalBlock)warhead).SetValueBool("Safety", true);
            Check(((Sandbox.ModAPI.Ingame.IMyWarhead)warhead).IsArmed, "the warhead did not arm");
            Request(warhead, "DetonateRequest");
            yield return Until(() => warhead.MarkedForClose || warhead.Closed ? 1 : 0, v => v > 0, "the warhead blown up", 10);
            yield return Seconds(1);
            var left = grid.MarkedForClose ? 0 : grid.BlocksCount;
            Check(left < blocks - 1, "the blast took nothing with the warhead: " + left + " of " + blocks + " blocks left");
            Result("warhead blew up, " + (blocks - left) + " of " + blocks + " blocks gone");
        }
    }
}
