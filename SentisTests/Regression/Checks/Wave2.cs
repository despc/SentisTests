using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ObjectBuilders.Components;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    internal static class Toolbars
    {
        /// <summary>A toolbar of one slot: the action of a block (by its entity id, given to it in its builder).</summary>
        public static MyObjectBuilder_Toolbar One(long blockId, string action) => new MyObjectBuilder_Toolbar
        {
            ToolbarType = MyToolbarType.ButtonPanel,
            Slots = new List<MyObjectBuilder_Toolbar.Slot>
            {
                new MyObjectBuilder_Toolbar.Slot
                {
                    Index = 0,
                    Item = "",
                    Data = new MyObjectBuilder_ToolbarItemTerminalBlock { BlockEntityId = blockId, _Action = action },
                },
            },
        };
    }

    /// <summary>
    /// A programmable block with a script, run from its terminal with an argument: the script finds a light by its name
    /// and switches it off.
    /// </summary>
    public sealed class ProgrammableBlockCheck : RegressionCheck
    {
        public override string Name => Prefix + "programmable_block";

        private const string Script = @"
public void Main(string argument)
{
    var light = GridTerminalSystem.GetBlockWithName(""Regression light"") as IMyFunctionalBlock;
    if (light != null && argument == ""off"") light.Enabled = false;
    Echo(""ran "" + argument + (light == null ? "", no light"" : """"));
}";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.EnableIngameScripts, "in-game scripts are off in this world");
            yield return Arrive(Site.At(0, 4, -6));
            var grid = Build("programmable", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("InteriorLight/SmallLight", 1, 0, 0).Named("Regression light"),
                new Part("MyProgrammableBlock/LargeProgrammableBlock", 2, 0, 0).With(b => ((MyObjectBuilder_MyProgrammableBlock)b).Program = Script));
            var light = Block<MyLightingBlock>(grid);
            var pb = Block<MyProgrammableBlock>(grid);
            var instance = typeof(MyProgrammableBlock).GetField("m_instance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            yield return Until(() => pb.IsWorking && light.IsWorking && (instance == null || instance.GetValue(pb) != null) ? 1 : 0, v => v > 0, "the script compiled", 30,
                () => State(pb) + "; " + pb.DetailedInfo);
            var compress = typeof(MyProgrammableBlock).Assembly.GetType("Sandbox.Game.Entities.Blocks.StringCompressor")
                ?.GetMethod("CompressString", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Check(compress != null, "StringCompressor.CompressString is not in this game");
            Request(pb, "RunProgramRequest", compress.Invoke(null, new object[] { "off" }), Sandbox.ModAPI.Ingame.UpdateType.Terminal);
            yield return Until(() => light.Enabled ? 0 : 1, v => v > 0, "the light switched off by the script", 10, () => pb.DetailedInfo.ToString());
            Result("the script ran with its argument and switched the light off (" + pb.DetailedInfo.ToString().Trim().Replace("\n", " ") + ")");
        }
    }

    /// <summary>
    /// A player gives a block to another player in the terminal: the other gets the offer and takes it, and the block is
    /// theirs.
    /// </summary>
    public sealed class OwnershipCheck : RegressionCheck
    {
        public override string Name => Prefix + "ownership";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var other = FakeClients.Count;
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 40 }, i => (Site.At(4, 4, -6), 0, 0), withCharacters: true);
            yield return Until(() => FakeClients.HasLiveCharacter(other) ? 1 : 0, v => v > 0, "the other player's character", 30);
            Track(FakeClients.Character(other));
            var otherIdentity = FakeClients.PlayerOf(other).Identity.IdentityId;
            var grid = Build("ownership", Site.Origin, true,
                new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var cargo = Block<MyCargoContainer>(grid);
            Check(cargo.OwnerId == Identity, "the cargo is not the player's to give");

            var offer = new List<MyCubeGrid.MySingleOwnershipRequest> { new MyCubeGrid.MySingleOwnershipRequest { BlockId = cargo.EntityId, Owner = otherIdentity } };
            Request(typeof(MyCubeGrid), "AddChangeOwnersRequestServerside", MyOwnershipShareModeEnum.None, offer);
            MyCubeGrid.MySingleOwnershipRequest pending = default(MyCubeGrid.MySingleOwnershipRequest);
            yield return Until(() =>
            {
                pending = MySession.Static.OwnershipChangeRequests.FirstOrDefault(r => r.BlockId == cargo.EntityId);
                return pending.BlockId == cargo.EntityId ? 1 : 0;
            }, v => v > 0, "the offer waiting for the other player", 5);
            Check(cargo.OwnerId == Identity, "the block changed hands before the other player took it");
            var confirm = typeof(MyCubeGrid).GetMethod("OnChangeOwnersRequestConfirmation", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            Check(confirm != null, "MyCubeGrid.OnChangeOwnersRequestConfirmation is not in this game");
            FakeClients.AsClient(other, () => confirm.Invoke(null, new object[] { pending.BatchGuid, grid.EntityId, true }));
            yield return Until(() => cargo.OwnerId == otherIdentity ? 1 : 0, v => v > 0, "the block the other player's", 5, () => "owner " + cargo.OwnerId);
            Result("offered, taken: the cargo is the other player's now");
        }
    }

    /// <summary>
    /// A safe zone block with zone chips, switched on as a player switches it on: after its countdown the safe zone is up
    /// around it and forbids damage in it; switched off, it is gone.
    /// </summary>
    public sealed class SafeZoneCheck : RegressionCheck
    {
        public override string Name => Prefix + "safe_zone";
        public override int TimeoutSeconds => 240;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -12));
            var grid = Build("safe-zone", Site.Origin, true,
                new Part("SafeZoneBlock/SafeZoneBlock", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 1, 0).With(b => Motion.Charged(b)));
            var block = Block<SpaceEngineers.Game.Entities.Blocks.SafeZone.MySafeZoneBlock>(grid);
            Put(Inventory(block), "Component/ZoneChip", 3);
            var zone = block.Components.Get<SpaceEngineers.Game.EntityComponents.Blocks.MySafeZoneComponent>();
            Check(zone != null, "the safe zone block has no safe zone component");
            yield return Until(() => block.IsWorking ? 1 : 0, v => v > 0, "the safe zone block working", 10, () => State(block));
            var inside = block.PositionComp.GetPosition() + Site.Up * 3;
            Check(MySessionComponentSafeZones.IsActionAllowed(inside, MySafeZoneAction.Damage), "damage is forbidden there before any safe zone");
            Request(zone, "OnSafezoneCreateRemove", true);
            var definition = (SpaceEngineers.Game.Definitions.SafeZone.MySafeZoneBlockDefinition)block.BlockDefinition;
            // the countdown is game time: under load it takes longer by the clock
            yield return Seconds(definition.SafeZoneActivationTimeS);
            yield return Until(() => zone.IsSafeZoneEnabled() ? 1 : 0, v => v > 0, "the safe zone up after the countdown", 30);
            Check(!MySessionComponentSafeZones.IsActionAllowed(inside, MySafeZoneAction.Damage), "the safe zone is up and damage is allowed in it");
            Check(Amount(Inventory(block), "Component/ZoneChip") < 3, "the safe zone took no zone chip");
            Request(zone, "OnSafezoneCreateRemove", false);
            yield return Until(() => zone.IsSafeZoneEnabled() ? 0 : 1, v => v > 0, "the safe zone down", 10);
            yield return Until(() => MySessionComponentSafeZones.IsActionAllowed(inside, MySafeZoneAction.Damage) ? 1 : 0, v => v > 0, "damage allowed again", 10);
            Result("safe zone up after " + definition.SafeZoneActivationTimeS + " s, forbade damage, took a chip; switched off, gone");
        }
    }

    /// <summary>A collector, on and powered, takes in what floats in front of it.</summary>
    public sealed class CollectorCheck : RegressionCheck
    {
        public override string Name => Prefix + "collector";
        protected override bool NeedsLooseItems => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -8));
            var grid = Build("collector", Site.Origin, true,
                new Part("Collector/Collector", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 1).With(b => Motion.Charged(b)));
            var collector = Block<MyCollector>(grid);
            Switch(collector, true);
            var item = new VRage.Game.Entity.MyPhysicalInventoryItem((MyFixedPoint)15, (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(Item("Component/SteelPlate")));
            // in the collector's field (where its model puts it), let go there
            var dummy = typeof(MyCollector).GetField("m_collectorDummyMatrix", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Check(dummy != null, "MyCollector.m_collectorDummyMatrix is not in this game");
            var field = Vector3D.Transform(((Matrix)dummy.GetValue(collector)).Translation, collector.WorldMatrix);
            MyFloatingObjects.Spawn(item, field, collector.WorldMatrix.Forward, collector.WorldMatrix.Up);
            yield return Until(() => Amount(Inventory(collector), "Component/SteelPlate"), v => v >= 15, "the plates collected", 20,
                () => State(collector) + ", loose in the world: " + LooseItems);
            Result("the collector took in the 15 plates floating in front of it");
        }
    }

    /// <summary>A player paints a block of their grid (<c>MyCubeGrid.ColorBlockRequest</c>): the block takes the colour.</summary>
    public sealed class PaintCheck : RegressionCheck
    {
        public override string Name => Prefix + "paint";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var grid = Build("paint", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            var colour = new Vector3(0.6f, 0.3f, 0.1f);
            Request(grid, "ColorBlockRequest", Vector3I.Zero, Vector3I.Zero, colour, false);
            var block = grid.GetCubeBlock(Vector3I.Zero);
            var other = grid.GetCubeBlock(new Vector3I(1, 0, 0));
            yield return Until(() => Vector3.Distance(block.ColorMaskHSV, colour), v => v < 0.01, "the block painted", 5, () => "colour " + block.ColorMaskHSV);
            Check(Vector3.Distance(other.ColorMaskHSV, colour) > 0.01, "the block next to it took the colour too");
            Result("the block took the colour " + block.ColorMaskHSV + ", the one next to it kept its own");
        }
    }

    /// <summary>A ship of heavy armor flung at a station's wall at 40 m/s: the impact damages the blocks that met.</summary>
    public sealed class CollisionCheck : RegressionCheck
    {
        public override string Name => Prefix + "collision";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(20, 10, 0));
            var wall = new List<Part>();
            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
                wall.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, 0));
            var station = Build("collision-wall", Site.Origin, true, wall);
            // of heavy armor, some 10 t: SentisGameplayImprovements lets no ship lighter than its MinimumMassForKineticDamage
            // (5 t on the server) damage what it hits
            var ship = Build("collision-ship", Site.Origin - Site.Forward * 30, false,
                new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 1),
                new Part("CubeBlock/LargeHeavyBlockArmorBlock", 0, 0, 2));
            Func<double> whole = () => station.GetBlocks().Sum(b => (double)b.Integrity) + (ship.MarkedForClose ? 0 : ship.GetBlocks().Sum(b => (double)b.Integrity));
            var before = whole();
            yield return null;
            ship.Physics.LinearVelocity = (Vector3)(Site.Forward * 40);
            yield return Until(() => before - whole(), v => v > 0, "damage from the impact", 10,
                () => "the ship " + F(Vector3D.Distance(ship.PositionComp.GetPosition(), station.PositionComp.GetPosition())) + " m off at " + F(Motion.Speed(ship)) + " m/s");
            yield return Seconds(1);
            Result("the impact at 40 m/s took " + F(before - whole()) + " integrity off the blocks");
        }
    }

    /// <summary>An O2/H2 generator with ice fills an empty hydrogen bottle in its inventory.</summary>
    public sealed class BottleCheck : RegressionCheck
    {
        public override string Name => Prefix + "bottle";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -6));
            var grid = Build("bottle", Site.Origin, true,
                new Part("OxygenGenerator/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 1, 0).With(b => Motion.Charged(b)));
            var generator = Block<MyGasGenerator>(grid);
            // the player ticks "Auto-Refill" in the generator's terminal
            ((Sandbox.ModAPI.IMyTerminalBlock)generator).SetValueBool("Auto-Refill", true);
            var inventory = Inventory(generator);
            Put(inventory, "Ore/Ice", 500);
            var bottle = (Sandbox.Common.ObjectBuilders.Definitions.MyObjectBuilder_GasContainerObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(Item("GasContainerObject/HydrogenBottle"));
            bottle.GasLevel = 0;
            Check(inventory.AddItems(1, bottle), "the bottle does not go into the generator");
            Func<double> level = () => inventory.GetItems().Select(i => i.Content).OfType<Sandbox.Common.ObjectBuilders.Definitions.MyObjectBuilder_GasContainerObject>()
                .Select(b => (double)b.GasLevel).DefaultIfEmpty(0).Max();
            yield return Until(level, v => v > 0.05, "the bottle filling", 30, () => State(generator));
            Result("the hydrogen bottle in the generator filled to " + (level() * 100).ToString("F0") + " %");
        }
    }

    /// <summary>
    /// A player's suit in open space: breathing from it, the suit's oxygen goes down; its hydrogen low, the suit refills
    /// from a hydrogen bottle in the player's inventory, and the bottle empties by as much.
    /// </summary>
    public sealed class LifeSupportCheck : RegressionCheck
    {
        public override string Name => Prefix + "life_support";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.Origin);
            var suit = Body.OxygenComponent;
            Check(suit != null, "the astronaut has no suit");
            var settings = MySession.Static.Settings;
            if (settings.EnableOxygen)
            {
                // the helmet closed, as a player closes it in space
                if (!suit.HelmetEnabled) Request(Body, "OnSwitchHelmet");
                yield return Until(() => suit.HelmetEnabled ? 1 : 0, v => v > 0, "the helmet closed", 5);
                var oxygen = suit.SuitOxygenLevel;
                yield return Until(() => oxygen - suit.SuitOxygenLevel, v => v > 0, "the suit's oxygen breathed", 30,
                    () => "oxygen " + suit.SuitOxygenLevel.ToString("F6"));
            }
            var hydrogenId = Sandbox.Game.Entities.Character.Components.MyCharacterOxygenComponent.HydrogenId;
            suit.UpdateStoredGasLevel(ref hydrogenId, 0.2f);
            var bottle = (Sandbox.Common.ObjectBuilders.Definitions.MyObjectBuilder_GasContainerObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(Item("GasContainerObject/HydrogenBottle"));
            bottle.GasLevel = 1f;
            var pocket = Inventory(Body);
            Check(pocket.AddItems(1, bottle), "the bottle does not go into the player's inventory");
            yield return Until(() => suit.GetGasFillLevel(hydrogenId), v => v > 0.5, "the suit's hydrogen refilled from the bottle", 20);
            var left = pocket.GetItems().Select(i => i.Content).OfType<Sandbox.Common.ObjectBuilders.Definitions.MyObjectBuilder_GasContainerObject>().Select(b => (double)b.GasLevel).First();
            Check(left < 1, "the suit refilled and the bottle is still full");
            Result((settings.EnableOxygen ? "oxygen breathed from the suit; " : "") + "hydrogen 20 -> " + (suit.GetGasFillLevel(hydrogenId) * 100).ToString("F0") +
                   " % from the bottle, the bottle at " + (left * 100).ToString("F0") + " %");
        }
    }

    /// <summary>A button panel with a light's "switch off" on its first button: a player presses it, the light goes off.</summary>
    public sealed class ButtonPanelCheck : RegressionCheck
    {
        public override string Name => Prefix + "button_panel";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var lightId = VRage.MyEntityIdentifier.AllocateId();
            var grid = Build("buttons", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("InteriorLight/SmallLight", 1, 0, 0).With(b => b.EntityId = lightId),
                new Part("ButtonPanel/ButtonPanelLarge", 2, 0, 0).With(b => ((MyObjectBuilder_ButtonPanel)b).Toolbar = Toolbars.One(lightId, "OnOff_Off")));
            var light = Block<MyLightingBlock>(grid);
            var panel = Block<SpaceEngineers.Game.Entities.Blocks.MyButtonPanel>(grid);
            yield return Until(() => light.IsWorking ? 1 : 0, v => v > 0, "the light working", 10);
            Request(panel, "ActivateButton", 0);
            yield return Until(() => light.Enabled ? 0 : 1, v => v > 0, "the light switched off by the button", 5);
            Result("the button switched the light off");
        }
    }
}
