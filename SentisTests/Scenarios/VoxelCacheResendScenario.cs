using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Replication;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Library.Collections;
using VRage.Network;
using VRage.Serialization;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The client stuck in the respawn screen after choosing a drop pod on a planet that has been dug.
    ///
    /// The server remembers, per client, which voxel storages it has sent (<c>MyClient.m_clientCachedData</c>), and when the
    /// planet is streamed to that client again (it left the planet's range and came back - a respawn does exactly that) it
    /// sends "take it from your cache" instead of the data. A client whose own copy is gone or never written logs
    /// "Failed to load voxel from cache.", never confirms the planet, and the respawn screen waits for it until the client
    /// reconnects (the client log on the stand: <c>contentChanged:True data?: False</c> at each hang).
    ///
    /// The scenario digs the planet (so it differs from what a client could have from the disk), then streams the planet
    /// the way the replication server does for a client said to hold it already, and reads what was written: a changed
    /// planet must go with its data.
    /// </summary>
    public sealed class VoxelCacheResendScenario : TestScenario
    {
        public const string ScenarioName = "voxel_cache_resend";
        private const double WaitSeconds = 60;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + "vcr-anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");

            // the planet players respawn on: the one already dug if any (drop pods land on the Earth-like one)
            foreach (var entity in MyEntities.GetEntities())
                if (entity is MyPlanet other && (other.ContentChanged || other.BeforeContentChanged)) { planet = other; break; }
            var center = planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchorM.Translation - center);
            // away from the stand's own area, where zones may keep the ground as it is
            var east = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var surface = Vector3D.Zero;
            var cut = 0f;
            for (var i = 1; i <= 10 && cut <= 0; i++)
            {
                var direction = Vector3D.Normalize(up + east * (i * 5000.0 / planet.AverageRadius));
                surface = planet.GetClosestSurfacePointGlobal(center + direction * planet.AverageRadius);
                var shape = new MyShapeSphere { Center = surface - direction * 2, Radius = 8 };
                MyVoxelGenerator.CutOutShapeWithProperties(planet, shape, out cut, out _, null, updateSync: true);
            }
            Note("planet " + planet.StorageName + ", surface " + (surface - center).Length().ToString("F0") + " m from its centre");
            yield return WaitForTicks(5);
            Note("dug " + cut + " voxels, content changed: " + (planet.ContentChanged || planet.BeforeContentChanged));
            Check(planet.ContentChanged || planet.BeforeContentChanged, "the planet does not count as changed");

            var replicable = MyExternalReplicable.FindByObject(planet);
            Check(replicable != null, "the planet has no replicable");
            var serialize = replicable.GetType().GetMethod("Serialize", BindingFlags.Instance | BindingFlags.Public,
                null, new[] { typeof(BitStream), typeof(HashSet<string>), typeof(Endpoint), typeof(Action) }, null);
            Check(serialize != null, "MyVoxelReplicable.Serialize not found");

            // a client the server believes has the planet cached
            var cached = new HashSet<string> { planet.StorageName };
            var stream = new BitStream();
            stream.ResetWrite();
            var written = false;
            serialize.Invoke(replicable, new object[] { stream, cached, new Endpoint(new EndpointId(1), 0), new Action(() => written = true) });

            var deadline = DateTime.UtcNow.AddSeconds(WaitSeconds);
            while (!written && DateTime.UtcNow < deadline) yield return WaitForTicks(1);
            Check(written, "the planet was not written within " + WaitSeconds + " s");

            stream.ResetRead();
            var isUserCreated = MySerializer.CreateAndRead<bool>(stream);
            var isFromPrefab = MySerializer.CreateAndRead<bool>(stream);
            var sendContent = MySerializer.CreateAndRead<bool>(stream);
            var contentChanged = MySerializer.CreateAndRead<bool>(stream);
            var bytes = sendContent ? MySerializer.CreateAndRead<byte[]>(stream)?.Length ?? 0 : 0;
            Note("VOXEL CACHE RESEND RESULT | userCreated " + isUserCreated + ", fromPrefab " + isFromPrefab + ", contentChanged " +
                 contentChanged + ", data sent " + sendContent + " (" + bytes + " bytes) to a client said to hold " + planet.StorageName);

            Check(contentChanged, "the stream says the planet is unchanged");
            Check(sendContent && bytes > 0,
                "a changed planet went without its data to a client said to cache it: a client without that cache hangs in the respawn screen");
        }
    }
}
