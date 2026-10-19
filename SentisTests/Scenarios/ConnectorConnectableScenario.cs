using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A connector shows yellow ("Connectable") when another one is held to it (complaints from the live server,
    /// 19.10.2026: connectors that would not turn yellow, the more so on grids with blocks of the Factorum style). Pairs
    /// side by side by the test place, each a static base (a block, a battery, a large connector facing up) and a ship
    /// (a large connector facing down, a block and a battery over it) let down onto it, a player beside them; each pair
    /// must show Connectable within ten seconds:
    /// <list type="bullet">
    /// <item>plain blocks;</item>
    /// <item>both grids with blocks of the Contact pack (the Factorum's style: floodlights, the modular bridge);</item>
    /// <item>the base frozen first by the freezer with nobody near (its physics left alone, and frozen with it), then
    /// the player back, the base thawed, and the ship let down onto it;</item>
    /// <item>a ship of the Factorum (owned by its founder): not friendly, so not yellow - the game's own rule, the check
    /// that the scenario sees a "no" where there is one.</item>
    /// </list>
    /// </summary>
    public sealed class ConnectorConnectableScenario : TestScenario
    {
        public const string ScenarioName = "connector_connectable";
        private const string Prefix = "ConnYellow_";
        private const double FreezeDistance = 500;
        private readonly ConfigOverride _config = new ConfigOverride();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 600;

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }

        private Vector3D _ground, _up, _side;
        private readonly List<string> _results = new List<string>();
        private readonly List<string> _failures = new List<string>();

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchor = WorldApi.PlanetAnchor("drill_dock");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var probe = anchor.Translation + Vector3D.Normalize(anchor.Translation - centre) * 50 - Vector3D.CalculatePerpendicularVector(anchor.Translation - centre) * 600;
            _ground = planet.GetClosestSurfacePointGlobal(ref probe);
            _up = Vector3D.Normalize(_ground - centre);
            _side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(_up));
            WorldApi.RevertTerrain(planet, _ground, 120);
            var factorum = MySession.Static.Factions.TryGetFactionByTag("FCTM");
            Note("CONNECTOR | the Factorum " + (factorum == null ? "is not in this world" : "founder " + factorum.FounderId));

            _config.Set("FreezeDistanceDynamic", (int)FreezeDistance);
            _config.Set("FreezeDistanceStatic", (int)FreezeDistance);
            _config.Set("FreezerEnabled", true);

            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (_ground + _up * 3 - Vector3D.Cross(_up, _side) * 12, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(4, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            var plain = Pair("plain", 0, false, WorldApi.TestIdentityId(), true);
            while (plain.MoveNext()) yield return plain.Current;
            var contact = Pair("Contact blocks", 1, true, WorldApi.TestIdentityId(), true);
            while (contact.MoveNext()) yield return contact.Current;
            if (factorum != null)
            {
                var npc = Pair("Factorum's ship", 2, true, factorum.FounderId, false);
                while (npc.MoveNext()) yield return npc.Current;
            }
            foreach (var physics in new[] { false, true })
            {
                _config.Set("FreezePhysics", physics);
                var thawed = FrozenThenDocked("frozen base (Freeze Physics " + (physics ? "on" : "off") + ")", physics ? 4 : 3, physics);
                while (thawed.MoveNext()) yield return thawed.Current;
            }

            Note("CONNECTOR RESULT | " + string.Join(" | ", _results));
            Check(_failures.Count == 0, string.Join("; ", _failures));
        }

        /// <summary>A pair in the row's place <paramref name="slot"/>: the base, the ship let down onto it, the yellow waited for.</summary>
        private IEnumerator Pair(string name, int slot, bool contactBlocks, long shipOwner, bool yellowWanted, MyCubeGrid existingBase = null)
        {
            var home = existingBase ?? SpawnBase(name, slot, contactBlocks);
            var dock = home.GetFatBlocks().OfType<MyShipConnector>().First();
            var ship = SpawnShip(name, home, contactBlocks, shipOwner);
            var made = Wait(() => ship.Physics != null, name + ": the ship's body", 10);
            while (made.MoveNext()) yield return made.Current;
            var own = ship.GetFatBlocks().OfType<MyShipConnector>().First();
            var started = DateTime.UtcNow;
            var seen = false;
            while ((DateTime.UtcNow - started).TotalSeconds < 10)
            {
                var a = ((Sandbox.ModAPI.Ingame.IMyShipConnector)dock).Status;
                var b = ((Sandbox.ModAPI.Ingame.IMyShipConnector)own).Status;
                if (a != Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Unconnected || b != Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Unconnected) { seen = true; break; }
                yield return null;
            }
            var said = $"{name}: {(seen ? "yellow" : "not yellow")} after {(DateTime.UtcNow - started).TotalSeconds:0.0} s " +
                       $"(base {((Sandbox.ModAPI.Ingame.IMyShipConnector)dock).Status}, ship {((Sandbox.ModAPI.Ingame.IMyShipConnector)own).Status}; " +
                       $"lock points {Vector3D.Distance(dock.ConstraintPositionWorld(), own.ConstraintPositionWorld()):0.00} m apart; " +
                       $"grids detected base {dock.DetectedGridCount}, ship {own.DetectedGridCount}; friendly {dock.FriendlyWithBlock(own) && own.FriendlyWithBlock(dock)})";
            _results.Add(said);
            Note("CONNECTOR | " + said);
            if (seen != yellowWanted) _failures.Add(said);
        }

        /// <summary>The base made, frozen with nobody near, the player back, thawed - then the ship let down onto it.</summary>
        private IEnumerator FrozenThenDocked(string name, int slot, bool physics)
        {
            var home = SpawnBase(name, slot, true);
            var settle = WaitForSeconds(3, name + ": the base settles");
            while (settle.MoveNext()) yield return settle.Current;
            FakeClients.RemoveAll();
            var frozen = Wait(() => FreezerState.IsFrozen(home), name + ": the base frozen", 120);
            while (frozen.MoveNext()) yield return frozen.Current;
            var hold = WaitForSeconds(5, name + ": frozen");
            while (hold.MoveNext()) yield return hold.Current;
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (_ground + _up * 3 - Vector3D.Cross(_up, _side) * 12, 0, 0), withCharacters: true);
            var thawed = Wait(() => !FreezerState.IsFrozen(home) && !FreezerState.IsPhysicsFrozen(home), name + ": the base thawed", 60);
            while (thawed.MoveNext()) yield return thawed.Current;
            var after = WaitForSeconds(2, name + ": thawed");
            while (after.MoveNext()) yield return after.Current;
            var pair = Pair(name, slot, true, WorldApi.TestIdentityId(), true, home);
            while (pair.MoveNext()) yield return pair.Current;
        }

        private Vector3D Origin(int slot) => _ground + _up * 1.5 + _side * (slot * 25);

        private MyCubeGrid SpawnBase(string name, int slot, bool contactBlocks)
        {
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "base-" + slot, MyCubeSize.Large, true, Origin(slot), Enumerable.Empty<BlockSpec>(), (Vector3)_side, (Vector3)_up);
            Add(ob, "LargeBlockArmorBlock", 0, 0, 0, WorldApi.TestIdentityId());
            Add(ob, "LargeBlockBatteryBlock", 1, 0, 0, WorldApi.TestIdentityId());
            var connector = Add(ob, "Connector", 0, 1, 0, WorldApi.TestIdentityId());
            connector.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Up, Base6Directions.Direction.Backward);
            if (contactBlocks)
            {
                Add(ob, "LargeBlockFloodlight", -1, 0, 0, WorldApi.TestIdentityId());
                Add(ob, "LargeBlockModularBridgeCorner", 0, 0, 1, WorldApi.TestIdentityId());
            }
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            WorldApi.ChargeBatteries(grid);
            return grid;
        }

        private MyCubeGrid SpawnShip(string name, MyCubeGrid home, bool contactBlocks, long owner)
        {
            var dock = home.GetFatBlocks().OfType<MyShipConnector>().First();
            // its connector right over the base's, face to face: the connector's cell 2.5 m over the base's
            var at = dock.PositionComp.GetPosition() + _up * 2.55;
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "ship-" + home.EntityId, MyCubeSize.Large, false, at, Enumerable.Empty<BlockSpec>(), (Vector3)_side, (Vector3)_up);
            var connector = Add(ob, "Connector", 0, 0, 0, owner);
            connector.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Down, Base6Directions.Direction.Forward);
            Add(ob, "LargeBlockArmorBlock", 0, 1, 0, owner);
            Add(ob, "LargeBlockBatteryBlock", 1, 1, 0, owner);
            if (contactBlocks) Add(ob, "LargeBlockFloodlight", -1, 1, 0, owner);
            ob.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.Enabled;
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            WorldApi.ChargeBatteries(grid);
            return grid;
        }

        private static MyObjectBuilder_CubeBlock Add(MyObjectBuilder_CubeGrid ob, string subtype, int x, int y, int z, long owner)
        {
            var block = WorldApi.MakeBlockOb(subtype);
            block.Min = new SerializableVector3I(x, y, z);
            block.Owner = owner;
            block.BuiltBy = owner;
            ob.CubeBlocks.Add(block);
            return block;
        }
    }
}
