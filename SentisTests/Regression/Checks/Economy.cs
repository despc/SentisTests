using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A player's store open to anyone: its owner puts up an offer of steel plates from the store's stock and an order
    /// for iron ore (<c>CreateNewOffer</c>, <c>CreateNewOrder</c>); another player buys plates into their suit and sells
    /// ore from it (<c>BuyItem</c>, <c>SellItem</c>). The items move, and the money moves at the prices set, the
    /// owner paid less the game's transaction fee.
    /// </summary>
    public sealed class StoreCheck : RegressionCheck
    {
        public override string Name => Prefix + "store";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableEconomy, "the economy is off in this world");
            var economy = MySession.Static.GetComponent<MySessionComponentEconomy>();
            yield return Arrive(Site.At(-2, 0, -3));
            var buyer = -1;
            yield return Join(Site.At(2, 0, -3), i => buyer = i);
            var buyerIdentity = FakeClients.PlayerOf(buyer).Identity.IdentityId;
            var grid = Build("store", Site.Origin, true,
                new Part("StoreBlock/StoreBlock", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var store = Block<MyStoreBlock>(grid);
            yield return Until(() => store.IsWorking ? 1 : 0, v => v > 0, "the store working", 30, () => State(store));
            Put(Inventory(store), "Component/SteelPlate", 20);
            MyBankingSystem.ChangeBalance(Identity, 1000000);
            MyBankingSystem.ChangeBalance(buyerIdentity, 1000000);

            var plate = Item("Component/SteelPlate");
            var ore = Item("Ore/Iron");
            var platePrice = Math.Max(economy.GetMinimumItemPrice(plate), 1) + 10;
            var orePrice = Math.Max(economy.GetMinimumItemPrice(ore), 1) + 1;
            Request(store, "CreateNewOffer", (SerializableDefinitionId)plate, 10, platePrice, null);
            Request(store, "CreateNewOrder", (SerializableDefinitionId)ore, 100, orePrice);
            var offer = store.PlayerItems.FirstOrDefault(i => i.Item == plate);
            var order = store.PlayerItems.FirstOrDefault(i => i.Item == ore);
            Check(offer != null, "the owner's offer of plates is not in the store (" + store.PlayerItems.Count + " items, the least price " + platePrice + ")");
            Check(order != null, "the owner's order for ore is not in the store");

            // a store that has stood a while: its conveyors worked out (before that, a purchase takes the money and gives
            // nothing - MyGridConveyorSystem.PullItem reports a pull it did not make)
            yield return Until(() => ConveyorsReady(store) ? 1 : 0, v => v > 0, "the store's conveyors worked out", 30);
            var seller0 = MyBankingSystem.GetBalance(Identity);
            var buyer0 = MyBankingSystem.GetBalance(buyerIdentity);
            var suit = Inventory(FakeClients.Character(buyer));
            RequestAs(buyer, store, "BuyItem", offer.Id, 5, FakeClients.Character(buyer).EntityId, 0.0);
            // with the store on its conveyors the plates come by a conveyor request, which may take a moment
            var buyerPlayer = FakeClients.PlayerOf(buyer);
            yield return Until(() => Amount(suit, "Component/SteelPlate"), v => v >= 5, "the 5 plates bought in the buyer's suit", 10,
                () => "balance " + MyBankingSystem.GetBalance(buyerIdentity) + ", the identity's character " +
                      (buyerPlayer.Identity.Character == FakeClients.Character(buyer) ? "theirs" : buyerPlayer.Identity.Character?.EntityId.ToString() ?? "none") +
                      ", relation " + store.GetUserRelationToOwner(buyerIdentity) + ", offer left " + offer.Amount + ", plates in the store " + Amount(Inventory(store), "Component/SteelPlate"));
            Check(Amount(suit, "Component/SteelPlate") == 5, "the buyer has " + Amount(suit, "Component/SteelPlate") + " plates, bought 5");
            Check(Amount(Inventory(store), "Component/SteelPlate") == 15, "the store has " + Amount(Inventory(store), "Component/SteelPlate") + " plates of 15 after selling 5");
            Check(offer.Amount == 5, "the offer has " + offer.Amount + " plates left of 5");
            var paid = buyer0 - MyBankingSystem.GetBalance(buyerIdentity);
            var got = MyBankingSystem.GetBalance(Identity) - seller0;
            var fee = economy.EconomyDefinition.TransactionFee;
            Check(paid == 5L * platePrice, "the buyer paid " + paid + " for 5 plates at " + platePrice);
            Check(Math.Abs(got - 5L * platePrice * (1 - fee)) <= 1, "the owner got " + got + " for 5 plates at " + platePrice + " less the fee " + fee);

            Put(suit, "Ore/Iron", 50);
            var buyer1 = MyBankingSystem.GetBalance(buyerIdentity);
            RequestAs(buyer, store, "SellItem", order.Id, 50, FakeClients.Character(buyer).EntityId, 0.0);
            Check(Amount(suit, "Ore/Iron") == 0, "the seller still has " + Amount(suit, "Ore/Iron") + " kg of the 50 kg of ore sold");
            Check(Amount(Inventory(store), "Ore/Iron") == 50, "the store took in " + Amount(Inventory(store), "Ore/Iron") + " kg of the 50 kg of ore");
            var earned = MyBankingSystem.GetBalance(buyerIdentity) - buyer1;
            Check(earned > 0 && Math.Abs(earned - 50L * orePrice) <= 50L * orePrice * fee + 1, "the player got " + earned + " for 50 kg of ore at " + orePrice);
            yield return null;
            Result("bought 5 plates at " + platePrice + " (paid " + paid + ", the owner got " + got + "), sold 50 kg of ore at " + orePrice + " (got " + earned + ")");
        }
    }

    /// <summary>A player puts space credits into an ATM and takes them out again: the account and the suit trade them one for one.</summary>
    public sealed class AtmCheck : RegressionCheck
    {
        public override string Name => Prefix + "atm";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableEconomy, "the economy is off in this world");
            yield return Arrive(Site.At(0, 0, -3));
            var grid = Build("atm", Site.Origin, true,
                new Part("StoreBlock/AtmBlock", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var atm = Block<MyStoreBlock>(grid);
            yield return Until(() => atm.IsWorking ? 1 : 0, v => v > 0, "the ATM working", 30, () => State(atm));
            var credit = MyBankingSystem.BankingSystemDefinition.PhysicalItemId;
            var suit = Inventory(Body);
            suit.AddItems(1000, MyObjectBuilderSerializer.CreateNewObject(credit));
            var balance0 = MyBankingSystem.GetBalance(Identity);
            Request(atm, "ChangeBalance", 1000, Body.EntityId);
            Check((double)suit.GetItemAmount(credit) == 0, "after putting in 1000 credits the suit still has " + suit.GetItemAmount(credit));
            Check(MyBankingSystem.GetBalance(Identity) - balance0 == 1000, "putting in 1000 credits changed the account by " + (MyBankingSystem.GetBalance(Identity) - balance0));
            Request(atm, "ChangeBalance", -400, Body.EntityId);
            Check((double)suit.GetItemAmount(credit) == 400, "after taking out 400 credits the suit has " + suit.GetItemAmount(credit));
            Check(MyBankingSystem.GetBalance(Identity) - balance0 == 600, "after taking out 400 the account is " + (MyBankingSystem.GetBalance(Identity) - balance0) + " over where it was, not 600");
            yield return null;
            Result("1000 credits into the account through the ATM and 400 back out, one for one");
        }
    }

    /// <summary>
    /// A player's acquisition contract: the owner of a contract block, open to anyone, asks for 10 steel plates for a
    /// reward (<c>CreateCustomContractObtainAndDeliver</c>); another player takes it, brings the plates in their suit
    /// and hands it in (<c>AcceptContract</c>, <c>FinishContract</c>). The plates leave the suit for the cargo container
    /// on the contract block's conveyors, the reward leaves the owner's account for the other player's.
    /// </summary>
    public sealed class ContractCheck : RegressionCheck
    {
        public override string Name => Prefix + "contract";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableEconomy, "the economy is off in this world");
            var contracts = MySession.Static.GetComponent<MySessionComponentContractSystem>();
            SkipUnless(contracts != null, "no contract system in this world");
            yield return Arrive(Site.At(-2, 0, -3));
            var worker = -1;
            yield return Join(Site.At(2, 0, -3), i => worker = i);
            var workerIdentity = FakeClients.PlayerOf(worker).Identity.IdentityId;
            // what is handed in goes onto the block's conveyors: a cargo container on them takes it
            var blockPart = new Part("ContractBlock/ContractBlock", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All);
            Part cargoPart = null;
            yield return ConveyorNeighbour(blockPart, "CargoContainer/LargeBlockSmallContainer", null, p => cargoPart = p);
            var taken = new System.Collections.Generic.HashSet<Vector3I>(Cells(blockPart).Concat(Cells(cargoPart)));
            var free = taken.SelectMany(c => Base6Directions.IntDirections.Select(d => c + d)).First(c => !taken.Contains(c));
            var grid = Build("contract", Site.Origin, true, blockPart, cargoPart,
                new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z).With(b => Motion.Charged(b)));
            var block = Block<MyContractBlock>(grid);
            var cargo = Inventory(Block<MyCargoContainer>(grid));
            yield return Until(() => block.IsWorking ? 1 : 0, v => v > 0, "the contract block working", 10, () => State(block));
            yield return Seconds(1);
            MyBankingSystem.ChangeBalance(Identity, 1000000);
            const int reward = 5000;
            var data = Activator.CreateInstance(typeof(MyContractBlock).GetNestedType("MyContractCreationDataWrapper_ObtainAndDeliver", System.Reflection.BindingFlags.NonPublic)
                                                ?? throw new Core.ScenarioFailedException("MyContractBlock.MyContractCreationDataWrapper_ObtainAndDeliver is not in this game"));
            void Set(string field, object value) => data.GetType().GetField(field).SetValue(data, value);
            Set("RewardMoney", reward);
            Set("StartingDeposit", 0);
            Set("DurationInMin", 30);
            Set("TargetBlockId", block.EntityId);
            Set("ItemTypeId", (SerializableDefinitionId)Item("Component/SteelPlate"));
            Set("ItemAmount", 10);
            var owner0 = MyBankingSystem.GetBalance(Identity);
            Request(block, "CreateCustomContractObtainAndDeliver", data);
            var count = contracts.GetAvailableContractsForBlockCount(block.EntityId);
            Check(count == 1, "the contract was not posted on the block (" + count + " there)");
            var contractId = contracts.GetAvailableContractsForBlock(block.EntityId)[0].Id;
            var escrow = owner0 - MyBankingSystem.GetBalance(Identity);
            Check(escrow >= reward, "posting the contract took " + escrow + " from the owner, the reward is " + reward);

            var worker0 = MyBankingSystem.GetBalance(workerIdentity);
            RequestAs(worker, block, "AcceptContract", workerIdentity, contractId);
            Check(contracts.IsContractActive(contractId), "the contract is not active after the player took it");
            var suit = Inventory(FakeClients.Character(worker));
            Put(suit, "Component/SteelPlate", 10);
            // on a base just built the first hand-in only sets the conveyors to work out where the plates go
            // (MyGridConveyorSystem.PushGenerateItem) and fails "not enough space": the player presses it again
            var tries = 0;
            var last = DateTime.MinValue;
            yield return Until(() =>
            {
                if (!contracts.IsContractActive(contractId)) return 1;
                if ((DateTime.UtcNow - last).TotalSeconds >= 1)
                {
                    last = DateTime.UtcNow;
                    tries++;
                    RequestAs(worker, block, "FinishContract", workerIdentity, contractId, FakeClients.Character(worker).EntityId);
                }
                return 0;
            }, v => v > 0, "the contract handed in", 15);
            if (contracts.IsContractActive(contractId))
            {
                var contract = contracts.GetType().GetMethod("GetActiveContractById", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(contracts, new object[] { contractId });
                var condition = contract?.GetType().GetProperty("ContractCondition")?.GetValue(contract);
                object Read(object o, string name) => o?.GetType().GetProperty(name)?.GetValue(o) ?? o?.GetType().GetField(name)?.GetValue(o);
                var controller = FakeClients.Character(worker).ControllerInfo?.Controller?.Player;
                Check(false, "the contract is still active after the plates were handed in: condition " + condition?.GetType().Name + ", finished " + Read(condition, "IsFinished") +
                             ", its end block " + Read(condition, "BlockEndId") + " (the block " + block.EntityId + "), owners " +
                             string.Join(",", (Read(contract, "Owners") as System.Collections.IEnumerable)?.Cast<object>() ?? new object[0]) + " (the player " + workerIdentity +
                             "), the character's controller " + (controller == null ? "none" : controller.Id.SteamId + " / " + FakeClients.PlayerOf(worker).Id.SteamId) +
                             ", plates in the suit " + Amount(suit, "Component/SteelPlate") + ", can be finished " + Read(contract, "CanBeFinished"));
            }
            Check(Amount(suit, "Component/SteelPlate") == 0, "the player still has " + Amount(suit, "Component/SteelPlate") + " of the 10 plates handed in");
            Check(Amount(cargo, "Component/SteelPlate") == 10, "the cargo on the contract block's conveyors has " + Amount(cargo, "Component/SteelPlate") + " of the 10 plates");
            var paid = MyBankingSystem.GetBalance(workerIdentity) - worker0;
            Check(paid == reward, "the player got " + paid + " for the contract, its reward is " + reward);
            yield return null;
            Result("contract for 10 plates posted (" + escrow + " held from the owner), taken, handed in (at the " + tries + ". try): the plates in the cargo on the block's conveyors, " + paid + " to the player");
        }
    }
}
