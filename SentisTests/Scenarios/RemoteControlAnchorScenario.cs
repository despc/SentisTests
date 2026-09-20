using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SpaceEngineers.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A player flying a ship from a remote control block is in two places, and both have to stay
    /// alive - and the ship has to be reachable in the first place while it is still frozen.
    ///
    /// The first half of the scenario answers that: a ship <see cref="ApartM"/> away with an antenna
    /// is left alone until the freezer takes it, and then the player reaches it over the antenna and
    /// takes control. Freezing only takes an entity off the update lists, so its antenna stays in the
    /// broadcaster network and the grid stays reachable; the moment control is taken the ship becomes
    /// one of the player's places and thaws.
    ///
    /// The scenario puts a fake player's character in one spot and a ship with a remote control block
    /// <see cref="ApartM"/> away, hands the ship to that player, and then asks the freezer about
    /// three grids:
    /// <list type="bullet">
    /// <item>one beside the character - it must stay thawed, which it did not before: a player's
    /// position is whatever they control, so the body counted for nothing;</item>
    /// <item>the controlled ship itself - thawed, as it always was;</item>
    /// <item>one far from both - frozen, so the test cannot pass by the freezer simply giving up.</item>
    /// </list>
    /// </summary>
    public sealed class RemoteControlAnchorScenario : TestScenario
    {
        public const string ScenarioName = "remote_control_anchors";
        private const string Prefix = "anchor-";
        private const double ApartM = 15000;
        private const float AntennaRangeM = 30000;
        private const double ThawSeconds = 10;
        private const double FarM = 40000;
        private const double BesideM = 60;
        private const double SettleSeconds = 10;
        private const double FreezeWaitSeconds = 90;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly ConfigOverride _config = new ConfigOverride();
        private MyCubeGrid _ship;
        private MyCubeGrid _besideCharacter;
        private MyCubeGrid _farFromBoth;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            _config.Set("FreezerEnabled", true);
            _config.Set("DelayBeforeFreezeSec", 5);
            _config.Set("FreezeDistanceStatic", 3000);
            _config.Set("FreezeDistanceDynamic", 10000);

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var east = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));

            var characterAt = anchorM.Translation + up * 3000;
            var shipAt = characterAt + east * ApartM;
            var farAt = characterAt - east * FarM;

            _ship = BuildShip(shipAt, up);
            _besideCharacter = BuildMarker("beside-character", characterAt + east * BesideM, up);
            _farFromBoth = BuildMarker("far-from-both", farAt, up);

            var settle = WaitForSeconds(SettleSeconds, "grids settle");
            while (settle.MoveNext()) yield return settle.Current;

            // One fake player, standing by the marker, flying the ship from far away.
            FakeClients.Add(1, Network, p => (characterAt, 0, 0), withCharacters: true);
            var settleClient = WaitForSeconds(SettleSeconds, "the player arrives");
            while (settleClient.MoveNext()) yield return settleClient.Current;

            var character = FakeClients.Character(0);
            Check(character != null, "the fake player has no character");
            var apart = Vector3D.Distance(character.PositionComp.GetPosition(), _ship.PositionComp.GetPosition());
            Check(apart > ApartM / 2, "the character and the ship ended up in the same place");

            // ---------------------------------------------- the ship is frozen and nobody controls it
            var waiting = WaitForSeconds(FreezeWaitSeconds, "the freezer settles");
            while (waiting.MoveNext()) yield return waiting.Current;

            var frozenBeforeControl = RuntimePluginControls.IsGridFrozen(_ship.EntityId);
            var antenna = WorldApi.FindFunctional<MyRadioAntenna>(_ship);
            var reachable = CanReachOverAntenna(character, _ship);
            Note("the ship is " + apart.ToString("F0") + " m away, " + (frozenBeforeControl ? "frozen" : "thawed") +
                 ", antenna " + (antenna != null && antenna.IsWorking ? "working" : "not working") +
                 ", reachable over the antenna network: " + reachable);

            Check(frozenBeforeControl, "the ship never froze, so this proves nothing about a frozen one");
            Check(antenna != null && antenna.IsWorking, "the ship's antenna is not working");
            Check(reachable, "a frozen grid with a working antenna is not reachable: the player could not connect to it");

            // ---------------------------------------------- the player connects and takes control
            var remote = WorldApi.FindFunctional<MyRemoteControl>(_ship);
            Check(remote != null, "the ship has no remote control block");
            Check(FakeClients.TakeControl(0, remote), "the player could not take control of the remote block");

            var thawedAfter = -1.0;
            var tookControl = DateTime.UtcNow;
            while ((DateTime.UtcNow - tookControl).TotalSeconds < ThawSeconds)
            {
                if (!RuntimePluginControls.IsGridFrozen(_ship.EntityId))
                {
                    thawedAfter = (DateTime.UtcNow - tookControl).TotalSeconds;
                    break;
                }

                yield return WaitForTicks(15);
            }

            Note("control taken; the ship thawed after " + (thawedAfter < 0 ? "never" : thawedAfter.ToString("F1") + " s"));
            Check(thawedAfter >= 0, "the ship the player now controls stayed frozen for " + ThawSeconds + " s");

            // Give the freezer a pass or two with the control held, then look at all three grids.
            var settleControl = WaitForSeconds(SettleSeconds, "the freezer sees the new places");
            while (settleControl.MoveNext()) yield return settleControl.Current;

            var besideFrozen = RuntimePluginControls.IsGridFrozen(_besideCharacter.EntityId);
            var shipFrozen = RuntimePluginControls.IsGridFrozen(_ship.EntityId);
            var farFrozen = RuntimePluginControls.IsGridFrozen(_farFromBoth.EntityId);

            Note("ANCHOR RESULT | beside the character: " + (besideFrozen ? "frozen" : "thawed") +
                 " | the controlled ship: " + (shipFrozen ? "frozen" : "thawed") +
                 " | far from both: " + (farFrozen ? "frozen" : "thawed") +
                 " | frozen grids in the world: " + RuntimePluginControls.FrozenGridCount);

            Check(!besideFrozen,
                "the grid beside the player's own body was frozen: only the ship they steer counts as their position");
            Check(!shipFrozen, "the ship the player is flying was frozen");
            Check(farFrozen, "the grid far from both was not frozen either, so the test proves nothing");
        }

        /// <summary>
        /// Whether the player can reach the grid over the antenna network - the same question the game
        /// asks before it lets somebody open a terminal or take remote control.
        ///
        /// It has to be asked of <c>MyAntennaSystem</c>: the character's own
        /// <c>HasAccessToLogicalGroup</c> reads a set that only the HUD of a local player fills, so
        /// on a dedicated server it is always empty.
        /// </summary>
        private static bool CanReachOverAntenna(Sandbox.Game.Entities.Character.MyCharacter character, MyCubeGrid grid)
        {
            var identity = character.GetPlayerIdentityId();
            // RadioReceiver is internal on MyCharacter; the component is the same object.
            var receiver = character.Components.Get<Sandbox.Game.Entities.MyDataReceiver>();
            return receiver != null && Sandbox.Game.GameSystems.MyAntennaSystem.Static.CheckConnection(
                receiver, grid, identity, mutual: false);
        }

        private MyCubeGrid BuildShip(Vector3D position, Vector3D up)
        {
            var blocks = new List<BlockSpec>
            {
                new BlockSpec("LargeBlockArmorBlock", new Vector3I(0, 0, 0)),
                new BlockSpec("LargeBlockArmorBlock", new Vector3I(1, 0, 0)),
                new BlockSpec("LargeBlockBatteryBlock", new Vector3I(0, 1, 0)),
                new BlockSpec("LargeBlockRemoteControl", new Vector3I(1, 1, 0)),
                new BlockSpec("LargeBlockRadioAntenna", new Vector3I(0, 2, 0)),
            };
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "ship", MyCubeSize.Large, false, position, blocks,
                Vector3.Forward, (Vector3)up);
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            WorldApi.ChargeBatteries(grid);

            // The antenna is what makes the ship reachable from far away.
            var antenna = WorldApi.FindFunctional<MyRadioAntenna>(grid);
            if (antenna != null)
            {
                antenna.Enabled = true;
                ((Sandbox.ModAPI.Ingame.IMyRadioAntenna)antenna).Radius = AntennaRangeM;
                ((Sandbox.ModAPI.Ingame.IMyRadioAntenna)antenna).EnableBroadcasting = true;
            }

            return grid;
        }

        private MyCubeGrid BuildMarker(string name, Vector3D position, Vector3D up)
        {
            var blocks = new List<BlockSpec>
            {
                new BlockSpec("LargeBlockArmorBlock", new Vector3I(0, 0, 0)),
                new BlockSpec("LargeBlockArmorBlock", new Vector3I(1, 0, 0)),
            };
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + name, MyCubeSize.Large, false, position, blocks,
                Vector3.Forward, (Vector3)up);
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!(grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                    grid.Close();
                }
            }
            finally { base.CleanupLeftovers(); }
        }
    }
}
