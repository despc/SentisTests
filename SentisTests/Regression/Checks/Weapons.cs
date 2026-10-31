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

    /// <summary>
    /// A player's warship with custom turrets (a prod blueprint, 30.10.2026): a large ship of 437 blocks with 8 turrets
    /// of its own make, each a hinge, a rotor and a small head of 6 railguns turned by a turret controller on the head
    /// (one head has no guns). Enemy stations come round the ship, 360-450 m off: each controller with railguns takes
    /// one for its target, turns its head on it, its railguns fire (their rounds go down) and the stations take damage;
    /// in a fight of 45 s after that each fires again, at the next block once the one it aimed at is gone.
    /// </summary>
    public sealed class RailTurretShipCheck : RegressionCheck
    {
        public override string Name => Prefix + "rail_turret_ship";
        public override int TimeoutSeconds => 300;
        protected override bool NeedsDamage => true;

        internal const string ResourceName = "SentisTests.Resources.RailTurretShip.xml";

        protected override IEnumerator Check()
        {
            SkipUnless(typeof(RailTurretShipCheck).Assembly.GetManifestResourceInfo(ResourceName) != null,
                "optional local RailTurretShip.xml fixture is not embedded");
            SkipUnless(!Game.Mods.WeaponCore, "WeaponCore runs the weapons in this world: the railguns are not the game's");
            HoldHighQuality();
            yield return Arrive(Site.At(-60, 0, -60));
            var enemy = -1;
            yield return Join(Site.At(15, 10, 5000), i => enemy = i);
            var group = WorldApi.LoadAuthoredGroup(ResourceName, WorldApi.EntityPrefix + "rail-turret-ship");
            var hull = group.OrderByDescending(g => g.CubeBlocks.Count).First();
            var move = MatrixD.Invert(hull.PositionAndOrientation.Value.GetMatrix()) * MatrixD.CreateWorld(Site.Origin, Site.Forward, Site.Up);
            foreach (var ob in group)
            {
                ob.PositionAndOrientation = new VRage.MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
                ob.LinearVelocity = default;
                ob.AngularVelocity = default;
                foreach (var block in ob.CubeBlocks)
                {
                    block.Owner = Identity;
                    block.BuiltBy = Identity;
                }
            }
            var grids = group.Select(WorldApi.SpawnGrid).ToList();
            foreach (var grid in grids) Track(grid);
            foreach (var grid in grids) WorldApi.ChargeBatteries(grid);
            var blocks = grids.SelectMany(g => g.GetFatBlocks()).ToList();
            var railguns = blocks.Where(b => b.BlockDefinition.Id.SubtypeName.EndsWith("Railgun")).ToList();
            var ammo = railguns.ToDictionary(b => b, b => b.BlockDefinition.Id.SubtypeName == "LargeRailgun" ? "AmmoMagazine/LargeRailgunAmmo" : "AmmoMagazine/SmallRailgunAmmo");
            foreach (var gun in railguns) Put(Inventory(gun), ammo[gun], 10);
            // the blueprint's railguns hold rounds of their own: shots are counted from what each holds now
            var loaded = railguns.ToDictionary(b => b, b => Amount(Inventory(b), ammo[b]));
            var controllers = blocks.Where(b => b.BlockDefinition.Id.TypeId.ToString().EndsWith("TurretControlBlock"))
                .Select(b => (SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock)b).ToList();
            Check(controllers.Count > 0, "the blueprint has no turret controllers");
            // a controller finds its rotors and tools by their ids once the grids are all in the world, a frame or so later
            yield return WaitForTicks(60);
            var tools = controllers.ToDictionary(c => c, c =>
            {
                var list = new System.Collections.Generic.List<Sandbox.ModAPI.Ingame.IMyFunctionalBlock>();
                c.GetTools(list);
                return list;
            });
            // the controllers that turn railguns: the ones that should shoot
            var armed = controllers.Where(c => c.AzimuthRotor != null && c.ElevationRotor != null && tools[c].Any(t => railguns.Contains((MyCubeBlock)t))).ToList();
            string Who(SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock c) => c.CustomName + " on " + ((MyCubeBlock)c).CubeGrid.DisplayName;
            Note(grids.Count + " grids, " + blocks.Count + " functional blocks, " + railguns.Count + " railguns, " + controllers.Count + " turret controllers: " + string.Join("; ", controllers.Select(c =>
                Who(c) + " (" + tools[c].Count + " tools, " + tools[c].Count(t => railguns.Contains((MyCubeBlock)t)) + " railguns, azimuth " + (c.AzimuthRotor != null) + ", elevation " + (c.ElevationRotor != null) + ")")));
            Check(armed.Count > 0, "no turret controller with rotors turns a railgun");
            foreach (var c in armed)
            {
                var az = (Sandbox.ModAPI.IMyMotorStator)c.AzimuthRotor;
                var el = (Sandbox.ModAPI.IMyMotorStator)c.ElevationRotor;
                // the elevation a direction asks: its angle over the plane square to the azimuth rotor's axis
                Func<Vector3D, double> over = d => 90 - Motion.Angle(az.WorldMatrix.Up, d);
                Note(Who(c) + ": azimuth rotor " + az.BlockDefinition.SubtypeName + " limits " + F(MathHelper.ToDegrees(az.LowerLimitRad)) + ".." + F(MathHelper.ToDegrees(az.UpperLimitRad)) +
                     ", its axis " + F(Motion.Angle(az.WorldMatrix.Up, Site.Up)) + " degrees off the site's up; elevation " + el.BlockDefinition.SubtypeName + " limits " +
                     F(MathHelper.ToDegrees(el.LowerLimitRad)) + ".." + F(MathHelper.ToDegrees(el.UpperLimitRad)) + "; the guns look " + F(over(((MyCubeBlock)tools[c][0]).WorldMatrix.Forward)) +
                     " degrees over the azimuth plane; up ahead 40 degrees asks " + F(over(Site.At(0, 290, 345) - az.PositionComp.GetPosition())) + ", up to the right " + F(over(Site.At(345, 290, 0) - az.PositionComp.GetPosition())) +
                     "; the head " + F(Vector3D.Dot(az.PositionComp.GetPosition() - Site.Origin, Site.Up)) + " m up, " + F(Vector3D.Dot(az.PositionComp.GetPosition() - Site.Origin, Site.Right)) + " m right, " +
                     F(Vector3D.Dot(az.PositionComp.GetPosition() - Site.Origin, Site.Forward)) + " m ahead");
            }
            yield return Until(() => armed.Count(c => ((MyCubeBlock)c).IsWorking), v => v == armed.Count, "the armed turret controllers working", 20,
                () => string.Join("; ", armed.Where(c => !((MyCubeBlock)c).IsWorking).Select(c => Who(c) + ": " + State((MyCubeBlock)c))));

            // The turrets stand with their guns straight along the azimuth rotor's axis (their hinges at -90 degrees,
            // a limit): the azimuth that brings them to a target is not defined there, and the controller left top
            // ones so for 25-50 s with a target in reach, until a knock moved them (the game's own way with this
            // blueprint, 31.10.2026). Their hinges are turned 5 degrees off first, as a player's turret is after its
            // first fight.
            var upright = armed.Where(c => Motion.Angle(((MyCubeBlock)tools[c][0]).WorldMatrix.Forward, c.AzimuthRotor.WorldMatrix.Up) < 2 ||
                                           Motion.Angle(((MyCubeBlock)tools[c][0]).WorldMatrix.Forward, c.AzimuthRotor.WorldMatrix.Up) > 178).ToList();
            // (the controllers hold their rotors even with no target: off meanwhile)
            foreach (var c in upright) ((Sandbox.ModAPI.IMyFunctionalBlock)c).Enabled = false;
            foreach (var c in upright)
            {
                var hinge = (Sandbox.ModAPI.IMyMotorStator)c.ElevationRotor;
                var toward = Math.Abs(hinge.Angle - hinge.LowerLimitRad) < Math.Abs(hinge.Angle - hinge.UpperLimitRad) ? 1 : -1;
                hinge.TargetVelocityRPM = toward * 2;
            }
            yield return Until(() => upright.Count(c => Math.Min(Motion.Angle(((MyCubeBlock)tools[c][0]).WorldMatrix.Forward, c.AzimuthRotor.WorldMatrix.Up),
                                                                  180 - Motion.Angle(((MyCubeBlock)tools[c][0]).WorldMatrix.Forward, c.AzimuthRotor.WorldMatrix.Up)) >= 5),
                v => v == upright.Count, "the upright turrets' hinges turned 5 degrees off", 15,
                () => string.Join("; ", upright.Select(c => Who(c) + " hinge at " + F(MathHelper.ToDegrees(c.ElevationRotor.Angle)))));
            foreach (var c in upright)
            {
                ((Sandbox.ModAPI.IMyMotorStator)c.ElevationRotor).TargetVelocityRPM = 0;
                ((Sandbox.ModAPI.IMyFunctionalBlock)c).Enabled = true;
            }
            Note(upright.Count + " upright turrets turned off their axis: " + string.Join("; ", upright.Select(c => Who(c) + " hinge at " + F(MathHelper.ToDegrees(c.ElevationRotor.Angle)))));

            // the enemy's stations round the ship, 40 degrees up ahead, behind and to the sides, each 450 m off, for the
            // turrets on the ship's sides (their rotors' axes level, their hinges from -90 to 5 degrees); and one 80
            // degrees up, 360 m off, for the 4 turrets on top: their hinges are held from -90 to -70 degrees, the guns
            // look 70 to 90 degrees up and no more (one at 70 degrees was out of reach; straight up the azimuth means
            // nothing). That one is straight over the ship's middle line, where the side turrets' azimuth axes (level,
            // across the ship) reach it too: off to a side it was nearest to them and out of their hinges' reach, and
            // one strained at it 50 s before it took another
            // Each a wall of 9 x 9 heavy armor blocks before 25 batteries: the blueprint's controllers aim at power
            // systems only and are left with no target once those are gone (one battery went with the first volley)
            var wall = new System.Collections.Generic.List<Part>();
            for (var x = -4; x <= 4; x++)
            for (var y = -4; y <= 4; y++)
            {
                wall.Add(new Part("CubeBlock/LargeHeavyBlockArmorBlock", x, y, 0));
                if (Math.Abs(x) <= 2 && Math.Abs(y) <= 2) wall.Add(new Part("BatteryBlock/LargeBlockBatteryBlock", x, y, 1).With(b => Motion.Charged(b)));
            }
            var places = new[] { Site.At(0, 290, 345), Site.At(0, 290, -345), Site.At(345, 290, 0), Site.At(-345, 290, 0), Site.At(0, 400, 70) };
            var stations = places.Select((at, i) => BuildFacing("rail-turret-target-" + i, at, wall, enemy)).ToList();
            var stationIds = new System.Collections.Generic.HashSet<long>(stations.Select(x => x.EntityId));
            Func<double> integrity = () => stations.Sum(station => station.MarkedForClose ? 0 : station.GetBlocks().Sum(b => (double)b.Integrity));
            var whole = integrity();
            var targeted = new System.Collections.Generic.HashSet<object>();
            var aimed = new System.Collections.Generic.HashSet<object>();
            var fired = new System.Collections.Generic.HashSet<MyCubeBlock>();
            var shots = 0.0;
            var seen = new System.Collections.Generic.Dictionary<object, string>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // which station a controller holds, and where it is from its head
            string Target(SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock c)
            {
                var t = c.GetTargetedEntity();
                var i = stations.FindIndex(x => x.EntityId == t.EntityId);
                var from = c.AzimuthRotor.GetPosition();
                return (i < 0 ? t.Name : "station " + i) + " (" + Math.Round(Vector3D.Distance(t.Position, from) / 10) * 10 + " m, " + Math.Round(90 - Motion.Angle(c.AzimuthRotor.WorldMatrix.Up, t.Position - from)) + " degrees over the azimuth plane)";
            }
            Func<double> look = () =>
            {
                foreach (var c in armed)
                {
                    if (c.HasTarget && stationIds.Contains(c.GetTargetedEntity().EntityId)) targeted.Add(c);
                    if (c.IsAimed) aimed.Add(c);
                    var gunsShot = tools[c].Sum(t => railguns.Contains((MyCubeBlock)t) ? loaded[(MyCubeBlock)t] - Amount(Inventory((MyCubeBlock)t), ammo[(MyCubeBlock)t]) : 0);
                    var now = (c.HasTarget ? "target " + Target(c) : "no target") + (c.IsAimed ? ", aimed" : "") + ", " + F(gunsShot) + " shots";
                    if (!seen.TryGetValue(c, out var was) || was != now)
                    {
                        seen[c] = now;
                        Note(F(clock.Elapsed.TotalSeconds) + " s: " + Who(c) + ": " + now + "; azimuth " + F(MathHelper.ToDegrees(c.AzimuthRotor.Angle)) + ", elevation " +
                             F(MathHelper.ToDegrees(c.ElevationRotor.Angle)) + "; the stations " + F(integrity()) + " of " + F(whole) + " integrity, their batteries " +
                             stations.Sum(x => x.GetFatBlocks().Count(b => b is Sandbox.Game.Entities.MyBatteryBlock)));
                    }
                }
                shots = 0;
                foreach (var gun in railguns)
                {
                    var used = loaded[gun] - Amount(Inventory(gun), ammo[gun]);
                    if (used > 0) fired.Add(gun);
                    shots += used;
                }
                return armed.Count(c => tools[c].Any(t => fired.Contains((MyCubeBlock)t)));
            };
            Func<string> state = () => "targeted " + targeted.Count + ", aimed " + aimed.Count + " of " + armed.Count + " armed controllers, " + fired.Count + " railguns fired (" + F(shots) + " shots), damage " +
                                       F(whole - integrity()) + "; not firing: " + string.Join("; ", armed.Where(c => !tools[c].Any(t => fired.Contains((MyCubeBlock)t))).Select(c =>
                                           Who(c) + " target " + (c.HasTarget ? c.GetTargetedEntity().Name : "none") + ", aimed " + c.IsAimed + ", azimuth " + F(MathHelper.ToDegrees(c.AzimuthRotor.Angle)) +
                                           ", elevation " + F(MathHelper.ToDegrees(c.ElevationRotor.Angle)) + ", " + ((Sandbox.ModAPI.IMyTerminalBlock)c).DetailedInfo.Trim().Replace("\n", " ")));
            yield return Until(look, v => v == armed.Count, "every armed turret controller's railguns firing at the enemy stations", 30, state);
            var first = clock.Elapsed.TotalSeconds;
            // then a fight of 45 s: the batteries a controller aims at go one by one, and it takes another and fires again
            Func<SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock, double> shotsOf = c =>
                tools[c].Where(t => railguns.Contains((MyCubeBlock)t)).Sum(t => loaded[(MyCubeBlock)t] - Amount(Inventory((MyCubeBlock)t), ammo[(MyCubeBlock)t]));
            // (45 s of the game's time, 2700 frames: a railgun charges in it, and the full regression slows the game down)
            var fight = System.Diagnostics.Stopwatch.StartNew();
            for (var frame = 0; frame < 2700 && fight.Elapsed.TotalSeconds < 150; frame++)
            {
                look();
                yield return null;
            }
            var speed = 45 / fight.Elapsed.TotalSeconds;
            var again = armed.Where(c => shotsOf(c) > tools[c].Count(t => railguns.Contains((MyCubeBlock)t))).ToList();
            Check(again.Count == armed.Count, "turret controllers that fired one volley and no more in 45 s of the game (the game at " + F(speed * 100) + " % speed): " + string.Join("; ", armed.Except(again).Select(c => Who(c) + " " + F(shotsOf(c)) + " shots")) + "; " + state());
            Check(whole - integrity() > 0, "the railguns fired but the enemy stations took no damage: " + state());
            Result("all " + armed.Count + " armed turret controllers fired within " + F(first) + " s and kept firing: " + F(shots) + " shots in " + F(clock.Elapsed.TotalSeconds) + " s, the fight at " + F(Math.Min(1, speed) * 100) + " % of the game's speed (" +
                   string.Join(", ", armed.Select(c => F(shotsOf(c)))) + " by controller), " + F(whole - integrity()) + " damage, the stations' batteries " +
                   stations.Sum(x => x.GetFatBlocks().Count(b => b is Sandbox.Game.Entities.MyBatteryBlock)) + " of " + stations.Count * 25 + " left" + Searches());
        }

        /// <summary>SentisOptimisations' turret controllers' target searches (TurretControlTargetSelect), when it has them.</summary>
        private static string Searches()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Optimizer.Optimizations.TurretControlTargetSelect", false)).FirstOrDefault(t => t != null);
            if (type == null) return "; the controllers search for a target every frame (the game's way)";
            return "; target searches since the start " + type.GetField("Started")?.GetValue(null) + ", skipped " + type.GetField("Skipped")?.GetValue(null) + " (SentisOptimisations: idle period " + type.GetField("Period")?.GetRawConstantValue() + " frames)";
        }

        /// <summary>The enemy's station built with its armor facing the ship (its battery behind, +Z being backward).</summary>
        private MyCubeGrid BuildFacing(string name, Vector3D at, System.Collections.Generic.List<Part> parts, int enemy)
        {
            var toShip = Vector3D.Normalize(Site.Origin - at);
            return Build(name, at, true, parts, forward: toShip, up: Vector3D.Normalize(Vector3D.Cross(Vector3D.Cross(toShip, Site.Up), toShip)),
                ownedBy: FakeClients.PlayerOf(enemy).Identity.IdentityId);
        }
    }
}
