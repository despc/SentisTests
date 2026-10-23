using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Contracts;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage.Game.Entity;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A station's bounty on a pirate ship (<c>MyContractPvEBounty</c>): taken, the pirates' ship comes out at the target
    /// point once the player is in view of it; its AI blocks destroyed (or, without them, its power), the target counts as
    /// killed - the contract finishes and pays.
    /// </summary>
    public sealed class ContractPvEBountyCheck : NpcStationContractCheck
    {
        public override string Name => Prefix + "contract_pve_bounty";
        public override int TimeoutSeconds => 120;
        protected override bool NeedsDamage => true;

        private readonly List<long> _targets = new List<long>();

        protected override IEnumerator Check()
        {
            var group = MyDefinitionManager.Static.GetSpawnGroupDefinitions().FirstOrDefault(g => g.Id.SubtypeName == "RE09_PirateCutter")
                        ?? MyDefinitionManager.Static.GetSpawnGroupDefinitions().FirstOrDefault(g => g.Id.SubtypeName.StartsWith("RE") && g.Prefabs.Count > 0);
            SkipUnless(group != null, "no pirate spawn group for a bounty in this game");
            var economy = MySession.Static.GetComponent<MySessionComponentEconomy>();
            var pirateTag = economy == null ? null : (MyDefinitionManager.Static.GetDefinition(economy.EconomyDefinition.PirateId) as MyFactionDefinition)?.Tag;
            var pirates = MySession.Static.Factions.Select(f => f.Value).FirstOrDefault(f => f.Tag == pirateTag);
            SkipUnless(pirates != null, "no pirate faction in this world");
            yield return Arrive(Site.At(0, 0, -3));
            yield return Setup(Site.Origin);
            var target = Site.At(0, 0, 600);
            Generate(new Sandbox.ModAPI.Contracts.MyContractPvEBounty(0, Reward, 0, 30, pirates.FactionId, target, group.Id));
            var money0 = MyBankingSystem.GetBalance(Identity);
            Request(Block, "AcceptContract", Identity, ContractId);
            Check(Contracts.IsContractActive(ContractId), "the bounty is not active after the player took it");
            var contract = Active();
            var ids = typeof(MyContractWithSpawnableGrid).GetField("m_gridEntityIds", Any);
            List<MyCubeGrid> Targets() => ((List<long>)ids.GetValue(contract)).Select(id => MyEntities.GetEntityById(id) as MyCubeGrid).Where(g => g != null && !g.MarkedForClose).ToList();
            yield return Until(() => Targets().Count, v => v > 0, "the pirate ship of the bounty spawned", 30);
            var ships = Targets();
            _targets.AddRange(ships.Select(g => g.EntityId));
            var spawned = ships.Min(g => Vector3D.Distance(g.PositionComp.GetPosition(), target));
            yield return Seconds(1);
            // what makes it a target: its AI blocks, or its power when it has none
            var ai = ships.SelectMany(g => g.GetFatBlocks()).OfType<MyFunctionalBlock>()
                .Where(b => b is Sandbox.ModAPI.Ingame.IMyRemoteControl || b.GetType().Name.Contains("Combat") || b.GetType().Name.Contains("FlightMovement") ||
                            b.GetType().Name.Contains("PathRecorder")).Cast<MyCubeBlock>().ToList();
            var victims = ai.Count > 0 ? ai : ships.SelectMany(g => g.GetFatBlocks()).Where(b => b.Components.Get<MyResourceSourceComponent>() != null).ToList();
            foreach (var block in victims) block.SlimBlock.DoDamage(block.SlimBlock.MaxIntegrity * 100, MyDamageType.Bullet, true, null, Body.EntityId);
            yield return Until(() => Contracts.IsContractActive(ContractId) ? 0 : 1, v => v > 0,
                "the bounty finished with the pirate ship's " + (ai.Count > 0 ? "AI" : "power") + " destroyed", 15);
            var paid = MyBankingSystem.GetBalance(Identity) - money0;
            Check(paid > 0, "the player got nothing for the bounty");
            Result("the bounty taken: " + group.Id.SubtypeName + " came out " + F(spawned) + " m from the target point; " + victims.Count + " of its " +
                   (ai.Count > 0 ? "AI" : "power") + " blocks destroyed, " + paid + " paid");
        }

        public override void Cleanup()
        {
            try
            {
                foreach (var id in _targets)
                    if (MyEntities.GetEntityById(id) is MyCubeGrid grid && !grid.MarkedForClose) grid.Close();
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// A station's grid hauling contract (<c>MyContractGridHauling</c>, <c>MyContractConditionDeliverGrid</c>): a cargo
    /// ship of the player's to bring to another station of the faction; taken, the ship brought into the end station's zone
    /// (<c>SAFEZONE_SIZE</c>) and the contract handed in at the end station's contract block - it finishes and pays. The
    /// game picks the end station at random among the friendly ones of the world: it is set to the check's second station.
    /// </summary>
    public sealed class ContractGridHaulingCheck : NpcStationContractCheck
    {
        public override string Name => Prefix + "contract_grid_hauling";
        public override int TimeoutSeconds => 120;

        private MyFactionStation _end;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            yield return Setup(Site.Origin);
            var endGrid = BuildFor(Faction.FounderId, "hauling-end-station", Site.At(0, 0, 800),
                new Part("ContractBlock/ContractBlock", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var endBlock = Block<Sandbox.Game.Entities.Blocks.MyContractBlock>(endGrid);
            _end = new MyFactionStation(VRage.MyEntityIdentifier.AllocateId(VRage.MyEntityIdentifier.ID_OBJECT_TYPE.STATION), "Regression hauling end", endGrid.PositionComp.GetPosition(),
                VRage.Game.MyStationTypeEnum.SpaceStation, Faction, "", null) { StationEntityId = endGrid.EntityId };
            Faction.AddStation(_end);
            var cargo = Build("hauling-cargo", Site.At(0, 0, 40), false, new[]
            {
                new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0),
            });
            // the ship hauled is the player's while hauled: the hand-in takes a grid of the sender's (CheckConnectedEntityOwnership)
            Generate(new Sandbox.ModAPI.Contracts.MyContractGridHauling(0, Reward, 0, 30, cargo.EntityId, 0));
            var contract = Contracts.InactiveContracts.Concat(Contracts.ActiveContracts).FirstOrDefault(c => c.Id == ContractId) as MyContract
                           ?? (MyContract)typeof(MySessionComponentContractSystem).GetMethod("GetContract", Any)?.Invoke(Contracts, new object[] { ContractId });
            var all = typeof(MySessionComponentContractSystem).GetFields(Any).Select(f => f.GetValue(Contracts)).OfType<Dictionary<long, MyContract>>().SelectMany(d => d.Values);
            contract = contract ?? all.FirstOrDefault(c => c.Id == ContractId);
            Check(contract?.ContractCondition != null, "the hauling contract has no delivery condition");
            var condition = contract.ContractCondition;
            typeof(MyContractCondition).GetProperty("StationEndId").SetValue(condition, _end.Id);
            typeof(MyContractCondition).GetProperty("FactionEndId").SetValue(condition, Faction.FactionId);
            var money0 = MyBankingSystem.GetBalance(Identity);
            Request(Block, "AcceptContract", Identity, ContractId);
            Check(Contracts.IsContractActive(ContractId), "the hauling contract is not active after the player took it");
            yield return Seconds(1);
            Check(Contracts.IsContractActive(ContractId), "the hauling contract failed before the ship moved");
            // brought to the end station, as the player flies it there
            var to = cargo.WorldMatrix;
            to.Translation = endGrid.PositionComp.GetPosition() + Site.Right * 40;
            cargo.Teleport(to);
            FakeClients.MoveTo(Me, endGrid.PositionComp.GetPosition() + Site.Up * 4);
            yield return Seconds(1);
            var last = DateTime.MinValue;
            yield return Until(() =>
            {
                if (!Contracts.IsContractActive(ContractId)) return 1;
                if ((DateTime.UtcNow - last).TotalSeconds >= 1)
                {
                    last = DateTime.UtcNow;
                    Request(endBlock, "FinishContract", Identity, ContractId, cargo.EntityId);
                }
                return 0;
            }, v => v > 0, "the hauling handed in at the end station", 15);
            var paid = MyBankingSystem.GetBalance(Identity) - money0;
            Check(paid > 0, "the player got nothing for the grid hauled");
            Result("the faction's cargo ship hauled 800 m to its other station and handed in there: " + paid + " paid");
        }

        public override void Cleanup()
        {
            try
            {
                if (_end != null && typeof(MyFaction).GetField("m_stations", Any)?.GetValue(Faction) is Dictionary<long, VRage.Game.ModAPI.IMyFactionStation> stations) stations.Remove(_end.Id);
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// The suit's look as the wardrobe sets it (<c>MyCharacter.ChangeModel_Implementation</c>): a new color and the other
    /// body model - the character takes both and its save has them.
    /// </summary>
    public sealed class SuitAppearanceCheck : RegressionCheck
    {
        public override string Name => Prefix + "suit_appearance";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            var body = Body;
            var model = body.ModelName.Contains("Female") ? "Default_Astronaut" : "Default_Astronaut_Female";
            SkipUnless(MyDefinitionManager.Static.Characters.Any(c => c.Name == model), "no character " + model + " in this game");
            var color = new Vector3(0.3f, 0.4f, 0.2f);
            Request(body, "ChangeModel_Implementation", model, color, false);
            yield return WaitForTicks(5);
            body = Body;
            var saved = (MyObjectBuilder_Character)body.GetObjectBuilder();
            Check(saved.CharacterModel == model, "the character is " + saved.CharacterModel + ", the wardrobe asked " + model);
            Check(Vector3.Distance(saved.ColorMaskHSV, color) < 0.01f, "the suit's color is " + (Vector3)saved.ColorMaskHSV + ", the wardrobe asked " + color);
            Result("the wardrobe's model (" + model + ") and color taken by the character and saved");
        }
    }

    /// <summary>
    /// A station's salvage contract (<c>MyContractSalvage</c>): taken, the wreck comes out once the player is in view of
    /// it, with the item to salvage in one of its inventories; the player takes the item and hands it in at the station's
    /// block - the contract finishes and pays.
    /// </summary>
    public sealed class ContractSalvageCheck : NpcStationContractCheck
    {
        public override string Name => Prefix + "contract_salvage";
        public override int TimeoutSeconds => 120;

        private readonly List<long> _wrecks = new List<long>();

        protected override IEnumerator Check()
        {
            var group = MyDefinitionManager.Static.GetSpawnGroupDefinitions().FirstOrDefault(g => g.Id.SubtypeName == "Salvage_Space_EscapePod" && g.Prefabs.Count > 0)
                        ?? MyDefinitionManager.Static.GetSpawnGroupDefinitions().FirstOrDefault(g => g.Id.SubtypeName.StartsWith("Salvage_Space") && g.Prefabs.Count > 0);
            SkipUnless(group != null, "no salvage wreck in this game");
            yield return Arrive(Site.At(0, 0, -3));
            yield return Setup(Site.Origin);
            var at = Site.At(0, 0, 300);
            Generate(new Sandbox.ModAPI.Contracts.MyContractSalvage(0, Reward, 0, 30, at, group.Prefabs[0].SubtypeId));
            var money0 = MyBankingSystem.GetBalance(Identity);
            Request(Block, "AcceptContract", Identity, ContractId);
            Check(Contracts.IsContractActive(ContractId), "the salvage is not active after the player took it");
            var contract = Active();
            var ids = typeof(MyContractWithSpawnableGrid).GetField("m_gridEntityIds", Any);
            List<MyCubeGrid> Wrecks() => ((List<long>)ids.GetValue(contract)).Select(id => MyEntities.GetEntityById(id) as MyCubeGrid).Where(g => g != null && !g.MarkedForClose).ToList();
            yield return Until(() => Wrecks().Count, v => v > 0, "the wreck of the salvage spawned", 30);
            _wrecks.AddRange(Wrecks().Select(g => g.EntityId));
            var condition = contract.ContractCondition as MyContractConditionDeliverItems;
            Check(condition != null, "the salvage has no delivery of an item");
            var item = condition.ItemType;
            var holder = Wrecks().SelectMany(g => g.GetFatBlocks()).Where(b => b.HasInventory)
                .SelectMany(b => Enumerable.Range(0, b.InventoryCount).Select(i => (Sandbox.Game.MyInventory)b.GetInventory(i)))
                .FirstOrDefault(i => i.GetItemAmount(item) > 0);
            Check(holder != null, "no inventory of the wreck holds the item to salvage (" + item.SubtypeName + ")");
            var found = holder.GetItems().First(i => i.Content.GetId() == item);
            // a transfer from a block is the player's at hand: by the wreck's block, then back to the station to hand it in
            var holderBlock = (MyEntity)holder.Owner;
            FakeClients.MoveTo(Me, holderBlock.PositionComp.GetPosition() + Site.Up * 3);
            yield return Seconds(0.5);
            Request(holder, "InventoryTransferItem_Implementation", (VRage.MyFixedPoint)1, found.ItemId, Body.EntityId, (byte)0, -1);
            Check(Inventory(Body).GetItemAmount(item) >= 1, "the item did not come into the player's suit");
            FakeClients.MoveTo(Me, Site.At(0, 0, -3));
            yield return Seconds(0.5);
            var last = DateTime.MinValue;
            yield return Until(() =>
            {
                if (!Contracts.IsContractActive(ContractId)) return 1;
                if ((DateTime.UtcNow - last).TotalSeconds >= 1)
                {
                    last = DateTime.UtcNow;
                    Request(Block, "FinishContract", Identity, ContractId, Body.EntityId);
                }
                return 0;
            }, v => v > 0, "the salvage handed in at the station", 15);
            var paid = MyBankingSystem.GetBalance(Identity) - money0;
            Check(paid > 0, "the player got nothing for the salvage");
            Result("the salvage taken: " + group.Prefabs[0].SubtypeId + " came out with " + item.SubtypeName + " aboard; taken and handed in, " + paid + " paid");
        }

        public override void Cleanup()
        {
            try
            {
                foreach (var id in _wrecks)
                    if (MyEntities.GetEntityById(id) is MyCubeGrid grid && !grid.MarkedForClose) grid.Close();
            }
            finally { base.Cleanup(); }
        }
    }
}
