using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Blocks that only look at the world wait where the world stands still, and look again when a player comes
    /// (SentisOptimisations SelectivePhysicsBodies).
    ///
    /// Where no one is, the game does not step the physics cluster, and the plugin leaves out there what only makes
    /// sense with a moving world: the thrust of a grid, a connector looking for another, a sensor looking over its
    /// field. This puts a station with a sensor and a connector in space, and a free grid with its own connector a hand
    /// away from it, with the freezer off and nobody near, as on a server nobody is on:
    /// <list type="bullet">
    /// <item>alone, the cluster is not stepped and the connectors do not find each other (the skip is on);</item>
    /// <item>a player comes beside the station: the cluster is stepped, the sensor sees the player and the connectors
    /// find each other within seconds - everything takes up where it was.</item>
    /// </list>
    /// </summary>
    public sealed class UnsteppedBlocksScenario : TestScenario
    {
        public const string ScenarioName = "unstepped_blocks";
        private const string Prefix = "unstep-";
        private const double SpaceHeightM = 150000;
        private const double AloneSeconds = 10;
        private const int ResumeSeconds = 30;
        private const float SensorRangeM = 20;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly ConfigOverride _config = new ConfigOverride();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);
            // as on the old server: nothing frozen, only the physics clusters decide
            _config.Set("FreezerEnabled", false);

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchorM.Translation - centre);
            var site = centre + up * ((anchorM.Translation - centre).Length() + SpaceHeightM);
            var forward = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));

            // the station: battery, sensor, and a connector facing forward; the free grid turned round, its connector
            // facing back at the station's, the two faces 0.2 m apart
            var station = WorldApi.SpawnGrid(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "station", MyCubeSize.Large, true, site, new[]
            {
                new BlockSpec("LargeBlockBatteryBlock", new Vector3I(0, 0, 0)),
                new BlockSpec("LargeBlockSensor", new Vector3I(0, 1, 0)),
                new BlockSpec("Connector", new Vector3I(0, 0, -1)),
            }, forward, up));
            Track(station);
            var free = WorldApi.SpawnGrid(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "free", MyCubeSize.Large, false, site + forward * 7.7, new[]
            {
                new BlockSpec("LargeBlockBatteryBlock", new Vector3I(0, 0, 0)),
                new BlockSpec("Connector", new Vector3I(0, 0, -1)),
            }, -forward, up));
            Track(free);
            yield return WaitForTicks(10);

            var sensor = Require<Sandbox.ModAPI.IMySensorBlock>(WorldApi.FindFunctional<Sandbox.ModAPI.IMySensorBlock>(station), "the station's sensor");
            var stationConnector = Require<Sandbox.ModAPI.IMyShipConnector>(WorldApi.FindFunctional<Sandbox.ModAPI.IMyShipConnector>(station), "the station's connector");
            var freeConnector = Require<Sandbox.ModAPI.IMyShipConnector>(WorldApi.FindFunctional<Sandbox.ModAPI.IMyShipConnector>(free), "the free grid's connector");
            sensor.LeftExtend = sensor.RightExtend = sensor.TopExtend = sensor.BottomExtend = sensor.FrontExtend = sensor.BackExtend = SensorRangeM;
            sensor.DetectPlayers = sensor.DetectOwner = sensor.DetectFriendly = sensor.DetectNeutral = sensor.DetectEnemy = true;
            sensor.Enabled = true;
            stationConnector.Enabled = freeConnector.Enabled = true;

            var alone = WaitForSeconds(AloneSeconds, "alone, nobody near");
            while (alone.MoveNext()) yield return alone.Current;
            var steppedAlone = Stepped(station);
            var statusAlone = stationConnector.Status;
            Note("alone: cluster stepped " + steppedAlone + ", connector " + statusAlone + ", sensor active " + sensor.IsActive);
            Check(steppedAlone == false, "the station's cluster is stepped with nobody near, so nothing is measured");
            Check(statusAlone == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Unconnected,
                "the connectors found each other with nobody near: the skip is off (" + statusAlone + ")");

            // a player beside the station, inside the sensor's field
            var playerAt = site + up * 6;
            FakeClients.Add(1, Network, i => (playerAt, 0, 0), withCharacters: true);
            var started = MySession.Static.ElapsedPlayTime.TotalSeconds;
            var resumed = Wait(() => Stepped(station) == true && sensor.IsActive &&
                                     stationConnector.Status != Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Unconnected,
                "the player's cluster stepped, the sensor sees the player, the connectors find each other", ResumeSeconds);
            while (resumed.MoveNext()) yield return resumed.Current;
            var took = MySession.Static.ElapsedPlayTime.TotalSeconds - started;
            Note("UNSTEPPED BLOCKS RESULT | alone: not stepped, connectors unconnected | with the player after " + took.ToString("F1") +
                 " s: stepped, sensor active, connector " + stationConnector.Status);
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

        /// <summary>Whether the game steps the Havok world the grid is in; null without a body in a world.</summary>
        private static bool? Stepped(MyCubeGrid grid)
        {
            var world = grid.Physics?.HavokWorld;
            if (world == null) return null;
            var physics = MySession.Static.GetComponent<MyPhysics>();
            foreach (var cluster in MyPhysics.Clusters.GetClusters())
                if (cluster.UserData == world)
                    return (bool)IsClusterActive.Invoke(physics, new object[] { cluster.ClusterId, world.CharacterRigidBodies.Count });
            return null;
        }

        private static readonly MethodInfo IsClusterActive =
            typeof(MyPhysics).GetMethod("IsClusterActive", Any, null, new[] { typeof(int), typeof(int) }, null)
            ?? throw new System.MissingMethodException("MyPhysics", "IsClusterActive");
    }
}
