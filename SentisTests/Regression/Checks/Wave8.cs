using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Contracts;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.ObjectBuilders.Components.Contracts;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A decoy draws a turret's fire: an enemy station of a battery and, 15 m off along a beam of armor, a decoy; the
    /// player's gatling turret 150 m away, set to aim at weapons (a decoy is one), shoots the decoy and leaves the battery
    /// whole.
    /// </summary>
    public sealed class DecoyCheck : RegressionCheck
    {
        public override string Name => Prefix + "decoy";
        public override int TimeoutSeconds => 120;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            // at the game's low quality (its own under load) bullets check what they hit a third as often and fly through walls
            HoldHighQuality();
            yield return Arrive(Site.At(0, 10, 20));
            var parts = new List<Part> { new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)) };
            for (var x = 1; x < 6; x++) parts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, 0, 0));
            parts.Add(new Part("Decoy/LargeDecoy", 6, 0, 0));
            var station = BuildFor(WorldApi.TestIdentityId(), "decoy-station", Site.At(0, 0, 150), parts.ToArray());
            var battery = station.GetCubeBlock(Vector3I.Zero);
            var decoy = station.GetCubeBlock(new Vector3I(6, 0, 0));
            var turretGrid = Build("decoy-turret", Site.Origin, true,
                new Part("LargeGatlingTurret/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 2).With(b => Motion.Charged(b)));
            var turret = Block<SpaceEngineers.Game.Entities.Weapons.MyLargeGatlingTurret>(turretGrid);
            Put(Inventory(turret), "AmmoMagazine/NATO_25x184mm", 10);
            // the turret set to aim at weapons, as a player sets it in its terminal: a decoy counts as one
            ((Sandbox.ModAPI.Ingame.IMyLargeTurretBase)turret).SetTargetingGroup("Weapons");
            var batteryWhole = battery.Integrity;
            var decoyWhole = decoy.Integrity;
            string first = null;
            yield return Until(() =>
            {
                if (first == null && battery.Integrity < batteryWhole) first = "the battery";
                if (first == null && (decoy.IsDestroyed || decoy.Integrity < decoyWhole)) first = "the decoy";
                return decoy.IsDestroyed ? decoyWhole : decoyWhole - decoy.Integrity;
            }, v => v > 0, "the turret shooting the decoy", 60, () => State(turret) + ", battery " + F(battery.Integrity) + "; " + State(decoy.FatBlock));
            Check(battery.Integrity >= batteryWhole, "the turret hit the battery too (" + F(batteryWhole) + " -> " + F(battery.Integrity) + ") with a decoy on the station; hit first: " + first +
                                                     "; " + State(decoy.FatBlock));
            Result("the turret shot the decoy (" + F(decoyWhole - (decoy.IsDestroyed ? 0 : decoy.Integrity)) + " damage), the battery 15 m from it untouched");
        }
    }

    /// <summary>A searchlight with its AI on finds an enemy station 100 m off and turns its light onto it.</summary>
    public sealed class SearchlightCheck : RegressionCheck
    {
        public override string Name => Prefix + "searchlight";

        protected override IEnumerator Check()
        {
            SkipUnless(!Game.Mods.WeaponCoreManages(Definition("Searchlight/LargeSearchlight").Id), "WeaponCore runs the searchlights in this world");
            yield return Arrive(Site.At(0, 10, 20));
            var station = BuildFor(WorldApi.TestIdentityId(), "searchlight-target", Site.At(60, 0, 80),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", -1, 0, 0));
            var grid = Build("searchlight", Site.Origin, true,
                new Part("Searchlight/LargeSearchlight", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var light = Block<SpaceEngineers.Game.Entities.Blocks.MySearchlight>(grid);
            var ingame = (Sandbox.ModAPI.Ingame.IMySearchlight)light;
            ingame.TargetStations = true;
            ingame.TargetLargeShips = true;
            ingame.TargetEnemy = true;
            ingame.TargetNeutrals = true;
            yield return Until(() => light.Target?.GetTopMostParent() == station ? 1 : 0, v => v > 0, "the searchlight taking the enemy station for its target", 30,
                () => State(light) + ", target " + (light.Target?.GetTopMostParent()?.DisplayName ?? "none"));
            yield return Until(() => light.IsAimed ? 1 : 0, v => v > 0, "the searchlight turned onto the station", 20);
            Result("the searchlight found the enemy station and turned its light onto it");
        }
    }

    /// <summary>A contract of a player's: posted at the owner's contract block, taken and done by another player.</summary>
    public abstract class PlayerContractCheck : RegressionCheck
    {
        protected const int Reward = 3000;
        protected MySessionComponentContractSystem Contracts;
        protected int Worker = -1;
        protected long WorkerIdentity;

        protected IEnumerator Setup()
        {
            SkipUnless(MySession.Static.Settings.EnableEconomy, "the economy is off in this world");
            Contracts = MySession.Static.GetComponent<MySessionComponentContractSystem>();
            SkipUnless(Contracts != null, "no contract system in this world");
            yield return Join(Site.At(2, 0, -3), i => Worker = i);
            WorkerIdentity = FakeClients.PlayerOf(Worker).Identity.IdentityId;
            MyBankingSystem.ChangeBalance(Identity, 1000000);
        }

        protected MyContractBlock ContractBlock(string name, Vector3D at)
        {
            var grid = Build(name, at, true,
                new Part("ContractBlock/ContractBlock", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            return Block<MyContractBlock>(grid);
        }

        /// <summary>The owner posts the contract the event of the block makes of the data (a wrapper struct of the block's).</summary>
        protected long Post(MyContractBlock block, string wrapper, string method, Dictionary<string, object> fields)
        {
            var type = typeof(MyContractBlock).GetNestedType(wrapper, System.Reflection.BindingFlags.NonPublic)
                       ?? throw new Core.ScenarioFailedException("MyContractBlock." + wrapper + " is not in this game");
            var data = Activator.CreateInstance(type);
            foreach (var field in fields) type.GetField(field.Key).SetValue(data, field.Value);
            Request(block, method, data);
            var count = Contracts.GetAvailableContractsForBlockCount(block.EntityId);
            Check(count == 1, "the contract was not posted on the block (" + count + " there)");
            return Contracts.GetAvailableContractsForBlock(block.EntityId)[0].Id;
        }

        /// <summary>The active contract of this id (the game's own lookup is internal).</summary>
        protected MyContract Active(long id)
        {
            var all = (Dictionary<long, MyContract>)typeof(MySessionComponentContractSystem)
                .GetField("m_activeContracts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(Contracts);
            Check(all.TryGetValue(id, out var contract), "the contract " + id + " is not active");
            return contract;
        }

        /// <summary>Hands the contract in at the block, again each second as a player presses again.</summary>
        protected IEnumerator HandIn(MyContractBlock block, long contractId)
        {
            var last = DateTime.MinValue;
            yield return Until(() =>
            {
                if (!Contracts.IsContractActive(contractId)) return 1;
                if ((DateTime.UtcNow - last).TotalSeconds >= 1)
                {
                    last = DateTime.UtcNow;
                    RequestAs(Worker, block, "FinishContract", WorkerIdentity, contractId, FakeClients.Character(Worker).EntityId);
                }
                return 0;
            }, v => v > 0, "the contract handed in", 15);
        }
    }

    /// <summary>
    /// A hauling contract: posted at one contract block for a package to another 60 m off; the player taking it gets the
    /// package, hands it in at the other block and is paid.
    /// </summary>
    public sealed class ContractHaulingCheck : PlayerContractCheck
    {
        public override string Name => Prefix + "contract_hauling";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(-2, 0, -3));
            yield return Setup();
            var start = ContractBlock("hauling-start", Site.Origin);
            var end = ContractBlock("hauling-end", Site.At(60, 0, 0));
            yield return Seconds(1);
            var contractId = Post(start, "MyContractCreationDataWrapper_Deliver", "CreateCustomContractDeliver", new Dictionary<string, object>
            {
                ["RewardMoney"] = Reward, ["StartingDeposit"] = 0, ["DurationInMin"] = 30, ["TargetBlockId"] = end.EntityId,
            });
            RequestAs(Worker, start, "AcceptContract", WorkerIdentity, contractId);
            Check(Contracts.IsContractActive(contractId), "the contract is not active after the player took it");
            var suit = Inventory(FakeClients.Character(Worker));
            var packages = suit.GetItems().Count(i => i.Content.GetType().Name == "MyObjectBuilder_Package");
            Check(packages == 1, "the player taking the contract has " + packages + " packages");
            var paid0 = MyBankingSystem.GetBalance(WorkerIdentity);
            yield return HandIn(end, contractId);
            Check(suit.GetItems().All(i => !(i.Content.GetType().Name == "MyObjectBuilder_Package")), "the package is still with the player after the contract was handed in");
            var paid = MyBankingSystem.GetBalance(WorkerIdentity) - paid0;
            Check(paid == Reward, "the player got " + paid + " for the delivery, the reward is " + Reward);
            Result("the package taken at one contract block and handed in at the other 60 m off: " + paid + " paid");
        }
    }

    /// <summary>
    /// A repair contract: the owner posts it for a station of theirs with hurt blocks; the player taking it welds them
    /// whole, hands it in and is paid.
    /// </summary>
    public sealed class ContractRepairCheck : PlayerContractCheck
    {
        public override string Name => Prefix + "contract_repair";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(-2, 0, -3));
            yield return Setup();
            var block = ContractBlock("repair-office", Site.Origin);
            var hurt = Build("repair-target", Site.At(0, 0, 20), true,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            foreach (var slim in hurt.GetBlocks()) slim.DoDamage(slim.MaxIntegrity * 0.5f, MyDamageType.Bullet, true, null, 0);
            yield return Seconds(1);
            var contractId = Post(block, "MyContractCreationDataWrapper_Repair", "CreateCustomContractRepair", new Dictionary<string, object>
            {
                ["RewardMoney"] = Reward, ["StartingDeposit"] = 0, ["DurationInMin"] = 30, ["TargetGridId"] = hurt.EntityId,
            });
            RequestAs(Worker, block, "AcceptContract", WorkerIdentity, contractId);
            Check(Contracts.IsContractActive(contractId), "the contract is not active after the player took it");
            // the reward comes as the last hurt block is whole (the contract finishes then): the balance before the welding
            var paid0 = MyBankingSystem.GetBalance(WorkerIdentity);
            // welded whole with a ship welder of the player's over it, plates in the welder (the hurt blocks lost theirs)
            var middle = (hurt.GridIntegerToWorld(new Vector3I(0, 0, 0)) + hurt.GridIntegerToWorld(new Vector3I(1, 0, 0))) / 2;
            var rig = ToolRig("repair-welder", "ShipWelder/LargeShipWelder", middle, -Site.Up);
            var welder = Block<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(rig);
            Put(Inventory(welder), "Component/SteelPlate", 100);
            Switch(welder, true);
            yield return Until(() => hurt.GetBlocks().Count(b => b.IsFullIntegrity), v => v >= hurt.BlocksCount, "the station's hurt blocks welded whole", 60, () => State(welder));
            Switch(welder, false);
            yield return HandIn(block, contractId);
            var paid = MyBankingSystem.GetBalance(WorkerIdentity) - paid0;
            Check(paid == Reward, "the player got " + paid + " for the repair, the reward is " + Reward);
            Result("the station's hurt blocks welded whole and the repair contract handed in: " + paid + " paid");
        }
    }

    /// <summary>
    /// A search contract of a player's: the owner posts it for a powered grid of theirs 300 m off, the reward is held from
    /// the owner; the player taking it comes up to the grid (it counts as found), the grid is brought to 40 m of the
    /// contract block, and the contract finishes: the player gets the whole reward, the owner nothing back. In vanilla the
    /// game fails it within seconds (<c>MyContractFind</c> wants an NPC-spawned grid and a faction station to bring it
    /// to); SGI <c>FindContractPatch</c> makes it work, without it the check fails on that.
    /// </summary>
    public sealed class ContractFindCheck : PlayerContractCheck
    {
        public override string Name => Prefix + "contract_find";
        public override int TimeoutSeconds => 120;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(-2, 0, -3));
            yield return Setup();
            var block = ContractBlock("find-office", Site.Origin);
            var target = Build("find-target", Site.At(0, 0, 300), true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)));
            yield return Seconds(1);
            var owner0 = MyBankingSystem.GetBalance(Identity);
            var contractId = Post(block, "MyContractCreationDataWrapper_Find", "CreateCustomContractFind", new Dictionary<string, object>
            {
                ["RewardMoney"] = Reward, ["StartingDeposit"] = 0, ["DurationInMin"] = 30, ["TargetGridId"] = target.EntityId, ["SearchRadius"] = 30.0,
            });
            var held = owner0 - MyBankingSystem.GetBalance(Identity);
            Check(held == Reward, "posting the contract held " + held + " from the owner, the reward is " + Reward);
            var paid0 = MyBankingSystem.GetBalance(WorkerIdentity);
            RequestAs(Worker, block, "AcceptContract", WorkerIdentity, contractId);
            Check(Contracts.IsContractActive(contractId), "the contract is not active after the player took it");
            yield return Seconds(3);
            Check(Contracts.IsContractActive(contractId), "the game failed the player's search contract (" +
                  (MyBankingSystem.GetBalance(Identity) - (owner0 - held)) + " back to the owner) before the grid was found: vanilla MyContractFind wants an NPC-spawned grid (SGI FindContractPatch fixes it)");
            var contract = Contract(contractId);
            FakeClients.MoveTo(Worker, target.PositionComp.GetPosition() + Site.Right * 8);
            yield return Until(() => contract.GridFound ? 1 : 0, v => v > 0, "the player at the grid finding it", 15);
            Check(Contracts.IsContractActive(contractId), "the contract failed when the player found the grid");
            var to = target.WorldMatrix;
            to.Translation = Site.At(0, 0, 40);
            target.Teleport(to);
            yield return Until(() => Contracts.IsContractActive(contractId) ? 0 : 1, v => v > 0, "the contract finishing with the grid brought back", 15);
            var paid = MyBankingSystem.GetBalance(WorkerIdentity) - paid0;
            var back = MyBankingSystem.GetBalance(Identity) - (owner0 - held);
            Check(contract.State == MyContractStateEnum.Finished || contract.State == MyContractStateEnum.ToBeDisposed || contract.State == MyContractStateEnum.Disposed,
                "the contract ended " + contract.State + ", not finished");
            Check(paid == Reward, "the player got " + paid + " for the grid found and brought back, the reward is " + Reward);
            Check(back == 0, "the owner got " + back + " back of a finished contract's reward");
            Result("the player's search contract posted (" + held + " held), the grid found by the player and brought to the block: " + paid + " paid, " + back + " back to the owner");
        }

        private MyContractFind Contract(long id)
        {
            var contract = Active(id);
            Check(contract is MyContractFind, "the active contract " + id + " is not a search contract");
            return (MyContractFind)contract;
        }
    }
}
