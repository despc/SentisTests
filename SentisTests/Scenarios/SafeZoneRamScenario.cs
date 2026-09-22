using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.Components;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A piloted ship rams a static grid that stands in a safe zone with damage off, at 100 m/s.
    ///
    /// Checked:
    /// <list type="bullet">
    /// <item>neither grid loses a block, integrity or shape - the damage checks look at where the
    /// grids hit, not at the zone's list of what is inside it;</item>
    /// <item>the zone takes in the ship after it crosses its border and lets it go when it is out
    /// again, and tells the clients both times (the events they learn about the zone from); how many
    /// frames each took is reported. The seated pilot is reported too: the game does not put a
    /// seated character in the zone at all - it counts as a part of the grid;</item>
    /// <item>the same ram far from any zone does damage both - otherwise the test proves nothing.</item>
    /// </list>
    /// <see cref="GameScenarioName"/> runs the same with SentisOptimisations' safe zone grid tracking
    /// off - the zone as the game has it.
    /// </summary>
    internal sealed class SafeZoneRamScenario : TestScenario
    {
        public const string ScenarioName = "safezone_ram";
        public const string GameScenarioName = "safezone_ram_vanilla";
        private const string Prefix = "szram-";
        private const float ZoneRadius = 80;
        private const float RamSpeed = 100;
        private const double StartDistance = 160;
        private const int RamFrames = 360;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly string _name;
        private readonly bool _gameTracking;
        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private Vector3D _up, _side, _forward;
        private MyEntity _zone;

        public SafeZoneRamScenario() : this(ScenarioName, false)
        {
        }

        public SafeZoneRamScenario(string name, bool gameTracking)
        {
            _name = name;
            _gameTracking = gameTracking;
        }

        public override string Name => _name;
        public override int TimeoutSeconds => 180;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _config.Set("SafeZoneGridTracking", !_gameTracking);
            // SentisGameplayImprovements lets no damage through for a while after the server starts
            _gameplay.Set("DisableAnyDamageAfterStartTime", 0);
            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            _up = Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            _side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(_up));
            _forward = Vector3D.Cross(_side, _up);
            Vector3D centre = default;
            for (var d = 100000.0; d <= 1000000; d += 50000)
            {
                centre = anchor + _up * d - _forward * 5000;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(centre).Length() < 0.001f) break;
            }
            var control = centre + _side * 3000;

            FakeClients.RemoveAll();
            FakeClients.Add(1, Network, p => (centre - _forward * StartDistance + _up * 20, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the pilot arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            var pilot = FakeClients.Character(0);
            Check(pilot != null, "the fake player has no character");

            _zone = (MyEntity)MySessionComponentSafeZones.CrateSafeZone(MatrixD.CreateWorld(centre, _forward, _up), MySafeZoneShape.Sphere,
                MySafeZoneAccess.Blacklist, null, null, ZoneRadius, enable: true, isVisible: true);
            Check(_zone != null, "no safe zone");
            var zone = (MySafeZone)_zone;
            zone.AccessTypeGrids = MySafeZoneAccess.Blacklist;
            zone.AllowedActions = MySafeZoneAction.All & ~MySafeZoneAction.Damage;
            Note("zone of radius " + ZoneRadius + " m, every grid welcome, damage off; its phantom on layer " +
                 (_zone.Physics?.RigidBody?.Layer.ToString() ?? "?") + (_gameTracking ? " (the game's tracking)" : " (the plugin's tracking)"));

            // ------------------------------------------------------------- the ram in the zone
            var wall = Slab(centre, "wall");
            var ship = Ship(centre - _forward * StartDistance, "ship");
            var place = Place(wall, centre, ship, centre - _forward * StartDistance);
            while (place.MoveNext()) yield return place.Current;

            var cockpit = WorldApi.FindFunctional<MyCockpit>(ship);
            Check(cockpit != null, "the ship has no cockpit");
            cockpit.AttachPilot(pilot, -1);
            FakeClients.TakeControl(0, cockpit);
            var seated = Wait(() => cockpit.Pilot == pilot, "the pilot sits down", 10);
            while (seated.MoveNext()) yield return seated.Current;

            var contained = (MyConcurrentHashSet<long>)typeof(MySafeZone)
                .GetField("m_containedEntities", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(zone);
            Check(!contained.Contains(ship.EntityId) && !contained.Contains(pilot.EntityId), "the zone holds the ship before it came in");

            Check(SafeZoneProbe.Start() == "safe zone probe on", "the safe zone probe did not start");
            var wallBefore = Health(wall);
            var shipBefore = Health(ship);
            ship.Physics.SetSpeeds(_forward * RamSpeed, Vector3.Zero);
            var sphere = new BoundingSphereD(centre, ZoneRadius);
            int crossedAt = -1, shipHeldAt = -1, pilotHeldAt = -1, hitAt = -1;
            var worstFrame = 0.0;
            for (var f = 0; f < RamFrames; f++)
            {
                var at = DateTime.UtcNow;
                yield return null;
                worstFrame = Math.Max(worstFrame, (DateTime.UtcNow - at).TotalMilliseconds);
                if (crossedAt < 0 && sphere.Contains(ship.PositionComp.WorldAABB) != ContainmentType.Disjoint) crossedAt = f;
                if (shipHeldAt < 0 && contained.Contains(ship.EntityId)) shipHeldAt = f;
                if (pilotHeldAt < 0 && contained.Contains(pilot.EntityId)) pilotHeldAt = f;
                if (hitAt < 0 && ship.Physics != null && ship.Physics.LinearVelocity.Length() < RamSpeed * 0.5f) hitAt = f;
            }
            var wallAfter = Health(wall);
            var shipAfter = Health(ship);
            Note("ram in the zone at " + RamSpeed + " m/s: crossed the border at frame " + crossedAt + ", hit the wall at frame " + hitAt +
                 "; the zone took in the ship " + Late(shipHeldAt, crossedAt) + ", the pilot " + Late(pilotHeldAt, crossedAt) +
                 "; wall " + Describe(wallBefore, wallAfter) + "; ship " + Describe(shipBefore, shipAfter) + "; worst frame " + worstFrame.ToString("F0") + " ms");
            Check(hitAt >= 0, "the ship never hit the wall");
            Check(shipHeldAt >= 0, "the zone never took in the ship");
            Check(SafeZoneProbe.SentIn.Contains(ship.EntityId), "the clients were not told the ship came in");
            Check(Same(wallBefore, wallAfter), "the wall in the zone was damaged: " + Describe(wallBefore, wallAfter));
            Check(Same(shipBefore, shipAfter), "the ship that rammed it in the zone was damaged: " + Describe(shipBefore, shipAfter));

            // out of the zone again: both let go
            var outside = ship.WorldMatrix;
            outside.Translation = centre - _forward * (ZoneRadius + 200);
            ship.Teleport(outside);
            ship.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
            int shipLetGo = -1, pilotLetGo = -1;
            for (var f = 0; f < 60; f++)
            {
                yield return null;
                if (shipLetGo < 0 && !contained.Contains(ship.EntityId)) shipLetGo = f + 1;
                if (pilotLetGo < 0 && !contained.Contains(pilot.EntityId)) pilotLetGo = f + 1;
            }
            Note("out of the zone: the ship let go " + (shipLetGo < 0 ? "NEVER" : "after " + shipLetGo + " frames") +
                 ", the pilot " + (pilotLetGo < 0 ? "NEVER" : "after " + pilotLetGo + " frames") + "; the pilot still seated: " + (cockpit.Pilot == pilot));
            Check(shipLetGo >= 0 && pilotLetGo >= 0, "the zone kept the ship or its pilot after they left");
            Check(SafeZoneProbe.SentOut.Contains(ship.EntityId), "the clients were not told the ship went out");
            Note("told the clients: in " + string.Join(",", SafeZoneProbe.SentIn.Select(id => id == ship.EntityId ? "ship" : id == pilot.EntityId ? "pilot" : id.ToString())) +
                 "; out " + string.Join(",", SafeZoneProbe.SentOut.Select(id => id == ship.EntityId ? "ship" : id == pilot.EntityId ? "pilot" : id.ToString())));
            SafeZoneProbe.Stop();
            var result = "in the zone: wall " + Describe(wallBefore, wallAfter) + ", ship " + Describe(shipBefore, shipAfter) +
                         ", ship taken in " + Late(shipHeldAt, crossedAt) + " (seated pilot " + Late(pilotHeldAt, crossedAt) + ")" +
                         ", let go after " + shipLetGo + "/" + pilotLetGo + " frames";
            cockpit.RemovePilot();
            ship.Close();
            wall.Close();

            // ------------------------------------------------------------- the same ram with no zone
            var controlWall = Slab(control, "control-wall");
            var controlShip = Ship(control - _forward * StartDistance, "control-ship");
            var placeControl = Place(controlWall, control, controlShip, control - _forward * StartDistance);
            while (placeControl.MoveNext()) yield return placeControl.Current;
            var controlWallBefore = Health(controlWall);
            var controlShipBefore = Health(controlShip);
            controlShip.Physics.SetSpeeds(_forward * RamSpeed, Vector3.Zero);
            var controlHitAt = -1;
            var closest = double.MaxValue;
            var controlZones = new List<MySafeZone>();
            for (var f = 0; f < RamFrames; f++)
            {
                yield return null;
                if (controlShip.MarkedForClose) break;
                closest = Math.Min(closest, Vector3D.Distance(controlShip.PositionComp.WorldAABB.Center, control));
                if (controlHitAt < 0 && controlShip.Physics != null && controlShip.Physics.LinearVelocity.Length() < RamSpeed * 0.5f) controlHitAt = f;
            }
            MySessionComponentSafeZones.GetBelongingSafezones(controlShip.EntityId, controlZones);
            var controlWallAfter = Health(controlWall);
            var controlShipAfter = Health(controlShip);
            Note("the same ram with no zone: hit the wall at frame " + controlHitAt + ", came within " + closest.ToString("F1") +
                 " m of its middle, ship in " + controlZones.Count + " zones, destructible blocks " + controlShip.BlocksDestructionEnabled +
                 "; wall " + Describe(controlWallBefore, controlWallAfter) + "; ship " + Describe(controlShipBefore, controlShipAfter));
            Check(!Same(controlWallBefore, controlWallAfter) && !Same(controlShipBefore, controlShipAfter),
                "the ram with no zone damaged nothing, so the test cannot show the zone protects");
            result += " || no zone: wall " + Describe(controlWallBefore, controlWallAfter) + ", ship " + Describe(controlShipBefore, controlShipAfter);
            Note("SAFEZONE RAM RESULT: " + result);
        }

        private IEnumerator Place(MyCubeGrid wall, Vector3D wallAt, MyCubeGrid ship, Vector3D shipAt)
        {
            yield return null;
            yield return null;
            // put the middle of each grid's box where it belongs (a grid's origin is a corner of it)
            foreach (var (grid, at) in new[] { (wall, wallAt), (ship, shipAt) })
            {
                var matrix = grid.WorldMatrix;
                matrix.Translation += at - grid.PositionComp.WorldAABB.Center;
                grid.Teleport(matrix);
                grid.Physics?.SetSpeeds(Vector3.Zero, Vector3.Zero);
            }
            var settle = WaitForSeconds(2, "the grids settle");
            while (settle.MoveNext()) yield return settle.Current;
        }

        private struct Hurt
        {
            public int Blocks;
            public double Integrity;
            public int Deformed;
            public bool Gone;
        }

        private static Hurt Health(MyCubeGrid grid)
        {
            var hurt = new Hurt { Gone = grid == null || grid.MarkedForClose || grid.Closed };
            if (hurt.Gone) return hurt;
            foreach (var block in grid.CubeBlocks)
            {
                hurt.Blocks++;
                hurt.Integrity += block.Integrity;
                if (block.HasDeformation) hurt.Deformed++;
            }
            return hurt;
        }

        private static bool Same(Hurt before, Hurt after) =>
            !after.Gone && after.Blocks == before.Blocks && after.Deformed == before.Deformed &&
            Math.Abs(after.Integrity - before.Integrity) < 0.001;

        private static string Describe(Hurt before, Hurt after) =>
            after.Gone ? "GONE" :
            Same(before, after) ? "untouched (" + after.Blocks + " blocks)" :
            "blocks " + before.Blocks + "->" + after.Blocks + ", integrity " + before.Integrity.ToString("F0") + "->" +
            after.Integrity.ToString("F0") + ", deformed " + before.Deformed + "->" + after.Deformed;

        private static string Late(int heldAt, int crossedAt) =>
            heldAt < 0 ? "NEVER" : crossedAt < 0 ? "at frame " + heldAt : (heldAt - crossedAt) + " frames after it crossed";

        /// <summary>A static slab of light armour 12 x 4 x 12, facing the ship's way in.</summary>
        private MyCubeGrid Slab(Vector3D middle, string name)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 12; x++)
            for (var y = 0; y < 4; y++)
            for (var z = 0; z < 12; z++)
            {
                var block = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                block.Min = new SerializableVector3I(x, y, z);
                blocks.Add(block);
            }
            return Spawn(middle, name, true, blocks);
        }

        /// <summary>A ship of light armour 3 x 3 x 6 with a cockpit and a battery on top.</summary>
        private MyCubeGrid Ship(Vector3D middle, string name)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 3; x++)
            for (var y = 0; y < 3; y++)
            for (var z = 0; z < 6; z++)
            {
                var block = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                block.Min = new SerializableVector3I(x, y, z);
                blocks.Add(block);
            }
            var cockpit = WorldApi.MakeBlockOb("LargeBlockCockpit");
            cockpit.Min = new SerializableVector3I(1, 3, 3);
            blocks.Add(cockpit);
            var battery = (MyObjectBuilder_BatteryBlock)WorldApi.MakeBlockOb("LargeBlockBatteryBlock");
            battery.Min = new SerializableVector3I(1, 3, 5);
            battery.CurrentStoredPower = 3f;
            blocks.Add(battery);
            return Spawn(middle, name, false, blocks);
        }

        private MyCubeGrid Spawn(Vector3D at, string name, bool isStatic, List<MyObjectBuilder_CubeBlock> blocks)
        {
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = isStatic,
                // the ship's forward (-Z) along the ram
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, _forward, _up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            return grid;
        }

        public override void Cleanup()
        {
            try
            {
                SafeZoneProbe.Stop();
                _config.Restore();
                _gameplay.Restore();
                FakeClients.RemoveAll();
                if (_zone != null && !_zone.Closed) _zone.Close();
            }
            catch (Exception e)
            {
                Log.Warn("cleaning up after the test failed: " + e.Message);
            }
            finally { base.Cleanup(); }
        }
    }
}
