using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Replication;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Network;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// An asteroid or a grid a client was streamed and failed to load must come to it again. Players say some asteroids
    /// and grids never appear: a client whose load of a streamed replicable fails (a voxel's cached copy gone, a broken
    /// stream) drops it and answers nothing, and the server keeps it pending for that client while it stays in range (SO
    /// ReplicableResend).
    ///  - "asteroid_resend": an asteroid from a prefab (sent with its data) in empty space;
    ///  - "grid_resend": a small grid there.
    /// A fake client 200 m off; its first load of the object fails (<see cref="FakeClients.FailLoad"/>). Passes when the
    /// object is ready for the client within <see cref="ReadySeconds"/> s of the failed load.
    /// </summary>
    public sealed class AsteroidResendScenario : TestScenario
    {
        public const string AsteroidName = "asteroid_resend";
        public const string GridName = "grid_resend";

        private readonly bool _grid;
        private MyEntity _object;

        public AsteroidResendScenario(bool grid) => _grid = grid;

        /// <summary>The server's wait for a delivered stream (30 s for an asteroid, 60 for a grid) and time to stream again.</summary>
        private double ReadySeconds => _grid ? 105 : 75;

        public override string Name => _grid ? GridName : AsteroidName;
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var spot = Vector3D.Zero;
            var planets = MyEntities.GetEntities().OfType<MyPlanet>().ToList();
            for (var i = 0; i < 40; i++)
            {
                var probe = new Vector3D(-160000 - i * 20000, 85000 + (_grid ? 3000 : 0), 130000 + i * 15000);
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(probe).LengthSquared() < 1e-6 &&
                    planets.All(p => Vector3D.Distance(p.PositionComp.GetPosition(), probe) > p.MaximumRadius + 5000))
                {
                    spot = probe;
                    break;
                }
            }
            Check(spot != Vector3D.Zero, "no place in space without gravity");

            FakeClients.RemoveAll();
            string what;
            if (_grid)
            {
                var owner = WorldApi.PlayerIdentityId();
                var blocks = new List<MyObjectBuilder_CubeBlock>();
                for (var x = 0; x < 3; x++)
                for (var z = 0; z < 3; z++)
                {
                    var b = WorldApi.MakeBlockOb(x == 1 && z == 1 ? "LargeBlockBatteryBlock" : "LargeBlockArmorBlock");
                    b.Min = new SerializableVector3I(x, 0, z);
                    b.Owner = owner;
                    b.BuiltBy = owner;
                    blocks.Add(b);
                }
                var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
                {
                    Name = WorldApi.EntityPrefix + "grid-resend", DisplayName = WorldApi.EntityPrefix + "grid-resend",
                    GridSizeEnum = MyCubeSize.Large, IsStatic = true,
                    PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(spot, Vector3D.Forward, Vector3D.Up)),
                    PersistentFlags = MyPersistentEntityFlags2.InScene,
                    CubeBlocks = blocks,
                });
                Check(grid != null, "the grid was not made");
                Track(grid);
                _object = grid;
                what = $"a grid of {grid.BlocksCount} blocks";
            }
            else
            {
                var prefab = MyDefinitionManager.Static.GetVoxelMapStorageDefinitions()
                    .Where(d => d.Id.SubtypeName.IndexOf("small", StringComparison.OrdinalIgnoreCase) >= 0 || d.Id.SubtypeName.StartsWith("Asteroid", StringComparison.OrdinalIgnoreCase))
                    .Select(d => d.Id.SubtypeName).OrderBy(n => n.Length).FirstOrDefault();
                Check(prefab != null, "no asteroid prefab");
                _object = MyWorldGenerator.AddAsteroidPrefab(prefab, spot, WorldApi.EntityPrefix + "asteroid-resend");
                Check(_object != null, "the asteroid " + prefab + " was not made");
                what = "the asteroid " + prefab;
            }
            var replicable = MyExternalReplicable.FindByObject(_object);
            Check(replicable != null, "no replicable");
            var layer = (MyReplicationLayer)MyMultiplayer.Static.ReplicationLayer;
            var made = WaitForSeconds(1, "the object gets its network id");
            while (made.MoveNext()) yield return made.Current;
            Check(layer.TryGetNetworkIdByObject(replicable, out var networkId), "no network id");
            var id = (uint)typeof(NetworkId).GetField("Value", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(networkId);
            var failedAt = 0L;
            FakeClients.FailLoad = (client, streamed) => streamed == id && Interlocked.CompareExchange(ref failedAt, DateTime.UtcNow.Ticks, 0) == 0;
            Note($"RESEND | {what} at {spot:0}, network id {id}; its first load on the client fails");
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 60 }, i => (spot + new Vector3D(0, 0, 200), 0, 0), withCharacters: true);
            var entities = new[] { _object };

            var start = DateTime.UtcNow;
            while (Interlocked.Read(ref failedAt) == 0 && (DateTime.UtcNow - start).TotalSeconds < 90) yield return null;
            Check(Interlocked.Read(ref failedAt) != 0, $"the object never reached the client in 90 s ({FakeClients.Arrivals(entities)})");
            var failed = new DateTime(Interlocked.Read(ref failedAt), DateTimeKind.Utc);
            Note($"RESEND | streamed whole in {(failed - start).TotalSeconds:0.0} s, the client's load failed: {FakeClients.Arrivals(entities)}");

            var lastNote = DateTime.UtcNow;
            while (FakeClients.ArrivedCount(entities) > 0 && (DateTime.UtcNow - failed).TotalSeconds < ReadySeconds)
            {
                if ((DateTime.UtcNow - lastNote).TotalSeconds >= 10)
                {
                    lastNote = DateTime.UtcNow;
                    Note($"RESEND | {(DateTime.UtcNow - failed).TotalSeconds:0} s after the failed load: {FakeClients.Arrivals(entities)}");
                }
                yield return null;
            }
            var ready = FakeClients.ArrivedCount(entities) == 0;
            Note($"RESEND | {(ready ? $"ready for the client {(DateTime.UtcNow - failed).TotalSeconds:0.0} s after the failed load" : $"not ready {ReadySeconds} s after the failed load")}: " +
                 FakeClients.Arrivals(entities));
            Check(ready, $"the object the client failed to load was not sent again in {ReadySeconds} s");
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.FailLoad = null;
                FakeClients.RemoveAll();
                if (!_grid && _object != null && !_object.MarkedForClose) _object.Close();
            }
            finally { base.Cleanup(); }
        }
    }
}
