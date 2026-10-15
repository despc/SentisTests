using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Havok;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A door on a dynamic grid locked by connectors to a static one must work: from the server (15.10.2026), such a door
    /// in a safe zone showed open and stopped the player, or stayed open and did not answer. SentisOptimisations'
    /// DockedCollisions puts the locked grid's body in the static grid's collision group (so the two do not collide);
    /// the door's leaves stayed in the grid's old group, collided with their own grid, sat a metre off where they are
    /// drawn, and a leaf touching anything while the door closes opens it again (<c>MyDoor.ContactCallbackInternal</c>).
    /// A static station (a plate, a connector, a battery) and a dynamic ship (a plate, a connector, a battery, a door) in
    /// space, locked; then the door is closed, opened and closed. Passes when the leaves are in the grid's collision
    /// group, each time the door gets where it was told within <see cref="MoveSeconds"/> s, and the leaves' bodies are
    /// where the leaves are (under <see cref="LeafOffM"/>).
    /// </summary>
    public sealed class DockedDoorScenario : TestScenario
    {
        public const string ScenarioName = "docked_door";
        private const string Prefix = "docked-door-";
        private const double MoveSeconds = 6, LeafOffM = 0.1;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly ConfigOverride _config = new ConfigOverride();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _config.Set("DockedCollisionsOff", true);
            _config.Set("FreezerEnabled", false);
            var doorDef = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyCubeBlockDefinition>()
                .Where(d => d.Id.TypeId.ToString() == "MyObjectBuilder_Door" && d.CubeSize == MyCubeSize.Large && d.Public)
                .OrderBy(d => d.Id.SubtypeName.IndexOf("Offset", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).ThenBy(d => d.Size.Volume())
                .FirstOrDefault();
            Check(doorDef != null, "no large door");

            var spot = new Vector3D(0, 0, 0);
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            for (var i = 0; i < 40; i++)
            {
                var probe = new Vector3D(170000 + i * 20000, 95000, -110000 - i * 15000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() < 1e-6 &&
                    planets.All(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) > p.MaximumRadius + 5000))
                {
                    spot = probe;
                    break;
                }
            }
            Check(MyGravityProviderSystem.CalculateNaturalGravityInPoint(spot).LengthSquared() < 1e-6, "no place in space without gravity");

            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (spot + new Vector3D(0, 6, 30), 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            var up = Vector3D.Up;
            var forward = Vector3D.Forward;
            var station = Spawn("station", spot, forward, up, 7, true, new[]
            {
                Block("Connector", new Vector3I(3, 1, 3), Base6Directions.Direction.Up, Base6Directions.Direction.Forward),
                Block("LargeBlockBatteryBlock", new Vector3I(0, 1, 0)),
            });
            var ship = Spawn("ship", spot + up * 60.0, forward, -up, 3, false, new[]
            {
                Block("Connector", new Vector3I(1, 1, 1), Base6Directions.Direction.Up, Base6Directions.Direction.Forward),
                Block("LargeBlockBatteryBlock", new Vector3I(0, 1, 0)),
                Block(doorDef.Id.SubtypeName, new Vector3I(1, -1, 1)),
            });
            var door = ship.GetFatBlocks().OfType<MyDoor>().FirstOrDefault();
            Check(door != null, $"the door ({doorDef.Id.SubtypeName}) was not made");
            var a = station.GetFatBlocks().OfType<MyShipConnector>().First();
            var b = ship.GetFatBlocks().OfType<MyShipConnector>().First();
            {
                // the ship turned and moved so that its port lies on the station's, facing it, a little apart (as docked_shake)
                var axisOf = typeof(MyShipConnector).GetMethod("ConstraintAxisGridSpace", Any);
                Vector3D Axis(MyShipConnector c) => Vector3D.TransformNormal((Vector3)axisOf.Invoke(c, null), c.CubeGrid.WorldMatrix);
                var turn = MatrixD.CreateFromQuaternion(QuaternionD.CreateFromTwoVectors(Axis(b), -Axis(a)));
                var m = ship.WorldMatrix;
                var pivot = b.ConstraintPositionWorld();
                m = m * MatrixD.CreateTranslation(-pivot) * turn * MatrixD.CreateTranslation(pivot);
                var turnedPort = Vector3D.Transform(Vector3D.Transform(pivot, ship.PositionComp.WorldMatrixNormalizedInv), m);
                m.Translation += a.ConstraintPositionWorld() - turnedPort + Axis(a) * DockedShakeScenario.PortGapM;
                ship.Teleport(m);
                ship.Physics.LinearVelocity = Vector3.Zero;
                ship.Physics.AngularVelocity = Vector3.Zero;
            }
            WorldApi.ChargeBatteries(station);
            WorldApi.ChargeBatteries(ship);
            for (var i = 0; i < 300 && !a.Connected; i++)
            {
                if (i % 20 == 0) typeof(MyShipConnector).GetMethod("TryAttach", Any, null, new[] { typeof(long?) }, null)?.Invoke(b, new object[] { (long?)a.EntityId });
                if (i % 20 == 5) ((Sandbox.ModAPI.Ingame.IMyShipConnector)b).Connect();
                yield return null;
            }
            Check(a.Connected, "the connectors did not lock");
            var settle = WaitForSeconds(2, "the lock settles");
            while (settle.MoveNext()) yield return settle.Current;
            Note($"DOOR | locked, {doorDef.Id.SubtypeName}: {Describe(door)}");

            var problems = new List<string>();
            if (!LeavesInGridGroup(door)) problems.Add("after the lock the leaves are not in the grid's collision group");
            foreach (var open in new[] { false, true, false })
            {
                var d = (Sandbox.ModAPI.Ingame.IMyDoor)door;
                if (open) d.OpenDoor(); else d.CloseDoor();
                var want = open ? Sandbox.ModAPI.Ingame.DoorStatus.Open : Sandbox.ModAPI.Ingame.DoorStatus.Closed;
                var started = DateTime.UtcNow;
                while (d.Status != want && (DateTime.UtcNow - started).TotalSeconds < MoveSeconds) yield return null;
                yield return WaitForTicks(10);
                Note($"DOOR | told to {(open ? "open" : "close")}: {(d.Status == want ? $"{want} in {(DateTime.UtcNow - started).TotalSeconds:0.0} s" : $"still {d.Status} after {MoveSeconds} s")}; {Describe(door)}");
                if (d.Status != want) problems.Add($"told to {(open ? "open" : "close")}, the door is {d.Status}");
                var off = LeafOff(door);
                if (off > LeafOffM) problems.Add($"{(open ? "open" : "closed")}: a leaf's body {off:0.00} m off the leaf");
            }
            Check(problems.Count == 0, string.Join("; ", problems));
        }

        private static bool LeavesInGridGroup(MyDoor door)
        {
            var system = door.CubeGrid.Physics.HavokCollisionSystemID;
            return Leaves(door).All(p => p.Physics?.RigidBody == null || HkGroupFilter.GetSystemGroupFromFilterInfo(p.Physics.RigidBody.GetCollisionFilterInfo()) == system);
        }

        private static double LeafOff(MyDoor door) =>
            Leaves(door).Select(p => p.Physics is MyPhysicsBody body && body.RigidBody != null
                ? (body.GetWorldMatrix().Translation - p.PositionComp.WorldMatrixRef.Translation).Length() : 0).DefaultIfEmpty(0).Max();

        private static IEnumerable<MyEntitySubpart> Leaves(MyDoor door) =>
            new[] { "m_leftSubpart", "m_rightSubpart" }.Select(n => typeof(MyDoor).GetField(n, Any)?.GetValue(door) as MyEntitySubpart).Where(p => p != null);

        private static string Describe(MyDoor door)
        {
            var grid = door.CubeGrid.Physics;
            return $"open={door.Open} ratio={door.OpenRatio:0.00}; the grid's group {grid.HavokCollisionSystemID} (static body {grid.IsStatic}), the leaves' " +
                   string.Join(", ", Leaves(door).Select(p => p.Physics?.RigidBody == null ? "no body"
                       : $"{HkGroupFilter.GetSystemGroupFromFilterInfo(p.Physics.RigidBody.GetCollisionFilterInfo())}")) +
                   $", the leaves' bodies up to {LeafOff(door):0.00} m off";
        }

        private static MyObjectBuilder_CubeBlock Block(string subtype, Vector3I at, Base6Directions.Direction forward = Base6Directions.Direction.Forward,
            Base6Directions.Direction up = Base6Directions.Direction.Up)
        {
            var block = WorldApi.MakeBlockOb(subtype);
            block.Min = new SerializableVector3I(at.X, at.Y, at.Z);
            block.BlockOrientation = new SerializableBlockOrientation(forward, up);
            return block;
        }

        private MyCubeGrid Spawn(string name, Vector3D at, Vector3D forward, Vector3D up, int size, bool isStatic, IEnumerable<MyObjectBuilder_CubeBlock> extra)
        {
            var owner = WorldApi.PlayerIdentityId();
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < size; x++)
            for (var z = 0; z < size; z++)
                blocks.Add(Block("LargeBlockArmorBlock", new Vector3I(x, 0, z)));
            blocks.AddRange(extra);
            foreach (var block in blocks)
            {
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
            }
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = isStatic,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, forward, up)),
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
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
