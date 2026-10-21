using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.Entity.UseObject;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Thrusters by where they are: in a planet's atmosphere the atmospheric thruster gives most of its thrust and the ion
    /// one little; in space the atmospheric one gives none and the ion one all.
    /// </summary>
    public sealed class ThrustEnvironmentCheck : RegressionCheck
    {
        private readonly SiteKind _kind;

        public ThrustEnvironmentCheck(SiteKind kind) { _kind = kind; }

        public override string Name => Prefix + "thrust_environment" + (_kind == SiteKind.Planet ? "_planet" : "_space");
        public override SiteKind Kind => _kind;

        protected override IEnumerator Check()
        {
            var at = Site.Origin + Site.Up * 6;
            yield return Arrive(at + Site.Forward * 6);
            var grid = Build("thrust-environment", at, true,
                new Part("Thrust/LargeBlockSmallAtmosphericThrust", 0, 0, 0),
                new Part("Thrust/LargeBlockSmallThrust", 2, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            yield return Seconds(1);
            Func<string, double> share = subtype =>
            {
                var thrust = (Sandbox.ModAPI.Ingame.IMyThrust)grid.GetFatBlocks().OfType<MyThrust>().First(t => t.BlockDefinition.Id.SubtypeName == subtype);
                return thrust.MaxEffectiveThrust / thrust.MaxThrust;
            };
            var atmospheric = share("LargeBlockSmallAtmosphericThrust");
            var ion = share("LargeBlockSmallThrust");
            if (_kind == SiteKind.Planet)
            {
                Check(atmospheric > 0.5, "on the planet the atmospheric thruster gives " + F(atmospheric * 100) + " % of its thrust");
                Check(ion < 0.5, "on the planet the ion thruster gives " + F(ion * 100) + " % of its thrust");
                Check(atmospheric > ion, "on the planet the ion thruster outdoes the atmospheric one");
            }
            else
            {
                Check(atmospheric < 0.01, "in space the atmospheric thruster gives " + F(atmospheric * 100) + " % of its thrust");
                Check(ion > 0.99, "in space the ion thruster gives " + F(ion * 100) + " % of its thrust");
            }
            Result((_kind == SiteKind.Planet ? "on the planet" : "in space") + ": atmospheric " + F(atmospheric * 100) + " %, ion " + F(ion * 100) + " % of their thrust");
        }
    }

    /// <summary>A player in the flame of a thruster firing at full is burnt.</summary>
    public sealed class FlameHurtsPlayerCheck : RegressionCheck
    {
        public override string Name => Prefix + "flame_hurts_player";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.ThrusterDamage, "thruster damage is off in this world");
            yield return Arrive(Site.At(10, 6, 0));
            // two thrusters back to back, their thrusts cancel: the ship stays, the flames go out both ways
            var ship = Build("flame-player", Site.Origin, false, new[]
            {
                new Part("Thrust/LargeBlockSmallThrust", 0, 0, 0).Facing(Direction.Forward),
                new Part("Thrust/LargeBlockSmallThrust", 0, 0, 2).Facing(Direction.Backward),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 1).With(b => Motion.Charged(b)),
            });
            var ends = ship.GetBlocks().Where(b => b.FatBlock is MyThrust).Select(b => b.FatBlock.PositionComp.GetPosition()).ToList();
            var line = Vector3D.Normalize(ends[1] - ends[0]);
            var spot = ends[1] + line * 3.5;
            StandAt(spot, ends[1]);
            Body.JetpackComp?.TurnOnJetpack(true);
            Body.StatComp.Health.Value = Body.StatComp.Health.MaxValue;
            var health = Body.StatComp.Health.Value;
            foreach (var thruster in Blocks<MyThrust>(ship)) ((Sandbox.ModAPI.Ingame.IMyThrust)thruster).ThrustOverridePercentage = 1f;
            yield return Until(() =>
            {
                StandAt(spot, ends[1]);
                return health - (Body.IsDead ? 0 : Body.StatComp.Health.Value);
            }, v => v > 0, "the player burnt by the flame", 10, () => string.Join("; ", Blocks<MyThrust>(ship).Select(t => State(t) + ", thrust " + F(t.CurrentStrength))));
            var lost = health - (Body.IsDead ? 0 : Body.StatComp.Health.Value);
            foreach (var thruster in Blocks<MyThrust>(ship)) ((Sandbox.ModAPI.Ingame.IMyThrust)thruster).ThrustOverridePercentage = 0f;
            Result("3.5 m behind a thruster at full the player lost " + F(lost) + " health" + (Body.IsDead ? " (dead)" : ""));
        }
    }

    /// <summary>A warhead going off 6 m from a player hurts them.</summary>
    public sealed class ExplosionHurtsPlayerCheck : RegressionCheck
    {
        public override string Name => Prefix + "explosion_hurts_player";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            var grid = Build("blast-player", Site.Origin, true, new Part("Warhead/LargeWarhead", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            var warhead = Block<MyWarhead>(grid);
            yield return Arrive(warhead.PositionComp.GetPosition() - Site.Right * 6);
            Body.JetpackComp?.TurnOnJetpack(true);
            Body.StatComp.Health.Value = Body.StatComp.Health.MaxValue;
            var health = Body.StatComp.Health.Value;
            ((Sandbox.ModAPI.IMyTerminalBlock)warhead).SetValueBool("Safety", true);
            Request(warhead, "DetonateRequest");
            yield return Until(() => warhead.MarkedForClose || warhead.Closed ? 1 : 0, v => v > 0, "the warhead blown up", 10);
            yield return Until(() => health - (Body.IsDead ? 0 : Body.StatComp.Health.Value), v => v > 0, "the player hurt by the blast", 5);
            Result("a warhead 6 m off took " + F(health - (Body.IsDead ? 0 : Body.StatComp.Health.Value)) + " of the player's health" + (Body.IsDead ? " (dead)" : ""));
        }
    }

    /// <summary>On a planet with breathable air the player with the helmet open is not hurt and keeps the suit's oxygen.</summary>
    public sealed class PlanetBreathCheck : RegressionCheck
    {
        public override string Name => Prefix + "planet_breath";
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableOxygen, "oxygen is off in this world");
            // the planet sites may be on a planet without air (on the stand: Mars): the check goes to one with it
            var planet = MyEntities.GetEntities().OfType<MyPlanet>().FirstOrDefault(p => p.GetOxygenForPosition(p.PositionComp.GetPosition() + Vector3D.Up * (p.AverageRadius + 50)) > 0.5);
            SkipUnless(planet != null, "no planet with breathable air in this world");
            var guess = planet.PositionComp.GetPosition() + Vector3D.Normalize(Site.Origin - planet.PositionComp.GetPosition()) * planet.MaximumRadius * 1.1;
            var ground = planet.GetClosestSurfacePointGlobal(ref guess);
            var up = Vector3D.Normalize(ground - planet.PositionComp.GetPosition());
            yield return Arrive(ground + up * 3);
            Body.JetpackComp?.TurnOnJetpack(true);
            yield return Seconds(2);
            var suit = Body.OxygenComponent;
            var air = Body.EnvironmentOxygenLevel;
            SkipUnless(air > 0.5, "the planet of the site has " + F(air * 100) + " % oxygen, not breathable");
            if (suit.HelmetEnabled) Request(Body, "OnSwitchHelmet");
            yield return Until(() => suit.HelmetEnabled ? 0 : 1, v => v > 0, "the helmet open", 5);
            Body.StatComp.Health.Value = Body.StatComp.Health.MaxValue;
            var health = Body.StatComp.Health.Value;
            var oxygen = suit.SuitOxygenLevel;
            yield return Seconds(8);
            Check(Body.StatComp.Health.Value >= health, "breathing the planet's air the player lost " + F(health - Body.StatComp.Health.Value) + " health (last damage "
                + Body.StatComp.LastDamage.Type + " " + F(Body.StatComp.LastDamage.Amount) + ", air " + F(Body.EnvironmentOxygenLevel * 100) + " %)");
            Check(suit.SuitOxygenLevel >= oxygen - 0.001, "with the helmet open in breathable air the suit's oxygen went " + F(oxygen * 100) + " -> " + F(suit.SuitOxygenLevel * 100) + " %");
            Request(Body, "OnSwitchHelmet");
            Result("helmet open in the planet's air (" + F(air * 100) + " %): no harm in 8 s, the suit's oxygen kept");
        }
    }

    /// <summary>The suit out of hydrogen fills itself from a hydrogen bottle the player carries.</summary>
    public sealed class JetpackBottleCheck : RegressionCheck
    {
        public override string Name => Prefix + "jetpack_bottle";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            var suit = Body.OxygenComponent;
            var hydrogen = Sandbox.Game.Entities.Character.Components.MyCharacterOxygenComponent.HydrogenId;
            suit.UpdateStoredGasLevel(ref hydrogen, 0f);
            var bottle = (MyObjectBuilder_GasContainerObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(Item("GasContainerObject/HydrogenBottle"));
            bottle.GasLevel = 1f;
            Inventory(Body).AddItems(1, bottle);
            yield return Until(() => suit.GetGasFillLevel(hydrogen), v => v > 0.5, "the suit's hydrogen from the bottle", 15);
            var left = Inventory(Body).GetItems().Select(i => i.Content).OfType<MyObjectBuilder_GasContainerObject>().Select(b => b.GasLevel).FirstOrDefault();
            Check(left < 1f, "the bottle is still full after filling the suit");
            Result("the empty suit took hydrogen from the bottle carried: " + F(suit.GetGasFillLevel(hydrogen) * 100) + " %, the bottle left at " + F(left * 100) + " %");
        }
    }

    /// <summary>A gatling turret shoots an enemy player standing in the open in its range.</summary>
    public sealed class TurretCharacterCheck : RegressionCheck
    {
        public override string Name => Prefix + "turret_character";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            // at the game's low quality (its own under load) bullets check what they hit a third as often and fly through walls
            HoldHighQuality();
            yield return Arrive(Site.At(-30, 0, 0));
            var enemy = -1;
            var spot = Site.At(0, 0, 80);
            yield return Join(spot, i => enemy = i);
            var victim = FakeClients.Character(enemy);
            victim.JetpackComp?.TurnOnJetpack(true);
            victim.StatComp.Health.Value = victim.StatComp.Health.MaxValue;
            var health = victim.StatComp.Health.Value;
            var grid = Build("turret-character", Site.Origin, true,
                new Part("LargeGatlingTurret/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var turret = (MyCubeBlock)grid.GetCubeBlock(Vector3I.Zero).FatBlock;
            var magazine = Sandbox.Definitions.MyDefinitionManager.Static.GetWeaponDefinition(((Sandbox.Definitions.MyWeaponBlockDefinition)turret.BlockDefinition).WeaponDefinitionId).AmmoMagazinesId[0];
            Inventory(turret).AddItems(5, (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(magazine));
            var rounds = (double)Inventory(turret).GetItemAmount(magazine) * Sandbox.Definitions.MyDefinitionManager.Static.GetAmmoMagazineDefinition(magazine).Capacity + (((Sandbox.Game.Weapons.MyLargeTurretBase)turret).GunBase?.CurrentAmmo ?? 0);
            ((Sandbox.ModAPI.IMyTerminalBlock)turret).SetValueBool("TargetCharacters", true);
            yield return Until(() => health - (victim.IsDead ? 0 : victim.StatComp.Health.Value), v => v > 0, "the enemy player shot by the turret", 30, () => State(turret));
            var left = (double)Inventory(turret).GetItemAmount(magazine) * Sandbox.Definitions.MyDefinitionManager.Static.GetAmmoMagazineDefinition(magazine).Capacity + (((Sandbox.Game.Weapons.MyLargeTurretBase)turret).GunBase?.CurrentAmmo ?? 0);
            Check(left < rounds, "the turret fired and used no ammunition");
            Result("the turret shot the enemy player 80 m off (ammunition used): " + F(health - (victim.IsDead ? 0 : victim.StatComp.Health.Value)) + " health taken" + (victim.IsDead ? " (dead)" : ""));
        }
    }

    /// <summary>
    /// An unknown signal: a personal drop container is sent for the player, as the drop system does when the player's
    /// timer runs out; a grid with the drop's container appears near them, with its GPS for the player. Removed after.
    /// </summary>
    public sealed class UnknownSignalCheck : RegressionCheck
    {
        public override string Name => Prefix + "unknown_signal";

        private static readonly System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        private Sandbox.Game.SessionComponents.MySessionComponentContainerDropSystem _added;

        public override void Cleanup()
        {
            try { _added?.RemovePlayerDataForIdentity(Identity); }
            finally { base.Cleanup(); }
        }

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableContainerDrops, "drop containers are off in this world");
            var drops = MySession.Static.GetComponent<Sandbox.Game.SessionComponents.MySessionComponentContainerDropSystem>();
            SkipUnless(drops != null, "no drop container system in this world");
            yield return Arrive(Site.At(0, 0, -3));
            var list = (IList)typeof(Sandbox.Game.SessionComponents.MySessionComponentContainerDropSystem).GetField("m_playerData", Any).GetValue(drops);
            // the system keeps a timer for each player it was told of when they first came in: a fake player is given one as they are
            var data = list.Cast<object>().FirstOrDefault(d => (long)d.GetType().GetField("PlayerId").GetValue(d) == Identity);
            if (data == null)
            {
                var type = typeof(Sandbox.Game.SessionComponents.MySessionComponentContainerDropSystem).GetNestedType("MyPlayerContainerData", System.Reflection.BindingFlags.NonPublic);
                data = Activator.CreateInstance(type, Identity, 600, true, false, 0L);
                list.Add(data);
                _added = drops;
            }
            var gps0 = MySession.Static.Gpss.ExistsForPlayer(Identity) ? MySession.Static.Gpss[Identity].Count : 0;
            var before = new HashSet<long>(MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.EntityId));
            var spawn = typeof(Sandbox.Game.SessionComponents.MySessionComponentContainerDropSystem).GetMethod("SpawnContainerDropInternal", Any);
            var args = new object[] { data, false, false, false, null };
            var sent = (bool)spawn.Invoke(drops, args);
            Check(sent, "the drop system found no place for the player's drop");
            MyCubeGrid drop = null;
            yield return Until(() => (drop = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !before.Contains(g.EntityId) && !g.MarkedForClose
                && g.GetFatBlocks().Any(b => b.Components.Contains(typeof(Sandbox.Game.Components.MyContainerDropComponent))))) != null ? 1 : 0, v => v > 0, "the drop's grid", 20);
            Track(drop);
            var distance = Vector3D.Distance(drop.PositionComp.GetPosition(), Body.PositionComp.GetPosition());
            var gps = MySession.Static.Gpss.ExistsForPlayer(Identity) ? MySession.Static.Gpss[Identity].Count : 0;
            Check(gps > gps0, "the player got no GPS of the drop");
            foreach (var mark in MySession.Static.Gpss[Identity].Values.Where(g => Vector3D.Distance(g.Coords, drop.PositionComp.GetPosition()) < 2000).ToList())
                MySession.Static.Gpss.SendDeleteGpsRequest(Identity, mark.Hash);
            Result("a drop " + drop.DisplayName + " sent for the player " + F(distance) + " m off, with its GPS");
        }
    }
}
