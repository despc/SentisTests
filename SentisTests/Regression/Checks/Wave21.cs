using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{

    /// <summary>
    /// "Claim NPC grid" and the ownership of the blocks. The game's claim gives the player the grid's PCU (built by them)
    /// and the blocks stay the NPC's, to be hacked; with SentisGameplayImprovements' <c>ClaimNpcGridTakesOwnership</c>
    /// (off by default, on in memory for the second grid) the blocks the NPC owns become the player's, a block of nobody's
    /// stays nobody's. Two grids of an NPC trader's, NPC-spawned, the player by them, a container of each made nobody's
    /// (as hacked) so the terminal offers the claim.
    /// </summary>
    public sealed class ClaimNpcOwnershipCheck : RegressionCheck
    {
        public override string Name => Prefix + "claim_npc_ownership";
        private Scenarios.ConfigOverride _gameplay;

        protected override IEnumerator Check()
        {
            var npc = Sandbox.Game.World.MySession.Static.Factions.Select(f => f.Value).OfType<Sandbox.Game.World.MyFaction>()
                .FirstOrDefault(f => f.FactionTypeString == "Trader" && Sandbox.Game.World.MySession.Static.Players.IdentityIsNpc(f.FounderId));
            SkipUnless(npc != null, "no NPC trader faction in this world");
            SkipUnless(SentisTests.Core.Integrations.TypeOf(Scenarios.ConfigOverride.Gameplay, "SentisGameplayImprovements.ClaimNpcGridOwnership") != null,
                "SentisGameplayImprovements has no ClaimNpcGridOwnership");
            yield return Arrive(Site.At(0, 0, -6));
            MyCubeGrid Grid(string name, Vector3D at)
            {
                var g = BuildFor(npc.FounderId, name, at,
                    new Part("CargoContainer/LargeBlockSmallContainer", 0, 0, 0),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
                g.IsNpcSpawnedGrid = true;
                // the way the game's terminal offers the claim: a container open to the player (hacked: nobody's)
                Block<MyCargoContainer>(g).ChangeOwner(0, MyOwnershipShareModeEnum.All);
                return g;
            }
            var plain = Grid("claim-owner-off", Site.Origin);
            var taken = Grid("claim-owner-on", Site.At(0, 0, 6));
            yield return Seconds(1);

            // off: as the game does it
            _gameplay = new Scenarios.ConfigOverride(Scenarios.ConfigOverride.Gameplay);
            _gameplay.Set("ClaimNpcGridTakesOwnership", false);
            Request(typeof(Sandbox.Game.World.MyBlockLimits), "ClaimNPCGrid", plain.EntityId);
            yield return null;
            var battery = Block<Sandbox.Game.Entities.MyBatteryBlock>(plain);
            Check(!plain.IsNpcSpawnedGrid && plain.CubeBlocks.First().BuiltBy == Identity, "the player by the NPC's grid could not claim it");
            Check(battery.OwnerId == npc.FounderId, "with ClaimNpcGridTakesOwnership off the claim made the NPC's battery owned by " + battery.OwnerId + ", not the NPC's");

            // on: the NPC's blocks the player's
            _gameplay.Set("ClaimNpcGridTakesOwnership", true);
            Request(typeof(Sandbox.Game.World.MyBlockLimits), "ClaimNPCGrid", taken.EntityId);
            yield return null;
            var takenBattery = Block<Sandbox.Game.Entities.MyBatteryBlock>(taken);
            var takenCargo = Block<MyCargoContainer>(taken);
            Check(!taken.IsNpcSpawnedGrid, "the player could not claim the second NPC grid");
            Check(takenBattery.OwnerId == Identity && takenBattery.IDModule.ShareMode == MyOwnershipShareModeEnum.None,
                "with ClaimNpcGridTakesOwnership on the claimed grid's battery is owned by " + takenBattery.OwnerId + " (" + takenBattery.IDModule.ShareMode + "), not the player alone");
            Check(takenCargo.OwnerId == 0, "the claim gave the player the nobody's container too: owned by " + takenCargo.OwnerId);
            Result("the game's claim: PCU to the player, the battery the NPC's; with ClaimNpcGridTakesOwnership: the battery the player's, the nobody's container nobody's");
        }

        public override void Cleanup()
        {
            _gameplay?.Restore();
            base.Cleanup();
        }
    }
}
