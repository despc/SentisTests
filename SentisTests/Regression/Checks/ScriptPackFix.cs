using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisTests.Core;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The modded V5's Sentis_v5_ScriptPack_Fix (its game logic, compiled by the game from the mod), with the blocks the
    /// V5 packs define (the SafeZone, Cockpit, NanoBot and Human inhibitors have scripts but no blocks in them):
    /// - a Custom inhibitor whose CustomData repeats a line, with Windows line ends, still switches the jump drive off
    ///   (a repeated line threw every 5 s, and a "\r" left the line unread);
    /// - a loot collector takes a loose item once, as much as there was (no copy);
    /// - a grid with two admin protectors stays immortal when one is taken off, and is no more when both are;
    /// - an enemy's nanobot welder stays on next to a Voxel_Protector (it was switched off as by a suppressor) and is
    ///   switched off next to a Nanobot_suppressor.
    /// Skipped where the mod is not loaded.
    /// </summary>
    public sealed class ScriptPackFixCheck : RegressionCheck
    {
        public override string Name => Prefix + "scriptpack_fix";
        public override int TimeoutSeconds => 120;
        protected override bool NeedsLooseItems => true;

        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        protected override IEnumerator Check()
        {
            // by a type of its own: the assembly is named after the mod's folder locally and after its workshop id on a server
            var mod = MyScriptManager.Static?.Scripts?.Values.FirstOrDefault(a => a.GetType("AdminProtector.AdminProtectorBlock") != null);
            SkipUnless(mod != null, "no Sentis_v5_ScriptPack_Fix mod in this world");
            foreach (var id in new[] { "Beacon/Custom_Inhibitor", "ConveyorSorter/Loot_Collector", "Beacon/AdminGridProtectorLarge", "JumpDrive/LargeJumpDrive",
                         "UpgradeModule/Nanobot_suppressor", "UpgradeModule/Voxel_Protector", "ShipWelder/SELtdLargeNanobotBuildAndRepairSystem" })
                SkipUnless(Has(id), "no " + id + " in this game");

            yield return Arrive(Site.At(0, 0, -30));
            var failures = new List<string>();

            // 1. Custom inhibitor, a jump drive 30 m away
            var custom = Build("custom-inhibitor", Site.At(-300, 0, 0), true, Beacon("Custom_Inhibitor", 80), BatteryAbove("Beacon/Custom_Inhibitor"));
            var customBeacon = (Sandbox.ModAPI.IMyBeacon)Fat(custom, "Custom_Inhibitor");
            const string config = "LargeJumpDrive:DISABLE\r\nLargeJumpDrive:DISABLE\r\n";
            customBeacon.CustomData = config;
            customBeacon.Radius = 80;
            var jumper = Build("custom-jump", Site.At(-300, 0, 30), true, new Part("JumpDrive/LargeJumpDrive", 0, 0, 0).With(b => ((MyObjectBuilder_FunctionalBlock)b).Enabled = true),
                BatteryAbove("JumpDrive/LargeJumpDrive"));
            var jumpDrive = (Sandbox.ModAPI.IMyFunctionalBlock)Fat(jumper, "LargeJumpDrive");

            // 2. a loot collector (lets everything through), 100 iron ore loose 20 m away
            var collector = Build("loot-collector", Site.At(0, 300, 0), true,
                new Part("ConveyorSorter/Loot_Collector", 0, 0, 0).With(b => ((MyObjectBuilder_FunctionalBlock)b).Enabled = true), BatteryAbove("ConveyorSorter/Loot_Collector"));
            var sorter = (Sandbox.ModAPI.IMyConveyorSorter)Fat(collector, "Loot_Collector");
            sorter.SetFilter(Sandbox.ModAPI.Ingame.MyConveyorSorterMode.Blacklist, new List<Sandbox.ModAPI.Ingame.MyInventoryItemFilter>());

            // 3. two admin protectors on one grid
            // (on a bed of armour: taking one off leaves the rest one grid, not split into another)
            var protectorSize = Definition("Beacon/AdminGridProtectorLarge").Size;
            var bed = Enumerable.Range(0, protectorSize.X * 2 + 1).SelectMany(x => Enumerable.Range(0, protectorSize.Z)
                .Select(z => new Part("CubeBlock/LargeBlockArmorBlock", x, -1, z)));
            var guarded = Build("admin-guarded", Site.At(0, -300, 0), true, bed.Concat(new[]
            {
                new Part("Beacon/AdminGridProtectorLarge", 0, 0, 0),
                new Part("Beacon/AdminGridProtectorLarge", protectorSize.X + 1, 0, 0),
            }));

            // 4. an enemy's nanobot welder (the tests' identity's) next to a Voxel_Protector of the player's: it stays on
            const string nanobot = "ShipWelder/SELtdLargeNanobotBuildAndRepairSystem";
            var welderGrid = Build("enemy-nanobot", Site.At(300, 0, 0), true, new[]
            {
                new Part(nanobot, 0, 0, 0).With(b => ((MyObjectBuilder_FunctionalBlock)b).Enabled = true), BatteryAbove(nanobot),
            }, ownedBy: SentisTests.Game.WorldApi.TestIdentityId());
            var welder = (Sandbox.ModAPI.IMyFunctionalBlock)Fat(welderGrid, "SELtdLargeNanobotBuildAndRepairSystem");
            var protector = Build("voxel-protector", Site.At(300, 0, 40), true,
                new Part("UpgradeModule/Voxel_Protector", 0, 0, 0), BatteryAbove("UpgradeModule/Voxel_Protector"));
            yield return Seconds(5);
            Note("enemy nanobot next to a Voxel_Protector: " + (welder.Enabled ? "on" : "off"));
            if (!welder.Enabled) failures.Add("a Voxel_Protector switched an enemy nanobot welder off (it is no suppressor)");
            yield return CloseAndWait(protector);
            // ... and next to a Nanobot_suppressor: off
            welder.Enabled = true;
            Build("nanobot-suppressor", Site.At(300, 0, 40), true,
                new Part("UpgradeModule/Nanobot_suppressor", 0, 0, 0), BatteryAbove("UpgradeModule/Nanobot_suppressor"));

            var lootAt = collector.PositionComp.GetPosition() + Site.Forward * 20;
            var ore = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Iron"));
            MyFloatingObjects.Spawn(new VRage.Game.Entity.MyPhysicalInventoryItem((MyFixedPoint)100, ore), lootAt, Site.Forward, Site.Up);

            // the custom inhibitor's loop runs every 5 s, the collector's every 2 s, the suppressor every 100 frames
            yield return Seconds(12);
            if (customBeacon.CustomData != config)
            {
                // the loop read the CustomData empty before it was set and wrote its default: set again
                Note("the custom inhibitor's CustomData was its default, set again");
                customBeacon.CustomData = config;
                customBeacon.Radius = 80;
                jumpDrive.Enabled = true;
                yield return Seconds(12);
            }

            Note("enemy nanobot next to a Nanobot_suppressor: " + (welder.Enabled ? "on" : "off"));
            if (welder.Enabled) failures.Add("the Nanobot_suppressor did not switch the enemy nanobot welder off");
            if (jumpDrive.Enabled) failures.Add("the jump drive next to the Custom inhibitor (a repeated line, \\r\\n ends) is still on");

            var taken = (double)Inventory(sorter as VRage.Game.Entity.MyEntity).GetItemAmount(new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Iron"));
            var left = MyEntities.GetEntities().OfType<MyFloatingObject>()
                .Where(f => !f.MarkedForClose && Vector3D.Distance(f.PositionComp.GetPosition(), lootAt) < 50).Sum(f => (double)f.Item.Amount);
            Note("loot collector: " + taken.ToString("F1") + " iron taken, " + left.ToString("F1") + " left loose");
            if (Math.Abs(taken + left - 100) > 0.01) failures.Add("the loot collector made " + (taken + left).ToString("F1") + " iron of 100");
            if (taken < 99.99) failures.Add("the loot collector took " + taken.ToString("F1") + " of the 100 iron");

            // admin protectors: one off, then the other
            var immortal = (HashSet<long>)mod.GetType("AdminProtector.AdminProtectorBlock")?.GetField("ImmortalGrids", Any)?.GetValue(null);
            if (immortal == null) failures.Add("no AdminProtectorBlock.ImmortalGrids in the mod");
            else
            {
                if (!immortal.Contains(guarded.EntityId)) failures.Add("the grid with two admin protectors is not immortal");
                var protectors = guarded.GetFatBlocks().Where(b => b.BlockDefinition.Id.SubtypeName == "AdminGridProtectorLarge").ToList();
                guarded.RazeBlock(protectors[0].Position);
                yield return Seconds(1);
                if (!immortal.Contains(guarded.EntityId)) failures.Add("one admin protector of two taken off: the grid is no more immortal");
                guarded.RazeBlock(protectors[1].Position);
                yield return Seconds(1);
                if (immortal.Contains(guarded.EntityId)) failures.Add("both admin protectors taken off: the grid is still immortal");
            }

            foreach (var failure in failures) Note("FAILED: " + failure);
            Check(failures.Count == 0, string.Join("; ", failures));
            Result("the jump drive switched off by the custom inhibitor; the enemy nanobot left on by a Voxel_Protector, switched off by a suppressor; " +
                   "the loot collector took the 100 iron once; the admin protectors counted");
        }

        private static bool Has(string id)
        {
            var parts = id.Split('/');
            var type = MyObjectBuilderType.Parse("MyObjectBuilder_" + parts[0]);
            return MyDefinitionManager.Static.TryGetCubeBlockDefinition(new MyDefinitionId(type, parts[1]), out _);
        }

        private static Part Beacon(string subtype, float radius) =>
            new Part("Beacon/" + subtype, 0, 0, 0).With(b =>
            {
                var beacon = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_Beacon)b;
                beacon.BroadcastRadius = radius;
                beacon.Enabled = true;
            });

        private static Part Battery(int x, int y, int z) => new Part("BatteryBlock/LargeBlockBatteryBlock", x, y, z).With(b => Motion.Charged(b));

        /// <summary>A battery on top of the block <paramref name="id"/> at the grid's origin (its height in cells up).</summary>
        private static Part BatteryAbove(string id) => Battery(0, Definition(id).Size.Y, 0);

        private static MyCubeBlock Fat(MyCubeGrid grid, string subtype) =>
            grid.GetFatBlocks().First(b => b.BlockDefinition.Id.SubtypeName == subtype);
    }
}
