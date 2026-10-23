using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Contracts;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.GameSystems.Trading;
using Sandbox.Game.Replication;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.Game.World.Generator;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Library.Collections;
using VRage.Game.ObjectBuilders.Components.Trading;
using VRage.Network;
using VRage.ObjectBuilders;
using VRage.Serialization;
using VRage.Utils;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A trade between two players (<c>MyTradingManager</c>, the requests of the trade window): one asks, the other
    /// agrees, each offers (plates and credits for ore), both accept - the items and the credits change hands. Asked
    /// from 10 m off, the trade does not start (the game wants 5 m).
    /// </summary>
    public sealed class TradeWindowCheck : RegressionCheck
    {
        public override string Name => Prefix + "trade_window";

        private static readonly BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static MyObjectBuilder_SubmitOffer Offer(long credits, params (string Item, int Amount)[] items) => new MyObjectBuilder_SubmitOffer
        {
            CurrencyAmount = credits,
            InventoryItems = items.Select(i => new MyObjectBuilder_InventoryItem
            {
                Amount = i.Amount,
                PhysicalContent = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(Item(i.Item)),
            }).ToList(),
        };

        protected override IEnumerator Check()
        {
            SkipUnless(MyTradingManager.Static != null, "no trading in this session");
            var trades = typeof(MyTradingManager).GetField("m_activePlayerTrades", Any)?.GetValue(MyTradingManager.Static) as IDictionary
                         ?? throw new Core.ScenarioFailedException("MyTradingManager.m_activePlayerTrades is not in this game");
            yield return Arrive(Site.At(0, 0, -3));
            var other = -1;
            yield return Join(Site.At(10, 0, -3), i => other = i);
            FakeClients.SetFree(other, false);
            var me = Player.Id.SteamId;
            var them = FakeClients.PlayerOf(other).Id.SteamId;
            var otherIdentity = FakeClients.PlayerOf(other).Identity.IdentityId;

            // 10 m apart: not started
            RequestAs(Me, typeof(MyTradingManager), "TradeRequest_Server", them);
            Check(!trades.Contains(me), "a trade started between players 10 m apart");

            FakeClients.MoveTo(other, Site.At(2, 0, -3));
            yield return Seconds(0.5);
            MyBankingSystem.ChangeBalance(Identity, 5000);
            var mine = Inventory(Body);
            var theirs = Inventory(FakeClients.Character(other));
            Put(mine, "Component/SteelPlate", 10);
            Put(theirs, "Ore/Iron", 50);
            var money0 = MyBankingSystem.GetBalance(Identity);
            var theirMoney0 = MyBankingSystem.GetBalance(otherIdentity);

            RequestAs(Me, typeof(MyTradingManager), "TradeRequest_Server", them);
            Check(trades.Contains(me) && trades.Contains(them), "the trade was not started for the two players 2 m apart");
            RequestAs(other, typeof(MyTradingManager), "TradeRequest_StartTrade_Server", MyTradeResponseReason.Ok);
            RequestAs(Me, typeof(MyTradingManager), "SubmitTradingOffer_Server", Offer(1000, ("Component/SteelPlate", 10)));
            RequestAs(other, typeof(MyTradingManager), "SubmitTradingOffer_Server", Offer(0, ("Ore/Iron", 50)));
            RequestAs(Me, typeof(MyTradingManager), "AcceptOffer_Server", true);
            RequestAs(other, typeof(MyTradingManager), "AcceptOffer_Server", true);
            yield return WaitForTicks(2);

            Check(!trades.Contains(me), "the trade is still open after both accepted");
            Check(Amount(mine, "Component/SteelPlate") == 0 && Amount(theirs, "Component/SteelPlate") == 10,
                "the plates did not change hands: the offerer has " + Amount(mine, "Component/SteelPlate") + ", the other " + Amount(theirs, "Component/SteelPlate"));
            Check(Amount(theirs, "Ore/Iron") == 0 && Amount(mine, "Ore/Iron") == 50,
                "the ore did not change hands: the other has " + Amount(theirs, "Ore/Iron") + ", the offerer " + Amount(mine, "Ore/Iron"));
            var paid = money0 - MyBankingSystem.GetBalance(Identity);
            var got = MyBankingSystem.GetBalance(otherIdentity) - theirMoney0;
            Check(paid == 1000 && got == 1000, "1000 credits offered: " + paid + " went from the offerer, " + got + " came to the other");
            Result("not started from 10 m; at 2 m the trade went through: 10 plates and 1000 credits for 50 kg of ore");
        }
    }

    /// <summary>
    /// Pasting a grid (<c>MyCubeGrid.TryPasteGrid_Implementation</c>, the client's Ctrl+V): a player without creative tools
    /// is refused, nothing appears; an admin with creative tools on gets the grid where it was pasted.
    /// </summary>
    public sealed class GridPasteCheck : RegressionCheck
    {
        public override string Name => Prefix + "grid_paste";

        private ulong _admin;

        private static object Parameters(MyObjectBuilder_CubeGrid grid)
        {
            var type = typeof(MyCubeGrid).GetNestedType("MyPasteGridParameters", BindingFlags.Public | BindingFlags.NonPublic)
                       ?? throw new Core.ScenarioFailedException("MyCubeGrid.MyPasteGridParameters is not in this game");
            var constructor = type.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 6)
                              ?? throw new Core.ScenarioFailedException("MyPasteGridParameters has no constructor of 6");
            var offsetType = constructor.GetParameters()[4].ParameterType;
            return constructor.Invoke(new object[] { new List<MyObjectBuilder_CubeGrid> { grid }, false, Vector3.Zero, false, Activator.CreateInstance(offsetType), new List<ulong>() });
        }

        private MyObjectBuilder_CubeGrid Blueprint(string name, Vector3D at) => new MyObjectBuilder_CubeGrid
        {
            Name = WorldApi.EntityPrefix + name,
            DisplayName = WorldApi.EntityPrefix + name,
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = true,
            PositionAndOrientation = new MyPositionAndOrientation(at, (Vector3)Site.Forward, (Vector3)Site.Up),
            PersistentFlags = MyPersistentEntityFlags2.InScene,
            CubeBlocks = new List<MyObjectBuilder_CubeBlock>
            {
                new MyObjectBuilder_CubeBlock { SubtypeName = "LargeBlockArmorBlock", Min = Vector3I.Zero, Owner = Identity, BuiltBy = Identity },
            },
        };

        private static MyCubeGrid Pasted(string name) =>
            MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !g.MarkedForClose && g.DisplayName == WorldApi.EntityPrefix + name);

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -6));
            var steamId = Player.Id.SteamId;
            Check(!MySession.Static.IsCopyPastingEnabledForUser(steamId), "a plain player may paste grids in this world");
            Request(typeof(MyCubeGrid), "TryPasteGrid_Implementation", Parameters(Blueprint("paste-refused", Site.At(0, 0, 10))));
            yield return Seconds(2);
            Check(Pasted("paste-refused") == null, "a player without creative tools pasted a grid");

            MySession.Static.SetUserPromoteLevel(steamId, MyPromoteLevel.Admin);
            _admin = steamId;
            // the player's creative tools on, as their client's switch sets them (OnCreativeToolsEnabled from that client):
            // MySession.EnableCreativeTools called on the server puts them on for the server itself too, and the server's
            // own creative tools take nothing from any inventory for the while (every other check's welding and grinding)
            MySession.Static.CreativeTools.Add(steamId);
            Check(MySession.Static.IsCopyPastingEnabledForUser(steamId), "an admin with creative tools may not paste");
            Request(typeof(MyCubeGrid), "TryPasteGrid_Implementation", Parameters(Blueprint("paste-allowed", Site.At(0, 0, 10))));
            MyCubeGrid grid = null;
            yield return Until(() => (grid = Pasted("paste-allowed")) != null ? 1 : 0, v => v > 0, "the admin's grid pasted", 15);
            Track(grid);
            Check(Vector3D.Distance(grid.PositionComp.GetPosition(), Site.At(0, 0, 10)) < 5, "the pasted grid is " +
                  F(Vector3D.Distance(grid.PositionComp.GetPosition(), Site.At(0, 0, 10))) + " m from where it was pasted");
            Result("a plain player's paste refused; an admin with creative tools pasted a grid where it was asked");
        }

        public override void Cleanup()
        {
            try
            {
                if (_admin != 0)
                {
                    MySession.Static.CreativeTools.Remove(_admin);
                    MySession.Static.SetUserPromoteLevel(_admin, MyPromoteLevel.None);
                }
                foreach (var name in new[] { "paste-refused", "paste-allowed" })
                    Pasted(name)?.Close();
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// A block's skin (<c>MyCubeGrid.SkinBlockRequest</c>, the paint tool with a skin): the server takes the skin only
    /// when the player owns it (<c>MySessionComponentGameInventory.ValidateItem</c>) - one the player does not own is left
    /// off, and the color sent with it is applied all the same.
    /// </summary>
    public sealed class BlockSkinCheck : RegressionCheck
    {
        public override string Name => Prefix + "block_skin";

        protected override IEnumerator Check()
        {
            var skin = MyDefinitionManager.Static.GetAssetModifierDefinitions()
                .FirstOrDefault(d => d.Id.SubtypeName.EndsWith("_Armor"));
            SkipUnless(skin != null, "no armor skins in this game");
            var inventory = MySession.Static.GetComponent<MySessionComponentGameInventory>();
            yield return Arrive(Site.At(0, 0, -4));
            Check(inventory == null || inventory.ValidateItem(skin.Id.SubtypeId, Player.Id.SteamId) == MyStringHash.NullOrEmpty,
                "the fake player owns the skin " + skin.Id.SubtypeName);
            var grid = Build("block-skin", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            var block = grid.GetCubeBlock(Vector3I.Zero);
            var color = new Vector3(0.5f, 0.2f, 0.1f);
            var visuals = new MyCubeGrid.MyBlockVisuals(color.PackHSVToUint(), skin.Id.SubtypeId, true, true);
            Request(grid, "SkinBlockRequest", Vector3I.Zero, Vector3I.Zero, visuals, false);
            yield return WaitForTicks(2);
            Check(block.SkinSubtypeId != skin.Id.SubtypeId, "the skin the player does not own was put on the block");
            Check(Vector3.Distance(block.ColorMaskHSV, ColorExtensions.UnpackHSVFromUint(color.PackHSVToUint())) < 0.01f,
                "the color sent with the skin was not applied: " + block.ColorMaskHSV);
            Result("an unowned skin (" + skin.Id.SubtypeName + ") left off the block, the color with it applied");
        }
    }

    /// <summary>
    /// A cockpit's toolbar (<c>MyShipController.SendToolbarItemChanged</c>, the client dragging a block's action onto a
    /// slot): the slot holds the action, the save has it, the slot used switches the block off; the slot cleared
    /// (<c>SendToolbarItemRemoved</c>) is empty.
    /// </summary>
    public sealed class CockpitToolbarCheck : RegressionCheck
    {
        public override string Name => Prefix + "cockpit_toolbar";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -4));
            var grid = Build("cockpit-toolbar", Site.Origin, true,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)),
                new Part("InteriorLight/SmallLight", 1, 0, 0));
            var seat = Block<MyCockpit>(grid);
            var light = Block<MyLightingBlock>(grid);
            Check(light.Enabled, "the light is off from the start");
            var item = new Sandbox.Common.ObjectBuilders.MyObjectBuilder_ToolbarItemTerminalBlock { BlockEntityId = light.EntityId, _Action = "OnOff_Off" };
            Request(seat, "SendToolbarItemChanged", item, 0, false, 0);
            var slot = seat.Toolbar.GetSlotItem(0) as Sandbox.Game.Screens.Helpers.MyToolbarItemTerminalBlock;
            Check(slot != null && slot.BlockEntityId == light.EntityId, "the slot does not hold the light's action");
            var saved = ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_Cockpit)seat.GetObjectBuilderCubeBlock(true)).Toolbar?.Slots?
                .Where(s => s.Index == 0).Select(s => s.Data).FirstOrDefault() as Sandbox.Common.ObjectBuilders.MyObjectBuilder_ToolbarItemTerminalBlock;
            Check(saved != null && saved.BlockEntityId == light.EntityId && saved._Action == "OnOff_Off", "the cockpit's save has no light action in the slot");
            yield return Sit(seat);
            seat.Toolbar.ActivateItemAtSlot(0);
            yield return WaitForTicks(2);
            Check(!light.Enabled, "the slot used did not switch the light off");
            Request(seat, "SendToolbarItemRemoved", 0, false, 0);
            Check(seat.Toolbar.GetSlotItem(0) == null, "the cleared slot still holds an item");
            Result("the light's action put on slot 1, saved, used from the seat (light off), cleared");
        }
    }

    /// <summary>
    /// Procedural asteroids (<c>MyProceduralWorldGenerator</c>): a player in open space far from everything the world
    /// has gets asteroids of the world's seed made around them - voxel maps that were not there before.
    /// </summary>
    public sealed class ProceduralAsteroidsCheck : RegressionCheck
    {
        public override string Name => Prefix + "procedural_asteroids";
        public override int TimeoutSeconds => 120;

        protected override IEnumerator Check()
        {
            SkipUnless(MyProceduralWorldGenerator.Static != null && MyProceduralWorldGenerator.Static.Enabled && MySession.Static.Settings.ProceduralDensity > 0,
                "procedural asteroids are off in this world");
            // a point 300 km out, out of every planet's gravity, with no voxel map near it
            Vector3D? far = null;
            var rng = new Random(Site.Index * 7919 + DateTime.UtcNow.Minute);
            for (var i = 0; i < 50 && far == null; i++)
            {
                var direction = Vector3D.Normalize(new Vector3D(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5));
                var point = Site.Origin + direction * 300000;
                var planet = MyGamePruningStructure.GetClosestPlanet(point);
                if (planet != null && Vector3D.Distance(planet.PositionComp.GetPosition(), point) < planet.MaximumRadius * 3) continue;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(point).LengthSquared() > 0) continue;
                if (Near(point, 20000).Count > 0) continue;
                far = point;
            }
            SkipUnless(far.HasValue, "no empty point 300 km out to go to");
            yield return Arrive(Site.At(0, 0, -3));
            FakeClients.MoveTo(Me, far.Value);
            Body.PositionComp.SetPosition(far.Value);
            List<MyVoxelBase> made = null;
            yield return Until(() => (made = Near(far.Value, 20000)).Count, v => v > 0, "asteroids made around the player 300 km out", 60);
            Result(made.Count + " asteroids made within 20 km of the player out in empty space, the nearest " +
                   F(made.Min(v => Vector3D.Distance(v.PositionComp.GetPosition(), far.Value))) + " m off");
        }

        private static List<MyVoxelBase> Near(Vector3D point, double radius) =>
            MyEntities.GetEntities().OfType<MyVoxelBase>().Where(v => !(v is MyPlanet) && !v.MarkedForClose &&
                                                                     Vector3D.Distance(v.PositionComp.GetPosition(), point) < radius).ToList();
    }

    /// <summary>The station of an NPC faction of the economy a contract check makes for itself, with its contract block, as <see cref="NpcContractCheck"/> does.</summary>
    public abstract class NpcStationContractCheck : RegressionCheck
    {
        protected const int Reward = 2500;
        protected MySessionComponentContractSystem Contracts;
        protected MyFaction Faction;
        protected MyFactionStation Station;
        protected MyContractBlock Block;
        protected long ContractId;

        protected static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        protected IEnumerator Setup(Vector3D at)
        {
            SkipUnless(MySession.Static.Settings.EnableEconomy, "the economy is off in this world");
            Contracts = MySession.Static.GetComponent<MySessionComponentContractSystem>();
            SkipUnless(Contracts != null, "no contract system in this world");
            // an NPC faction of the economy other than the first trader, whose money npc_station and others count to the credit
            var npc = MySession.Static.Factions.Select(f => f.Value).OfType<MyFaction>()
                .Where(f => f.Stations.Count > 0 && MySession.Static.Players.IdentityIsNpc(f.FounderId) && (f.FactionTypeString == "Trader" || f.FactionTypeString == "Miner" ||
                                                                                                       f.FactionTypeString == "Builder")).ToList();
            var firstTrader = npc.FirstOrDefault(f => f.FactionTypeString == "Trader");
            Faction = npc.FirstOrDefault(f => f != firstTrader) ?? firstTrader;
            SkipUnless(Faction != null, "no NPC faction of the economy in this world");
            var grid = BuildFor(Faction.FounderId, "npc-contract-station", at,
                new Part("ContractBlock/ContractBlock", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            Block = Block<MyContractBlock>(grid);
            yield return Until(() => Block.IsWorking ? 1 : 0, v => v > 0, "the contract block working", 30, () => State(Block));
            Station = new MyFactionStation(MyEntityIdentifier.AllocateId(MyEntityIdentifier.ID_OBJECT_TYPE.STATION), "Regression contracts", grid.PositionComp.GetPosition(),
                MyStationTypeEnum.SpaceStation, Faction, "", null) { StationEntityId = grid.EntityId };
            Faction.AddStation(Station);
        }

        /// <summary>The station's contract of this data, as the economy makes them (<c>GenerateCustomContract</c>).</summary>
        protected void Generate<T>(T data)
        {
            var generate = typeof(MySessionComponentContractSystem).GetMethods(Any).First(m => m.Name == "GenerateCustomContract"
                && m.GetParameters().Length == 5 && m.GetParameters()[2].ParameterType.IsAssignableFrom(typeof(T)));
            var args = new object[] { null, Station, data, 0L, 0L };
            var made = generate.Invoke(Contracts, args);
            ContractId = (long)args[3];
            if (made.ToString() != "Success")
            {
                var economy = MySession.Static.GetComponent<MySessionComponentEconomy>();
                var pirates = economy == null ? null : MyDefinitionManager.Static.GetDefinition(economy.EconomyDefinition.PirateId) as MyFactionDefinition;
                SkipUnless(false, "the station's contract was not made: " + made + " (the economy's pirates " + (pirates?.Tag ?? "none") +
                                  (pirates == null ? "" : MySession.Static.Factions.Any(f => f.Value.Tag == pirates.Tag) ? ", in the world" : ", not in the world") + ")");
            }
        }

        protected MyContract Active()
        {
            var all = (Dictionary<long, MyContract>)typeof(MySessionComponentContractSystem).GetField("m_activeContracts", Any).GetValue(Contracts);
            return all.TryGetValue(ContractId, out var contract) ? contract : null;
        }

        public override void Cleanup()
        {
            try
            {
                if (ContractId != 0 && Contracts != null && Contracts.InactiveContracts.Concat(Contracts.ActiveContracts).Any(c => c.Id == ContractId))
                    Contracts.RemoveContract(ContractId);
                if (Station != null && typeof(MyFaction).GetField("m_stations", Any)?.GetValue(Faction) is Dictionary<long, IMyFactionStation> stations) stations.Remove(Station.Id);
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// A station's bounty on a player (<c>MyContractHunt</c>): the player taking it kills the target near them - the
    /// contract finishes and pays its reward.
    /// </summary>
    public sealed class ContractHuntCheck : NpcStationContractCheck
    {
        public override string Name => Prefix + "contract_hunt";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -3));
            yield return Setup(Site.Origin);
            var target = -1;
            yield return Join(Site.At(4, 0, -3), i => target = i);
            var targetIdentity = FakeClients.PlayerOf(target).Identity.IdentityId;
            Generate(new Sandbox.ModAPI.Contracts.MyContractBounty(0, Reward, 0, 30, targetIdentity));
            var money0 = MyBankingSystem.GetBalance(Identity);
            Request(Block, "AcceptContract", Identity, ContractId);
            Check(Contracts.IsContractActive(ContractId), "the bounty is not active after the player took it");
            var victim = FakeClients.Character(target);
            victim.DoDamage(victim.StatComp.Health.Value + 100, MyDamageType.Bullet, true, attackerId: Body.EntityId);
            yield return Until(() => Contracts.IsContractActive(ContractId) ? 0 : 1, v => v > 0, "the bounty finished with the target killed", 15,
                () => "the target " + (victim.IsDead ? "dead" : "alive"));
            var paid = MyBankingSystem.GetBalance(Identity) - money0;
            Check(paid == Reward, "the hunter got " + paid + " for the bounty, its reward is " + Reward);
            Result("the bounty taken, the target killed by the hunter: " + paid + " paid");
        }
    }

    /// <summary>
    /// A dug planet sent to a client that the server thinks holds it in its cache (<c>MyVoxelReplicable.Serialize</c>):
    /// the stream says it changed and carries its data, so a client without the cache does not hang on it (the scenario
    /// <c>voxel_cache_resend</c>, at the site's planet).
    /// </summary>
    public sealed class VoxelCacheResendCheck : RegressionCheck
    {
        public override string Name => Prefix + "voxel_cache_resend";
        public override SiteKind Kind => SiteKind.Planet;
        public override int TimeoutSeconds => 120;

        private Vector3D _pit;

        protected override IEnumerator Check()
        {
            var planet = Site.Planet;
            SkipUnless(planet != null, "no planet here");
            var ground = Site.Ground(Site.Origin + Site.Right * 20);
            yield return Arrive(ground + Site.Up * 3 - Site.Forward * 10);
            _pit = ground - Site.Up * 1;
            MyVoxelGenerator.CutOutShapeWithProperties(planet, new MyShapeSphere { Center = _pit, Radius = 3 }, out var cut, out _, null, updateSync: true);
            yield return WaitForTicks(5);
            Check(planet.ContentChanged || planet.BeforeContentChanged, "the planet does not count as changed after " + cut + " was dug");
            var replicable = MyExternalReplicable.FindByObject(planet);
            Check(replicable != null, "the planet has no replicable");
            var serialize = replicable.GetType().GetMethod("Serialize", BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(BitStream), typeof(HashSet<string>), typeof(Endpoint), typeof(Action) }, null);
            Check(serialize != null, "MyVoxelReplicable.Serialize is not in this game");
            var stream = new BitStream();
            stream.ResetWrite();
            var written = false;
            serialize.Invoke(replicable, new object[] { stream, new HashSet<string> { planet.StorageName }, new Endpoint(new EndpointId(1), 0), new Action(() => written = true) });
            yield return Until(() => written ? 1 : 0, v => v > 0, "the planet written for the client", 60);
            stream.ResetRead();
            MySerializer.CreateAndRead<bool>(stream);
            MySerializer.CreateAndRead<bool>(stream);
            var sendContent = MySerializer.CreateAndRead<bool>(stream);
            var contentChanged = MySerializer.CreateAndRead<bool>(stream);
            var bytes = sendContent ? MySerializer.CreateAndRead<byte[]>(stream)?.Length ?? 0 : 0;
            Check(contentChanged, "the stream says the dug planet is unchanged");
            Check(sendContent && bytes > 0, "the dug planet went without its data to a client said to cache it: a client without that cache hangs on it");
            Result("the dug " + planet.StorageName + " went with its data (" + bytes + " bytes) to a client said to hold it");
        }

        public override void CleanupLeftovers()
        {
            if (Site?.Planet != null && _pit != Vector3D.Zero) WorldApi.RevertTerrain(Site.Planet, _pit, 6);
        }
    }

    /// <summary>
    /// Deformation in a crash (<c>MyGridPhysics</c> contacts: the damage of the deformation type on the blocks that
    /// touch): a ship of armor flown at 40 m/s into a station's wall of armor - both take deformation damage.
    /// </summary>
    public sealed class CrashDeformationCheck : RegressionCheck
    {
        public override string Name => Prefix + "crash_deformation";
        protected override bool NeedsDamage => true;

        private static readonly Dictionary<long, double> Taken = new Dictionary<long, double>();
        private static bool _registered;

        protected override IEnumerator Check()
        {
            if (!_registered)
            {
                _registered = true;
                MyDamageSystem.Static.RegisterBeforeDamageHandler(0, (object target, ref VRage.Game.ModAPI.MyDamageInformation info) =>
                {
                    if (info.IsDeformation && target is MySlimBlock block && block.CubeGrid != null)
                        lock (Taken)
                            if (Taken.ContainsKey(block.CubeGrid.EntityId)) Taken[block.CubeGrid.EntityId] += info.Amount;
                });
            }
            HoldHighQuality();
            yield return Arrive(Site.At(10, 0, -10));
            var wall = Build("deformation-wall", Site.At(0, 0, 30), true, Bodies.Wall("CubeBlock/LargeBlockArmorBlock", 2, 2).ToArray());
            var ship = Build("deformation-ship", Site.Origin, false, Bodies.Wall("CubeBlock/LargeBlockArmorBlock", 1, 3).ToArray());
            lock (Taken) { Taken[wall.EntityId] = 0; Taken[ship.EntityId] = 0; }
            ship.Physics.LinearVelocity = (Vector3)(Site.Forward * 40);
            yield return Until(() => { lock (Taken) return Math.Min(Taken[wall.EntityId], Taken[ship.EntityId]); }, v => v > 0, "both grids deformed in the crash", 10,
                () => { lock (Taken) return "wall " + F(Taken[wall.EntityId]) + ", ship " + F(Taken[ship.EntityId]) + "; ship at " + F(Motion.Speed(ship)) + " m/s"; });
            yield return Seconds(1);
            double onWall, onShip;
            lock (Taken) { onWall = Taken[wall.EntityId]; onShip = Taken[ship.EntityId]; Taken.Remove(wall.EntityId); Taken.Remove(ship.EntityId); }
            Result("at 40 m/s into the wall: deformation damage " + F(onWall) + " on the wall, " + F(onShip) + " on the ship");
        }
    }
}
