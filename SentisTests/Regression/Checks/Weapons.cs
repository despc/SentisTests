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
            // at the game's low quality (its own under load) bullets check what they hit a third as often and fly through walls
            HoldHighQuality();
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

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A player's custom turret (a blueprint known to work on the server: a turret controller with its AI on, rotors for
    /// azimuth and elevation, a rocket launcher and a camera) and a station of another player 150 m off, out of its line,
    /// the player himself 2 km away (the turret shoots characters too): the controller takes the station for its target,
    /// turns onto it and the rockets damage it.
    /// </summary>
    public sealed class TurretControllerCheck : RegressionCheck
    {
        public override string Name => Prefix + "turret_controller";
        public override int TimeoutSeconds => 150;
        protected override bool NeedsDamage => true;

        internal const string ResourceName = "SentisTests.Resources.CustomTurret.xml";

        protected override IEnumerator Check()
        {
            SkipUnless(!Game.Mods.WeaponCore, "WeaponCore runs the weapons in this world: the custom turret's launcher is not the game's");
            // at the game's low quality (its own under load) bullets check what they hit a third as often and fly through walls
            HoldHighQuality();
            yield return Arrive(Site.At(-15, 10, -15));
            var enemy = -1;
            // the other player himself far out of the turret's reach: it shoots characters too
            yield return Join(Site.At(15, 10, 2000), i => enemy = i);
            var group = WorldApi.LoadAuthoredGroup(ResourceName, WorldApi.EntityPrefix + "custom-turret");
            // the blueprint moved to the site as it stands, the player's
            var base0 = group[0].PositionAndOrientation.Value.GetMatrix();
            var move = MatrixD.Invert(base0) * MatrixD.CreateWorld(Site.Origin, Site.Forward, Site.Up);
            foreach (var ob in group)
            {
                ob.PositionAndOrientation = new VRage.MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
                foreach (var block in ob.CubeBlocks)
                {
                    block.Owner = Identity;
                    block.BuiltBy = Identity;
                }
            }
            var grids = group.Select(WorldApi.SpawnGrid).ToList();
            foreach (var grid in grids) Track(grid);
            foreach (var grid in grids) WorldApi.ChargeBatteries(grid);
            var controller = grids.SelectMany(g => g.GetFatBlocks()).First(b => b.BlockDefinition.Id.TypeId.ToString().EndsWith("TurretControlBlock"));
            var launcher = grids.SelectMany(g => g.GetFatBlocks()).FirstOrDefault(b => b.BlockDefinition.Id.SubtypeName == "LargeMissileLauncher");
            Check(launcher != null, "the blueprint has no rocket launcher");
            Put(Inventory(launcher), "AmmoMagazine/Missile200mm", 4);
            var turret = (SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock)controller;
            yield return Until(() => controller.IsWorking && turret.AzimuthRotor != null && turret.ElevationRotor != null ? 1 : 0, v => v > 0,
                "the turret controller working with its rotors", 15, () => State(controller) + "; azimuth " + (turret.AzimuthRotor != null) + ", elevation " + (turret.ElevationRotor != null));

            // the other player's station 100 m off, off to the side of where the turret looks: a wall of 7 x 7 blocks,
            // as big as a ship - the controller shoots once it is within its angle deviation (5 degrees in the
            // blueprint), 9 m off at 100 m
            var wall = new System.Collections.Generic.List<Part> { new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, -1).With(b => Motion.Charged(b)) };
            for (var x = -3; x <= 3; x++)
            for (var y = -3; y <= 3; y++)
                wall.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, 0));
            var station = Build("custom-turret-target", Site.At(10, 7, 100), true, wall, ownedBy: FakeClients.PlayerOf(enemy).Identity.IdentityId);
            Func<double> integrity = () => station.MarkedForClose ? 0 : station.GetBlocks().Sum(b => (double)b.Integrity);
            var whole = integrity();
            yield return Until(() => turret.HasTarget ? 1 : 0, v => v > 0, "the turret controller taking the enemy for its target", 30,
                () => State(controller) + "; " + ((Sandbox.ModAPI.IMyTerminalBlock)controller).DetailedInfo.Trim().Replace("\n", " "));
            var target = turret.GetTargetedEntity();
            if (target.EntityId != station.EntityId)
            {
                var other = MyEntities.GetEntityById(target.EntityId) as MyCubeGrid;
                Check(false, "the turret controller targets " + target.Name + " (" + target.Relationship + ", " + F(Vector3D.Distance(target.Position, controller.PositionComp.GetPosition())) +
                             " m off, owned by " + (other == null ? "?" : string.Join(",", other.BigOwners.Select(o => Sandbox.Game.World.MySession.Static.Players.TryGetIdentity(o)?.DisplayName ?? o.ToString()))) +
                             ", " + (other?.BlocksCount ?? 0) + " blocks), not the enemy station");
            }
            yield return Until(() => turret.IsAimed ? 1 : 0, v => v > 0, "the turret aimed at the enemy", 30,
                () => "the target " + Describe(turret.GetTargetedEntity(), launcher) + "; azimuth " + F(MathHelper.ToDegrees(turret.AzimuthRotor.Angle)) + ", elevation " +
                      F(MathHelper.ToDegrees(turret.ElevationRotor.Angle)) + " (limits " + F(MathHelper.ToDegrees(turret.ElevationRotor.LowerLimitRad)) + ".." +
                      F(MathHelper.ToDegrees(turret.ElevationRotor.UpperLimitRad)) + ")");
            var tools = new System.Collections.Generic.List<Sandbox.ModAPI.Ingame.IMyFunctionalBlock>();
            turret.GetTools(tools);
            // where the rockets go: the nearest each came to the station, and how far off the line to it they set out
            var closest = new System.Collections.Generic.Dictionary<long, double>();
            var setOff = new System.Collections.Generic.Dictionary<long, double>();
            var stationAt = station.PositionComp.WorldAABB.Center;
            yield return Until(() =>
                {
                    foreach (var missile in MyEntities.GetEntities().Where(e => e.GetType().Name == "MyMissile" && !e.MarkedForClose))
                    {
                        var at = missile.PositionComp.GetPosition();
                        var d = Vector3D.Distance(at, stationAt);
                        closest[missile.EntityId] = closest.TryGetValue(missile.EntityId, out var was) ? Math.Min(was, d) : d;
                        if (!setOff.ContainsKey(missile.EntityId) && missile.Physics != null && missile.Physics.LinearVelocity.LengthSquared() > 1)
                            setOff[missile.EntityId] = Motion.Angle(missile.Physics.LinearVelocity, stationAt - at);
                    }
                    return whole - integrity();
                }, v => v > 0, "the rockets damaging the enemy station", 30,
                () => "the target now " + Describe(turret.GetTargetedEntity(), launcher) + ", the launcher " + F(Motion.Angle(launcher.WorldMatrix.Forward, stationAt - launcher.PositionComp.GetPosition())) +
                      " degrees off the station; rockets seen " + closest.Count + ", nearest to the station " + string.Join(", ", closest.Values.Select(F)) + " m, set out " +
                      string.Join(", ", setOff.Values.Select(F)) + " degrees off the line to it; " + State(launcher) + "; rockets left " + F(Amount(Inventory(launcher), "AmmoMagazine/Missile200mm")) + " of 4, the controller's tools: " +
                      string.Join(", ", tools.Select(x => x.CustomName)) + ", aimed " + turret.IsAimed + ", shooting " + ((Sandbox.Game.Weapons.MySmallMissileLauncher)launcher).IsShooting);
            Result("the custom turret took the other player's station for its target, aimed and hit it (" + F(whole - integrity()) + " damage)");
        }

        private static string Describe(Sandbox.ModAPI.Ingame.MyDetectedEntityInfo target, MyCubeBlock launcher) =>
            target.IsEmpty() ? "none" : target.Name + " " + target.EntityId + " at " + F(Vector3D.Distance(target.Position, launcher.PositionComp.GetPosition())) + " m, the launcher " +
                                        F(Motion.Angle(launcher.WorldMatrix.Forward, target.Position - launcher.PositionComp.GetPosition())) + " degrees off it";
    }
}
