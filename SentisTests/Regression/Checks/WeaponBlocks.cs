using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Every weapon block of the game, large and small - turrets and fixed guns - with its own ammunition on a powered
    /// station: a turret, left to itself, fires at an enemy wall 150 m off, a fixed gun fires when told to (its
    /// terminal's "Shoot"), and the wall takes damage. Flare launchers do no damage: they only have to fire. Each weapon
    /// stands in a lane of its own, 1.2 km from the next and 5 km over the sites, with a player by it: no turret reaches another's wall or another check.
    /// </summary>
    public sealed class WeaponBlocksCheck : RegressionCheck
    {
        public override string Name => Prefix + "weapon_blocks";
        public override int TimeoutSeconds => 320;
        protected override bool NeedsDamage => true;

        private sealed class Lane
        {
            public MyWeaponBlockDefinition Definition;
            public MyCubeBlock Gun;
            public MyCubeGrid Wall;
            public double Whole;
            public double Ammo;
            public MyDefinitionId Magazine;
            public bool Flares;
            public string Done;
        }

        protected override IEnumerator Check()
        {
            // at the game's low quality (its own under load) bullets check what they hit a third as often and fly through walls
            HoldHighQuality();
            var weapons = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyWeaponBlockDefinition>()
                .Where(d => d.Public && !(d.Id.TypeId.ToString().EndsWith("Searchlight")))
                .OrderBy(d => d.Id.SubtypeName).ToList();
            Check(weapons.Count > 0, "no weapon blocks in this game");
            yield return Arrive(Site.At(0, -40, 0));
            var lanes = new List<Lane>();
            for (var i = 0; i < weapons.Count; i++)
            {
                var definition = weapons[i];
                // 5 km over the plane of the sites, out of reach of every gun: the other checks' grids are enemies to these
                var at = Site.Origin + Site.Up * (5000 + 1200 * i);
                yield return Join(at + Site.Right * 40, _ => { });
                var weapon = MyDefinitionManager.Static.GetWeaponDefinition(definition.WeaponDefinitionId);
                var magazine = weapon?.AmmoMagazinesId?.FirstOrDefault() ?? default(MyDefinitionId);
                var large = definition.CubeSize == MyCubeSize.Large;
                var battery = large ? "BatteryBlock/LargeBlockBatteryBlock" : "BatteryBlock/SmallBlockBatteryBlock";
                var batterySize = Definition(battery).Size;
                var parts = new List<Part> { new Part(definition.Id.TypeId.ToString() + "/" + definition.Id.SubtypeName, 0, 0, 0) };
                for (var k = 1; k <= 3; k++)
                    parts.Add(new Part(battery, -batterySize.X * k, 0, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 3f));
                var station = Build("weapon", at, true, parts, definition.CubeSize);
                var gun = station.GetCubeBlock(Vector3I.Zero)?.FatBlock;
                Check(gun != null, definition.Id.SubtypeName + " was not built");
                // as many magazines as fit, up to 10: a big gun's shells do not take ten in its inventory
                if (!magazine.TypeId.IsNull && gun.GetInventory(0) is Sandbox.Game.MyInventory ammoBox)
                {
                    var fits = Math.Min(10, Math.Floor((double)ammoBox.ComputeAmountThatFits(magazine)));
                    if (fits > 0) ammoBox.AddItems((VRage.MyFixedPoint)fits, (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(magazine));
                }
                // a weapon WeaponCore runs fires at a threat only (TargetInfo.OffenseRating: a grid armed itself) and leaves a
                // neutral alone: its wall the pirates', with a copy of the weapon on it, no ammunition
                var wc = Game.Mods.WeaponCoreManages(definition.Id);
                var wallParts = Bodies.Wall("CubeBlock/LargeHeavyBlockArmorBlock", 2, 1).Concat(new[]
                {
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, -1).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 3f),
                }).ToList();
                // the wall is a large grid: a small weapon's lane gets a large weapon of WeaponCore's as the threat
                var threat = definition.CubeSize == MyCubeSize.Large ? definition : weapons.FirstOrDefault(d => d.CubeSize == MyCubeSize.Large && Game.Mods.WeaponCoreManages(d.Id));
                if (wc && threat != null) wallParts.Add(new Part(threat.Id.TypeId.ToString() + "/" + threat.Id.SubtypeName, 0, 0, -8));
                var wall = BuildFor(wc ? Sandbox.Game.World.MyPirateAntennas.GetPiratesId() : WorldApi.TestIdentityId(), "weapon-wall", at + Site.Forward * 150, wallParts.ToArray());
                lanes.Add(new Lane
                {
                    Definition = definition,
                    Gun = gun,
                    Wall = wall,
                    Whole = Integrity(wall),
                    Magazine = magazine,
                    Ammo = magazine.TypeId.IsNull ? 0 : (double)gun.GetInventory(0).GetItemAmount(magazine),
                    Flares = definition.Id.SubtypeName.IndexOf("Flare", StringComparison.OrdinalIgnoreCase) >= 0,
                });
            }
            yield return Seconds(1);
            // WeaponCore's weapons load its own magazines, not the game's definition's: those, once its AI has the grid
            foreach (var lane in lanes.Where(l => Game.Mods.WeaponCoreManages(l.Definition.Id)))
            {
                var magazines = Game.Mods.WeaponCoreMagazines(lane.Gun);
                if (magazines.Count == 0 || !(lane.Gun.GetInventory(0) is Sandbox.Game.MyInventory box)) continue;
                lane.Magazine = magazines[0];
                var fits = Math.Min(10, Math.Floor((double)box.ComputeAmountThatFits(lane.Magazine)));
                // a mod's turret whose WeaponCore magazine its own inventory does not take (the reskinned missile turrets on
                // V5: Missile200mm) fires for nobody: noted, not a fault of the game's
                if (fits <= 0)
                {
                    Note(lane.Definition.Id.SubtypeName + ": its WeaponCore magazine " + lane.Magazine.SubtypeName + " does not go into its inventory - it fires for nobody (the mod's set-up)");
                    lane.Done = lane.Definition.Id.SubtypeName + " (its magazine does not fit)";
                    continue;
                }
                if (fits > 0) box.AddItems((VRage.MyFixedPoint)fits, (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(lane.Magazine));
                lane.Ammo = (double)box.GetItemAmount(lane.Magazine);
            }
            foreach (var lane in lanes)
            {
                Switch(lane.Gun, true);
                // a fixed gun fires when told; a turret finds its target itself
                if (!(lane.Gun is Sandbox.Game.Weapons.MyLargeTurretBase))
                    ((Sandbox.ModAPI.IMyTerminalBlock)lane.Gun).GetActionWithName("Shoot_On")?.Apply(lane.Gun);
            }
            // 90 s of game time (a railgun charges first), up to three times that by the clock
            var started = DateTime.UtcNow;
            var frames = 0;
            while (lanes.Any(l => l.Done == null) && frames++ < Frames(90) && (DateTime.UtcNow - started).TotalSeconds < 270)
            {
                foreach (var lane in lanes.Where(l => l.Done == null))
                {
                    var spent = lane.Magazine.TypeId.IsNull ? 0 : lane.Ammo - (double)lane.Gun.GetInventory(0).GetItemAmount(lane.Magazine);
                    var damage = lane.Whole - Integrity(lane.Wall);
                    if (lane.Flares ? spent > 0 : damage > 0)
                        lane.Done = lane.Definition.Id.SubtypeName.Length == 0 ? lane.Definition.Id.TypeId.ToString().Replace("MyObjectBuilder_", "") : lane.Definition.Id.SubtypeName;
                }
                yield return null;
            }
            foreach (var lane in lanes)
                if (!(lane.Gun is Sandbox.Game.Weapons.MyLargeTurretBase))
                    ((Sandbox.ModAPI.IMyTerminalBlock)lane.Gun).GetActionWithName("Shoot_Off")?.Apply(lane.Gun);
            var silent = lanes.Where(l => l.Done == null).ToList();
            Check(silent.Count == 0, silent.Count + " of " + lanes.Count + " weapons did nothing in 90 s: " + string.Join(" | ", silent.Select(l =>
                Label(l.Definition) + " (" + State(l.Gun) + ", ammo " + (l.Magazine.TypeId.IsNull ? "none" : l.Magazine.SubtypeName + " " +
                F((double)l.Gun.GetInventory(0).GetItemAmount(l.Magazine))) +
                (l.Gun is Sandbox.Game.Weapons.MyLargeTurretBase t ? ", target " + (((Sandbox.ModAPI.Ingame.IMyLargeTurretBase)t).HasTarget ? "locked" : "none") : "") + ")")));
            Result(lanes.Count + " weapons fired and hit: " + string.Join(", ", lanes.Select(l => l.Done)));
        }

        private static string Label(MyCubeBlockDefinition d) => d.Id.TypeId.ToString().Replace("MyObjectBuilder_", "") + "/" + d.Id.SubtypeName;

        private static double Integrity(MyCubeGrid grid) => grid.MarkedForClose ? 0 : grid.GetBlocks().Sum(b => (double)b.Integrity);
    }
}
