using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Two dynamic grids locked together by connectors in space must stand still. Reported from the server: docked ships
    /// shake, a little or a lot ("Бур" on "Опорник Бурмалда"); the dampeners of the docked grid kicked against the lock's
    /// constraint every frame (connector_shake). A station (a big plate with a connector and a battery) and a ship (a
    /// plate with a connector, a battery, thrusters on every side and a gyroscope, dampeners on) are made in space, the
    /// ship's connector against the station's, locked, and watched for <see cref="WatchSeconds"/>: how fast the ship moves
    /// and how much its speed changes a frame, and what its thrusters fire.
    ///  - "docked_shake": SentisOptimisations' DockedDampenersCalm and DockedCollisionsOff on - the ship must stand (under
    ///    <see cref="CalmMps"/>) and not be thrown when it lets go;
    ///  - "docked_shake_off": both off - what the game does by itself, written for comparison (the ports are put a
    ///    little into each other, as a ship docked crooked: the game shakes it and throws it off).
    /// Then the ship lets go and must not be thrown: its speed a second after the release under <see cref="ReleaseMps"/>.
    /// </summary>
    public sealed class DockedShakeScenario : TestScenario
    {
        public const string ScenarioName = "docked_shake";
        public const string OffName = "docked_shake_off";
        private const string Prefix = "docked-";
        private const double WatchSeconds = 15, CalmMps = 0.003, ReleaseMps = 0.5;

        /// <summary>How far apart the two ports are put before the lock: the blocks touch, they do not overlap.</summary>
        public static double PortGapM = 0.3;

        private readonly bool _calm;
        private readonly ConfigOverride _config = new ConfigOverride();

        public DockedShakeScenario(bool calm) => _calm = calm;

        public override string Name => _calm ? ScenarioName : OffName;
        public override int TimeoutSeconds => 120;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _config.Set("DockedDampenersCalm", _calm);
            _config.Set("DockedCollisionsOff", _calm);
            _config.Set("FreezerEnabled", false);

            // a place in space: no planet's gravity
            var spot = new Vector3D(0, 0, 0);
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            for (var i = 0; i < 40; i++)
            {
                var probe = new Vector3D(150000 + i * 20000, 90000, -120000 - i * 15000);
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
            // the station: a 7x7 plate, its connector on top facing up, a battery
            var station = Spawn("station", spot, forward, up, 7, new[]
            {
                Block("Connector", new Vector3I(3, 1, 3), Base6Directions.Direction.Up, Base6Directions.Direction.Forward),
                Block("LargeBlockBatteryBlock", new Vector3I(0, 1, 0)),
            });
            // the ship over it, upside down: its connector facing down onto the station's, thrusters on every side
            // made away from the station (made over it, it overlapped the station and set it spinning), then moved in
            var shipAt = spot + up * 60.0;
            var ship = Spawn("ship", shipAt, forward, -up, 3, new[]
            {
                Block("Connector", new Vector3I(1, 1, 1), Base6Directions.Direction.Up, Base6Directions.Direction.Forward),
                Block("LargeBlockBatteryBlock", new Vector3I(0, 1, 0)),
                Block("LargeBlockGyro", new Vector3I(2, 1, 2)),
                Block("LargeBlockSmallThrust", new Vector3I(0, 1, 2), Base6Directions.Direction.Forward, Base6Directions.Direction.Up),
                Block("LargeBlockSmallThrust", new Vector3I(2, 1, 0), Base6Directions.Direction.Backward, Base6Directions.Direction.Up),
                Block("LargeBlockSmallThrust", new Vector3I(0, -1, 0), Base6Directions.Direction.Left, Base6Directions.Direction.Up),
                Block("LargeBlockSmallThrust", new Vector3I(2, -1, 2), Base6Directions.Direction.Right, Base6Directions.Direction.Up),
                Block("LargeBlockSmallThrust", new Vector3I(1, -1, 0), Base6Directions.Direction.Up, Base6Directions.Direction.Forward),
                Block("LargeBlockSmallThrust", new Vector3I(0, 2, 1), Base6Directions.Direction.Down, Base6Directions.Direction.Forward),
            });
            // the ship turned and moved so that its connector's port lies on the station's, facing it (the port's axis
            // and point are the connector's own: ConstraintAxisGridSpace, ConstraintPositionWorld)
            {
                var sa = station.GetFatBlocks().OfType<MyShipConnector>().First();
                var sb = ship.GetFatBlocks().OfType<MyShipConnector>().First();
                var axisOf = typeof(MyShipConnector).GetMethod("ConstraintAxisGridSpace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Vector3D Axis(MyShipConnector c) => Vector3D.TransformNormal((Vector3)axisOf.Invoke(c, null), c.CubeGrid.WorldMatrix);
                var want = -Axis(sa);
                var have = Axis(sb);
                var turn = MatrixD.CreateFromQuaternion(QuaternionD.CreateFromTwoVectors(have, want));
                var m = ship.WorldMatrix;
                var pivot = sb.ConstraintPositionWorld();
                m = m * MatrixD.CreateTranslation(-pivot) * turn * MatrixD.CreateTranslation(pivot);
                // the port after the turn, worked out from the grid's own space (the grid is moved once, by Teleport:
                // setting the world matrix left the body where it was)
                var portInGrid = Vector3D.Transform(pivot, ship.PositionComp.WorldMatrixNormalizedInv);
                var turnedPort = Vector3D.Transform(portInGrid, m);
                // the ports a little apart: on the ports themselves the two blocks overlap and push each other off
                m.Translation += sa.ConstraintPositionWorld() - turnedPort + Axis(sa) * PortGapM;
                ship.Teleport(m);
                station.Physics.LinearVelocity = Vector3.Zero;
                station.Physics.AngularVelocity = Vector3.Zero;
                ship.Physics.LinearVelocity = Vector3.Zero;
                ship.Physics.AngularVelocity = Vector3.Zero;
                Note($"ports {Vector3D.Distance(sa.ConstraintPositionWorld(), sb.ConstraintPositionWorld()):0.000} m apart, axes at {MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(Axis(sa), -Axis(sb)), -1, 1))):0.0}°");
            }
            WorldApi.ChargeBatteries(station);
            WorldApi.ChargeBatteries(ship);
            var a = station.GetFatBlocks().OfType<MyShipConnector>().First();
            var b = ship.GetFatBlocks().OfType<MyShipConnector>().First();
            Note($"station {station.Physics?.Mass:0} kg, ship {ship.Physics?.Mass:0} kg; the connectors {Vector3D.Distance(a.PositionComp.GetPosition(), b.PositionComp.GetPosition()):0.00} m apart");

            var shipStart = ship.PositionComp.GetPosition();
            var stationStart = station.PositionComp.GetPosition();
            // the ship brought onto the station's connector and locked
            for (var i = 0; i < 300 && !a.Connected; i++)
            {
                // the game's own search for a connector to hold (MyShipConnector.TryAttach: ports within 0.59 m, facing)
                // at once, before the blocks push each other off, then the lock
                if (i % 20 == 0) typeof(MyShipConnector).GetMethod("TryAttach", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                    new[] { typeof(long?) }, null)?.Invoke(b, new object[] { (long?)a.EntityId });
                if (i % 20 == 5) ((Sandbox.ModAPI.Ingame.IMyShipConnector)b).Connect();
                if (i % 60 == 0)
                    Note($"locking {i / 60} s: station connector working {a.IsWorking} ({((Sandbox.ModAPI.Ingame.IMyShipConnector)a).Status}), ship connector working {b.IsWorking} " +
                         $"({((Sandbox.ModAPI.Ingame.IMyShipConnector)b).Status}); ports {Vector3D.Distance(a.ConstraintPositionWorld(), b.ConstraintPositionWorld()):0.00} m apart; " +
                         $"ship at {ship.Physics.LinearVelocity.Length():0.00} m/s, thrust {ship.Components.Get<MyEntityThrustComponent>()?.FinalThrust.Length() ?? 0:0} N, station at {station.Physics.LinearVelocity.Length():0.00} m/s, turning {station.Physics.AngularVelocity.Length():0.000}/{ship.Physics.AngularVelocity.Length():0.000} rad/s; " +
                         $"ship moved {Vector3D.Distance(ship.PositionComp.GetPosition(), shipStart):0.00} m, station {Vector3D.Distance(station.PositionComp.GetPosition(), stationStart):0.00} m");
                yield return null;
            }
            Check(a.Connected, $"the connectors did not lock ({Vector3D.Distance(a.PositionComp.GetPosition(), b.PositionComp.GetPosition()):0.00} m apart, " +
                                $"ready {((Sandbox.ModAPI.Ingame.IMyShipConnector)b).Status})");
            var thrust = ship.Components.Get<MyEntityThrustComponent>();
            yield return WaitForTicks(60);
            var sbody = station.Physics.RigidBody;
            var shbody = ship.Physics.RigidBody;
            Note($"locked; the ship's dampeners {thrust?.DampenersEnabled}; collisions between them " +
                 $"{(station.Physics.HavokWorld.IsCollisionEnabled(sbody.GetCollisionFilterInfo(), shbody.GetCollisionFilterInfo()) ? "ON" : "off")}");

            double maxV = 0, sumDv = 0, maxThrust = 0;
            var frames = 0;
            var last = ship.Physics.LinearVelocity;
            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started).TotalSeconds < WatchSeconds)
            {
                var v = ship.Physics.LinearVelocity;
                maxV = Math.Max(maxV, v.Length());
                sumDv += (v - last).Length();
                last = v;
                maxThrust = Math.Max(maxThrust, thrust?.FinalThrust.Length() ?? 0);
                frames++;
                yield return null;
            }
            Note($"DOCKED SHAKE | calm {(_calm ? "on" : "off")}: the ship at up to {maxV * 1000:0.0} mm/s, its speed changing {sumDv / Math.Max(1, frames) * 1000:0.00} mm/s a frame on average, " +
                 $"its thrusters at up to {maxThrust:0} N, the station at {station.Physics.LinearVelocity.Length() * 1000:0.0} mm/s");

            ((Sandbox.ModAPI.Ingame.IMyShipConnector)b).Disconnect();
            var release = WaitForSeconds(1, "the ship lets go");
            while (release.MoveNext()) yield return release.Current;
            var after = (ship.Physics.LinearVelocity - station.Physics.LinearVelocity).Length();
            Note($"DOCKED SHAKE | a second after letting go the ship moves from the station at {after:0.000} m/s");

            if (_calm)
            {
                Check(maxV < CalmMps, $"the docked ship shook at up to {maxV * 1000:0.0} mm/s (calm under {CalmMps * 1000:0.0})");
                Check(after < ReleaseMps, $"the ship was thrown off at {after:0.00} m/s");
            }
            else Check(maxV >= CalmMps || after >= ReleaseMps, "without the fixes the docked ship neither shook nor was thrown: the test shows nothing");
        }

        private static MyObjectBuilder_CubeBlock Block(string subtype, Vector3I at, Base6Directions.Direction forward = Base6Directions.Direction.Forward,
            Base6Directions.Direction up = Base6Directions.Direction.Up)
        {
            var block = WorldApi.MakeBlockOb(subtype);
            block.Min = new SerializableVector3I(at.X, at.Y, at.Z);
            block.BlockOrientation = new SerializableBlockOrientation(forward, up);
            return block;
        }

        private MyCubeGrid Spawn(string name, Vector3D at, Vector3D forward, Vector3D up, int size, IEnumerable<MyObjectBuilder_CubeBlock> extra)
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
                IsStatic = false,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, forward, up)),
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
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
