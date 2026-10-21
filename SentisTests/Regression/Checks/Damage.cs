using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SentisTests.Game;
using Sandbox.ModAPI.Interfaces;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A battery hurt below its critical line (the red one) stops being functional, and the light it fed goes dark; welded
    /// back with the components it lost, the battery works and the light is lit again.
    /// </summary>
    public sealed class FunctionalThresholdCheck : HandCheck
    {
        public override string Name => Prefix + "functional_threshold";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -4));
            var grid = Build("threshold", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("InteriorLight/SmallLight", 0, 0, 1));
            var battery = Block<Sandbox.Game.Entities.MyBatteryBlock>(grid);
            var light = Block<Sandbox.Game.Entities.Blocks.MyLightingBlock>(grid);
            var slim = battery.SlimBlock;
            yield return Until(() => light.IsWorking ? 1 : 0, v => v > 0, "the light working on the battery", 10);
            var critical = slim.BlockDefinition.CriticalIntegrityRatio * slim.MaxIntegrity;
            slim.DoDamage(slim.Integrity - critical * 0.5f, MyDamageType.Bullet, true, null, 0);
            yield return Seconds(1);
            Check(!slim.IsDestroyed, "the battery was destroyed instead of hurt");
            Check(!battery.IsFunctional && !battery.IsWorking, "a battery hurt below its critical line still works (integrity " + F(slim.Integrity) + ", critical " + F(critical) + ")");
            yield return Until(() => light.IsWorking ? 0 : 1, v => v > 0, "the light dark with its battery hurt", 5);
            var hurt = slim.Integrity;
            // the player brings everything a battery is built of, and welds it whole
            foreach (var component in slim.BlockDefinition.Components)
                Put(Inventory(Body), "Component/" + component.Definition.Id.SubtypeName, component.Count);
            yield return Equip("WelderItem");
            yield return AimAt(slim);
            yield return Pull();
            yield return Until(() => slim.Integrity / slim.MaxIntegrity, v => v >= 0.999, "the battery welded whole again", 60, () => GunState());
            Release();
            yield return Until(() => battery.IsWorking && light.IsWorking ? 1 : 0, v => v > 0, "the battery working and the light lit again", 10, () => State(battery));
            Result("hurt to " + F(hurt) + " of " + F(slim.MaxIntegrity) + " (critical " + F(critical) + ") the battery stopped and the light went dark; welded whole, both work again");
        }
    }

    /// <summary>
    /// A player grinds an enemy's cargo container with a hand grinder: below its ownership line it becomes nobody's,
    /// before it is ground away.
    /// </summary>
    public sealed class HackCheck : HandCheck
    {
        public override string Name => Prefix + "hack";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -4));
            var grid = BuildFor(WorldApi.TestIdentityId(), "hack", Site.Origin, new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0));
            var cargo = Block<MyCargoContainer>(grid);
            var slim = cargo.SlimBlock;
            Check(cargo.OwnerId == WorldApi.TestIdentityId(), "the cargo is not the enemy's");
            yield return Equip("AngleGrinderItem");
            yield return AimAt(slim);
            yield return Pull();
            yield return Until(() => cargo.OwnerId == 0 || slim.IsDestroyed || grid.MarkedForClose ? 1 : 0, v => v > 0, "the cargo nobody's or gone", 60,
                () => "integrity " + F(slim.Integrity) + " of " + F(slim.MaxIntegrity) + ", ownership line " + F(slim.BlockDefinition.OwnershipIntegrityRatio * slim.MaxIntegrity));
            Release();
            Check(!slim.IsDestroyed && !grid.MarkedForClose, "the cargo was ground away and never came to be nobody's");
            Result("ground below its ownership line (" + F(slim.Integrity) + " of " + F(slim.MaxIntegrity) + "), the enemy's cargo became nobody's");
        }
    }

    /// <summary>A player falls from 30 m on a planet with the jetpack off: they land hurt.</summary>
    public sealed class FallDamageCheck : RegressionCheck
    {
        public override string Name => Prefix + "fall_damage";
        public override SiteKind Kind => SiteKind.Planet;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            var pad = Pad("fall-pad", Site.Origin, 2);
            var floor = pad.PositionComp.GetPosition() + Site.Up * 1.25;
            yield return Arrive(floor + Site.Up * 30);
            var health = Body.StatComp.Health;
            var before = health.Value;
            FakeClients.SetFree(Me, true);
            var top = 0.0;
            for (var f = Frames(6); f > 0; f--)
            {
                Keys(Vector3.Zero, jetpack: false);
                top = Math.Max(top, Motion.Speed(Body));
                yield return null;
            }
            Check(health.Value < before, "a fall at " + F(top) + " m/s onto the floor did no harm (health " + F(health.Value) + ")");
            Result("fell 30 m at up to " + F(top) + " m/s, health " + F(before) + " -> " + F(health.Value));
        }
    }

    /// <summary>A player in open space with the helmet open takes harm from the vacuum.</summary>
    public sealed class VacuumCheck : RegressionCheck
    {
        public override string Name => Prefix + "vacuum";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableOxygen, "oxygen is off in this world");
            yield return Arrive(Site.Origin);
            var suit = Body.OxygenComponent;
            if (suit.HelmetEnabled) Request(Body, "OnSwitchHelmet");
            yield return Until(() => suit.HelmetEnabled ? 0 : 1, v => v > 0, "the helmet open", 5);
            var health = Body.StatComp.Health;
            var before = health.Value;
            yield return Until(() => before - health.Value, v => v > 0, "harm from the vacuum", 30);
            Result("helmet open in vacuum: health " + F(before) + " -> " + F(health.Value));
        }
    }

    /// <summary>A player carrying plates dies: what they carried stays in the world - on the body or in a bag by it.</summary>
    public sealed class DeathLootCheck : RegressionCheck
    {
        public override string Name => Prefix + "death_loot";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.Origin);
            var body = Body;
            Put(Inventory(body), "Component/SteelPlate", 17);
            body.DoDamage(1000f, MyDamageType.Suicide, true, attackerId: 0);
            yield return Until(() => body.IsDead ? 1 : 0, v => v > 0, "the player dead", 10);
            Track(body);
            var died = body.PositionComp.GetPosition();
            Func<IEnumerable<VRage.Game.Entity.MyEntity>> bags = () => MyEntities.GetEntities().Where(e => (e is MyInventoryBagEntity || e is MyCargoContainerInventoryBagEntity) && !e.MarkedForClose &&
                                                                                                         Vector3D.Distance(e.PositionComp.GetPosition(), died) < 50);
            Func<double> kept = () =>
            {
                var onBody = body.MarkedForClose ? 0 : (double)(body.GetInventory()?.GetItemAmount(Item("Component/SteelPlate")) ?? 0);
                return onBody + bags().Sum(e => (double)(e.GetInventory()?.GetItemAmount(Item("Component/SteelPlate")) ?? 0));
            };
            // the body may give its things to a bag a moment after the death
            yield return Seconds(2);
            // the bag is queued (MyFloatingObjects' spawn queue, a few a frame): under the suite's load it comes late, and
            // meanwhile the plates are nowhere - the body's inventory is cleared at the death
            yield return Until(kept, v => Math.Abs(v - 17) < 0.01, "the 17 plates on the body or in a bag", 40,
                () => "the body " + (body.MarkedForClose ? "gone" : "there, " + F(Vector3D.Distance(body.PositionComp.GetPosition(), died)) + " m from where it died, its items: "
                          + string.Join(", ", body.GetInventory()?.GetItems().Select(i => i.Content.SubtypeName + " " + i.Amount) ?? Enumerable.Empty<string>())) + ", bags " + bags().Count()
                      + "; anything with 17 plates in the world: " + string.Join(", ", MyEntities.GetEntities().Where(e => e.HasInventory && e.GetInventory()?.GetItemAmount(Item("Component/SteelPlate")) == 17)
                          .Select(e => e.GetType().Name + " " + F(Vector3D.Distance(e.PositionComp.GetPosition(), died)) + " m")));
            foreach (var bag in bags().ToList())
                Track(bag);
            Result("dead, the 17 plates kept " + (body.MarkedForClose ? "in a bag" : "on the body"));
        }
    }

    /// <summary>A warhead blown up on a planet's ground leaves a crater: the rock under it is gone.</summary>
    public sealed class CraterCheck : RegressionCheck
    {
        public override string Name => Prefix + "crater";
        public override SiteKind Kind => SiteKind.Planet;
        protected override bool NeedsDamage => true;

        private Vector3D _ground;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableVoxelDestruction, "voxel destruction is off in this world");
            // SentisGameplayImprovements lets explosions cut the ground only when the server says so
            var sgi = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisGameplayImprovements.SentisGameplayImprovementsPlugin")).FirstOrDefault(t => t != null);
            var config = sgi?.GetProperty("Config")?.GetValue(null);
            var voxels = config?.GetType().GetProperty("DamageVoxelsFromExplosions")?.GetValue(config) as bool?;
            SkipUnless(voxels != false, "SentisGameplayImprovements does not let explosions cut the ground here (DamageVoxelsFromExplosions)");
            _ground = Site.Ground(Site.Origin + Site.Right * 40);
            yield return Arrive(_ground + Site.Up * 40 - Site.Forward * 40);
            var before = Rock(_ground, 3);
            var grid = Build("crater", _ground + Site.Up * 1.25, true, new Part("Warhead/LargeWarhead", 0, 0, 0));
            var warhead = Block<MyWarhead>(grid);
            ((Sandbox.ModAPI.IMyTerminalBlock)warhead).SetValueBool("Safety", true);
            // the game cuts craters only at its high simulation quality (MyExplosion), which it drops while the server's
            // load is over 90 % (AdaptiveSimulationQuality, MySession.CheckSimSpeed): the stand is loaded so most of the
            // time. The cutting itself is what is checked: the quality held high for the blast, as on a server at ease
            var quality = MySession.Static.HighSimulationQuality;
            HoldHighQuality();
            Request(warhead, "DetonateRequest");
            yield return Until(() => warhead.MarkedForClose ? 1 : 0, v => v > 0, "the warhead blown up", 10);
            yield return Seconds(2);
            ReleaseHighQuality();
            var after = Rock(_ground, 3);
            Check(after < before, "the ground under the warhead is whole after the blast (" + before + " -> " + after + " of rock in 6 m around)");
            Result("the blast took " + (100.0 * (before - after) / Math.Max(1, before)).ToString("F0") + " % of the rock in 6 m around it (" + before + " -> " + after +
                   "; the server's own simulation quality " + (quality ? "high" : "low, held high for the blast") + ")");
        }



        /// <summary>The rock in a cube about the point: the content of all its voxels summed.</summary>
        private long Rock(Vector3D point, double half)
        {
            var planet = Site.Planet;
            var low = point - new Vector3D(half);
            var high = point + new Vector3D(half);
            VRage.Voxels.MyVoxelCoordSystems.WorldPositionToVoxelCoord(planet.PositionLeftBottomCorner, ref low, out Vector3I min);
            VRage.Voxels.MyVoxelCoordSystems.WorldPositionToVoxelCoord(planet.PositionLeftBottomCorner, ref high, out Vector3I max);
            min += planet.StorageMin;
            max += planet.StorageMin;
            var cache = new VRage.Voxels.MyStorageData();
            cache.Resize(min, max);
            planet.Storage.ReadRange(cache, VRage.Voxels.MyStorageDataTypeFlags.Content, 0, min, max);
            long sum = 0;
            var size = max - min + Vector3I.One;
            for (var i = 0; i < size.X * size.Y * size.Z; i++) sum += cache.Content(i);
            return sum;
        }

        public override void CleanupLeftovers()
        {
            if (Site?.Planet != null && _ground != Vector3D.Zero) WorldApi.RevertTerrain(Site.Planet, _ground, 12);
        }
    }
}
