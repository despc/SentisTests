using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// What a save keeps: a grid with things in it - items in a cargo container and an assembler, an assembler's queue, a
    /// battery's charge, a hydrogen tank's fill, a light switched off and named, a timer's toolbar pointing at the light, a
    /// group, a programmable block's script, a block's custom data - is written as a save writes it (its object builder,
    /// to XML and back) and made again. The copy has all of it, block by block, and its toolbar points at the copy's own
    /// light.
    /// </summary>
    public sealed class SaveRoundTripCheck : RegressionCheck
    {
        public override string Name => Prefix + "save_roundtrip";

        private const string Script = "public void Main(string argument) { Echo(\"saved script\"); }";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -10));
            var lightId = MyEntityIdentifier.AllocateId();
            var scripts = MySession.Static.EnableIngameScripts;
            var parts = new List<Part>
            {
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => ((MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1.5f),
                new Part("CargoContainer/LargeBlockSmallContainer", 1, 0, 0).Named("Saved cargo"),
                new Part("Assembler/LargeAssembler", 2, 0, 0),
                new Part("InteriorLight/SmallLight", 3, 0, 0).With(b => { b.EntityId = lightId; ((MyObjectBuilder_TerminalBlock)b).CustomName = "Saved light"; }),
                new Part("TimerBlock/TimerBlockLarge", 4, 0, 0).With(b => ((MyObjectBuilder_TimerBlock)b).Toolbar = Toolbars.One(lightId, "OnOff_Off")),
                new Part("OxygenTank/LargeHydrogenTankSmall", 5, 0, 0).With(b => ((MyObjectBuilder_GasTank)b).FilledRatio = 0.4f),
            };
            if (scripts) parts.Add(new Part("MyProgrammableBlock/LargeProgrammableBlock", 6, 0, 0).With(b => ((MyObjectBuilder_MyProgrammableBlock)b).Program = Script));
            var grid = Build("saved", Site.Origin, true, parts);
            var cargo = Block<MyCargoContainer>(grid);
            var assembler = Block<MyAssembler>(grid);
            var light = Block<MyLightingBlock>(grid);
            Put(Inventory(cargo), "Ore/Iron", 123.456);
            Put(Inventory(cargo), "Component/SteelPlate", 7);
            Put(assembler.InputInventory, "Ingot/Iron", 50);
            assembler.Enabled = false;
            Request(assembler, "OnAddQueueItemRequest", -1, (SerializableDefinitionId)Blueprints.Get("SteelPlate").Id, (MyFixedPoint)5);
            ((IMyTerminalBlock)light).SetValueBool("OnOff", false);
            ((IMyTerminalBlock)cargo).CustomData = "kept in the save";
            Request(grid, "OnModifyGroupSuccess", "Saved group", new List<long> { light.EntityId, cargo.EntityId });
            yield return Seconds(1);

            // written and read back as a save is
            var ob = (MyObjectBuilder_CubeGrid)grid.GetObjectBuilder(true);
            var xml = MyAPIGateway.Utilities.SerializeToXML(ob);
            var read = MyAPIGateway.Utilities.SerializeFromXML<MyObjectBuilder_CubeGrid>(xml);
            MyEntities.RemapObjectBuilder(read);
            read.Name = read.DisplayName = WorldApi.EntityPrefix + "saved-copy";
            read.PositionAndOrientation = new MyPositionAndOrientation(Site.At(0, 0, 15), (Vector3)Site.Forward, (Vector3)Site.Up);
            var copy = WorldApi.SpawnGrid(read);
            Track(copy);
            yield return Seconds(1);

            var wrong = new List<string>();
            Check(copy.BlocksCount == grid.BlocksCount, "the copy has " + copy.BlocksCount + " blocks of " + grid.BlocksCount);
            foreach (var slim in grid.GetBlocks())
            {
                var twin = copy.GetCubeBlock(slim.Min);
                var label = slim.BlockDefinition.Id.SubtypeName;
                if (twin == null || twin.BlockDefinition.Id != slim.BlockDefinition.Id) { wrong.Add(label + ": not in the copy"); continue; }
                if (Math.Abs(twin.Integrity - slim.Integrity) > 0.01) wrong.Add(label + ": integrity " + F(twin.Integrity) + " of " + F(slim.Integrity));
                var a = slim.FatBlock;
                var b = twin.FatBlock;
                if (a is MyTerminalBlock ta && b is MyTerminalBlock tb)
                {
                    if (ta.CustomName.ToString() != tb.CustomName.ToString()) wrong.Add(label + ": named '" + tb.CustomName + "', not '" + ta.CustomName + "'");
                    if (ta.CustomData != tb.CustomData) wrong.Add(label + ": custom data '" + tb.CustomData + "'");
                    if (a is MyFunctionalBlock fa && b is MyFunctionalBlock fb && fa.Enabled != fb.Enabled) wrong.Add(label + ": on " + fb.Enabled + ", not " + fa.Enabled);
                }
                for (var i = 0; i < (a?.InventoryCount ?? 0); i++)
                {
                    var mine = Items(a.GetInventory(i));
                    var theirs = Items(b.GetInventory(i));
                    if (mine != theirs) wrong.Add(label + " inventory " + i + ": " + theirs + ", not " + mine);
                }
            }
            var copyBattery = Block<MyBatteryBlock>(copy);
            if (Math.Abs(copyBattery.CurrentStoredPower - Block<MyBatteryBlock>(grid).CurrentStoredPower) > 0.01)
                wrong.Add("battery charge " + copyBattery.CurrentStoredPower.ToString("F3") + " of " + Block<MyBatteryBlock>(grid).CurrentStoredPower.ToString("F3"));
            var fill = ((Sandbox.ModAPI.Ingame.IMyGasTank)Block<MyGasTank>(grid)).FilledRatio;
            var copyFill = ((Sandbox.ModAPI.Ingame.IMyGasTank)Block<MyGasTank>(copy)).FilledRatio;
            if (Math.Abs(fill - copyFill) > 0.001) wrong.Add("tank " + copyFill.ToString("F3") + " of " + fill.ToString("F3"));
            var queue = new List<Sandbox.ModAPI.Ingame.MyProductionItem>();
            var copyQueue = new List<Sandbox.ModAPI.Ingame.MyProductionItem>();
            ((Sandbox.ModAPI.Ingame.IMyProductionBlock)assembler).GetQueue(queue);
            ((Sandbox.ModAPI.Ingame.IMyProductionBlock)Block<MyAssembler>(copy)).GetQueue(copyQueue);
            if (string.Join(",", queue.Select(q => q.BlueprintId.SubtypeName + "x" + q.Amount)) != string.Join(",", copyQueue.Select(q => q.BlueprintId.SubtypeName + "x" + q.Amount)))
                wrong.Add("the assembler's queue " + string.Join(",", copyQueue.Select(q => q.BlueprintId.SubtypeName + "x" + q.Amount)) + ", not " +
                          string.Join(",", queue.Select(q => q.BlueprintId.SubtypeName + "x" + q.Amount)));
            var timer = (MyObjectBuilder_TimerBlock)Block<SpaceEngineers.Game.Entities.Blocks.MyTimerBlock>(copy).GetObjectBuilderCubeBlock();
            var slot = timer.Toolbar?.Slots?.FirstOrDefault().Data as MyObjectBuilder_ToolbarItemTerminalBlock;
            if (slot == null || slot.BlockEntityId != Block<MyLightingBlock>(copy).EntityId || slot._Action != "OnOff_Off")
                wrong.Add("the timer's toolbar points at " + (slot == null ? "nothing" : slot.BlockEntityId + " " + slot._Action) + ", not the copy's light " + Block<MyLightingBlock>(copy).EntityId);
            var group = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(copy).GetBlockGroupWithName("Saved group");
            var members = new List<Sandbox.ModAPI.Ingame.IMyTerminalBlock>();
            group?.GetBlocks(members);
            if (members.Count != 2) wrong.Add("the group has " + members.Count + " blocks in the copy");
            if (scripts)
            {
                var program = ((Sandbox.ModAPI.IMyProgrammableBlock)Block<MyProgrammableBlock>(copy)).ProgramData;
                if (program != Script) wrong.Add("the programmable block's script is " + (program?.Length ?? 0) + " characters, not the " + Script.Length + " saved");
            }
            Check(wrong.Count == 0, "the copy is not what was saved: " + string.Join("; ", wrong));
            Result("written to XML and made again, the copy has it all: items, the queue, charge, gas, names, switches, custom data, the toolbar, the group" +
                   (scripts ? ", the script" : "") + " (" + (xml.Length / 1024) + " KB of XML)");
        }

        private static string Items(VRage.Game.Entity.MyInventoryBase inventory) =>
            inventory == null ? "none" : string.Join(",", inventory.GetItems().Select(i => i.Content.TypeId.ToString().Replace("MyObjectBuilder_", "") + "/" + i.Content.SubtypeName + " " + i.Amount)
                .OrderBy(x => x));
    }
}
