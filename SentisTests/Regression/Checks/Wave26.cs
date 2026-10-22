using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A player's suit holds what the character's definition says (times the world's inventory multiplier, where the
    /// game applies it), and no more: ore put in beyond that is turned away.
    /// </summary>
    public sealed class CharacterInventoryCheck : RegressionCheck
    {
        public override string Name => Prefix + "character_inventory";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -4));
            var suit = Inventory(Body);
            var definition = Body.Definition.InventoryDefinition;
            var volume = (double)suit.MaxVolume;
            var byDefinition = definition.InventoryVolume;
            var multiplied = byDefinition * MySession.Static.Settings.InventorySizeMultiplier;
            Check(Math.Abs(volume - byDefinition) <= byDefinition * 0.01 || Math.Abs(volume - multiplied) <= multiplied * 0.01,
                "the suit holds " + F(volume * 1000) + " L; its definition says " + F(byDefinition * 1000) + " L, times the world's " +
                MySession.Static.Settings.InventorySizeMultiplier + " " + F(multiplied * 1000) + " L");
            suit.Clear();
            var ore = Item("Ore/Stone");
            var fits = (double)suit.ComputeAmountThatFits(ore);
            Check(fits > 0, "nothing fits into an empty suit");
            suit.AddItems((MyFixedPoint)(fits * 2), MyObjectBuilderSerializer.CreateNewObject(ore));
            var held = Amount(suit, "Ore/Stone");
            Check(held <= fits * 1.001, "the suit took " + F(held) + " kg of stone, " + F(fits) + " kg fit");
            Check(suit.CurrentVolume <= suit.MaxVolume, "the suit holds more than its volume");
            suit.Clear();
            yield return null;
            Result("suit of " + F(volume * 1000) + " L: " + F(fits) + " kg of stone fit, twice that " + (held > 0 ? "was cut to " + F(held) : "was turned away"));
        }
    }

    /// <summary>
    /// A vending machine sells what its definition offers (<c>DefaultItems</c>): a player buys one item
    /// (<c>BuyItem</c>, as from its screen), the price leaves their account and the item comes out of the machine.
    /// </summary>
    public sealed class VendingMachineCheck : RegressionCheck
    {
        public override string Name => Prefix + "vending_machine";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableEconomy, "the economy is off in this world");
            var definition = Sandbox.Definitions.MyDefinitionManager.Static.GetAllDefinitions().OfType<Sandbox.Definitions.MyVendingMachineDefinition>()
                .FirstOrDefault(d => d.Public && d.CubeSize == MyCubeSize.Large && d.DefaultItems != null && d.DefaultItems.Count > 0);
            SkipUnless(definition != null, "no vending machine with offers in this game");
            yield return Arrive(Site.At(0, 0, -3));
            var grid = Build("vending", Site.Origin, true, new[]
            {
                new Part(definition.Id.TypeId.ToString().Replace("MyObjectBuilder_", "") + "/" + definition.Id.SubtypeName, 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -3, 0).With(b => Motion.Charged(b)),
            });
            var machine = Block<MyVendingMachine>(grid);
            Check(machine != null, "no vending machine built of " + definition.Id);
            yield return Until(() => machine.IsWorking ? 1 : 0, v => v > 0, "the vending machine working", 30, () => State(machine));
            var offer = machine.PlayerItems.FirstOrDefault(i => i.StoreItemType == StoreItemTypes.Offer && i.Item.HasValue);
            Check(offer != null, "the vending machine offers nothing (" + machine.PlayerItems.Count + " items)");
            var item = offer.Item.Value;
            MyBankingSystem.ChangeBalance(Identity, 100000);
            var balance = MyBankingSystem.GetBalance(Identity);
            var near = machine.PositionComp.GetPosition();
            Func<double> got = () => Amount(Inventory(Body), item.TypeId.ToString().Replace("MyObjectBuilder_", "") + "/" + item.SubtypeName) +
                                     MyEntities.GetEntities().OfType<MyFloatingObject>().Where(o => !o.MarkedForClose && Vector3D.Distance(o.PositionComp.GetPosition(), near) < 10 &&
                                                                                                    o.Item.Content.GetObjectId() == item).Sum(o => (double)o.Item.Amount);
            var before = got();
            Request(machine, "BuyItem", offer.Id, 1, Body.EntityId, 0.0);
            yield return Until(got, v => v > before, "the bought " + item.SubtypeName + " out of the machine", 10,
                () => "balance " + MyBankingSystem.GetBalance(Identity) + " of " + balance);
            foreach (var floating in MyEntities.GetEntities().OfType<MyFloatingObject>().Where(o => Vector3D.Distance(o.PositionComp.GetPosition(), near) < 10).ToList())
                Track(floating);
            var paid = balance - MyBankingSystem.GetBalance(Identity);
            Check(paid == offer.PricePerUnit, "the player paid " + paid + ", the price is " + offer.PricePerUnit);
            Result("bought a " + item.SubtypeName + " at " + offer.PricePerUnit + " from " + definition.Id.SubtypeName + ", paid " + paid);
        }
    }

    /// <summary>
    /// Research (the world's progression): with the player's research reset, a block of the tree is locked for them
    /// (<c>CanUse</c> false); welding by hand a block that unlocks it (<c>MyCubeGrids.BlockFunctional</c>, hand
    /// welded) researches it, and then it is placed. The player's research is given back whole after.
    /// </summary>
    public sealed class ResearchCheck : HandCheck
    {
        public override string Name => Prefix + "research";
        public override int TimeoutSeconds => 240;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.ResearchEnabled, "research is off in this world");
            var research = MySessionComponentResearch.Static;
            var unlocks = typeof(MySessionComponentResearch).GetField("m_unlocks", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(research)
                as Dictionary<MyDefinitionId, List<MyDefinitionId>>;
            Check(unlocks != null, "MySessionComponentResearch has no m_unlocks");
            Sandbox.Definitions.MyCubeBlockDefinition Cube(MyDefinitionId id) =>
                Sandbox.Definitions.MyDefinitionManager.Static.GetCubeBlockDefinition(id);
            bool Simple(Sandbox.Definitions.MyCubeBlockDefinition d) =>
                d != null && d.Public && d.AvailableInSurvival && d.CubeSize == MyCubeSize.Large && d.Size == Vector3I.One;
            // a block needing no research that unlocks a one-cell block needing it: the basic assembler (it unlocks the
            // batteries) when it is in the tree, as a new player welds it first
            bool Weldable(Sandbox.Definitions.MyCubeBlockDefinition d) => d != null && d.Public && d.AvailableInSurvival && d.CubeSize == MyCubeSize.Large;
            var pair = unlocks.Where(p => !research.RequiresResearch(p.Key) && Weldable(Cube(p.Key)))
                .SelectMany(p => p.Value.Where(u => research.RequiresResearch(u) && Simple(Cube(u))).Select(u => (Key: p.Key, Unlocked: u)))
                .OrderBy(p => p.Key.SubtypeName == "BasicAssembler" ? 0 : 1).ThenBy(p => Cube(p.Key).Components.Sum(c => c.Count)).FirstOrDefault();
            SkipUnless(pair.Key != default(MyDefinitionId), "no one-cell block of the research tree unlocks another one-cell block");
            var key = Cube(pair.Key);
            var locked = Cube(pair.Unlocked);
            yield return Arrive(Site.At(0, 0, -4));
            research.ResetResearch(Identity);
            Check(!research.CanUse(Identity, locked.Id), "after the reset the player can still use " + locked.Id.SubtypeName);

            var floor = Build("research-floor", Site.At(0, -3, 6), true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            var cell = new Vector3I(0, 1, 0);
            foreach (var component in locked.Components) Put(Inventory(Body), "Component/" + component.Definition.Id.SubtypeName, component.Count);
            var request = typeof(MyCubeGrid).GetMethod("BuildBlocksRequest", Placing.Any);
            void Place()
            {
                var location = new MyCubeGrid.MyBlockLocation(locked.Id, cell, cell, cell, Quaternion.Identity, 0, Identity);
                AsMe(() => request.Invoke(floor, new object[] { Placing.Visuals, new HashSet<MyCubeGrid.MyBlockLocation> { location }, Body.EntityId, false, Identity }));
            }

            // the key block, a frame of the player's, welded whole by hand
            var keyGrid = Build("research-key", Site.Origin, true, new Part(key.Id.TypeId.ToString().Replace("MyObjectBuilder_", "") + "/" + key.Id.SubtypeName, 0, 0, 0).At(0.05f));
            var keyBlock = keyGrid.GetCubeBlock(Vector3I.Zero);
            foreach (var component in key.Components) Put(Inventory(Body), "Component/" + component.Definition.Id.SubtypeName, component.Count * 2);
            yield return Equip("WelderItem");
            yield return AimAt(keyBlock);
            yield return Pull();
            yield return Until(() => keyBlock.Integrity / keyBlock.MaxIntegrity, v => v >= 0.999, key.Id.SubtypeName + " welded by hand", 150);
            Release();
            yield return Until(() => research.CanUse(Identity, locked.Id) ? 1 : 0, v => v > 0, locked.Id.SubtypeName + " researched by welding " + key.Id.SubtypeName, 10);
            Place();
            yield return Until(() => floor.GetCubeBlock(cell) != null ? 1 : 0, v => v > 0, locked.Id.SubtypeName + " placed once researched", 10);
            Result(locked.Id.SubtypeName + ": locked after the reset; welding " + key.Id.SubtypeName + " by hand researched it, then it was placed");
        }

        public override void Cleanup()
        {
            try { if (Me >= 0) WorldApi.UnlockResearch(Identity); } catch { }
            base.Cleanup();
        }
    }

    /// <summary>
    /// The game's trash removal (<c>MySessionComponentTrash</c>, with the world's trash settings): a small grid of
    /// armor, still and unpowered, far from every player (past <c>PlayerDistanceThreshold</c>) is removed; the same
    /// grid by the player is kept (<c>GetTrashState</c>: <c>DistanceFromPlayer</c>).
    /// </summary>
    public sealed class TrashRemovalCheck : RegressionCheck
    {
        public override string Name => Prefix + "trash_removal";
        public override int TimeoutSeconds => 180;

        protected override IEnumerator Check()
        {
            var settings = MySession.Static.Settings;
            SkipUnless(settings.TrashRemovalEnabled, "trash removal is off in this world");
            SkipUnless(settings.TrashFlags.HasFlag(MyTrashRemovalFlags.Stationary) && settings.TrashFlags.HasFlag(MyTrashRemovalFlags.DistanceFromPlayer),
                "the world's trash removal takes no still grids by distance (" + settings.TrashFlags + ")");
            SkipUnless(settings.BlockCountThreshold > 2, "the world's block count threshold keeps a grid of 2 blocks");
            var trash = MySession.Static.GetComponent<MySessionComponentTrash>();
            yield return Arrive(Site.At(0, 0, -6));
            MyCubeGrid Junk(string name, Vector3D at) =>
                Build(name, at, false, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            var near = Junk("trash-near", Site.Origin);
            // past the distance from every player online
            var away = Site.Origin + Site.Up * (settings.PlayerDistanceThreshold + 20000);
            var players = MySession.Static.Players.GetOnlinePlayers().Where(p => p.Character != null).Select(p => p.Character.PositionComp.GetPosition()).ToList();
            var closest = players.Count == 0 ? double.MaxValue : players.Min(p => Vector3D.Distance(p, away));
            SkipUnless(closest > settings.PlayerDistanceThreshold + 1000, "no spot past the trash distance from every player (the closest " + F(closest) + " m)");
            var far = Junk("trash-far", away);
            var farId = far.EntityId;
            yield return Seconds(1);
            var nearState = trash.GetTrashState(near);
            Check(nearState != MyTrashRemovalFlags.None, "the grid by the player is trash for the game");
            yield return Until(() => MyEntities.EntityExists(farId) ? 0 : 1, v => v > 0, "the far grid removed by the trash removal", 150,
                () => "its trash state " + (MyEntities.GetEntityById(farId) is MyCubeGrid g ? trash.GetTrashState(g).ToString() : "gone"));
            Check(!near.MarkedForClose && MyEntities.EntityExists(near.EntityId), "the grid by the player was removed too");
            Result("a still 2-block grid " + F(closest / 1000) + " km from the players removed; the same by the player kept (" + nearState + ")");
        }
    }
}
