using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A player's request to change blocks' owner naming a block the server does not have (12.10.2026: a player changing
    /// the owner of a block by Cedar's flying drill was dropped - ArgumentOutOfRangeException out of
    /// <c>MyCubeGrid.AddChangeOwnersRequestServerside</c>, which for a block it does not find steps the index on and writes
    /// past the end of the list). Sent as a client's event: a block of its own and an id no entity has, last. Passes when
    /// the request goes through without a throw and the block that is there gets its share mode (SO OwnershipRequestCheck).
    /// </summary>
    public sealed class OwnershipMissingBlockScenario : TestScenario
    {
        public const string ScenarioName = "ownership_missing_block";
        private const string Prefix = "OwnMissing_";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 60;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var planet = MyEntities.GetEntities().OfType<MyPlanet>().OrderByDescending(x => x.MaximumRadius).FirstOrDefault();
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var probe = centre + Vector3D.Normalize(new Vector3D(0.3, 0.7, -0.6)) * planet.MaximumRadius;
            var ground = planet.GetClosestSurfacePointGlobal(ref probe);
            var up = Vector3D.Normalize(ground - centre);
            var origin = ground + up * 40;

            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (origin + up * 6, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            var identity = FakeClients.PlayerOf(0).Identity.IdentityId;

            var battery = WorldApi.MakeBlockOb("LargeBlockBatteryBlock");
            battery.Owner = identity;
            battery.BuiltBy = identity;
            battery.ShareMode = MyOwnershipShareModeEnum.None;
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "grid", DisplayName = WorldApi.EntityPrefix + Prefix + "grid",
                GridSizeEnum = MyCubeSize.Large, IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(origin, Vector3D.CalculatePerpendicularVector(up), up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = { battery },
            });
            Track(grid);
            var block = grid.GetFatBlocks().FirstOrDefault();
            Check(block != null, "the battery was not made");

            var method = typeof(MyCubeGrid).GetMethod("AddChangeOwnersRequestServerside", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Check(method != null, "no MyCubeGrid.AddChangeOwnersRequestServerside");
            long missing = 1;
            while (MyEntities.EntityExists(missing)) missing++;
            var requests = new List<MyCubeGrid.MySingleOwnershipRequest>
            {
                new MyCubeGrid.MySingleOwnershipRequest { BlockId = block.EntityId, Owner = identity },
                new MyCubeGrid.MySingleOwnershipRequest { BlockId = missing, Owner = identity },
            };
            Exception thrown = null;
            FakeClients.AsClient(0, () =>
            {
                try { method.Invoke(null, new object[] { MyOwnershipShareModeEnum.Faction, requests }); }
                catch (TargetInvocationException e) { thrown = e.InnerException; }
            });
            Note($"OWNERSHIP | a request for the battery and for id {missing} that nothing has: {(thrown == null ? "no throw" : thrown.GetType().Name + ": " + thrown.Message)}");
            Check(thrown == null, $"the request threw {thrown?.GetType().Name}: {thrown?.Message}");
            var settle = WaitForSeconds(1, "the change goes through");
            while (settle.MoveNext()) yield return settle.Current;
            Note($"OWNERSHIP | the battery's share mode after it: {block.IDModule?.ShareMode}, owner {(block.OwnerId == identity ? "the player" : block.OwnerId.ToString())}");
            Check(block.IDModule?.ShareMode == MyOwnershipShareModeEnum.Faction, $"the battery's share mode is {block.IDModule?.ShareMode}, not Faction");
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
