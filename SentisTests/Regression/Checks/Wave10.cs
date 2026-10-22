using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Trade with an NPC station: a store of an NPC trader faction's, made a station of the faction for the check (the
    /// world's own stations are left as they are), with an offer of plates and an order for ore. The player buys and
    /// sells by the store block (<c>BuyItem</c>, <c>SellItem</c>): the game takes the station's own list, the money goes
    /// to and from the faction's account. After, the station is taken off the faction, the faction's money set back.
    /// </summary>
    public sealed class NpcStationCheck : RegressionCheck
    {
        public override string Name => Prefix + "npc_station";

        private MyFaction _faction;
        private MyFactionStation _station;
        private long _factionMoney;

        private static readonly System.Reflection.FieldInfo Stations =
            typeof(MyFaction).GetField("m_stations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableEconomy, "the economy is off in this world");
            var economy = MySession.Static.GetComponent<MySessionComponentEconomy>();
            _faction = MySession.Static.Factions.Select(f => f.Value).OfType<MyFaction>()
                .FirstOrDefault(f => f.FactionTypeString == "Trader" && f.Stations.Count > 0 && MySession.Static.Players.IdentityIsNpc(f.FounderId));
            SkipUnless(_faction != null, "no NPC trader faction in this world");
            yield return Arrive(Site.At(0, 0, -3));
            MyBankingSystem.ChangeBalance(Identity, 1000000);
            var grid = BuildFor(_faction.FounderId, "npc-station", Site.Origin,
                new Part("StoreBlock/StoreBlock", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var store = Block<MyStoreBlock>(grid);
            yield return Until(() => store.IsWorking ? 1 : 0, v => v > 0, "the store working", 30, () => State(store));

            var plate = Item("Component/SteelPlate");
            var ore = Item("Ore/Iron");
            _station = new MyFactionStation(MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.STATION), "Regression trade post", grid.PositionComp.GetPosition(),
                MyStationTypeEnum.SpaceStation, _faction, "", null) { StationEntityId = grid.EntityId };
            var offer = new MyStoreItem(MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.STORE_ITEM), plate, 20, 150, ItemTypes.PhysicalItem, StoreItemTypes.Offer);
            var order = new MyStoreItem(MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.STORE_ITEM), ore, 1000, 3, ItemTypes.PhysicalItem, StoreItemTypes.Order);
            _station.StoreItems.Add(offer);
            _station.StoreItems.Add(order);
            _factionMoney = MyBankingSystem.GetBalance(_faction.FactionId);
            _faction.AddStation(_station);
            Check(MySession.Static.Factions.GetStationByGridId(grid.EntityId) == _station, "the store's grid is not the faction's station");

            var relation = MySession.Static.Factions.GetRelationBetweenPlayerAndFaction(Identity, _faction.FactionId);
            var friends = relation.Item1 == MyRelationsBetweenFactions.Friends;
            var offerBonus = friends ? Internal<float>(economy, "GetOffersFriendlyBonus", relation.Item2) : 1f;
            var orderBonus = friends ? Internal<float>(economy, "GetOrdersFriendlyBonus", relation.Item2) : 1f;
            var tick = (double)typeof(MySessionComponentEconomy).GetProperty("LastEconomyTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(economy);
            var suit = Inventory(Body);

            var money0 = MyBankingSystem.GetBalance(Identity);
            Request(store, "BuyItem", offer.Id, 5, Body.EntityId, tick);
            yield return null;
            var price = (long)(offer.PricePerUnit * (5f * offerBonus));
            Check(Amount(suit, "Component/SteelPlate") == 5, "the player has " + Amount(suit, "Component/SteelPlate") + " plates, bought 5");
            Check(offer.Amount == 15, "the station offers " + offer.Amount + " plates left of 15");
            var paid = money0 - MyBankingSystem.GetBalance(Identity);
            Check(paid == price, "the player paid " + paid + " for 5 plates at " + offer.PricePerUnit + " (" + relation.Item1 + ")");
            var got = MyBankingSystem.GetBalance(_faction.FactionId) - _factionMoney;
            Check(got == price, "the faction got " + got + " of " + price);

            Put(suit, "Ore/Iron", 100);
            var money1 = MyBankingSystem.GetBalance(Identity);
            var faction1 = MyBankingSystem.GetBalance(_faction.FactionId);
            Request(store, "SellItem", order.Id, 100, Body.EntityId, tick);
            yield return null;
            var earned = MyBankingSystem.GetBalance(Identity) - money1;
            var expected = (long)(order.PricePerUnit * 100L * orderBonus);
            Check(Amount(suit, "Ore/Iron") == 0, "the player still has " + Amount(suit, "Ore/Iron") + " kg of the 100 kg sold");
            Check(order.Amount == 900, "the station orders " + order.Amount + " kg left of 900");
            Check(earned == expected, "the player got " + earned + " for 100 kg of ore at " + order.PricePerUnit);
            Check(faction1 - MyBankingSystem.GetBalance(_faction.FactionId) == expected, "the faction paid " + (faction1 - MyBankingSystem.GetBalance(_faction.FactionId)) + " of " + expected);
            Result("at a station of " + _faction.Tag + " (" + relation.Item1 + ") bought 5 plates for " + paid + " and sold 100 kg of ore for " + earned + ", the faction's account moved by both");
        }

        private static T Internal<T>(object on, string method, params object[] args) =>
            (T)on.GetType().GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).Invoke(on, args);

        public override void Cleanup()
        {
            try
            {
                if (_station != null && Stations?.GetValue(_faction) is Dictionary<long, IMyFactionStation> stations) stations.Remove(_station.Id);
                if (_faction != null && _station != null)
                {
                    var off = _factionMoney - MyBankingSystem.GetBalance(_faction.FactionId);
                    if (off != 0) MyBankingSystem.ChangeBalance(_faction.FactionId, off);
                }
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// An NPC station's acquisition contract: a contract block at a station of an NPC trader faction (made for the check,
    /// as for <see cref="NpcStationCheck"/>), a contract of the station's for 10 steel plates. The player takes it at the
    /// block, brings the plates in the suit and hands them in there: the plates go, the reward comes.
    /// </summary>
    public sealed class NpcContractCheck : RegressionCheck
    {
        public override string Name => Prefix + "npc_contract";

        private const int Reward = 2500;
        private MyFaction _faction;
        private MyFactionStation _station;
        private long _contract;

        private static readonly System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableEconomy, "the economy is off in this world");
            var contracts = MySession.Static.GetComponent<MySessionComponentContractSystem>();
            SkipUnless(contracts != null, "no contract system in this world");
            _faction = MySession.Static.Factions.Select(f => f.Value).OfType<MyFaction>()
                .FirstOrDefault(f => f.FactionTypeString == "Trader" && f.Stations.Count > 0 && MySession.Static.Players.IdentityIsNpc(f.FounderId));
            SkipUnless(_faction != null, "no NPC trader faction in this world");
            yield return Arrive(Site.At(0, 0, -3));
            // the delivered items go into a container on the contract block's conveyors (MyGridConveyorSystem.PushGenerateItem)
            var contractPart = new Part("ContractBlock/ContractBlock", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All);
            Part cargo = null;
            yield return ConveyorNeighbour(contractPart, "CargoContainer/LargeBlockSmallContainer", null, p => cargo = p);
            var used = Cells(cargo);
            used.Add(Vector3I.Zero);
            var free = used.SelectMany(c => Base6Directions.IntDirections.Select(d => c + (Vector3I)d)).First(c => !used.Contains(c));
            var grid = BuildFor(_faction.FounderId, "npc-contracts", Site.Origin, contractPart, cargo,
                new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z).With(b => Motion.Charged(b)));
            var block = Block<MyContractBlock>(grid);
            yield return Until(() => block.IsWorking ? 1 : 0, v => v > 0, "the contract block working", 30, () => State(block));
            _station = new MyFactionStation(MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.STATION), "Regression contracts", grid.PositionComp.GetPosition(),
                MyStationTypeEnum.SpaceStation, _faction, "", null) { StationEntityId = grid.EntityId };
            _faction.AddStation(_station);

            // the station's contract, as the economy makes them (MySessionComponentContractSystem.GenerateCustomContract)
            var data = new Sandbox.ModAPI.Contracts.MyContractAcquisition(0, Reward, 0, 30, 0, Item("Component/SteelPlate"), 10);
            var generate = typeof(MySessionComponentContractSystem).GetMethods(Any).First(m => m.Name == "GenerateCustomContract"
                && m.GetParameters().Length == 5 && m.GetParameters()[2].ParameterType == typeof(VRage.Game.ModAPI.IMyContractAcquisition));
            var args = new object[] { null, _station, data, 0L, 0L };
            var made = generate.Invoke(contracts, args);
            _contract = (long)args[3];
            Check(made.ToString() == "Success", "the station's contract was not made: " + made);

            var money0 = MyBankingSystem.GetBalance(Identity);
            Request(block, "AcceptContract", Identity, _contract);
            Check(contracts.IsContractActive(_contract), "the contract is not active after the player took it at the station's block");
            var suit = Inventory(Body);
            Put(suit, "Component/SteelPlate", 10);
            var last = DateTime.MinValue;
            yield return Until(() =>
            {
                if (!contracts.IsContractActive(_contract)) return 1;
                if ((DateTime.UtcNow - last).TotalSeconds >= 1)
                {
                    last = DateTime.UtcNow;
                    Request(block, "FinishContract", Identity, _contract, Body.EntityId);
                }
                return 0;
            }, v => v > 0, "the contract handed in at the station", 15, () => "plates in the suit " + Amount(suit, "Component/SteelPlate"));
            Check(Amount(suit, "Component/SteelPlate") == 0, "the player still has " + Amount(suit, "Component/SteelPlate") + " of the 10 plates handed in");
            var box = Amount(Inventory((MyCubeBlock)grid.GetCubeBlock(cargo.Min).FatBlock), "Component/SteelPlate");
            Check(box == 10, "the station's container has " + box + " of the 10 plates delivered");
            var paid = MyBankingSystem.GetBalance(Identity) - money0;
            Check(paid == Reward, "the player got " + paid + " for the contract, its reward is " + Reward);
            Result("the station's contract for 10 plates taken and handed in at its block: the plates taken, " + paid + " paid");
        }

        public override void Cleanup()
        {
            try
            {
                var contracts = MySession.Static.GetComponent<MySessionComponentContractSystem>();
                if (_contract != 0 && contracts != null && contracts.InactiveContracts.Concat(contracts.ActiveContracts).Any(c => c.Id == _contract))
                    contracts.RemoveContract(_contract);
                if (_station != null && typeof(MyFaction).GetField("m_stations", Any)?.GetValue(_faction) is Dictionary<long, IMyFactionStation> stations) stations.Remove(_station.Id);
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// The economy's restock of an NPC station's store (<c>MyStoreItemsGenerator.Update</c> of the faction, what the
    /// economy tick <c>UpdateStations</c> calls; the tick's timing is not checked): a station of an NPC trader faction's
    /// made for the check, its store empty, gets offers and orders of the game's own, priced. The faction's own stations
    /// are not restocked by it. After, the station is taken off the faction.
    /// </summary>
    public sealed class EconomyRestockCheck : RegressionCheck
    {
        public override string Name => Prefix + "economy_restock";

        private MyFaction _faction;
        private MyFactionStation _station;

        private static readonly System.Reflection.FieldInfo Stations =
            typeof(MyFaction).GetField("m_stations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableEconomy, "the economy is off in this world");
            var economy = MySession.Static.GetComponent<MySessionComponentEconomy>();
            var generator = typeof(MySessionComponentEconomy).GetField("m_storeItemsGenerator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(economy);
            Check(generator != null, "MySessionComponentEconomy has no m_storeItemsGenerator");
            _faction = MySession.Static.Factions.Select(f => f.Value).OfType<MyFaction>()
                .FirstOrDefault(f => f.FactionTypeString == "Trader" && f.Stations.Count > 0 && MySession.Static.Players.IdentityIsNpc(f.FounderId));
            SkipUnless(_faction != null, "no NPC trader faction in this world");
            yield return Arrive(Site.At(0, 0, -3));
            var grid = BuildFor(_faction.FounderId, "npc-restock", Site.Origin,
                new Part("StoreBlock/StoreBlock", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            _station = new MyFactionStation(MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.STATION), "Regression restock post", grid.PositionComp.GetPosition(),
                MyStationTypeEnum.SpaceStation, _faction, "", null) { StationEntityId = grid.EntityId };
            _faction.AddStation(_station);
            Check(_station.StoreItems.Count == 0, "the new station's store is not empty: " + _station.StoreItems.Count + " items");
            // the restock goes over every station of the faction: the world's own ones are taken off it for that call, so
            // only the check's gets restocked (all in one call of the game thread, nobody sees them away)
            var stations = (Dictionary<long, IMyFactionStation>)Stations.GetValue(_faction);
            var others = stations.Where(p => p.Key != _station.Id).ToList();
            foreach (var other in others) stations.Remove(other.Key);
            try { generator.GetType().GetMethod("Update").Invoke(generator, new object[] { _faction, false }); }
            finally { foreach (var other in others) stations[other.Key] = other.Value; }
            yield return null;
            var offers = _station.StoreItems.Where(i => i.StoreItemType == StoreItemTypes.Offer).ToList();
            var orders = _station.StoreItems.Where(i => i.StoreItemType == StoreItemTypes.Order).ToList();
            Check(offers.Count > 0, "the economy's restock gave the station no offers");
            Check(orders.Count > 0, "the economy's restock gave the station no orders");
            // the roll can offer an item in zero amount (e.g. a plushie) - that is the game's own randomness, not a break;
            Check(_station.StoreItems.All(i => i.PricePerUnit > 0), "an item of the restock has no price: " +
                string.Join(", ", _station.StoreItems.Where(i => i.PricePerUnit <= 0).Select(i => (i.Item?.SubtypeName ?? i.ItemType.ToString()) + " " + i.Amount + " at " + i.PricePerUnit)));
            Check(offers.Any(i => i.Amount > 0), "no offer of the restock has an amount");
            Result("the restock of " + _faction.Tag + " gave the new station " + offers.Count + " offers and " + orders.Count + " orders (e.g. " +
                   string.Join(", ", offers.Take(3).Select(i => (i.Item?.SubtypeName ?? i.ItemType.ToString()) + " x" + i.Amount + " at " + i.PricePerUnit)) + ")");
        }

        public override void Cleanup()
        {
            try
            {
                if (_station != null && Stations?.GetValue(_faction) is Dictionary<long, IMyFactionStation> stations) stations.Remove(_station.Id);
            }
            finally { base.Cleanup(); }
        }
    }
}
