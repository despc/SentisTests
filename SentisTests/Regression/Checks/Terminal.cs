using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// What a player does in a grid's terminal, each as the client asks for it: renames a block and the grid, switches a
    /// light off and on (it goes dark and lights up again), changes its radius, shares the block with all and back, and
    /// makes a group of blocks.
    /// </summary>
    public sealed class TerminalCheck : RegressionCheck
    {
        public override string Name => Prefix + "terminal";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var grid = Build("terminal", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f),
                new Part("InteriorLight/SmallLight", 1, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 2, 0, 0));
            var light = Block<MyLightingBlock>(grid);
            var cargo = Block<MyCargoContainer>(grid);
            yield return Until(() => light.IsWorking ? 1 : 0, v => v > 0, "the light working", 10);

            Request(light, "SetCustomNameEvent", "Regression light");
            Check(light.CustomName.ToString() == "Regression light", "the light is called '" + light.CustomName + "' after renaming");
            Request(grid, "OnChangeDisplayNameRequest", "Regression grid");
            Check(grid.DisplayName == "Regression grid", "the grid is called '" + grid.DisplayName + "' after renaming");

            ((Sandbox.ModAPI.IMyTerminalBlock)light).SetValueBool("OnOff", false);
            yield return Until(() => light.IsWorking ? 1 : 0, v => v <= 0, "the light dark once switched off", 10);
            ((Sandbox.ModAPI.IMyTerminalBlock)light).SetValueBool("OnOff", true);
            yield return Until(() => light.IsWorking ? 1 : 0, v => v > 0, "the light lit once switched on", 10);
            ((Sandbox.ModAPI.IMyTerminalBlock)light).SetValueFloat("Radius", 7.5f);
            yield return Until(() => ((Sandbox.ModAPI.Ingame.IMyLightingBlock)light).Radius, v => System.Math.Abs(v - 7.5) < 0.01, "the light's radius", 5);

            var share = new List<MyCubeGrid.MySingleOwnershipRequest> { new MyCubeGrid.MySingleOwnershipRequest { BlockId = cargo.EntityId, Owner = Identity } };
            Request(typeof(MyCubeGrid), "OnChangeShareRequest", MyOwnershipShareModeEnum.All, share);
            yield return Until(() => cargo.IDModule.ShareMode == MyOwnershipShareModeEnum.All ? 1 : 0, v => v > 0, "the cargo shared with all", 5);
            share = new List<MyCubeGrid.MySingleOwnershipRequest> { new MyCubeGrid.MySingleOwnershipRequest { BlockId = cargo.EntityId, Owner = Identity } };
            Request(typeof(MyCubeGrid), "OnChangeShareRequest", MyOwnershipShareModeEnum.None, share);
            yield return Until(() => cargo.IDModule.ShareMode == MyOwnershipShareModeEnum.None ? 1 : 0, v => v > 0, "the cargo shared with nobody", 5);

            Request(grid, "OnModifyGroupSuccess", "Regression group", new List<long> { light.EntityId, cargo.EntityId });
            var terminal = Sandbox.ModAPI.MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
            var group = terminal.GetBlockGroupWithName("Regression group");
            var members = new List<Sandbox.ModAPI.Ingame.IMyTerminalBlock>();
            group?.GetBlocks(members);
            Check(group != null && members.Count == 2, "the group is " + (group == null ? "not there" : "of " + members.Count + " blocks"));
            Request(grid, "OnModifyGroupSuccess", "Regression group", new List<long>());
            Check(terminal.GetBlockGroupWithName("Regression group") == null, "the group was not removed");
            Result("renamed block and grid, light off/on and radius, shared with all and back, group made and removed");
        }
    }

    /// <summary>
    /// A player moves items between a cargo container and their own inventory, as the inventory screen asks for it
    /// (<c>MyInventory.InventoryTransferItem</c>): what leaves one arrives in the other, no more, no less.
    /// </summary>
    public sealed class InventoryTransferCheck : RegressionCheck
    {
        public override string Name => Prefix + "inventory_transfer";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 3, -3));
            var grid = Build("inventory", Site.Origin, true, new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0));
            var cargo = Inventory(Block<MyCargoContainer>(grid));
            var pocket = Inventory(Body);
            Put(cargo, "Component/SteelPlate", 100);
            Put(cargo, "Ore/Iron", 50.5);
            yield return null;

            var plates = cargo.GetItems().First(i => i.Content.SubtypeName == "SteelPlate");
            Request(cargo, "InventoryTransferItem_Implementation", (MyFixedPoint)40, plates.ItemId, Body.EntityId, (byte)0, -1);
            Check(Amount(cargo, "Component/SteelPlate") == 60 && Amount(pocket, "Component/SteelPlate") == 40,
                "40 plates moved: the cargo has " + Amount(cargo, "Component/SteelPlate") + ", the player " + Amount(pocket, "Component/SteelPlate"));
            var ore = cargo.GetItems().First(i => i.Content.SubtypeName == "Iron");
            Request(cargo, "InventoryTransferItem_Implementation", (MyFixedPoint)20.25, ore.ItemId, Body.EntityId, (byte)0, -1);
            Check(System.Math.Abs(Amount(cargo, "Ore/Iron") - 30.25) < 1e-4 && System.Math.Abs(Amount(pocket, "Ore/Iron") - 20.25) < 1e-4,
                "20.25 kg of ore moved: the cargo has " + Amount(cargo, "Ore/Iron") + ", the player " + Amount(pocket, "Ore/Iron"));

            var back = pocket.GetItems().First(i => i.Content.SubtypeName == "SteelPlate");
            Request(pocket, "InventoryTransferItem_Implementation", (MyFixedPoint)15, back.ItemId, grid.GetFatBlocks().First().EntityId, (byte)0, -1);
            Check(Amount(cargo, "Component/SteelPlate") == 75 && Amount(pocket, "Component/SteelPlate") == 25,
                "15 plates back: the cargo has " + Amount(cargo, "Component/SteelPlate") + ", the player " + Amount(pocket, "Component/SteelPlate"));
            Result("plates and ore moved both ways, amounts exact");
        }
    }
}
