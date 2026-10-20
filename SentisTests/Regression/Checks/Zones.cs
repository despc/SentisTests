using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using SentisTests.Game;
using VRage;
using VRage.Game;
using Sandbox.ModAPI.Interfaces;
using VRage.Game.ObjectBuilders.Components;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A check inside a safe zone made as an admin makes one (no block, no countdown): its rules - what may be done
    /// in it, who may be in it - are set straight away.
    /// </summary>
    public abstract class ZoneCheck : HandCheck
    {
        protected const float Radius = 50;

        protected MySafeZone Zone(MySafeZoneAction allowed, MySafeZoneAccess access, long[] players, string name, Vector3D? at = null)
        {
            var ob = new MyObjectBuilder_SafeZone
            {
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at ?? Site.Origin, Site.Forward, Site.Up)),
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                Shape = MySafeZoneShape.Sphere,
                Radius = Radius,
                Enabled = true,
                AllowedActions = allowed,
                AccessTypePlayers = access,
                AccessTypeFactions = access,
                AccessTypeGrids = access,
                AccessTypeFloatingObjects = access,
                Players = players ?? new long[0],
                DisplayName = WorldApi.EntityPrefix + name,
                Name = WorldApi.EntityPrefix + name,
            };
            var zone = MyEntities.CreateFromObjectBuilderAndAdd(ob, false) as MySafeZone;
            Check(zone != null, "the safe zone was not made");
            Track(zone);
            return zone;
        }

        /// <summary>The trigger held for a while, pulled again and again as a player keeps clicking.</summary>
        protected IEnumerator TryShoot(double seconds)
        {
            for (var i = Frames(seconds); i > 0; i--)
            {
                if (i % 10 == 0) Trigger();
                yield return null;
            }
            Release();
        }

        /// <summary>The integrity of a grid's blocks, all of them.</summary>
        protected static double Integrity(MyCubeGrid grid) => grid.MarkedForClose ? 0 : grid.GetBlocks().Sum(b => (double)b.Integrity);
    }

    /// <summary>
    /// A safe zone that allows nothing: in it the player's hand grinder, welder and rifle do nothing, nor do a ship
    /// grinder and welder, a block is not placed, a warhead hurts nothing, a ship rammed into a wall does no damage, and
    /// a turret outside does nothing to an enemy inside. Each of these does its work outside a zone (the other checks).
    /// </summary>
    public sealed class SafeZoneForbidsCheck : ZoneCheck
    {
        public override string Name => Prefix + "safe_zone_forbids";
        public override int TimeoutSeconds => 240;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -6));
            Zone(0, MySafeZoneAccess.Blacklist, null, "zone-forbids");
            yield return Seconds(1);
            Check(!Sandbox.Game.Entities.MySessionComponentSafeZones.IsActionAllowed(Body, MySafeZoneAction.Grinding), "the player is not in the safe zone, or it allows grinding");
            var done = new List<string>();

            // the hand tools and the rifle, at a block in the middle
            var target = Build("zone-target", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            var block = target.GetCubeBlock(Vector3I.Zero);
            yield return Equip("AngleGrinderItem");
            yield return AimAt(block);
            var whole = block.Integrity;
            yield return TryShoot(2);
            Check(block.Integrity >= whole, "the hand grinder ground in the safe zone: " + F(whole) + " -> " + F(block.Integrity));
            done.Add("hand grinder");

            var frame = Build("zone-frame", Site.At(0, 0, 10), true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0).At(0.05f));
            var frameBlock = frame.GetCubeBlock(Vector3I.Zero);
            Put(Inventory(Body), "Component/SteelPlate", 30);
            yield return Equip("WelderItem");
            yield return AimAt(frameBlock);
            var built = frameBlock.Integrity;
            yield return TryShoot(2);
            Check(frameBlock.Integrity <= built, "the hand welder welded in the safe zone: " + F(built) + " -> " + F(frameBlock.Integrity));
            done.Add("hand welder");

            Put(Inventory(Body), "AmmoMagazine/AutomaticRifleGun_Mag_20rd", 2);
            yield return Equip("AutomaticRifleItem");
            yield return AimAt(block);
            Body.SetHeadLocalXAngle(0);
            whole = block.Integrity;
            yield return TryShoot(2);
            yield return Seconds(0.5);
            Check(block.Integrity >= whole, "the rifle damaged a block in the safe zone: " + F(whole) + " -> " + F(block.Integrity));
            done.Add("rifle");

            // ship tools
            var grindRig = ToolRig("zone-grinder", "ShipGrinder/LargeShipGrinder", Site.Origin - Site.Right * 1.0, Site.Right);
            Switch(Block<MyShipGrinder>(grindRig), true);
            whole = block.Integrity;
            yield return Seconds(4);
            Check(block.Integrity >= whole, "a ship grinder ground in the safe zone: " + F(whole) + " -> " + F(block.Integrity));
            done.Add("ship grinder");
            var weldRig = ToolRig("zone-welder", "ShipWelder/LargeShipWelder", Site.At(0, 0, 10) - Site.Right * 1.5, Site.Right);
            var shipWelder = Block<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(weldRig);
            Put(Inventory(shipWelder), "Component/SteelPlate", 30);
            Switch(shipWelder, true);
            built = frameBlock.Integrity;
            yield return Seconds(4);
            Check(frameBlock.Integrity <= built, "a ship welder welded in the safe zone: " + F(built) + " -> " + F(frameBlock.Integrity));
            done.Add("ship welder");

            // building
            Put(Inventory(Body), "Component/SteelPlate", 30);
            var definition = Definition("CubeBlock/LargeBlockArmorBlock");
            var cell = new Vector3I(0, 1, 0);
            var location = new MyCubeGrid.MyBlockLocation(definition.Id, cell, cell, cell, Quaternion.Identity, 0, Identity);
            var request = typeof(MyCubeGrid).GetMethod("BuildBlocksRequest", Placing.Any);
            AsMe(() => request.Invoke(target, new object[] { Placing.Visuals, new HashSet<MyCubeGrid.MyBlockLocation> { location }, Body.EntityId, false, Identity }));
            yield return Seconds(1);
            Check(target.GetCubeBlock(cell) == null, "a block was placed in the safe zone");
            done.Add("placing a block");

            // a warhead between armor
            var bomb = Build("zone-warhead", Site.At(-20, 0, 0), true,
                new Part("Warhead/LargeWarhead", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", -1, 0, 0));
            var warhead = Block<MyWarhead>(bomb);
            var around = Integrity(bomb) - warhead.SlimBlock.Integrity;
            ((Sandbox.ModAPI.IMyTerminalBlock)warhead).SetValueBool("Safety", true);
            Request(warhead, "DetonateRequest");
            yield return Seconds(2);
            var aroundNow = bomb.MarkedForClose ? 0 : bomb.GetBlocks().Where(b => b.FatBlock != warhead).Sum(b => (double)b.Integrity);
            Check(aroundNow >= around, "a warhead hurt the blocks next to it in the safe zone: " + F(around) + " -> " + F(aroundNow));
            done.Add("warhead");

            // ramming
            var wall = new List<Part>();
            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
                wall.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, 0));
            var station = Build("zone-wall", Site.At(25, 0, 20), true, wall);
            var ram = Build("zone-ram", Site.At(25, 0, -10), false,
                new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 1),
                new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 2));
            var before = Integrity(station) + Integrity(ram);
            yield return null;
            ram.Physics.LinearVelocity = (Vector3)(Site.Forward * 40);
            yield return Seconds(3);
            var after = Integrity(station) + Integrity(ram);
            Check(after >= before, "ramming did damage in the safe zone: " + F(before) + " -> " + F(after));
            done.Add("ramming");

            // a turret outside at an enemy inside
            var turretGrid = Build("zone-turret", Site.At(0, 0, -(Radius + 70)), true,
                new Part("LargeGatlingTurret/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 2).With(b => Motion.Charged(b)));
            var turret = Block<SpaceEngineers.Game.Entities.Weapons.MyLargeGatlingTurret>(turretGrid);
            Put(Inventory(turret), "AmmoMagazine/NATO_25x184mm", 5);
            var enemy = BuildFor(WorldApi.TestIdentityId(), "zone-enemy", Site.At(0, 0, -(Radius - 15)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", -1, 0, 0));
            var enemyWhole = Integrity(enemy);
            yield return Seconds(12);
            Check(Integrity(enemy) >= enemyWhole, "a turret outside the safe zone damaged an enemy in it: " + F(enemyWhole) + " -> " + F(Integrity(enemy)));
            done.Add("a turret from outside");
            Result("in the safe zone nothing happened from: " + string.Join(", ", done));
        }
    }

    /// <summary>A safe zone that allows everything: in it the hand grinder, the welder, the rifle and a ship grinder work as anywhere.</summary>
    public sealed class SafeZoneAllowsCheck : ZoneCheck
    {
        public override string Name => Prefix + "safe_zone_allows";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -6));
            Zone(MySafeZoneAction.All, MySafeZoneAccess.Blacklist, null, "zone-allows");
            yield return Seconds(1);
            var target = Build("zone-target", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            var block = target.GetCubeBlock(Vector3I.Zero);
            var done = new List<string>();

            yield return Equip("AngleGrinderItem");
            yield return AimAt(block);
            var whole = block.Integrity;
            yield return Pull();
            yield return Until(() => whole - block.Integrity, v => v > 0, "the hand grinder grinding in the zone", 10, () => GunState());
            Release();
            done.Add("hand grinder");

            Put(Inventory(Body), "Component/SteelPlate", 30);
            yield return Equip("WelderItem");
            yield return AimAt(block);
            var ground = block.Integrity;
            yield return Pull();
            yield return Until(() => block.Integrity - ground, v => v > 0, "the hand welder welding in the zone", 10, () => GunState());
            Release();
            done.Add("hand welder");

            Put(Inventory(Body), "AmmoMagazine/AutomaticRifleGun_Mag_20rd", 2);
            yield return Equip("AutomaticRifleItem");
            yield return AimAt(block);
            Body.SetHeadLocalXAngle(0);
            whole = block.Integrity;
            yield return Pull();
            yield return Until(() => whole - block.Integrity, v => v > 0, "the rifle damaging the block in the zone", 10, () => GunState());
            Release();
            done.Add("rifle");

            var rig = ToolRig("zone-grinder", "ShipGrinder/LargeShipGrinder", Site.Origin - Site.Right * 1.0, Site.Right);
            Switch(Block<MyShipGrinder>(rig), true);
            whole = block.Integrity;
            yield return Until(() => whole - (target.GetCubeBlock(Vector3I.Zero)?.Integrity ?? 0), v => v > 0, "a ship grinder grinding in the zone", 15);
            done.Add("ship grinder");
            Result("in a safe zone that allows it all worked: " + string.Join(", ", done));
        }
    }

    /// <summary>
    /// Who may be in a safe zone: in one whose white list has someone else, the player is pushed out, and so is their
    /// ship; in one with the player on its white list, the player stays.
    /// </summary>
    public sealed class SafeZoneAccessCheck : ZoneCheck
    {
        public override string Name => Prefix + "safe_zone_access";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -10));
            FakeClients.SetFree(Me, true);
            var ship = Build("zone-ship", Site.At(8, 0, 0), false,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            var stranger = WorldApi.TestIdentityId();
            Zone(0, MySafeZoneAccess.Whitelist, new[] { stranger }, "zone-closed");
            Func<VRage.ModAPI.IMyEntity, double> off = e => Vector3D.Distance(e.PositionComp.WorldAABB.Center, Site.Origin);
            yield return Until(() => Math.Min(off(Body), off(ship)), v => v > Radius - 3, "the player and their ship pushed out of a zone not open to them, m from its middle", 30,
                () => "the player " + F(off(Body)) + " m, the ship " + F(off(ship)) + " m");
            var pushed = "player " + F(off(Body)) + " m, ship " + F(off(ship)) + " m";

            // a zone of their own, further on: the player stays in it
            var home = Site.At(0, 0, 400);
            Zone(0, MySafeZoneAccess.Whitelist, new[] { Identity }, "zone-open", home);
            FakeClients.SetFree(Me, false);
            StandAt(home + Site.Forward * 10, home);
            yield return Seconds(1);
            FakeClients.SetFree(Me, true);
            yield return Hold(5, Vector3.Zero, jetpack: true, dampeners: true);
            var inside = Vector3D.Distance(Body.PositionComp.GetPosition(), home);
            Check(inside < Radius - 5, "the player was pushed out of a zone open to them: " + F(inside) + " m from its middle");
            Result("pushed out of a closed zone (" + pushed + "), stayed in their own (" + F(inside) + " m from its middle)");
        }
    }
}
