using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A check with players in factions: made, joined and left as clients ask for it (<c>MyFactionCollection
    /// .CreateFactionRequest</c>, <c>FactionStateChangeRequest</c>). Every faction it made is left by its members at the
    /// end, and so goes away: factions are saved with the world.
    /// </summary>
    public abstract class FactionCheck : RegressionCheck
    {
        private static readonly Random Tags = new Random();
        private readonly List<(int Client, long Faction)> _members = new List<(int, long)>();

        protected long IdentityOf(int client) => FakeClients.PlayerOf(client).Identity.IdentityId;

        private static object Change(string name)
        {
            var method = typeof(MyFactionCollection).GetMethod("FactionStateChangeRequest", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                         ?? throw new Core.ScenarioFailedException("MyFactionCollection.FactionStateChangeRequest is not in this game");
            return Enum.Parse(method.GetParameters()[0].ParameterType, name);
        }

        /// <summary>The client founds a faction of a fresh tag.</summary>
        protected IMyFaction Found(int client)
        {
            // tags are three letters (a longer one is cut); one not taken yet
            string tag;
            do tag = "" + (char)('A' + Tags.Next(26)) + (char)('A' + Tags.Next(26)) + (char)('A' + Tags.Next(26));
            while (MySession.Static.Factions.FactionTagExists(tag));
            var messageType = typeof(MyFactionCollection).GetNestedType("AddFactionMsg", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                              ?? throw new Core.ScenarioFailedException("MyFactionCollection.AddFactionMsg is not in this game");
            var message = Activator.CreateInstance(messageType);
            void Set(string field, object value) => messageType.GetField(field).SetValue(message, value);
            Set("FounderId", IdentityOf(client));
            Set("FactionTag", tag);
            Set("FactionName", WorldApi.EntityPrefix + tag);
            Set("FactionDescription", "regression");
            Set("FactionPrivateInfo", "");
            Set("Type", "PlayerMade");
            RequestAs(client, typeof(MyFactionCollection), "CreateFactionRequest", message);
            var faction = MySession.Static.Factions.TryGetFactionByTag(tag);
            Check(faction != null, "the faction " + tag + " was not made (factions of players " + MySession.Static.Factions.HumansCount() + ", the world allows " +
                                   MySession.Static.MaxFactionsCount + "; the founder in " + (MySession.Static.Factions.GetPlayerFaction(IdentityOf(client))?.Tag ?? "no faction") + ")");
            _members.Add((client, faction.FactionId));
            return faction;
        }

        /// <summary>The client asks to join, the faction's founder accepts.</summary>
        protected void Enter(int client, IMyFaction faction, int founder)
        {
            RequestAs(client, typeof(MyFactionCollection), "FactionStateChangeRequest", Change("FactionMemberSendJoin"), faction.FactionId, faction.FactionId, IdentityOf(client));
            if (!faction.IsMember(IdentityOf(client)))
                RequestAs(founder, typeof(MyFactionCollection), "FactionStateChangeRequest", Change("FactionMemberAcceptJoin"), faction.FactionId, faction.FactionId, IdentityOf(client));
            Check(faction.IsMember(IdentityOf(client)), "the player did not get into the faction " + faction.Tag);
            _members.Insert(0, (client, faction.FactionId));
        }

        protected void Leave(int client, IMyFaction faction)
        {
            RequestAs(client, typeof(MyFactionCollection), "FactionStateChangeRequest", Change("FactionMemberLeave"), faction.FactionId, faction.FactionId, IdentityOf(client));
            _members.RemoveAll(m => m.Client == client && m.Faction == faction.FactionId);
        }

        /// <summary>One faction asks for peace, the other's founder accepts.</summary>
        protected void Peace(int client, IMyFaction from, int otherFounder, IMyFaction to)
        {
            RequestAs(client, typeof(MyFactionCollection), "FactionStateChangeRequest", Change("SendPeaceRequest"), from.FactionId, to.FactionId, IdentityOf(client));
            RequestAs(otherFounder, typeof(MyFactionCollection), "FactionStateChangeRequest", Change("AcceptPeace"), to.FactionId, from.FactionId, IdentityOf(otherFounder));
        }

        public override void Cleanup()
        {
            foreach (var (client, factionId) in _members.ToList())
            {
                try
                {
                    var faction = MySession.Static.Factions.TryGetFactionById(factionId);
                    if (faction != null && faction.IsMember(IdentityOf(client))) Leave(client, faction);
                }
                catch (Exception e) { Log.Warn("leaving a faction after {0}: {1}", Name, e.Message); }
            }
            base.Cleanup();
        }
    }

    /// <summary>
    /// A player founds a faction, another asks to join and is taken in; a block of the founder's shared with the faction
    /// is open to the new member and not to a stranger; the member leaves, and it is closed to them.
    /// </summary>
    public sealed class FactionMembershipCheck : FactionCheck
    {
        public override string Name => Prefix + "faction";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -6));
            var member = -1;
            var stranger = -1;
            yield return Join(Site.At(4, 4, -6), i => member = i);
            yield return Join(Site.At(-4, 4, -6), i => stranger = i);
            var faction = Found(Me);
            Enter(member, faction, Me);
            var grid = Build("faction", Site.Origin, true,
                new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var cargo = Block<MyCargoContainer>(grid);
            Check(cargo.IDModule.ShareMode == MyOwnershipShareModeEnum.Faction && cargo.OwnerId == Identity, "the cargo is not the founder's, shared with the faction");
            Check(cargo.HasPlayerAccess(IdentityOf(member)), "the faction's member cannot get at the faction's cargo");
            Check(!cargo.HasPlayerAccess(IdentityOf(stranger)), "a stranger gets at the faction's cargo");
            Leave(member, faction);
            Check(!faction.IsMember(IdentityOf(member)), "the member is still in the faction after leaving it");
            Check(!cargo.HasPlayerAccess(IdentityOf(member)), "the cargo is still open to who left the faction");
            yield return null;
            Result("faction " + faction.Tag + " founded, joined, its cargo open to the member and closed to a stranger, closed to the member once they left");
        }
    }

    /// <summary>
    /// A gatling turret of a player's, among three stations 150 m off: one of a member of the player's own faction, one of
    /// a faction at peace with it, one of a player in no faction. The turret fires at the stranger's only.
    /// </summary>
    public sealed class TurretRelationsCheck : FactionCheck
    {
        public override string Name => Prefix + "turret_relations";
        public override int TimeoutSeconds => 150;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            // at the game's low quality (its own under load) bullets check what they hit a third as often and fly through walls
            HoldHighQuality();
            yield return Arrive(Site.At(0, 10, 20));
            int mate = -1, ally = -1, stranger = -1;
            yield return Join(Site.At(10, 10, 20), i => mate = i);
            yield return Join(Site.At(-10, 10, 20), i => ally = i);
            yield return Join(Site.At(0, 20, 20), i => stranger = i);
            var own = Found(Me);
            Enter(mate, own, Me);
            var other = Found(ally);
            Peace(Me, own, ally, other);
            var turretGrid = Build("relations-turret", Site.Origin, true,
                new Part("LargeGatlingTurret/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 2).With(b => Motion.Charged(b)));
            var turret = Block<SpaceEngineers.Game.Entities.Weapons.MyLargeGatlingTurret>(turretGrid);
            Put(Inventory(turret), "AmmoMagazine/NATO_25x184mm", 10);
            MyCubeGrid Station(int owner, string name, Vector3D at) => Build(name, at, true, new[]
            {
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", -1, 0, 0),
            }, ownedBy: IdentityOf(owner));
            var mates = Station(mate, "relations-mate", Site.At(0, 0, 150));
            var allies = Station(ally, "relations-ally", Site.At(150, 0, 0));
            var strangers = Station(stranger, "relations-stranger", Site.At(-150, 0, 0));
            Func<MyCubeGrid, double> integrity = g => g.MarkedForClose ? 0 : g.GetBlocks().Sum(b => (double)b.Integrity);
            var whole = new[] { integrity(mates), integrity(allies), integrity(strangers) };
            yield return Until(() => whole[2] - integrity(strangers), v => v > 0, "the turret firing at the stranger's station", 60, () => State(turret));
            yield return Seconds(5);
            Check(integrity(mates) >= whole[0], "the turret fired at a faction member's station");
            Check(integrity(allies) >= whole[1], "the turret fired at a station of a faction at peace");
            Result("the turret fired at the stranger's station only (" + F(whole[2] - integrity(strangers)) + " damage), the member's and the ally's untouched; " +
                   own.Tag + " and " + other.Tag + " at " + MySession.Static.Factions.GetRelationBetweenFactions(own.FactionId, other.FactionId).Item1);
        }
    }
}
