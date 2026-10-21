using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;
using Direction = VRageMath.Base6Directions.Direction;
using IngameInventory = VRage.Game.ModAPI.Ingame.IMyInventory;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Large items do not go through small conveyors: two small grid cargo containers on one small line pass a small
    /// component and not a large one (bigger than 0.25 m, MyGridConveyorSystem.NeedsLargeTube); two large grid ones on a
    /// large line pass both.
    /// </summary>
    public sealed class SmallConveyorCheck : RegressionCheck
    {
        public override string Name => Prefix + "small_conveyor";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -8));
            var components = MyDefinitionManager.Static.GetPhysicalItemDefinitions().Where(d => d.Id.TypeId == typeof(MyObjectBuilder_Component) && d.Public).ToList();
            var large = components.Where(d => d.Size.AbsMax() > 0.25f).OrderByDescending(d => d.Size.AbsMax()).FirstOrDefault();
            var small = components.FirstOrDefault(d => d.Id.SubtypeName == "SteelPlate" && d.Size.AbsMax() <= 0.25f) ?? components.FirstOrDefault(d => d.Size.AbsMax() <= 0.25f);
            Check(large != null && small != null, "no large and small component in this game");

            string Pair(MyCubeSize size, string cargo, Part second, Vector3D at, out IngameInventory a, out IngameInventory b)
            {
                var first = new Part(cargo, 0, 0, 0);
                var taken = new HashSet<Vector3I>(Cells(first).Concat(Cells(second)));
                var free = taken.SelectMany(c => Base6Directions.IntDirections.Select(d => c + d)).First(c => !taken.Contains(c));
                var battery = size == MyCubeSize.Large ? "BatteryBlock/LargeBlockBatteryBlock" : "BatteryBlock/SmallBlockSmallBatteryBlock";
                var grid = Build("conveyor-size-" + size, at, true, new[] { first, second, new Part(battery, free.X, free.Y, free.Z).With(x => Motion.Charged(x)) }, size);
                var cargos = Blocks<MyCargoContainer>(grid);
                a = Inventory(cargos[0]);
                b = Inventory(cargos[1]);
                return grid.DisplayName;
            }
            Part smallSecond = null, largeSecond = null;
            yield return ConveyorNeighbour(new Part("CargoContainer/SmallBlockSmallContainer", 0, 0, 0), "CargoContainer/SmallBlockSmallContainer", null, p => smallSecond = p, MyCubeSize.Small);
            yield return ConveyorNeighbour(new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0), "CargoContainer/LargeBlockSmallContainer", null, p => largeSecond = p);
            Pair(MyCubeSize.Small, "CargoContainer/SmallBlockSmallContainer", smallSecond, Site.Origin, out var sa, out var sb);
            Pair(MyCubeSize.Large, "CargoContainer/LargeBlockSmallContainer", largeSecond, Site.At(15, 0, 0), out var la, out var lb);
            yield return WaitForTicks(10);
            Check(sa.IsConnectedTo(sb), "the small cargo containers are not on one conveyor line");
            Check(sa.CanTransferItemTo(sb, small.Id), "a small " + small.Id.SubtypeName + " does not go through a small conveyor");
            Check(!sa.CanTransferItemTo(sb, large.Id), "a large " + large.Id.SubtypeName + " (" + F(large.Size.AbsMax()) + " m) goes through a small conveyor");
            Check(la.CanTransferItemTo(lb, large.Id), "a large " + large.Id.SubtypeName + " does not go through a large conveyor");
            Result("small conveyor: " + small.Id.SubtypeName + " passes, " + large.Id.SubtypeName + " (" + F(large.Size.AbsMax()) + " m) does not; a large conveyor passes both");
        }
    }

    /// <summary>
    /// A player with the helmet open in a closed room full of air breathes the room's air: no harm, the suit's oxygen
    /// untouched (in open space the same player takes harm, reg_vacuum).
    /// </summary>
    public sealed class BreathingRoomCheck : RegressionCheck
    {
        public override string Name => Prefix + "breathing_room";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            var settings = MySession.Static.Settings;
            SkipUnless(settings.EnableOxygen && settings.EnableOxygenPressurization, "oxygen pressurization is off in this world");
            yield return Arrive(Site.At(0, 6, -8));
            // reg_air_vent's room: the cells (0,1,0) and (0,2,0) inside armor, the vent under them, a full tank on its conveyor
            var vent = new Part("AirVent/", 0, 0, 0).Facing(Direction.Up, Direction.Backward);
            Part tank = null;
            yield return ConveyorNeighbour(vent, "OxygenTank/", p => Cells(p).All(c => c.Y < 0 || Math.Abs(c.X) > 1 || Math.Abs(c.Z) > 1), p => tank = p);
            var tankCells = Cells(tank);
            var room = new[] { new Vector3I(0, 1, 0), new Vector3I(0, 2, 0) };
            var parts = new List<Part> { vent, tank.With(b => ((MyObjectBuilder_GasTank)b).FilledRatio = 1f) };
            for (var x = -1; x <= 1; x++)
            for (var y = 0; y <= 3; y++)
            for (var z = -1; z <= 1; z++)
            {
                var cell = new Vector3I(x, y, z);
                if (cell == Vector3I.Zero || room.Contains(cell) || tankCells.Contains(cell)) continue;
                parts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, z));
            }
            parts.Add(new Part("BatteryBlock/LargeBlockBatteryBlock", tankCells.Contains(new Vector3I(-2, 0, 0)) ? 2 : -2, 0, 0).With(b => Motion.Charged(b)));
            var grid = Build("breathing-room", Site.Origin, true, parts);
            var airVent = (SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent)Block<SpaceEngineers.Game.Entities.Blocks.MyAirVent>(grid);
            airVent.Depressurize = false;
            yield return Until(() => airVent.GetOxygenLevel(), v => v > 0.9, "the room full of air", 30, () => State((MyCubeBlock)airVent));
            // the player inside, the helmet open
            FakeClients.SetFree(Me, true);
            var inside = grid.GridIntegerToWorld(room[0]) - grid.WorldMatrix.Up * 1.2;
            StandAt(inside, inside + grid.WorldMatrix.Forward);
            yield return Hold(1, Vector3.Zero, jetpack: true, dampeners: true);
            var suit = Body.OxygenComponent;
            if (suit.HelmetEnabled) Request(Body, "OnSwitchHelmet");
            yield return Until(() => suit.HelmetEnabled ? 0 : 1, v => v > 0, "the helmet open", 5);
            var health = Body.StatComp.Health.Value;
            var oxygen = suit.SuitOxygenLevel;
            yield return Hold(8, Vector3.Zero, jetpack: true, dampeners: true);
            Check(Body.OxygenComponent.EnvironmentOxygenLevel > 0.5f || airVent.GetOxygenLevel() > 0.5, "the room's air is gone (" + F(airVent.GetOxygenLevel() * 100) + " %)");
            Check(Body.StatComp.Health.Value >= health, "breathing the room's air the player took harm: " + F(health) + " -> " + F(Body.StatComp.Health.Value) +
                                                        " (air at the player " + F(Body.EnvironmentOxygenLevel * 100) + " %)");
            Check(suit.SuitOxygenLevel >= oxygen - 0.0001, "with the helmet open in air the suit's oxygen went down " + F((oxygen - suit.SuitOxygenLevel) * 100) + " %");
            Result("helmet open in a room of air (" + F(Body.EnvironmentOxygenLevel * 100) + " % at the player): no harm in 8 s, the suit's oxygen kept");
        }
    }

    /// <summary>A player sitting in a cockpit full of air with the suit's oxygen low gets the suit filled from it.</summary>
    public sealed class CockpitOxygenCheck : RegressionCheck
    {
        public override string Name => Prefix + "cockpit_oxygen";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableOxygen, "oxygen is off in this world");
            yield return Arrive(Site.At(0, 2, 4));
            var grid = Build("cockpit-oxygen", Site.Origin, true,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0).With(b => ((MyObjectBuilder_Cockpit)b).OxygenLevel = 1f),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var seat = Block<MyCockpit>(grid);
            var oxygenId = Sandbox.Game.Entities.Character.Components.MyCharacterOxygenComponent.OxygenId;
            Body.OxygenComponent.UpdateStoredGasLevel(ref oxygenId, 0.2f);
            var low = Body.OxygenComponent.SuitOxygenLevel;
            yield return Sit(seat);
            yield return Until(() => Body.OxygenComponent.SuitOxygenLevel - low, v => v > 0.05, "the suit's oxygen filled from the cockpit", 30,
                () => "suit " + F(Body.OxygenComponent.SuitOxygenLevel * 100) + " %, the cockpit " + F(seat.OxygenFillLevel * 100) + " %");
            Result("seated, the suit's oxygen went " + F(low * 100) + " -> " + F(Body.OxygenComponent.SuitOxygenLevel * 100) + " % (the cockpit at " + F(seat.OxygenFillLevel * 100) + " %)");
        }
    }

    /// <summary>On a planet the jetpack holds the player up in the air and lifts them on the keys.</summary>
    public sealed class JetpackGravityCheck : RegressionCheck
    {
        public override string Name => Prefix + "jetpack_gravity";
        public override SiteKind Kind => SiteKind.Planet;

        protected override IEnumerator Check()
        {
            var ground = Site.Ground(Site.Origin);
            yield return Arrive(ground + Site.Up * 20);
            FakeClients.SetFree(Me, true);
            StandAt(ground + Site.Up * 20, ground + Site.Up * 20 + Site.Forward);
            yield return Hold(2, Vector3.Zero, jetpack: true, dampeners: true);
            Func<double> height = () => Vector3D.Dot(Body.PositionComp.GetPosition() - ground, Site.Up);
            var held = height();
            yield return Hold(2, Vector3.Zero, jetpack: true, dampeners: true);
            var drop = held - height();
            Check(Math.Abs(drop) < 1.5, "on the jetpack with the dampeners the player drifted " + F(drop) + " m down in 2 s (" + Body.GetCurrentMovementState() + ")");
            yield return Hold(3, new Vector3(0, 1, 0), jetpack: true, dampeners: true);
            var lift = height() - held;
            Check(lift > 3, "3 s of up on the jetpack lifted the player " + F(lift) + " m in the planet's gravity");
            Result("in " + F(Body.Physics.Gravity.Length()) + " m/s2 the jetpack held the player (" + F(drop) + " m in 2 s) and lifted them " + F(lift) + " m in 3 s");
        }
    }

    /// <summary>A player shoots another player with a rifle: the other is hurt.</summary>
    public sealed class PlayerDamageCheck : HandCheck
    {
        public override string Name => Prefix + "player_damage";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            // at the game's low quality (its own under load) bullets check what they hit a third as often and fly through walls
            HoldHighQuality();
            yield return Arrive(Site.At(0, 0, -12));
            var target = -1;
            yield return Join(Site.Origin, i => target = i);
            FakeClients.SetFree(target, true);
            var victim = FakeClients.Character(target);
            var rifle = (MyWeaponItemDefinition)MyDefinitionManager.Static.GetPhysicalItemDefinition(Item("PhysicalGunObject/AutomaticRifleItem"));
            var magazine = MyDefinitionManager.Static.GetWeaponDefinition(rifle.WeaponDefinitionId).AmmoMagazinesId[0];
            Inventory(Body).AddItems(3, (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(magazine));
            yield return Equip("AutomaticRifleItem");
            // as reg_hand_weapons aims at its wall: the shooter's head level with the other's middle, looking straight at it
            FakeClients.SetFree(Me, true);
            var middle = victim.PositionComp.WorldAABB.Center;
            var spot = middle - Site.Forward * 12;
            StandAt(spot, spot + Site.Forward);
            yield return null;
            var head = Body.GetHeadMatrix(false).Translation - Body.PositionComp.GetPosition();
            StandAt(spot - head, spot - head + Site.Forward);
            Body.SetHeadLocalXAngle(0);
            yield return WaitForTicks(3);
            // turned so the head looks right at the other's middle (where the head is, not the feet)
            for (var i = 0; i < 2; i++)
            {
                head = Body.GetHeadMatrix(false).Translation - Body.PositionComp.GetPosition();
                StandAt(Body.PositionComp.GetPosition(), Body.PositionComp.GetPosition() + (victim.PositionComp.WorldAABB.Center - (Body.PositionComp.GetPosition() + head)));
                Body.SetHeadLocalXAngle(0);
                yield return WaitForTicks(3);
            }
            AimPoint = victim.PositionComp.WorldAABB.Center;
            // a wall 6 m behind the other player: whether the bullets get there through them
            var wall = Build("player-damage-wall", middle + Site.Forward * 6, true, Bodies.Wall("CubeBlock/LargeHeavyBlockArmorBlock", 2, 1));
            var wallWhole = wall.GetBlocks().Sum(b => (double)b.Integrity);
            var health = victim.StatComp.Health.Value;
            yield return Pull();
            var frames = 0;
            yield return Until(() =>
            {
                AimPoint = victim.PositionComp.WorldAABB.Center;
                Look();
                if (++frames % Frames(1.5) == 0 && victim.StatComp.Health.Value >= health)
                {
                    Release();
                    Trigger();
                }
                return health - victim.StatComp.Health.Value;
            }, v => v > 0, "the other player hurt by the rifle", 20, () => GunState() + ", the other " + F(Vector3D.Distance(victim.PositionComp.GetPosition(), Body.PositionComp.GetPosition())) +
                                                                              " m off, the wall behind them lost " + F(wallWhole - wall.GetBlocks().Sum(b => (double)b.Integrity)) +
                                                                              ", the shot " + F(Motion.Angle(Body.GetHeadMatrix(false).Forward, victim.PositionComp.WorldAABB.Center - Body.GetHeadMatrix(false).Translation)) +
                                                                              " degrees off the other's middle");
            Release();
            Result("shot by another player's rifle, the player's health went " + F(health) + " -> " + F(victim.StatComp.Health.Value) + (victim.IsDead ? " (dead)" : ""));
        }
    }
}
