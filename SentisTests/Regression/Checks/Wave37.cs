using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The production queue as the terminal edits it (<c>MyProductionBlock.OnMoveQueueItemCallback</c>,
    /// <c>OnRemoveQueueItemRequest</c>, <c>ClearQueueRequest</c>, <c>MyAssembler.DisassembleAllCallback</c>): an
    /// assembler, switched off so nothing is made, with three items queued - the last moved first, one removed, all
    /// cleared; "disassemble all" queues what lies in its output (the client switches the mode itself).
    /// </summary>
    public sealed class ProductionQueueCheck : RegressionCheck
    {
        public override string Name => Prefix + "production_queue";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -6));
            var grid = Build("production-queue", Site.Origin, true,
                new Part("Assembler/LargeAssembler", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 2).With(b => Motion.Charged(b)));
            var assembler = Block<MyAssembler>(grid);
            Switch(assembler, false);
            var names = new[] { "SteelPlate", "ConstructionComponent", "InteriorPlate" };
            foreach (var name in names)
                Request(assembler, "OnAddQueueItemRequest", -1, (SerializableDefinitionId)Blueprints.Get(name).Id, (MyFixedPoint)2);
            List<MyProductionBlock.QueueItem> Queue() => assembler.Queue.ToList();
            string Order() => string.Join(", ", Queue().Select(q => q.Blueprint.Id.SubtypeName + " x" + q.Amount));
            Check(Queue().Count == 3, "the queue has " + Queue().Count + " items of the 3 added: " + Order());
            var last = Queue()[2];
            Request(assembler, "OnMoveQueueItemCallback", last.ItemId, 0);
            Check(Queue()[0].ItemId == last.ItemId, "the last item was not moved first: " + Order());
            Request(assembler, "OnRemoveQueueItemRequest", 1, (MyFixedPoint)(-1), 0f);
            Check(Queue().Count == 2, "one item removed, the queue has " + Queue().Count + ": " + Order());
            Request(assembler, "ClearQueueRequest");
            Check(Queue().Count == 0, "the cleared queue has " + Queue().Count + " items: " + Order());

            Put(assembler.OutputInventory, "Component/SteelPlate", 5);
            Request(assembler, "DisassembleAllCallback");
            yield return WaitForTicks(2);
            Check(Queue().Any(q => q.Blueprint.Results.Any(r => r.Id.SubtypeName == "SteelPlate") && (double)q.Amount >= 5),
                "the 5 plates of the output are not queued for taking apart: " + Order());
            Request(assembler, "ClearQueueRequest");
            Result("queue of 3: the last moved first, one removed, all cleared; 'disassemble all' queued the 5 plates of the output");
        }
    }

    /// <summary>
    /// A rifle's magazine type switched (<c>MyCharacter.OnSwitchAmmoMagazineRequest</c>, the player's R twice): with both
    /// of its magazine types in the suit, the rifle takes the other.
    /// </summary>
    public sealed class SwitchMagazineCheck : HandCheck
    {
        public override string Name => Prefix + "switch_magazine";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            var rifle = (Sandbox.Definitions.MyWeaponItemDefinition)Sandbox.Definitions.MyDefinitionManager.Static.GetPhysicalItemDefinition(Item("PhysicalGunObject/AutomaticRifleItem"));
            var magazines = Sandbox.Definitions.MyDefinitionManager.Static.GetWeaponDefinition(rifle.WeaponDefinitionId).AmmoMagazinesId;
            SkipUnless(magazines.Length >= 2, "the rifle takes one magazine type in this game");
            foreach (var magazine in magazines.Take(2))
                Inventory(Body).AddItems(2, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(magazine));
            yield return Equip("AutomaticRifleItem");
            var gun = (Sandbox.Game.Weapons.MyAutomaticRifleGun)Body.CurrentWeapon;
            var before = gun.GunBase.CurrentAmmoMagazineId;
            Request(Body, "OnSwitchAmmoMagazineRequest");
            yield return Until(() => gun.GunBase.CurrentAmmoMagazineId != before ? 1 : 0, v => v > 0, "the rifle on its other magazine type", 5,
                () => "magazine " + gun.GunBase.CurrentAmmoMagazineId.SubtypeName);
            Result("the rifle switched from " + before.SubtypeName + " to " + gun.GunBase.CurrentAmmoMagazineId.SubtypeName);
        }
    }

    /// <summary>
    /// Research shared in a faction (<c>MySessionComponentResearch.CallShareResearch</c>): a member shares what they have
    /// researched with another member, who can use it from then on; to a player out of the faction it is not shared.
    /// </summary>
    public sealed class ResearchShareCheck : FactionCheck
    {
        public override string Name => Prefix + "research_share";

        private readonly List<long> _reset = new List<long>();

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.ResearchEnabled, "research is off in this world");
            var research = MySessionComponentResearch.Static;
            yield return Arrive(Site.At(0, 4, -6));
            int member = -1, stranger = -1;
            yield return Join(Site.At(4, 4, -6), i => member = i);
            yield return Join(Site.At(-4, 4, -6), i => stranger = i);
            var faction = Found(Me);
            Enter(member, faction, Me);
            var block = Sandbox.Definitions.MyDefinitionManager.Static.GetAllDefinitions().OfType<Sandbox.Definitions.MyCubeBlockDefinition>()
                .FirstOrDefault(d => d.Public && research.RequiresResearch(d.Id) && research.CanUse(Identity, d.Id));
            SkipUnless(block != null, "the player has no researched block to share");
            foreach (var other in new[] { IdentityOf(member), IdentityOf(stranger) })
            {
                research.ResetResearch(other);
                _reset.Add(other);
            }
            Check(!research.CanUse(IdentityOf(member), block.Id), "after the reset the member can still use " + block.Id.SubtypeName);
            Request(typeof(MySessionComponentResearch), "CallShareResearch", IdentityOf(stranger));
            Request(typeof(MySessionComponentResearch), "CallShareResearch", IdentityOf(member));
            yield return Until(() => research.CanUse(IdentityOf(member), block.Id) ? 1 : 0, v => v > 0, block.Id.SubtypeName + " shared with the faction's member", 5);
            Check(!research.CanUse(IdentityOf(stranger), block.Id), "the research was shared with a player out of the faction");
            Result("research (" + block.Id.SubtypeName + " among it) shared with the faction's member; not with a stranger");
        }

        public override void Cleanup()
        {
            try { foreach (var identity in _reset) WorldApi.UnlockResearch(identity); }
            catch { }
            base.Cleanup();
        }
    }

    /// <summary>
    /// A faction edited by its leader (<c>MyFactionCollection.EditFactionRequest</c>, <c>ChangeAutoAcceptRequest</c>):
    /// the leader renames it; a member who is not a leader cannot; with auto accept of members on, a player asking to
    /// join is in at once.
    /// </summary>
    public sealed class FactionEditCheck : FactionCheck
    {
        public override string Name => Prefix + "faction_edit";

        private static object Message(IMyFactionLike faction, string name, string description)
        {
            var type = typeof(MyFactionCollection).GetNestedType("AddFactionMsg", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var message = Activator.CreateInstance(type);
            void Set(string field, object value) => type.GetField(field).SetValue(message, value);
            Set("FactionId", faction.Faction.FactionId);
            Set("FactionTag", faction.Faction.Tag);
            Set("FactionName", name);
            Set("FactionDescription", description);
            Set("FactionPrivateInfo", "");
            Set("FounderId", faction.Faction.FounderId);
            Set("Type", "PlayerMade");
            return message;
        }

        private sealed class IMyFactionLike
        {
            public VRage.Game.ModAPI.IMyFaction Faction;
        }

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            int member = -1, joiner = -1;
            yield return Join(Site.At(4, 4, -6), i => member = i);
            yield return Join(Site.At(-4, 4, -6), i => joiner = i);
            var faction = Found(Me);
            Enter(member, faction, Me);
            var like = new IMyFactionLike { Faction = faction };
            var renamed = WorldApi.EntityPrefix + faction.Tag + " renamed";
            Request(typeof(MyFactionCollection), "EditFactionRequest", Message(like, renamed, "edited"));
            Check(faction.Name == renamed && faction.Description == "edited", "the leader's edit did not take: " + faction.Name + " / " + faction.Description);
            RequestAs(member, typeof(MyFactionCollection), "EditFactionRequest", Message(like, WorldApi.EntityPrefix + faction.Tag + " hijacked", "member"));
            Check(faction.Name == renamed, "a member who is not a leader renamed the faction to " + faction.Name);

            Request(typeof(MyFactionCollection), "ChangeAutoAcceptRequest", faction.FactionId, true, false);
            Check(faction.AutoAcceptMember, "auto accept of members did not switch on");
            var change = typeof(MyFactionCollection).GetMethod("FactionStateChangeRequest", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            RequestAs(joiner, typeof(MyFactionCollection), "FactionStateChangeRequest", Enum.Parse(change.GetParameters()[0].ParameterType, "FactionMemberSendJoin"),
                faction.FactionId, faction.FactionId, IdentityOf(joiner));
            yield return WaitForTicks(2);
            Check(faction.IsMember(IdentityOf(joiner)), "with auto accept on, the player asking to join is not in");
            Leave(joiner, faction);
            Result("the leader renamed the faction, a member could not; with auto accept on a player asking to join was in at once");
        }
    }

    /// <summary>
    /// A store owner takes back their listings (<c>MyStoreBlock.CancelStoreItemServer</c>): an offer and an order put up,
    /// each paying the economy's listing fee; both cancelled, the store lists nothing of theirs, and the fees are not given back.
    /// </summary>
    public sealed class StoreCancelCheck : RegressionCheck
    {
        public override string Name => Prefix + "store_cancel";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableEconomy, "the economy is off in this world");
            var economy = MySession.Static.GetComponent<MySessionComponentEconomy>();
            yield return Arrive(Site.At(-2, 0, -3));
            var grid = Build("store-cancel", Site.Origin, true,
                new Part("StoreBlock/StoreBlock", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var store = Block<MyStoreBlock>(grid);
            yield return Until(() => store.IsWorking ? 1 : 0, v => v > 0, "the store working", 30, () => State(store));
            Put(Inventory(store), "Component/SteelPlate", 20);
            MyBankingSystem.ChangeBalance(Identity, 1000000);
            var plate = Item("Component/SteelPlate");
            var ore = Item("Ore/Iron");
            var money0 = MyBankingSystem.GetBalance(Identity);
            var platePrice = Math.Max(economy.GetMinimumItemPrice(plate), 1) + 10;
            var orePrice = Math.Max(economy.GetMinimumItemPrice(ore), 1) + 1;
            Request(store, "CreateNewOffer", (SerializableDefinitionId)plate, 10, platePrice, null);
            Request(store, "CreateNewOrder", (SerializableDefinitionId)ore, 100, orePrice);
            // a listing pays the economy's fee of its whole price (MyStoreBlock.CreateNewOrder_Internal), kept on cancelling
            var fee = economy.EconomyDefinition.ListingFee;
            var fees = (long)(10L * platePrice * fee) + (long)(100L * orePrice * fee);
            Check(store.PlayerItems.Count == 2, "the store lists " + store.PlayerItems.Count + " of the owner's 2 items");
            var held = money0 - MyBankingSystem.GetBalance(Identity);
            foreach (var item in store.PlayerItems.ToList()) Request(store, "CancelStoreItemServer", item.Id);
            Check(store.PlayerItems.Count == 0, "the store still lists " + store.PlayerItems.Count + " of the owner's items after cancelling");
            Check(held == fees, "the listings took " + held + " from the owner, their fees are " + fees);
            Check(MyBankingSystem.GetBalance(Identity) == money0 - held, "cancelling moved the owner's account by " + (MyBankingSystem.GetBalance(Identity) - (money0 - held)));
            Result("an offer and an order put up (" + held + " of listing fees, " + (fee * 100).ToString("F1") + " % of their price) and cancelled: nothing listed, the fees kept");
        }
    }

    /// <summary>
    /// Mechanisms sent to a place (<c>MyPistonBase.RequestMoveToPosition</c>, <c>MyMotorStator.RequestRotation</c>, the
    /// toolbar actions "move to" and "rotate to"): a piston goes to 2 m and stops there, a rotor turns to 90 degrees and
    /// stops there.
    /// </summary>
    public sealed class MoveToPositionCheck : RegressionCheck
    {
        public override string Name => Prefix + "move_to_position";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(6, 4, 0));
            var pistonGrid = Build("move-to-piston", Site.Origin, true,
                new Part("ExtendedPistonBase/LargePistonBase", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var piston = Block<MyPistonBase>(pistonGrid);
            yield return AddHead(piston);
            var rotorGrid = Build("move-to-rotor", Site.At(-10, 0, 0), true,
                new Part("MotorStator/LargeStator", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var rotor = Block<MyMotorStator>(rotorGrid);
            yield return AddHead(rotor);
            ((Sandbox.ModAPI.IMyTerminalBlock)rotor).SetValueFloat("Velocity", 0f);

            Request(piston, "RequestMoveToPosition", 2f, 1f);
            var pistonIn = (Sandbox.ModAPI.Ingame.IMyPistonBase)piston;
            yield return Until(() => pistonIn.CurrentPosition, v => v > 1.95, "the piston at 2 m", 15, () => F(pistonIn.CurrentPosition) + " m");
            yield return Seconds(2);
            var stoppedAt = pistonIn.CurrentPosition;
            Check(Math.Abs(stoppedAt - 2f) < 0.05f, "the piston sent to 2 m stands at " + F(stoppedAt) + " m");

            var method = typeof(MyMotorStator).GetMethod("RequestRotation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Check(method != null, "MyMotorStator.RequestRotation is not in this game");
            var auto = Enum.Parse(method.GetParameters()[0].ParameterType, "AUTO");
            var rotorIn = (Sandbox.ModAPI.Ingame.IMyMotorStator)rotor;
            Request(rotor, "RequestRotation", auto, MathHelper.ToRadians(90), 5f);
            yield return Until(() => MathHelper.ToDegrees(rotorIn.Angle), v => Math.Abs(v - 90) < 2, "the rotor at 90 degrees", 20, () => F(MathHelper.ToDegrees(rotorIn.Angle)) + " degrees");
            yield return Seconds(2);
            var angle = MathHelper.ToDegrees(rotorIn.Angle);
            Check(Math.Abs(angle - 90) < 2, "the rotor sent to 90 degrees stands at " + F(angle));
            Result("the piston went to 2 m and stood at " + F(stoppedAt) + " m; the rotor turned to 90 degrees and stood at " + F(angle));
        }
    }

    /// <summary>
    /// Foraging (<c>MyForageableEntity.Forage_Implementation</c>, the player's F on a plant): a plant by the player on a
    /// planet with forageables gives its items into the suit and is spent (<c>WasForaged</c>).
    /// </summary>
    public sealed class ForageCheck : RegressionCheck
    {
        public override string Name => Prefix + "forage";
        public override int TimeoutSeconds => 120;

        private static List<MyForageableEntity> Plants(Vector3D near, double radius) =>
            MyEntities.GetEntities().OfType<MyForageableEntity>().Where(f => !f.MarkedForClose && !f.WasForaged && Vector3D.Distance(f.PositionComp.GetPosition(), near) < radius).ToList();

        protected override IEnumerator Check()
        {
            // a plant the world has loaded already (they are made near players, on the planets whose generator grows them:
            // Pertam, Earth, Alien); else one loaded around the player on such a planet
            var loaded = MyEntities.GetEntities().OfType<MyForageableEntity>().Where(f => !f.MarkedForClose && !f.WasForaged).ToList();
            List<MyForageableEntity> plants = loaded;
            MyPlanet planet;
            Vector3D ground, up;
            if (loaded.Count > 0)
            {
                var first = loaded[0];
                planet = MyGamePruningStructure.GetClosestPlanet(first.PositionComp.GetPosition());
                ground = first.PositionComp.GetPosition();
                up = Vector3D.Normalize(ground - planet.PositionComp.GetPosition());
                yield return Arrive(ground + up * 3);
            }
            else
            {
                var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
                planet = new[] { "Pertam", "Earth", "Alien" }.Select(n => planets.FirstOrDefault(p => p.StorageName.StartsWith(n))).FirstOrDefault(p => p != null);
                SkipUnless(planet != null, "no planet with forageables in this world (" + string.Join(", ", planets.Select(p => p.StorageName)) + ")");
                var centre = planet.PositionComp.GetPosition();
                var guess = centre + Vector3D.Normalize(Site.Origin - centre) * planet.MaximumRadius * 1.1;
                ground = planet.GetClosestSurfacePointGlobal(ref guess);
                up = Vector3D.Normalize(ground - centre);
                yield return Arrive(ground + up * 3);
                yield return Until(() => (plants = Plants(ground, 600)).Count, v => v > 0, "forageable plants loaded around the player on " + planet.StorageName, 60);
            }
            var plant = plants.OrderBy(p => Vector3D.Distance(p.PositionComp.GetPosition(), ground)).First();
            up = Vector3D.Normalize(plant.PositionComp.GetPosition() - planet.PositionComp.GetPosition());
            StandAt(plant.PositionComp.GetPosition() + up * 1.5 + Vector3D.CalculatePerpendicularVector(up) * 1.5, plant.PositionComp.GetPosition());
            yield return Seconds(1);
            var suit = Inventory(Body);
            var before = (double)suit.CurrentMass;
            var items0 = suit.GetItemsCount();
            Request(plant, "Forage_Implementation", (long?)null);
            yield return Until(() => plant.WasForaged ? 1 : 0, v => v > 0, "the plant foraged", 10);
            Check(suit.GetItemsCount() > items0 || (double)suit.CurrentMass > before, "foraging the plant put nothing into the suit");
            Result("a plant (" + plant.DisplayName + ") foraged on " + planet.StorageName + ": the suit " + F(before) + " -> " + F((double)suit.CurrentMass) + " kg, the plant spent");
        }
    }

    /// <summary>
    /// Target locking from a cockpit (<c>MyTargetLockingComponent.OnTargetRequestServer</c>, the pilot's lock key): a
    /// small ship with a gatling gun, a grid of another player's 300 m dead ahead - asked for, the lock on it builds up
    /// and holds; asked again, it is let go.
    /// </summary>
    public sealed class TargetLockCheck : RegressionCheck
    {
        public override string Name => Prefix + "target_lock";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(3, 0, -3));
            var ship = Build("target-lock-ship", Site.Origin, false, new[]
            {
                new Part("Cockpit/SmallBlockCockpit", 0, 0, 0),
                new Part("BatteryBlock/SmallBlockBatteryBlock", 0, 0, -3).With(b => Motion.Charged(b)),
                new Part("SmallGatlingGun/", 0, -1, 0),
            }, MyCubeSize.Small);
            var seat = Block<MyCockpit>(ship);
            var target = BuildFor(WorldApi.TestIdentityId(), "target-lock-target", seat.PositionComp.GetPosition() + seat.WorldMatrix.Forward * 300,
                Bodies.Wall("CubeBlock/LargeBlockArmorBlock", 2, 1).Concat(new[] { new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, -1).With(b => Motion.Charged(b)) }).ToArray());
            yield return Sit(seat);
            var locking = Body.TargetLockingComp;
            Check(locking != null, "the character has no target locking");
            Request(typeof(Sandbox.Game.EntityComponents.MyTargetLockingComponent), "OnTargetRequestServer", target.EntityId);
            yield return Until(() => locking.IsTargetLocked ? 1 : 0, v => v > 0, "the lock on the grid ahead", 20,
                () => "target " + locking.TargetData.TargetId + " (the grid " + target.EntityId + "), progress " + F(locking.TargetData.LockingProgress));
            Check(locking.TargetData.TargetId == target.EntityId, "locked on " + locking.TargetData.TargetId + ", not the grid ahead");
            Request(typeof(Sandbox.Game.EntityComponents.MyTargetLockingComponent), "OnTargetRequestServer", target.EntityId);
            yield return Until(() => locking.TargetData.TargetId == 0 ? 1 : 0, v => v > 0, "the lock let go", 5);
            Result("from the cockpit the grid 300 m ahead locked on and let go on the next request");
        }
    }

    /// <summary>
    /// The blocks of a grid handed over to another player's limits (<c>MyBlockLimits.SendTransferRequestMessage</c>, then
    /// <c>TransferBlocksBuiltByID</c> as the other accepts, the Info tab's "transfer"): built by the first, the blocks
    /// count for the second once they accept, the first's PCU go down by as much and the second's up; a third player
    /// accepting it in the second's place is refused.
    /// </summary>
    public sealed class PcuTransferCheck : RegressionCheck
    {
        public override string Name => Prefix + "pcu_transfer";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            int other = -1, third = -1;
            yield return Join(Site.At(4, 4, -6), i => other = i);
            yield return Join(Site.At(-4, 4, -6), i => third = i);
            var otherIdentity = FakeClients.PlayerOf(other).Identity;
            var mine = Player.Identity;
            var grid = Build("pcu-transfer", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            Check(mine.BlockLimits.BlocksBuiltByGrid.TryGetValue(grid.EntityId, out var data), "the grid is not among the player's built grids");
            var pcuMine = mine.BlockLimits.PCUBuilt;
            var pcuOther = otherIdentity.BlockLimits.PCUBuilt;
            var gridPcu = data.PCUBuilt;
            Request(typeof(MyBlockLimits), "SendTransferRequestMessage", data, otherIdentity.IdentityId);
            RequestAs(third, typeof(MyBlockLimits), "TransferBlocksBuiltByID", grid.EntityId, mine.IdentityId, otherIdentity.IdentityId);
            yield return WaitForTicks(2);
            Check(grid.GetBlocks().All(b => b.BuiltBy == mine.IdentityId), "a third player accepting the transfer in the other's place moved the blocks");
            RequestAs(other, typeof(MyBlockLimits), "TransferBlocksBuiltByID", grid.EntityId, mine.IdentityId, otherIdentity.IdentityId);
            yield return Until(() => grid.GetBlocks().All(b => b.BuiltBy == otherIdentity.IdentityId) ? 1 : 0, v => v > 0, "the blocks built by the other player", 5);
            Check(mine.BlockLimits.PCUBuilt == pcuMine - gridPcu, "the first player's PCU went " + pcuMine + " -> " + mine.BlockLimits.PCUBuilt + ", the grid has " + gridPcu);
            Check(otherIdentity.BlockLimits.PCUBuilt == pcuOther + gridPcu, "the other player's PCU went " + pcuOther + " -> " + otherIdentity.BlockLimits.PCUBuilt + ", the grid has " + gridPcu);
            Result("a grid of " + gridPcu + " PCU handed over: refused to a third player, the other's once they accepted, the PCU moved with it");
        }
    }

    /// <summary>
    /// Hydrogen bought at an NPC station's store (<c>MyStoreBlock.BuyGasStation</c>: a gas offer is sold in units of
    /// 1000 L into the buyer's tank): the player's empty hydrogen tank by the store gets the litres bought, the price
    /// goes from the player.
    /// </summary>
    public sealed class StoreGasCheck : StationServiceCheck
    {
        public override string Name => Prefix + "store_gas";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            yield return Setup(Site.At(30, 0, 0));
            var storeGrid = BuildFor(Faction.FounderId, "store-gas-station", Site.Origin,
                new Part("StoreBlock/StoreBlock", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var store = Block<MyStoreBlock>(storeGrid);
            yield return Until(() => store.IsWorking ? 1 : 0, v => v > 0, "the store working", 30, () => State(store));
            // the store's grid as the station (the services terminal's station set up the faction for it)
            Station.StationEntityId = storeGrid.EntityId;
            var offer = new MyStoreItem(VRage.MyEntityIdentifier.AllocateId(VRage.MyEntityIdentifier.ID_OBJECT_TYPE.STORE_ITEM), 10, 100, StoreItemTypes.Offer, ItemTypes.Hydrogen);
            Station.StoreItems.Add(offer);
            var tankGrid = Build("store-gas-tank", Site.At(0, 0, 8), true,
                new Part("OxygenTank/LargeHydrogenTankSmall", 0, 0, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_OxygenTank)b).FilledRatio = 0f),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -2, 0).With(b => Motion.Charged(b)));
            var tank = Block<Sandbox.Game.Entities.Blocks.MyGasTank>(tankGrid);
            MyBankingSystem.ChangeBalance(Identity, 1000000);
            var economy = MySession.Static.GetComponent<MySessionComponentEconomy>();
            var tick = (double)typeof(MySessionComponentEconomy).GetProperty("LastEconomyTick", Any).GetValue(economy);
            var money0 = MyBankingSystem.GetBalance(Identity);
            StandAt(store.PositionComp.GetPosition() + Site.Forward * -2, store.PositionComp.GetPosition());
            Request(store, "BuyItem", offer.Id, 2, tank.EntityId, tick);
            yield return Until(() => tank.FilledRatio * tank.Capacity, v => v >= 1999, "2000 L of hydrogen in the player's tank", 10,
                () => "the tank " + F(tank.FilledRatio * tank.Capacity) + " of " + F(tank.Capacity) + " L, the offer has " + offer.Amount);
            var paid = money0 - MyBankingSystem.GetBalance(Identity);
            Check(paid > 0, "the player paid nothing for the hydrogen");
            Check(offer.Amount == 8, "the offer has " + offer.Amount + " units left of 10, 2 sold");
            Result("2 units of hydrogen bought at the station: 2000 L in the player's tank, " + paid + " paid, 8 left on offer");
        }
    }

    /// <summary>
    /// Settings a client's terminal and HUD send as requests of their own (not through the terminal's property sync): a
    /// sorter's filter (<c>MyConveyorSorter.DoSetupFilter</c>), a warhead's countdown (<c>MyWarhead.SetCountdown</c>), a
    /// button's name (<c>MyButtonPanel.SetButtonName</c>), an antenna's HUD text (<c>SetHudTextEvent</c>), an LCD's text
    /// (<c>MyLcdSurfaceComponent.OnChangeTextRequest</c>), title (<c>MyTextPanel.OnChangeTitle</c>), custom data
    /// (<c>MyTerminalBlock.OnCustomDataChanged</c>) and images (<c>OnSelectImageRequest</c>,
    /// <c>OnRemoveSelectedImageRequest</c>), a GPS mark hidden from the HUD and made always visible
    /// (<c>MyGpsCollection.ChangeShowOnHudOnServer</c>, <c>ChangeAlwaysVisibleOnServer</c>), the sorter's list edited
    /// (<c>DoChangeListId</c>, <c>DoChangeBlWl</c>) - each takes on the server. (A mark's colour
    /// a dedicated server does not take at all: <c>ChangeColorOnServer</c> applies it only when not dedicated.)
    /// </summary>
    public sealed class TerminalRequestsCheck : RegressionCheck
    {
        public override string Name => Prefix + "terminal_requests";

        private int _gps;

        private const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance |
                                                           System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var grid = Build("terminal-requests", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("ConveyorSorter/LargeBlockConveyorSorter", 1, 0, 0),
                new Part("Warhead/LargeWarhead", -1, 0, 0),
                new Part("ButtonPanel/ButtonPanelLarge", 0, 1, 0),
                new Part("RadioAntenna/LargeBlockRadioAntenna", 0, 0, 2),
                new Part("TextPanel/LargeLCDPanel", 0, -1, 0));
            var sorter = Block<MyConveyorSorter>(grid);
            var setup = typeof(MyConveyorSorter).GetMethod("DoSetupFilter", Any);
            Check(setup != null, "MyConveyorSorter.DoSetupFilter is not in this game");
            var mode = Enum.Parse(setup.GetParameters()[0].ParameterType, "Whitelist");
            var filters = new List<MyTuple<SerializableDefinitionId, bool>> { new MyTuple<SerializableDefinitionId, bool>((SerializableDefinitionId)Item("Ore/Iron"), false) };
            Request(sorter, "DoSetupFilter", mode, filters);
            var list = new List<Sandbox.ModAPI.Ingame.MyInventoryItemFilter>();
            ((Sandbox.ModAPI.Ingame.IMyConveyorSorter)sorter).GetFilterList(list);
            Check(sorter.IsWhitelist && list.Count == 1 && list[0].ItemId.SubtypeName == "Iron", "the sorter's filter is not a whitelist of iron ore: " + sorter.IsWhitelist + ", " + list.Count + " entries");
            // the terminal's own edits of the list: an item added, the list made a blacklist
            Request(sorter, "DoChangeListId", (SerializableDefinitionId)Item("Ore/Nickel"), true);
            Request(sorter, "DoChangeBlWl", false);
            list.Clear();
            ((Sandbox.ModAPI.Ingame.IMyConveyorSorter)sorter).GetFilterList(list);
            Check(!sorter.IsWhitelist && list.Count == 2 && list.Any(f => f.ItemId.SubtypeName == "Nickel"), "the sorter's filter is not a blacklist of iron and nickel ore: " + sorter.IsWhitelist + ", " + list.Count + " entries");

            var warhead = Block<Sandbox.Game.Entities.Cube.MyWarhead>(grid);
            Request(warhead, "SetCountdown", true);
            Check(((Sandbox.ModAPI.Ingame.IMyWarhead)warhead).IsCountingDown, "the warhead's countdown did not start");
            Request(warhead, "SetCountdown", false);
            Check(!((Sandbox.ModAPI.Ingame.IMyWarhead)warhead).IsCountingDown, "the warhead's countdown did not stop");

            var panel = Block<SpaceEngineers.Game.Entities.Blocks.MyButtonPanel>(grid);
            Request(panel, "SetButtonName", "reg-button", 0);
            Check(panel.GetCustomButtonName(0) == "reg-button", "the button is named '" + panel.GetCustomButtonName(0) + "'");

            var antenna = Block<MyRadioAntenna>(grid);
            Request(antenna, "SetHudTextEvent", "reg-hud");
            Check(antenna.HudText.ToString() == "reg-hud", "the antenna's HUD text is '" + antenna.HudText + "'");

            var lcd = Block<MyTextPanel>(grid);
            // an LCD panel's own surface (MyLcdSurfaceComponent); a block of several screens has MultiTextPanel
            object surface = lcd.MultiTextPanel ?? (object)lcd.Components.Get<SpaceEngineers.Game.EntityComponents.Blocks.MyLcdSurfaceComponent>();
            Check(surface != null, "the LCD has no text surface component");
            Request(surface, "OnChangeTextRequest", 0, "reg-lcd text");
            Check(((Sandbox.ModAPI.Ingame.IMyTextSurface)lcd).GetText() == "reg-lcd text", "the LCD shows '" + ((Sandbox.ModAPI.Ingame.IMyTextSurface)lcd).GetText() + "'");
            // the panel's title and the block's custom data, each a request of its own
            Request(lcd, "OnChangeTitle", "reg-title", true);
            Check(lcd.PublicTitle.ToString() == "reg-title", "the LCD's title is '" + lcd.PublicTitle + "'");
            Request(lcd, "OnCustomDataChanged", "reg-data");
            Check(((Sandbox.ModAPI.Ingame.IMyTerminalBlock)lcd).CustomData == "reg-data", "the LCD's custom data is '" + ((Sandbox.ModAPI.Ingame.IMyTerminalBlock)lcd).CustomData + "'");
            // images picked from the list and one taken off again
            var images = new List<string>();
            Request(surface, "OnSelectImageRequest", 0, new[] { 0, 1 });
            ((Sandbox.ModAPI.Ingame.IMyTextSurface)lcd).GetSelectedImages(images);
            Check(images.Count == 2, "the LCD has " + images.Count + " images selected, not 2");
            var kept = images[1];
            Request(surface, "OnRemoveSelectedImageRequest", 0, new[] { 0 });
            images.Clear();
            ((Sandbox.ModAPI.Ingame.IMyTextSurface)lcd).GetSelectedImages(images);
            Check(images.Count == 1 && images[0] == kept, "after one image taken off the LCD has " + string.Join(", ", images));

            var gpss = MySession.Static.Gpss;
            var type = typeof(MyGpsCollection);
            var msg = Activator.CreateInstance(type.GetNestedType("AddMsg", Any));
            void Set(string field, object value) => msg.GetType().GetField(field, Any).SetValue(msg, value);
            Set("IdentityId", Identity); Set("Name", "reg-terminal mark"); Set("DisplayName", "reg-terminal mark"); Set("Description", "");
            Set("Coords", Site.At(100, 0, 0)); Set("ShowOnHud", true); Set("IsFinal", true); Set("GPSColor", Color.Yellow);
            FakeClients.AsClient(Me, () => type.GetMethod("AddGpsOnServer", Any).Invoke(null, new[] { msg }));
            var mark = gpss[Identity].Values.FirstOrDefault(g => g.Name == "reg-terminal mark");
            Check(mark != null, "the GPS mark was not made");
            _gps = mark.Hash;
            Request(typeof(MyGpsCollection), "ChangeShowOnHudOnServer", mark.Hash, false);
            var now = gpss[Identity][mark.Hash];
            Check(!now.ShowOnHud, "the GPS mark is still on the HUD");
            Request(typeof(MyGpsCollection), "ChangeAlwaysVisibleOnServer", mark.Hash, true);
            Check(now.AlwaysVisible && now.ShowOnHud, "the GPS mark made always visible: always " + now.AlwaysVisible + ", on the HUD " + now.ShowOnHud);
            Result("sorter whitelist set and made a blacklist, warhead countdown started and stopped, button named, antenna HUD text set, LCD text, title, custom data written and images picked, GPS mark hidden from the HUD and made always visible - all by the client's requests");
        }

        public override void Cleanup()
        {
            try
            {
                if (_gps != 0 && Me >= 0) FakeClients.AsClient(Me, () => typeof(MyGpsCollection).GetMethod("DeleteGpsOnServer", Any).Invoke(null, new object[] { _gps }));
            }
            catch { }
            base.Cleanup();
        }
    }

    /// <summary>
    /// A turret controller tracking the sun (<c>MyTurretControlBlock.ChangeSunTrackingEnabled</c>, <c>MySunTrackingComponent
    /// .AimToSun</c>, what solar arrays on rotors use): the custom turret of <c>reg_turret_controller</c> with sun tracking
    /// on turns its rotors until its direction source points at the sun.
    /// </summary>
    public sealed class SunTrackingCheck : RegressionCheck
    {
        public override string Name => Prefix + "sun_tracking";
        public override int TimeoutSeconds => 120;

        protected override IEnumerator Check()
        {
            var sun = (Vector3D)Sandbox.Game.World.MySector.DirectionToSunNormalized;
            Check(sun.LengthSquared() > 0.5, "the world has no sun");
            yield return Arrive(Site.At(-15, 10, -15));
            var group = WorldApi.LoadAuthoredGroup(TurretControllerCheck.ResourceName, WorldApi.EntityPrefix + "custom-turret");
            var base0 = group[0].PositionAndOrientation.Value.GetMatrix();
            // the base turned so that the sun stands about 45 degrees over its horizon: the elevation rotor reaches it
            // whatever time of the game's day it is (straight up from the site's floor it may be past the rotor's reach)
            var tilt = Vector3D.CalculatePerpendicularVector(sun);
            var up = Vector3D.Normalize(sun + tilt);
            var forward = Vector3D.Normalize(Vector3D.Reject(tilt, up));
            var move = MatrixD.Invert(base0) * MatrixD.CreateWorld(Site.Origin, forward, up);
            foreach (var ob in group)
            {
                ob.PositionAndOrientation = new MyPositionAndOrientation(ob.PositionAndOrientation.Value.GetMatrix() * move);
                foreach (var block in ob.CubeBlocks) { block.Owner = Identity; block.BuiltBy = Identity; }
            }
            var grids = group.Select(WorldApi.SpawnGrid).ToList();
            foreach (var grid in grids) Track(grid);
            foreach (var grid in grids) WorldApi.ChargeBatteries(grid);
            var controller = grids.SelectMany(g => g.GetFatBlocks()).First(b => b.BlockDefinition.Id.TypeId.ToString().EndsWith("TurretControlBlock"));
            var turret = (SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock)controller;
            yield return Until(() => controller.IsWorking && turret.AzimuthRotor != null && turret.ElevationRotor != null && turret.GetDirectionSource() != null ? 1 : 0, v => v > 0,
                "the turret controller working with its rotors and direction source", 15, () => State(controller));
            var source = (MyCubeBlock)turret.GetDirectionSource();
            // the sun as it is now: it turns a full circle in the game's day
            double Off() => MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(source.WorldMatrix.Forward, (Vector3D)Sandbox.Game.World.MySector.DirectionToSunNormalized), -1, 1)));
            var before = Off();
            Request(controller, "ChangeSunTrackingEnabled", true);
            yield return Until(Off, v => v < 5, "the direction source turned to the sun, degrees off", 60,
                () => F(Off()) + " degrees off; azimuth " + F(MathHelper.ToDegrees(turret.AzimuthRotor.Angle)) + ", elevation " + F(MathHelper.ToDegrees(turret.ElevationRotor.Angle)));
            Request(controller, "ChangeSunTrackingEnabled", false);
            Result("sun tracking on: the turret's direction source went from " + F(before) + " to " + F(Off()) + " degrees off the sun");
        }
    }

    /// <summary>
    /// A parachute's auto deploy (<c>MyParachute.AutoDeployRequest</c>, <c>DeployHeightRequest</c>, the terminal's
    /// settings): set to open by itself at 250 m, a small ship falling from 600 m through the air gets its parachute
    /// opened on the way down, near that height.
    /// </summary>
    public sealed class ParachuteAutoDeployCheck : RegressionCheck
    {
        public override string Name => Prefix + "parachute_autodeploy";
        public override SiteKind Kind => SiteKind.Planet;
        public override int TimeoutSeconds => 120;

        protected override IEnumerator Check()
        {
            Check(Site.Planet.HasAtmosphere, "the planet of the planet sites has no atmosphere");
            var ground = Site.Ground(Site.Origin + Site.Right * 40);
            yield return Arrive(ground + Site.Up * 400 + Site.Right * 40 + Site.Forward * 20);
            FakeClients.SetFree(Me, true);
            var ship = Build("parachute-auto", ground + Site.Up * 600, false, new[]
            {
                new Part("BatteryBlock/SmallBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("Parachute/SmParachute", 1, 2, 1),
                new Part("CubeBlock/SmallBlockArmorBlock", 1, -1, 1),
            }, MyCubeSize.Small);
            var chute = ship.GetFatBlocks().First(b => b.BlockDefinition.Id.SubtypeName == "SmParachute");
            Put(Inventory(chute), "Component/Canvas", 1);
            var ingame = (SpaceEngineers.Game.ModAPI.Ingame.IMyParachute)chute;
            Request(chute, "AutoDeployRequest", true, Identity);
            Request(chute, "DeployHeightRequest", 250f, Identity);
            Check(ingame.AutoDeploy, "auto deploy did not switch on");
            double Height() => Vector3D.Dot(ship.PositionComp.GetPosition() - ground, Site.Up);
            var opened = 0.0;
            yield return Until(() =>
                {
                    Keys(Vector3.Zero, jetpack: true, dampeners: true);
                    if (ingame.Status == Sandbox.ModAPI.Ingame.DoorStatus.Open || ingame.Status == Sandbox.ModAPI.Ingame.DoorStatus.Opening)
                    {
                        if (opened == 0) opened = Height();
                        return 1;
                    }
                    return 0;
                }, v => v > 0, "the parachute opening by itself", 60, () => ingame.Status + ", " + F(Height()) + " m over the ground, " + F(Motion.Speed(ship)) + " m/s");
            Check(opened < 450 && opened > 50, "the parachute opened at " + F(opened) + " m, set to 250 m");
            Result("set to open at 250 m, the parachute opened by itself at " + F(opened) + " m over the ground");
        }
    }

    /// <summary>
    /// A blueprint loaded into a projector and the projection removed, as the terminal does it
    /// (<c>MyProjectorBase.OnNewBlueprintSuccess</c>, <c>OnRemoveProjectionRequest</c>): an empty projector of the
    /// player's shows the two blocks of armor sent; the network refuses another player's load into it; removing the projection
    /// leaves the projector empty.
    /// </summary>
    public sealed class ProjectorLoadCheck : RegressionCheck
    {
        public override string Name => Prefix + "projector_load";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            int other = -1;
            yield return Join(Site.At(4, 4, -6), i => other = i);
            var grid = Build("projector-load", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("MyObjectBuilder_Projector/LargeProjector", 1, 0, 0));
            var projector = Block<MyProjectorBase>(grid);
            Check(projector.ProjectedGrid == null, "the new projector already shows a projection");
            List<MyObjectBuilder_CubeGrid> Blueprint(int blocks) => new List<MyObjectBuilder_CubeGrid>
            {
                new MyObjectBuilder_CubeGrid
                {
                    GridSizeEnum = MyCubeSize.Large,
                    CubeBlocks = Enumerable.Range(0, blocks).Select(x => (MyObjectBuilder_CubeBlock)new MyObjectBuilder_CubeBlock { SubtypeName = "LargeBlockArmorBlock", Min = new Vector3I(x, 0, 0) }).ToList(),
                },
            };
            var stranger = Rights(other, projector, "OnNewBlueprintSuccess");
            Check(stranger != VRage.Network.ValidationResult.Passed, "the network lets another player load a blueprint into the player's projector");
            Check(Rights(Me, projector, "OnNewBlueprintSuccess") == VRage.Network.ValidationResult.Passed, "the network refuses the owner's blueprint");
            Request(projector, "OnNewBlueprintSuccess", Blueprint(2));
            yield return Until(() => projector.ProjectedGrid?.CubeBlocks.Count ?? 0, v => v > 0, "the projection's blocks", 15);
            Check(projector.ProjectedGrid.CubeBlocks.Count == 2, "the projection has " + projector.ProjectedGrid.CubeBlocks.Count + " blocks, not the blueprint's 2");
            Request(projector, "OnRemoveProjectionRequest");
            yield return Until(() => projector.ProjectedGrid == null ? 1 : 0, v => v > 0, "the projection removed", 10);
            Result("the player's blueprint of 2 blocks projected and removed; another player's load refused (" + stranger + ")");
        }
    }
}
