using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Entities.Cube;
using SentisTests.Game;
using VRage.Game;
using VRage.Network;
using VRageMath;

namespace SentisTests.Regression.Checks
{

    /// <summary>
    /// A player who quits and comes back (one client, the others on): the game keeps the character where it was, seated
    /// or not, with what it carries (<c>MyPlayerCollection.Multiplayer_ClientRemoved</c>), and gives it back to the player
    /// when they join again (<c>OnNewPlayerRequest</c> with the identity they had, then the join's respawn request). One
    /// player quits standing with 7 plates on them, another seated in a cockpit: both characters stay put, with the
    /// plates, the seated one in the seat; joined again, each player has their own character back, the seated one controls
    /// the cockpit.
    /// </summary>
    public sealed class RejoinCheck : RegressionCheck
    {
        public override string Name => Prefix + "rejoin";
        public override int TimeoutSeconds => 90;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -6));
            int walker = -1, sitter = -1;
            yield return Join(Site.At(-3, 0, 0), i => walker = i);
            yield return Join(Site.At(3, 0, -3), i => sitter = i);
            var grid = Build("rejoin-cockpit", Site.At(3, 0, 3), true,
                new Part("Cockpit/LargeBlockCockpitSeat", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var seat = Block<MyCockpit>(grid);
            // the check's player built it: shared, so the second player may sit in it
            seat.ChangeOwner(seat.OwnerId, MyOwnershipShareModeEnum.All);
            var walkerBody = FakeClients.Character(walker);
            var sitterBody = FakeClients.Character(sitter);
            Put(Inventory(walkerBody), "Component/SteelPlate", 7);
            FakeClients.SetFree(sitter, true);
            var stand = seat.PositionComp.GetPosition() + seat.WorldMatrix.Backward * 1.5;
            FakeClients.MoveTo(sitter, stand);
            sitterBody.PositionComp.SetWorldMatrix(MatrixD.CreateWorld(stand, Vector3D.Normalize(seat.PositionComp.GetPosition() - stand), seat.WorldMatrix.Up));
            FakeClients.SetFree(sitter, true);
            RequestAs(sitter, seat, "AttachPilotEvent", VRage.Game.Entity.UseObject.UseActionEnum.Manipulate, sitterBody.EntityId, -1);
            yield return Until(() => seat.Pilot == sitterBody ? 1 : 0, v => v > 0, "the second player seated", 10);
            yield return Seconds(1);
            var walkerAt = walkerBody.PositionComp.GetPosition();
            var walkerIdentity = FakeClients.PlayerOf(walker).Identity.IdentityId;
            var sitterIdentity = FakeClients.PlayerOf(sitter).Identity.IdentityId;

            FakeClients.Quit(walker);
            FakeClients.Quit(sitter);
            yield return Seconds(2);
            var moved = Vector3D.Distance(walkerBody.PositionComp.GetPosition(), walkerAt);
            Check(!walkerBody.MarkedForClose && !walkerBody.IsDead, "the character of the player who quit standing is gone (closed " + walkerBody.MarkedForClose + ", dead " + walkerBody.IsDead + ")");
            Check(moved < 3, "the character of the player who quit moved " + F(moved) + " m");
            Check(Amount(Inventory(walkerBody), "Component/SteelPlate") == 7, "the character of the player who quit has " + F(Amount(Inventory(walkerBody), "Component/SteelPlate")) + " plates of 7");
            Check(!sitterBody.MarkedForClose && seat.Pilot == sitterBody, "the seated player's character is not in the cockpit after they quit (pilot " + (seat.Pilot?.DisplayName ?? "none") + ")");

            var walkerPlayer = FakeClients.Rejoin(walker);
            var sitterPlayer = FakeClients.Rejoin(sitter);
            Check(walkerPlayer != null && sitterPlayer != null, "a player who quit could not join again");
            Check(walkerPlayer.Identity.IdentityId == walkerIdentity && sitterPlayer.Identity.IdentityId == sitterIdentity,
                "a player joined again with another identity: " + walkerPlayer.Identity.IdentityId + "/" + walkerIdentity + ", " + sitterPlayer.Identity.IdentityId + "/" + sitterIdentity);
            yield return Until(() => walkerPlayer.Character == walkerBody ? 1 : 0, v => v > 0, "the first player's own character back", 10,
                () => "character " + (walkerPlayer.Character?.EntityId.ToString() ?? "none") + ", left one " + walkerBody.EntityId);
            yield return Until(() => sitterPlayer.Controller?.ControlledEntity == seat ? 1 : 0, v => v > 0, "the seated player controlling the cockpit again", 10,
                () => "controls " + (sitterPlayer.Controller?.ControlledEntity?.Entity?.DisplayName ?? "nothing") + ", pilot " + (seat.Pilot?.DisplayName ?? "none"));
            Check(Amount(Inventory(walkerBody), "Component/SteelPlate") == 7, "back in the game, the first player has " + F(Amount(Inventory(walkerBody), "Component/SteelPlate")) + " plates of 7");
            Result("quit: the characters stayed (one with its 7 plates, one in the seat); joined again: each player has their character, the seated one the cockpit");
        }
    }

    /// <summary>
    /// Giving blocks to another player (the terminal's ownership transfer): the owner asks
    /// (<c>MyCubeGrid.AddChangeOwnersRequestServerside</c>), the request waits for the other player, who accepts
    /// (<c>OnChangeOwnersRequestConfirmation</c>), and the blocks are theirs. A battery and a container given by the
    /// player to a second player: theirs only once accepted; a third player accepting it changes nothing; the request also
    /// naming a block the server does not have does not throw (the game's handler wrote past the end of the list for it and
    /// dropped the player; SO <c>OwnershipRequestCheck</c> leaves it out).
    /// </summary>
    public sealed class BlockTransferCheck : RegressionCheck
    {
        public override string Name => Prefix + "block_transfer";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -4));
            int receiver = -1, other = -1;
            yield return Join(Site.At(-4, 0, -4), i => receiver = i);
            yield return Join(Site.At(4, 0, -4), i => other = i);
            var grid = Build("block-transfer", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 1, 0, 0));
            var battery = Block<MyBatteryBlock>(grid);
            var cargo = Block<MyCargoContainer>(grid);
            var to = FakeClients.PlayerOf(receiver).Identity.IdentityId;
            var requests = new List<MyCubeGrid.MySingleOwnershipRequest>
            {
                new MyCubeGrid.MySingleOwnershipRequest { BlockId = battery.EntityId, Owner = to },
                new MyCubeGrid.MySingleOwnershipRequest { BlockId = cargo.EntityId, Owner = to },
                // a block the server does not have (closed meanwhile, or a client's own)
                new MyCubeGrid.MySingleOwnershipRequest { BlockId = 1, Owner = to },
            };
            string threw = null;
            try { Request(typeof(MyCubeGrid), "AddChangeOwnersRequestServerside", MyOwnershipShareModeEnum.None, requests); }
            catch (System.Exception e) { threw = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
            Check(threw == null, "the request naming a block the server does not have threw (the player is dropped): " + threw);
            yield return null;
            var pending = Sandbox.Game.World.MySession.Static.OwnershipChangeRequests.Where(r => r.BlockId == battery.EntityId || r.BlockId == cargo.EntityId).ToList();
            Check(pending.Count == 2, "the transfer is not waiting for the receiver: " + pending.Count + " requests");
            Check(battery.OwnerId == Identity && cargo.OwnerId == Identity, "the blocks changed hands before the receiver accepted");
            var batch = pending[0].BatchGuid;

            RequestAs(other, typeof(MyCubeGrid), "OnChangeOwnersRequestConfirmation", batch, grid.EntityId, true);
            yield return null;
            Check(battery.OwnerId == Identity && cargo.OwnerId == Identity, "a third player accepted the transfer and the blocks changed hands: " + battery.OwnerId + ", " + cargo.OwnerId);
            RequestAs(receiver, typeof(MyCubeGrid), "OnChangeOwnersRequestConfirmation", batch, grid.EntityId, true);
            yield return Until(() => battery.OwnerId == to && cargo.OwnerId == to ? 1 : 0, v => v > 0, "the blocks the receiver's", 5,
                () => "owners " + battery.OwnerId + ", " + cargo.OwnerId + ", the receiver " + to);
            Result("given: waiting until accepted, a third player's accepting did nothing, the receiver's made the blocks theirs; a missing block did not throw");
        }
    }
}
