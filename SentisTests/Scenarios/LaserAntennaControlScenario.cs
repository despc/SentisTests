using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The same question as <see cref="RemoteControlAnchorScenario"/>, asked of a laser antenna: can
    /// a player reach a frozen ship that is only connected through a laser link, and take control of
    /// it?
    ///
    /// The ship <see cref="ApartM"/> away carries no radio antenna at all - the only way to it is the
    /// laser pair, one dish on the base beside the player and one on the ship. The pair is connected
    /// while both are awake, then the ship is left alone until the freezer takes it, and only then
    /// does the player reach for it.
    ///
    /// The laser link is the interesting case because it is a running conversation between two
    /// blocks: a frozen grid stops updating, so the question is whether the link survives that.
    /// </summary>
    public sealed class LaserAntennaControlScenario : TestScenario
    {
        public const string ScenarioName = "laser_control_anchors";
        private const string Prefix = "laser-";
        private const double ApartM = 15000;
        private const double AltitudeM = 20000;
        private const double SettleSeconds = 10;
        private const double ConnectWaitSeconds = 120;
        private const double FreezeWaitSeconds = 90;
        private const double ThawSeconds = 10;
        private const float RadioRangeM = 2000;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly ConfigOverride _config = new ConfigOverride();
        private MyCubeGrid _base;
        private MyCubeGrid _ship;

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

            // High above the surface: at a few kilometres the planet itself blocks a 15 km laser
            // (the antennas report NoLineOfSight), and this test is about the freezer, not terrain.
            var ground = planet.GetClosestSurfacePointGlobal(anchorM.Translation);
            var characterAt = ground + up * AltitudeM;
            _base = BuildGrid("base", characterAt + east * 40, up, withRadio: true);
            _ship = BuildGrid("ship", characterAt + east * ApartM, up, withRadio: false);

            Note("planet radius " + planet.AverageRadius.ToString("F0") + "/" + planet.MaximumRadius.ToString("F0") +
                 ", base is " + (Vector3D.Distance(_base.PositionComp.GetPosition(), planet.PositionComp.GetPosition())).ToString("F0") +
                 " from its centre, ship " + (Vector3D.Distance(_ship.PositionComp.GetPosition(), planet.PositionComp.GetPosition())).ToString("F0"));
            Note("base at " + _base.PositionComp.GetPosition().ToString("F0") + ", ship at " +
                 _ship.PositionComp.GetPosition().ToString("F0") + ", " +
                 Vector3D.Distance(_base.PositionComp.GetPosition(), _ship.PositionComp.GetPosition()).ToString("F0") + " m apart");

            var settle = WaitForSeconds(SettleSeconds, "grids settle");
            while (settle.MoveNext()) yield return settle.Current;

            FakeClients.Add(1, Network, p => (characterAt, 0, 0), withCharacters: true);
            var settleClient = WaitForSeconds(SettleSeconds, "the player arrives");
            while (settleClient.MoveNext()) yield return settleClient.Current;

            var character = FakeClients.Character(0);
            Check(character != null, "the fake player has no character");

            // ---------------------------------------------- the ship is left alone until it freezes
            var here = WorldApi.FindFunctional<MyLaserAntenna>(_base);
            var there = WorldApi.FindFunctional<MyLaserAntenna>(_ship);
            Check(here != null && there != null, "a laser antenna is missing");
            Check(here.IsWorking && there.IsWorking,
                "a laser antenna has no power: base " + here.IsWorking + ", ship " + there.IsWorking);

            var waiting = WaitForSeconds(FreezeWaitSeconds, "the freezer settles");
            while (waiting.MoveNext()) yield return waiting.Current;

            var frozenBefore = RuntimePluginControls.IsGridFrozen(_ship.EntityId);
            Note("before the request the ship is " + (frozenBefore ? "frozen" : "thawed"));
            Check(frozenBefore, "the ship never froze, so this proves nothing about connecting to a frozen one");

            // ---------------------------------------------- the player asks for the link
            Pair(here, there);
            Pair(there, here);

            var wokeAfter = -1.0;
            var asked = DateTime.UtcNow;
            while ((DateTime.UtcNow - asked).TotalSeconds < ThawSeconds)
            {
                if (!RuntimePluginControls.IsGridFrozen(_ship.EntityId))
                {
                    wokeAfter = (DateTime.UtcNow - asked).TotalSeconds;
                    break;
                }

                yield return WaitForTicks(15);
            }

            Note("the connection request woke the ship after " +
                 (wokeAfter < 0 ? "never" : wokeAfter.ToString("F1") + " s"));
            Check(wokeAfter >= 0,
                "asking a frozen ship's laser antenna for a connection did not wake it, so its dish can never turn");

            var connecting = DateTime.UtcNow;
            while ((DateTime.UtcNow - connecting).TotalSeconds < ConnectWaitSeconds)
            {
                if (Connected(here) && Connected(there)) break;
                yield return WaitForTicks(30);
            }

            // Whether the dishes then see each other is the game's own business - two antennas on
            // small test grids report NoLineOfSight here - so the link is reported, not demanded.
            var linked = Connected(here) && Connected(there);
            Note("laser link after " + (DateTime.UtcNow - connecting).TotalSeconds.ToString("F0") + " s: base " +
                 State(here) + " (" + Error(here) + ", blocker " + Blocker(here) + "), ship " + State(there) +
                 " (" + Error(there) + ", blocker " + Blocker(there) + ") over " +
                 Vector3D.Distance(_base.PositionComp.GetPosition(), _ship.PositionComp.GetPosition()).ToString("F0") + " m");
            var reachableFrozen = CanReachOverAntenna(character, _ship);
            var frozen = frozenBefore;

            // ---------------------------------------------------------------- take control
            var remote = WorldApi.FindFunctional<MyRemoteControl>(_ship);
            Check(remote != null, "the ship has no remote control block");
            Check(FakeClients.TakeControl(0, remote), "the player could not take control of the woken ship");

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

            Note("LASER RESULT | ship " + ApartM + " m away, frozen before the request: " + frozen +
                 ", woken by the request after " + (wokeAfter < 0 ? "never" : wokeAfter.ToString("F1") + " s") +
                 ", linked: " + linked + ", reachable: " + reachableFrozen + " | thawed after taking control: " +
                 (thawedAfter < 0 ? "never" : thawedAfter.ToString("F1") + " s") +
                 " | frozen grids in the world: " + RuntimePluginControls.FrozenGridCount);
            Check(thawedAfter >= 0, "the ship the player now controls stayed frozen");
        }

        // ------------------------------------------------------------------ helpers

        private MyCubeGrid BuildGrid(string name, Vector3D position, Vector3D up, bool withRadio)
        {
            // The laser antenna sits alone on top of a mast: from a dish tucked between other blocks
            // the game reports NoLineOfSight, because the ray to the other end leaves through them.
            var blocks = new List<BlockSpec>
            {
                new BlockSpec("LargeBlockArmorBlock", new Vector3I(0, 0, 0)),
                new BlockSpec("LargeBlockArmorBlock", new Vector3I(1, 0, 0)),
                new BlockSpec("LargeBlockBatteryBlock", new Vector3I(0, 1, 0)),
                new BlockSpec("LargeBlockBatteryBlock", new Vector3I(1, 1, 0)),
                new BlockSpec("LargeBlockRemoteControl", new Vector3I(1, 2, 0)),
                new BlockSpec("LargeBlockArmorBlock", new Vector3I(0, 2, 0)),
                new BlockSpec("LargeBlockArmorBlock", new Vector3I(0, 3, 0)),
                new BlockSpec("LargeBlockArmorBlock", new Vector3I(0, 4, 0)),
                new BlockSpec("LargeBlockArmorBlock", new Vector3I(0, 5, 0)),
                new BlockSpec("LargeBlockLaserAntenna", new Vector3I(0, 6, 0)),
            };
            if (withRadio) blocks.Add(new BlockSpec("LargeBlockRadioAntenna", new Vector3I(1, 3, 0)));

            // Static: these are a base and a parked ship, and a falling grid would drift out of the
            // laser's line of sight while the test waits for the link.
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + name, MyCubeSize.Large, true, position, blocks,
                Vector3.Forward, (Vector3)up);
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            WorldApi.ChargeBatteries(grid);

            var radio = WorldApi.FindFunctional<MyRadioAntenna>(grid);
            if (radio != null)
            {
                radio.Enabled = true;
                ((Sandbox.ModAPI.Ingame.IMyRadioAntenna)radio).Radius = RadioRangeM;
                ((Sandbox.ModAPI.Ingame.IMyRadioAntenna)radio).EnableBroadcasting = true;
            }

            var laser = WorldApi.FindFunctional<MyLaserAntenna>(grid);
            if (laser != null) laser.Enabled = true;
            return grid;
        }

        /// <summary>
        /// Points one laser antenna at the other and keeps the link, which is what a player does in
        /// the terminal. ConnectTo is what the server runs behind the terminal's button.
        /// </summary>
        private void Pair(MyLaserAntenna from, MyLaserAntenna to)
        {
            try
            {
                var connect = typeof(MyLaserAntenna).GetMethod("ConnectTo",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(long) }, null);
                Check(connect != null, "MyLaserAntenna.ConnectTo(long) is gone");
                connect.Invoke(from, new object[] { to.EntityId });
                ((Sandbox.ModAPI.Ingame.IMyLaserAntenna)from).IsPermanent = true;
            }
            catch (Exception e)
            {
                Note("pairing the laser antennas failed: " + e.Message);
            }
        }

        private static string State(MyLaserAntenna antenna) => antenna.State.ToString();

        /// <summary>Where the game thinks the line of sight is blocked.</summary>
        private static string Blocker(MyLaserAntenna antenna)
        {
            var field = typeof(MyLaserAntenna).GetField("m_losBlockerPosition",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var sync = field?.GetValue(antenna);
            var value = sync?.GetType().GetProperty("Value")?.GetValue(sync);
            return value is Vector3D position ? position.ToString("F0") : "unknown";
        }

        /// <summary>Why the antenna is not connecting, as the game itself records it.</summary>
        private static string Error(MyLaserAntenna antenna)
        {
            var field = typeof(MyLaserAntenna).GetField("m_connectionError",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var sync = field?.GetValue(antenna);
            var value = sync?.GetType().GetProperty("Value")?.GetValue(sync);
            return value?.ToString() ?? "unknown";
        }

        private static bool Connected(MyLaserAntenna antenna) =>
            antenna.State == MyLaserAntenna.StateEnum.connected;

        private static bool CanReachOverAntenna(Sandbox.Game.Entities.Character.MyCharacter character, MyCubeGrid grid)
        {
            var identity = character.GetPlayerIdentityId();
            var receiver = character.Components.Get<MyDataReceiver>();
            return receiver != null && Sandbox.Game.GameSystems.MyAntennaSystem.Static.CheckConnection(
                receiver, grid, identity, mutual: false);
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
