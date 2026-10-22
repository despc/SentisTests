using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The game's pirate antennas (<c>MyPirateAntennas</c>): an antenna of the pirates' named as one of the
    /// <c>PirateAntennas</c> definitions ("Pirate Outpost"), working and broadcasting, sends its drones (its spawn
    /// groups) at an enemy within its radius: a grid of the pirates' comes by the player.
    /// </summary>
    public sealed class PirateDronesCheck : RegressionCheck
    {
        public override string Name => Prefix + "pirate_drones";
        public override int TimeoutSeconds => 150;
        private const string AntennaName = "Pirate Outpost";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableDrones, "drones are off in this world");
            var pirates = MyPirateAntennas.GetPiratesId();
            SkipUnless(pirates != 0, "no pirates identity in this world");
            SkipUnless(Sandbox.Definitions.MyDefinitionManager.Static.GetPirateAntennaDefinitions().Any(d => d.Name == AntennaName),
                "no pirate antenna definition \"" + AntennaName + "\" in this game");
            yield return Arrive(Site.At(0, 0, -150));
            var before = new HashSet<long>(MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.EntityId));
            var outpost = BuildFor(pirates, "pirate-outpost", Site.Origin,
                new Part("RadioAntenna/LargeBlockRadioAntenna", 0, 0, 0).Named(AntennaName),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => Motion.Charged(b)));
            var antenna = Block<MyRadioAntenna>(outpost);
            ((Sandbox.ModAPI.IMyTerminalBlock)antenna).SetValueFloat("Radius", 1500f);
            ((Sandbox.ModAPI.IMyTerminalBlock)antenna).SetValueBool("EnableBroadCast", true);
            yield return Until(() => antenna.IsWorking ? 1 : 0, v => v > 0, "the pirate antenna working", 10, () => State(antenna));
            antenna.UpdatePirateAntenna();
            var relation = MyIDModule.GetRelationPlayerPlayer(pirates, Identity);
            Check(relation == VRage.Game.Entity.MyRelationsBetweenPlayers.Enemies, "the pirates are " + relation + " to the player, not enemies");

            MyCubeGrid drone = null;
            yield return Until(() =>
            {
                drone = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !before.Contains(g.EntityId) && g != outpost && !g.MarkedForClose &&
                                                                                       g.BigOwners.Contains(pirates) && Vector3D.Distance(g.PositionComp.GetPosition(), Site.Origin) < 4000);
                return drone == null ? 0 : 1;
            }, v => v > 0, "a pirate drone sent at the player", 120);
            var away = Vector3D.Distance(drone.PositionComp.GetPosition(), Body.PositionComp.GetPosition());
            var name = drone.DisplayName;
            var blocks = drone.BlocksCount;
            // the drones go at once: armed, they would fight the grids of the checks beside this one
            antenna.Enabled = false;
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !before.Contains(g.EntityId) && g != outpost && g.BigOwners.Contains(pirates)).ToList())
                yield return CloseAndWait(grid);
            Result("the pirate antenna \"" + AntennaName + "\" sent " + name + " (" + blocks + " blocks), " + F(away) + " m from the player");
        }
    }

    /// <summary>
    /// Respawning at another player's medical room (<c>OnRespawnRequest</c> naming it; the game's
    /// <c>CanPlayerSpawn</c>): one of a faction mate's, shared with the faction, takes the player in - a new astronaut
    /// stands at it; one of a stranger's, shared with nobody, does not - the respawn fails and the player has no body.
    /// </summary>
    public sealed class RespawnFactionMedbayCheck : FactionCheck
    {
        public override string Name => Prefix + "respawn_faction_medbay";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -12));
            int mate = -1, stranger = -1;
            yield return Join(Site.At(-6, 6, -12), i => mate = i);
            yield return Join(Site.At(6, 6, -12), i => stranger = i);
            var faction = Found(mate);
            Enter(Me, faction, mate);
            MyCubeGrid Medbay(string name, Vector3D at, int owner, MyOwnershipShareModeEnum share) => Build(name, at, true, new[]
            {
                new Part("MedicalRoom/LargeMedicalRoom", 0, 0, 0).With(b => b.ShareMode = share),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 0, 0).With(b => { Motion.Charged(b); b.ShareMode = share; }),
            }, ownedBy: IdentityOf(owner));
            var shared = Medbay("medbay-faction", Site.Origin, mate, MyOwnershipShareModeEnum.Faction);
            var closed = Medbay("medbay-stranger", Site.At(15, 0, 0), stranger, MyOwnershipShareModeEnum.None);
            var sharedRoom = shared.GetCubeBlock(Vector3I.Zero).FatBlock;
            var closedRoom = closed.GetCubeBlock(Vector3I.Zero).FatBlock;
            yield return Until(() => sharedRoom.IsWorking && closedRoom.IsWorking ? 1 : 0, v => v > 0, "both medical rooms working", 10);

            var body = Body;
            body.DoDamage(1000f, MyDamageType.Suicide, true, attackerId: 0);
            yield return Until(() => body.IsDead ? 1 : 0, v => v > 0, "the player dead", 10);
            void Respawn(MyEntity at) => Request(typeof(Sandbox.Game.Multiplayer.MyPlayerCollection), "OnRespawnRequest",
                new Sandbox.Game.Multiplayer.MyPlayerCollection.RespawnMsg { JoinGame = false, NewIdentity = false, RespawnEntityId = at.EntityId, PlayerSerialId = 0, Color = Color.Red });

            Respawn(closedRoom);
            yield return Seconds(3);
            Check(Body == null || Body == body || Body.IsDead, "the player respawned at a stranger's medical room shared with nobody");
            Respawn(sharedRoom);
            yield return Until(() => Body != null && Body != body && !Body.IsDead ? 1 : 0, v => v > 0, "a new astronaut at the faction mate's medical room", 20);
            Track(Body);
            var off = Vector3D.Distance(Body.PositionComp.GetPosition(), sharedRoom.PositionComp.GetPosition());
            Check(off < 10, "the new astronaut is " + F(off) + " m from the faction mate's medical room");
            Result("a stranger's medical room refused the player; the faction mate's took them in, " + F(off) + " m from it");
        }
    }
}
