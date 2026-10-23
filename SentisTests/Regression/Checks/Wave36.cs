using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The services of an NPC station (the services terminal, <c>FunctionalBlock/ServicesTerminal</c>, of a station of an
    /// economy faction, working inside the station's safe zone): what a check of them builds - the station's grid with the
    /// terminal, the station on the faction with its safe zone (<c>MyFactionStation.CreateSafeZone</c>) - and takes away.
    /// </summary>
    public abstract class StationServiceCheck : RegressionCheck
    {
        protected static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        protected MyFaction Faction;
        protected MyFactionStation Station;
        protected MyCubeBlock Terminal;
        private MyEntity _zone;

        protected IEnumerator Setup(Vector3D at)
        {
            SkipUnless(MySession.Static.Settings.EnableEconomy, "the economy is off in this world");
            var npc = MySession.Static.Factions.Select(f => f.Value).OfType<MyFaction>()
                .Where(f => f.Stations.Count > 0 && MySession.Static.Players.IdentityIsNpc(f.FounderId) &&
                            (f.FactionTypeString == "Trader" || f.FactionTypeString == "Miner" || f.FactionTypeString == "Builder")).ToList();
            // not the first trader: npc_station counts its money to the credit
            var firstTrader = npc.FirstOrDefault(f => f.FactionTypeString == "Trader");
            Faction = npc.Skip(1).FirstOrDefault(f => f != firstTrader) ?? npc.FirstOrDefault(f => f != firstTrader) ?? firstTrader;
            SkipUnless(Faction != null, "no NPC faction of the economy in this world");
            var grid = BuildFor(Faction.FounderId, "service-station", at,
                new Part("FunctionalBlock/ServicesTerminal", 0, 0, 0).With(b => b.ShareMode = MyOwnershipShareModeEnum.All),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            Terminal = grid.GetCubeBlock(Vector3I.Zero).FatBlock;
            yield return Until(() => Terminal.IsWorking ? 1 : 0, v => v > 0, "the services terminal working", 30, () => State(Terminal));
            Station = new MyFactionStation(VRage.MyEntityIdentifier.AllocateId(VRage.MyEntityIdentifier.ID_OBJECT_TYPE.STATION), "Regression services", grid.PositionComp.GetPosition(),
                VRage.Game.MyStationTypeEnum.SpaceStation, Faction, "", null) { StationEntityId = grid.EntityId };
            Faction.AddStation(Station);
            var create = typeof(MyFactionStation).GetMethod("CreateSafeZone", Any) ?? throw new Core.ScenarioFailedException("MyFactionStation.CreateSafeZone is not in this game");
            _zone = create.Invoke(Station, new object[] { Faction }) as MyEntity;
            Check(_zone != null, "the station's safe zone was not made");
            yield return Seconds(1);
        }

        /// <summary>Until the station's safe zone has the grid among those inside it (it finds them on its own updates).</summary>
        protected IEnumerator InZone(MyCubeGrid grid) =>
            Until(() => _zone is MySafeZone zone && zone.GetGridsInside().Any(g => g.EntityId == grid.EntityId) ? 1 : 0, v => v > 0, grid.DisplayName + " inside the station's safe zone", 15);

        protected T Service<T>() where T : MyEntityComponentBase =>
            Terminal.Components.Get<T>() ?? throw new Core.ScenarioFailedException("the services terminal has no " + typeof(T).Name);

        public override void Cleanup()
        {
            try
            {
                if (_zone != null && !_zone.MarkedForClose) _zone.Close();
                if (Station != null && typeof(MyFaction).GetField("m_stations", Any)?.GetValue(Faction) is Dictionary<long, VRage.Game.ModAPI.IMyFactionStation> stations) stations.Remove(Station.Id);
            }
            finally { base.Cleanup(); }
        }
    }

    /// <summary>
    /// A station's repair service (<c>MyRepairServiceComponent</c>: the quote counts the damaged blocks and their missing
    /// components at the economy's prices, <c>Repair</c> takes the price and welds every damaged block whole): the
    /// player's damaged ship in the station's safe zone is repaired, its price taken from the player's account.
    /// </summary>
    public sealed class RepairServiceCheck : StationServiceCheck
    {
        public override string Name => Prefix + "repair_service";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -8));
            // damaged before the station's safe zone is there: no damage inside it
            // a battery of the player's: armor alone has no owner, and the service repairs only the sender's grid
            var ship = Build("repair-ship", Site.At(0, 0, 40), false,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
            foreach (var block in ship.GetBlocks().Take(2)) block.DoDamage(block.MaxIntegrity * 0.5f, MyDamageType.Bullet, true, null, 0);
            yield return Seconds(1);
            var damaged = ship.GetBlocks().Count(b => !b.IsFullIntegrity);
            Check(damaged == 2, damaged + " blocks of the ship are damaged, 2 were hit");
            yield return Setup(Site.Origin);
            var service = Service<SpaceEngineers.Game.EntityComponents.Blocks.MyRepairServiceComponent>();
            MyBankingSystem.ChangeBalance(Identity, 1000000);
            var money0 = MyBankingSystem.GetBalance(Identity);
            object quote = null;
            AsMe(() => quote = typeof(SpaceEngineers.Game.EntityComponents.Blocks.MyRepairServiceComponent).GetMethod("GetRepairQuote", Any).Invoke(service, new object[] { ship.EntityId }));
            Check(quote != null, "the station gave no repair quote for the ship in its zone");
            var cost = Convert.ToSingle(quote.GetType().GetField("TotalCost")?.GetValue(quote) ?? quote.GetType().GetProperty("TotalCost").GetValue(quote));
            var quoted = Convert.ToInt32(quote.GetType().GetField("DamagedBlocks")?.GetValue(quote) ?? quote.GetType().GetProperty("DamagedBlocks").GetValue(quote));
            Check(quoted == 2 && cost > 0, "the quote counts " + quoted + " damaged blocks at " + F(cost));
            Request(service, "Repair", quote);
            yield return Until(() => ship.GetBlocks().All(b => b.IsFullIntegrity) ? 1 : 0, v => v > 0, "the ship repaired by the station", 10,
                () => "owners " + string.Join(",", ship.BigOwners) + " (the player " + Identity + "), in the zone " +
                      typeof(SpaceEngineers.Game.EntityComponents.Blocks.MyRepairServiceComponent).GetMethod("IsGridInsideSafeZone", Any)?.Invoke(service, new object[] { ship }));
            var paid = money0 - MyBankingSystem.GetBalance(Identity);
            Check(paid == (long)cost, "the player paid " + paid + " for a repair quoted at " + F(cost));
            Result("2 damaged blocks quoted at " + F(cost) + " by " + Faction.Tag + "'s station, repaired whole, " + paid + " paid");
        }
    }

    /// <summary>
    /// A station's salvage service (<c>MySalvageServiceComponent</c>: it pays a share of the grid's lowest price at the
    /// economy, <c>MinPricePercentage</c>, and closes the grid): the player's ship in the station's zone sold - the ship is
    /// gone and its price paid.
    /// </summary>
    public sealed class SalvageServiceCheck : StationServiceCheck
    {
        public override string Name => Prefix + "salvage_service";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -8));
            yield return Setup(Site.Origin);
            var service = Service<SpaceEngineers.Game.EntityComponents.Blocks.MySalvageServiceComponent>();
            var ship = Build("salvage-ship", Site.At(0, 0, 40), false,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
            var economy = MySession.Static.GetComponent<MySessionComponentEconomy>();
            var definition = typeof(SpaceEngineers.Game.EntityComponents.Blocks.MySalvageServiceComponent).GetField("m_componentDefinition", Any)?.GetValue(service);
            Check(definition != null, "the salvage service has no definition");
            var share = (float)definition.GetType().GetField("MinPricePercentage").GetValue(definition);
            var offered = (long)(economy.GetGridMinimalPrice(ship, out _, out var blocks) * share);
            Check(offered > 0, "the station offers " + offered + " for the ship");
            var accept = typeof(SpaceEngineers.Game.EntityComponents.Blocks.MySalvageServiceComponent).GetMethod("AcceptOffer", Any);
            var quote = Activator.CreateInstance(accept.GetParameters()[0].ParameterType);
            void Set(string field, object value)
            {
                var f = quote.GetType().GetField(field);
                if (f != null) f.SetValue(quote, Convert.ChangeType(value, f.FieldType));
                else
                {
                    var p = quote.GetType().GetProperty(field);
                    p.SetValue(quote, Convert.ChangeType(value, p.PropertyType));
                }
            }
            Set("GridEntityId", ship.EntityId);
            Set("NumberOfBlocks", blocks);
            Set("TotalOffered", offered);
            yield return InZone(ship);
            var money0 = MyBankingSystem.GetBalance(Identity);
            Request(service, "AcceptOffer", quote);
            yield return Until(() => ship.MarkedForClose ? 1 : 0, v => v > 0, "the ship sold off to the station", 10);
            var got = MyBankingSystem.GetBalance(Identity) - money0;
            Check(got >= offered, "the player got " + got + " for a ship the station offered " + offered + " for");
            Result("a ship of " + blocks + " blocks sold to " + Faction.Tag + "'s station for " + got + " (" + (share * 100).ToString("F0") + " % of its lowest price, plus the reputation bonus)");
        }
    }
}
