using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Common.ObjectBuilders;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>A check whose player holds a hand tool or a gun: takes it out, aims, pulls the trigger - each as the client asks.</summary>
    public abstract class HandCheck : RegressionCheck
    {
        /// <summary>The tool from the player's inventory into their hands (<c>MyCharacter.SwitchToWeaponMessage</c>).</summary>
        protected IEnumerator Equip(string item)
        {
            var inventory = Inventory(Body);
            Put(inventory, "PhysicalGunObject/" + item, 1);
            var stack = inventory.GetItems().First(i => i.Content.SubtypeName == item);
            // the hand item the game makes of the inventory item (the welder's is Welder/Welder, the rifle's AutomaticRifle/RifleGun)
            var hand = Sandbox.Definitions.MyDefinitionManager.Static.TryGetHandItemForPhysicalItem(Item("PhysicalGunObject/" + item));
            Check(hand != null, "no hand item for " + item + " in this game");
            var id = hand.Id;
            Request(Body, "SwitchToWeaponMessage", (SerializableDefinitionId?)id, (uint?)stack.ItemId);
            yield return Until(() => Body.CurrentWeapon != null && Body.CurrentWeapon.DefinitionId == id ? 1 : 0, v => v > 0, "the " + item + " in the player's hands", 10,
                () => "holding " + (Body.CurrentWeapon?.DefinitionId.ToString() ?? "nothing") + "; can switch " + Body.CanSwitchToWeapon(id) +
                      ", the item for the weapon " + Sandbox.Definitions.MyDefinitionManager.Static.ItemIdFromWeaponId(id) + ", the stack " + stack.Content.GetObjectId() +
                      ", on a ladder " + Body.IsOnLadder + ", dead " + Body.IsDead);
        }

        /// <summary>
        /// The player stands facing the block with their head at its height, the given distance off its face along the
        /// site's forward; for an engineer's tool, moved about until the tool says it aims at the block.
        /// </summary>
        protected IEnumerator AimAt(MySlimBlock target)
        {
            var centre = target.CubeGrid.GridIntegerToWorld(target.Position);
            var tool = Body.CurrentWeapon as MyEngineerToolBase;
            foreach (var distance in new[] { 2.4, 2.8, 2.0, 3.2 })
            foreach (var lift in new[] { 0.0, -0.4, 0.4 })
            {
                var spot = centre - Site.Forward * distance + Site.Up * lift;
                StandAt(spot, spot + Site.Forward);
                yield return null;
                var head = Body.GetHeadMatrix(false).Translation - Body.PositionComp.GetPosition();
                StandAt(spot - head, spot - head + Site.Forward);
                yield return WaitForTicks(3);
                if (tool == null || tool.GetTargetBlock() == target) yield break;
            }
            Check(false, "the tool aims at no place in front of the block: it sees " + (tool?.GetTargetBlock()?.BlockDefinition.Id.SubtypeName ?? "nothing"));
        }

        /// <summary>The trigger pulled (<c>MyCharacter.ShootBeginCallback</c>), straight ahead of the player's head.</summary>
        /// <summary>Where the player aims: the client sends its shoot direction as it looks about (null: straight ahead of the head).</summary>
        protected Vector3D? AimPoint;

        private Vector3 ShootDirection() => AimPoint.HasValue
            ? (Vector3)Vector3D.Normalize(AimPoint.Value - Body.WeaponPosition.LogicalPositionWorld)
            : (Vector3)Body.GetHeadMatrix(false).Forward;

        protected void Trigger(MyShootActionEnum action = MyShootActionEnum.PrimaryAction) =>
            Request(Body, "ShootBeginCallback", ShootDirection(), action, false);

        /// <summary>The shoot direction the client sends while the button is held (<c>MyCharacter.ShootDirectionChangeCallback</c>).</summary>
        protected void Look() => Request(Body, "ShootDirectionChangeCallback", ShootDirection());

        protected void Release(MyShootActionEnum action = MyShootActionEnum.PrimaryAction) => Request(Body, "ShootEndCallback", action);

        /// <summary>The trigger pulled, and what the character and the gun say of it over the next frames written down.</summary>
        /// <summary>
        /// The trigger pulled once the gun is ready (a tool just taken out has no power for a moment, a rifle loads its
        /// magazine), and pulled again while it does not take - as a player clicks again. What the character and the
        /// gun said is written down.
        /// </summary>
        protected IEnumerator Pull()
        {
            var seen = new List<string>();
            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started).TotalSeconds < 15)
            {
                Body.CurrentWeapon.CanShoot(MyShootActionEnum.PrimaryAction, Identity, out var status);
                if (status == MyGunStatusEnum.OK)
                {
                    Trigger();
                    yield return WaitForTicks(5);
                    seen.Add("character " + Body.IsShooting(MyShootActionEnum.PrimaryAction) + ", " + GunState().Split(';')[0]);
                    // a tool holds on while the button is held; a rifle fires its burst and stops
                    if (Body.IsShooting(MyShootActionEnum.PrimaryAction) || Body.CurrentWeapon.IsShooting) break;
                }
                else
                {
                    seen.Add("waiting: " + status);
                    yield return WaitForTicks(10);
                }
            }
            Note("the trigger: " + string.Join(" | ", seen.Distinct()));
            Check(Body.IsShooting(MyShootActionEnum.PrimaryAction) || Body.CurrentWeapon.IsShooting, "the trigger did not take in 15 s: " + GunState());
        }

        /// <summary>What the gun in the player's hands says of shooting now, for a failure.</summary>
        protected string GunState()
        {
            var gun = Body.CurrentWeapon;
            if (gun == null) return "nothing in the hands";
            gun.CanShoot(MyShootActionEnum.PrimaryAction, Identity, out var status);
            return gun.DefinitionId.SubtypeName + ": shooting " + gun.IsShooting + ", can shoot " + status + ", suit energy " + Body.SuitEnergyLevel.ToString("F2") +
                   ", grinding allowed " + Sandbox.Game.Entities.MySessionComponentSafeZones.IsActionAllowed(Body, VRage.Game.ObjectBuilders.Components.MySafeZoneAction.Grinding, 0L, 0uL) +
                   ", drilling allowed " + Sandbox.Game.Entities.MySessionComponentSafeZones.IsActionAllowed(Body, VRage.Game.ObjectBuilders.Components.MySafeZoneAction.Drilling, 0L, 0uL) +
                   ", shooting allowed " + Sandbox.Game.Entities.MySessionComponentSafeZones.IsActionAllowed(Body, VRage.Game.ObjectBuilders.Components.MySafeZoneAction.Shooting, 0L, 0uL);
        }

        /// <summary>The ground a planet check's tool works dug back as it was.</summary>
        protected void RevertGround(Vector3D centre, double radius)
        {
            if (Site.Planet != null) WorldApi.RevertTerrain(Site.Planet, centre, radius);
        }
    }

    /// <summary>A player with a hand welder and steel plates welds a frame to the end: the plates go from their inventory.</summary>
    public sealed class HandWeldCheck : HandCheck
    {
        public override string Name => Prefix + "hand_weld";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -4));
            var grid = Build("hand-weld", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0).At(0.05f));
            var block = grid.GetCubeBlock(Vector3I.Zero);
            Put(Inventory(Body), "Component/SteelPlate", 30);
            yield return Equip("WelderItem");
            yield return AimAt(block);
            var plates = Amount(Inventory(Body), "Component/SteelPlate");
            yield return Pull();
            yield return Until(() => block.Integrity / block.MaxIntegrity, v => v >= 0.999, "the frame welded by hand", 120,
                () => "the welder aims at " + ((Body.CurrentWeapon as MyEngineerToolBase)?.GetTargetBlock()?.BlockDefinition.Id.SubtypeName ?? "nothing") +
                      ", shooting " + (Body.CurrentWeapon?.IsShooting ?? false) + ", energy " + Body.SuitEnergyLevel.ToString("F2"));
            Release();
            var spent = plates - Amount(Inventory(Body), "Component/SteelPlate");
            Check(spent > 0, "the frame was welded and the player's plates are all there");
            Result("welded by hand to 100 %, " + spent + " plates from the player");
        }
    }

    /// <summary>A player with a hand grinder grinds a block away: every component it was built of goes into their inventory.</summary>
    public sealed class HandGrindCheck : HandCheck
    {
        public override string Name => Prefix + "hand_grind";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -4));
            var grid = Build("hand-grind", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            var block = grid.GetCubeBlock(Vector3I.Zero);
            var plates = Definition("CubeBlock/LargeBlockArmorBlock").Components.Where(c => c.Definition.Id.SubtypeName == "SteelPlate").Sum(c => c.Count);
            yield return Equip("AngleGrinderItem");
            yield return AimAt(block);
            yield return Pull();
            yield return Until(() => grid.MarkedForClose || grid.GetCubeBlock(Vector3I.Zero) == null ? 1 : 0, v => v > 0, "the block ground away by hand", 120,
                () => "integrity " + F(block.Integrity) + "; " + GunState() + "; aims at " + ((Body.CurrentWeapon as MyEngineerToolBase)?.GetTargetBlock()?.BlockDefinition.Id.SubtypeName ?? "nothing"));
            Release();
            yield return Seconds(1);
            var got = Amount(Inventory(Body), "Component/SteelPlate");
            Check(Math.Abs(got - plates) < 0.01, "the player got " + got + " steel plates of the block's " + plates);
            Result("ground away by hand, " + got + " of " + plates + " plates in the player's inventory");
        }
    }

    /// <summary>A player on a planet drills the ground at their feet with a hand drill: ore goes into their inventory.</summary>
    public sealed class HandDrillCheck : HandCheck
    {
        public override string Name => Prefix + "hand_drill";
        public override SiteKind Kind => SiteKind.Planet;

        /// <summary>
        /// A place of bare ground near the site: straight down from over it the first thing met is the planet's voxels
        /// (not a boulder, a tree, a grid), and it lies flat enough to stand on.
        /// </summary>
        private Vector3D? BareGround(Vector3D centre)
        {
            for (var ring = 0; ring < 4; ring++)
            for (var x = -ring; x <= ring; x++)
            for (var z = -ring; z <= ring; z++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(z)) != ring) continue;
                var over = Site.Ground(centre + Site.Right * (x * 6) + Site.Forward * (z * 6)) + Site.Up * 6;
                var hit = Sandbox.Engine.Physics.MyPhysics.CastRay(over, over - Site.Up * 12);
                if (hit == null || !(Sandbox.Engine.Physics.MyPhysicsExtensions.GetHitEntity(hit.Value.HkHitInfo) is MyVoxelBase)) continue;
                if (Vector3D.Dot(hit.Value.HkHitInfo.Normal, Site.Up) < 0.95) continue;
                return hit.Value.Position;
            }
            return null;
        }

        private Vector3D _dug;

        /// <summary>Each run drills a fresh spot along the site: what an earlier one dug is not always put back at once.</summary>
        private static int _runs;

        protected override IEnumerator Check()
        {
            // the player there first: the planet's voxels have physics (what a ray meets) only near something
            var centre = Site.Origin + Site.Right * (8 * (_runs++ % 6) - 20);
            yield return Arrive(Site.Ground(centre) + Site.Up * 2);
            yield return Seconds(1);
            var bare = BareGround(centre);
            Check(bare.HasValue, "no bare flat ground near the site to drill");
            var ground = bare.Value;
            _dug = ground;
            StandAt(ground + Site.Up * 1.0, ground + Site.Up * 1.0 + Site.Forward);
            yield return Seconds(0.5);
            yield return Equip("HandDrillItem");
            // on their feet on the ground (let go, the jetpack off), crouched and looking down at it - the hand drill
            // reaches a metre or so off the head
            FakeClients.SetFree(Me, true);
            yield return Hold(3, Vector3.Zero, jetpack: false, face: Site.Forward);
            for (var i = 0; i < 30; i++)
            {
                Body.SetHeadLocalXAngle(-60);
                Keys(Vector3.Zero, jetpack: false, face: Site.Forward, crouch: true);
                yield return null;
            }
            // half a metre into the ground the feet stand on, just ahead of them (wherever that ground is: a pit an
            // earlier run dug is ground too)
            var feet = Body.PositionComp.GetPosition();
            AimPoint = feet + Site.Forward * 0.6 - Site.Up * 0.5;
            yield return Pull();
            var started = DateTime.UtcNow;
            Func<double> ore = () => Inventory(Body).GetItems().Where(i => i.Content is MyObjectBuilder_Ore).Sum(i => (double)i.Amount);
            while (ore() <= 1 && (DateTime.UtcNow - started).TotalSeconds < 30)
            {
                Body.SetHeadLocalXAngle(-60);
                Keys(Vector3.Zero, jetpack: false, face: Site.Forward, crouch: true);
                Look();
                yield return null;
            }
            Check(ore() > 1, "no ore drilled in 30 s: " + GunState() + "; " + F(Vector3D.Dot(Body.PositionComp.GetPosition() - ground, Site.Up)) + " m over the bare ground, " +
                             Body.GetCurrentMovementState() + "; " + DrillSensor(ground));
            Release();
            var kinds = Inventory(Body).GetItems().Where(i => i.Content is MyObjectBuilder_Ore).GroupBy(i => i.Content.SubtypeName)
                .Select(g => g.Key + " " + F(g.Sum(i => (double)i.Amount)));
            Result("hand drill: " + string.Join(", ", kinds));
        }

        /// <summary>Where the hand drill's sensor is against the ground, and whether it says it has something in range.</summary>
        private string DrillSensor(Vector3D ground)
        {
            var drill = Body.CurrentWeapon as MyHandDrill;
            if (drill == null) return "no drill";
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var drillBase = typeof(MyHandDrill).GetField("m_drillBase", any)?.GetValue(drill) as MyDrillBase;
            var inRange = typeof(MyHandDrill).GetField("m_objectInDrillingRange", any)?.GetValue(drill);
            if (drillBase == null) return "no drill base";
            var sensor = drillBase.Sensor;
            Func<Vector3D, double> over = p => Vector3D.Dot(p - Site.Ground(p), Site.Up);
            var cutOut = typeof(MyDrillBase).GetField("m_cutOut", any)?.GetValue(drillBase);
            var sphere = cutOut?.GetType().GetProperty("Sphere", any)?.GetValue(cutOut);
            var cutAt = sphere is BoundingSphereD round ? "cut-out " + F(over(round.Center)) + " m over the ground r " + F(round.Radius) : "no cut-out";
            return cutAt + ", the drill " + F(over(drill.PositionComp.GetPosition())) + " m over the ground facing " + F(Vector3D.Dot(drill.WorldMatrix.Forward, Site.Up)) +
                   ", sensor centre " + F(over(sensor.Center)) + " m over the ground, front " + F(over(sensor.FrontPoint)) + " m, in range " + inRange +
                   ", entities " + sensor.EntitiesInRange.Count + ", head " + F(Body.HeadLocalXAngle) + ", weapon at " + F(over(Body.WeaponPosition.LogicalPositionWorld)) +
                   " m looking " + F(Vector3D.Dot(Body.WeaponPosition.LogicalOrientationWorld, Site.Up));
        }

        public override void CleanupLeftovers()
        {
            if (_dug != Vector3D.Zero) RevertGround(_dug, 8);
        }
    }

    /// <summary>A ship drill, switched on and powered, bores into a planet's ground and fills its inventory with ore.</summary>
    public sealed class ShipDrillCheck : RegressionCheck
    {
        public override string Name => Prefix + "ship_drill";
        public override SiteKind Kind => SiteKind.Planet;

        /// <summary>Each run bores a fresh spot along the site: what an earlier one dug is not always put back at once.</summary>
        private static int _runs;
        private Vector3D _bored;

        protected override IEnumerator Check()
        {
            var ground = Site.Ground(Site.Origin + Site.Right * (10 * (_runs++ % 6) - 25));
            _bored = ground;
            yield return Arrive(ground + Site.Up * 12 + Site.Forward * 6);
            var rig = ToolRig("drill", "Drill/LargeBlockDrill", ground - Site.Up * 0.5, -Site.Up);
            var drill = Block<MyShipDrill>(rig);
            Switch(drill, true);
            yield return Until(() => Inventory(drill).GetItems().Where(i => i.Content is MyObjectBuilder_Ore).Sum(i => (double)i.Amount), v => v > 10,
                "ore in the drill, kg", 60, () => State(drill));
            var ore = Inventory(drill).GetItems().Where(i => i.Content is MyObjectBuilder_Ore).GroupBy(i => i.Content.SubtypeName)
                .Select(g => g.Key + " " + F(g.Sum(i => (double)i.Amount)));
            Result("ship drill: " + string.Join(", ", ore));
        }

        public override void CleanupLeftovers()
        {
            if (Site?.Planet != null && _bored != Vector3D.Zero) WorldApi.RevertTerrain(Site.Planet, _bored, 10);
        }
    }

    /// <summary>A player with a rifle and magazines shoots at a block: the block takes damage, the magazine empties.</summary>
    public sealed class RifleCheck : HandCheck
    {
        public override string Name => Prefix + "rifle";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -10));
            var wall = new System.Collections.Generic.List<Part>();
            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
                wall.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, 0));
            var grid = Build("rifle-target", Site.Origin, true, wall);
            var block = grid.GetCubeBlock(Vector3I.Zero);
            Func<double> integrity = () => grid.GetBlocks().Sum(b => (double)b.Integrity);
            Put(Inventory(Body), "AmmoMagazine/AutomaticRifleGun_Mag_20rd", 3);
            yield return Equip("AutomaticRifleItem");
            var spot = Site.Origin - Site.Forward * 6;
            StandAt(spot, spot + Site.Forward);
            yield return null;
            var head = Body.GetHeadMatrix(false).Translation - Body.PositionComp.GetPosition();
            StandAt(spot - head, spot - head + Site.Forward);
            yield return WaitForTicks(3);
            var whole = integrity();
            Body.SetHeadLocalXAngle(0);
            yield return Pull();
            yield return Until(() => whole - integrity(), v => v > 0, "damage from the rifle", 20,
                () => GunState() + ", magazines " + Amount(Inventory(Body), "AmmoMagazine/AutomaticRifleGun_Mag_20rd"));
            yield return Seconds(1);
            Release();
            Result("rifle: the wall took " + F(whole - integrity()) + " damage, " + Amount(Inventory(Body), "AmmoMagazine/AutomaticRifleGun_Mag_20rd") + " magazines left");
        }
    }
}
