using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SpaceEngineers.Game.EntityComponents.Blocks;
using SpaceEngineers.Game.SessionComponents;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The prod's crash of 23.11.2026 (13:10:47): a NullReferenceException in <c>MyVoxelBase.IsAnyOfPointInside</c> from
    /// <c>MyGridStorageHelper.IsInsideVoxels</c> on a retrieval from the garage. The check adds the static entities in
    /// the grid's box to a static list and clears it only when the grid is not in voxels; a planet's voxel physics left
    /// there and deleted later (no storage then) was read by the next retrieval. SentisOptimisations' GarageVoxelCheck
    /// clears the list after every check and before one.
    ///
    /// On a planet: a ship stored at a Services Terminal is fetched (<c>MyGridStorageHelper.LoadGrid</c>, placement
    /// precise) into the rock - refused, and the list is empty after it (the game left the physics sector in it). Then a
    /// deleted physics sector (the planet's, no storage) is put into the list and the ship fetched into the rock again:
    /// refused, the server standing, the list empty. Last it is fetched into the air: there, the player's. Without the
    /// fix the check stops at the first list ("left in the list") instead of the crash.
    /// </summary>
    public sealed class GridStorageStaleVoxelCheck : RegressionCheck
    {
        public override string Name => Prefix + "grid_storage_stale_voxel";
        public override SiteKind Kind => SiteKind.Planet;
        public override int TimeoutSeconds => 240;

        private Guid _record;

        public override void Cleanup()
        {
            try
            {
                if (_record != Guid.Empty && MyGridStorageHelper.Instance != null) MyGridStorageHelper.Instance.Delete(_record);
            }
            catch (Exception) { }
            finally { base.Cleanup(); }
        }

        /// <summary>The game's MyVoxelPhysics (internal): a planet's piece of voxel physics.</summary>
        internal static readonly Type VoxelPhysics = typeof(MyPlanet).Assembly.GetType("Sandbox.Game.Entities.MyVoxelPhysics", true);

        /// <summary>A planet's physics sector as it is once deleted: its planet, no storage (BeforeDelete drops it).</summary>
        internal static MyVoxelBase DeadSector(MyPlanet planet)
        {
            var dead = (MyVoxelBase)Activator.CreateInstance(VoxelPhysics, true);
            if (planet != null) VoxelPhysics.GetField("m_parentPlanet", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(dead, planet);
            return dead;
        }

        private static List<MyEntity> Penetrations() =>
            typeof(MyGridStorageHelper).GetField("m_potentialPenetrations", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as List<MyEntity>;

        protected override IEnumerator Check()
        {
            var session = MySession.Static.GetComponent<MyGridsStorageSessionComponent>();
            SkipUnless(session != null && MyGridStorageHelper.Instance != null, "no grid storage in this world");
            Check(Penetrations() != null, "MyGridStorageHelper.m_potentialPenetrations not found");
            var planet = Site.Planet;
            Check(planet != null, "no planet at the site");
            var ground = Highest(Site.Origin, 20);
            yield return Arrive(ground + Site.Up * 4 + Site.Forward * 10);
            var station = Build("stale-terminal", ground + Site.Up * 2, true,
                new Part("FunctionalBlock/ServicesTerminal", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 4, 0, 0).With(b => Motion.Charged(b)));
            var terminal = station.GetFatBlocks().FirstOrDefault(b => b.BlockDefinition.Id.SubtypeName == "ServicesTerminal");
            Check(terminal != null, "no Services Terminal built");
            var storage = terminal.Components.Get<MyGridsStorageEntityComponent>();
            Check(storage != null, "the Services Terminal has no grid storage");
            yield return Until(() => terminal.IsWorking ? 1 : 0, v => v > 0, "the Services Terminal working", 10);

            // (on the ground, at rest: a falling ship is refused, GridIsMoving)
            var ship = Build("stale-ship", Highest(Site.Origin + Site.Right * 25, 5) + Site.Up * 2, false,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("CargoContainer/LargeBlockSmallContainer", 1, 0, 0),   // (a block with an owner: the store wants the player's grid)
                new Part("CubeBlock/LargeBlockArmorBlock", 2, 0, 0));
            var shipId = ship.EntityId;
            var shipName = ship.DisplayName;
            if (ship.Physics != null) ship.Physics.LinearVelocity = Vector3.Zero;
            yield return Seconds(1);
            yield return Seconds(1);
            var before = session.GetStoredGridsData().Keys.ToList();
            // (a slope: it creeps; stopped right before the store, as the store looks at its speed then)
            if (ship.Physics != null)
            {
                ship.Physics.LinearVelocity = Vector3.Zero;
                ship.Physics.AngularVelocity = Vector3.Zero;
            }
            Request(storage, "StoreGridTrusted", shipId);
            yield return Until(() => MyEntities.EntityExists(shipId) ? 0 : 1, v => v > 0, "the ship stored (gone from the world)", 60);
            yield return Until(() =>
            {
                _record = session.GetStoredGridsData().FirstOrDefault(p => !before.Contains(p.Key) && p.Value.OwnerId == Identity).Key;
                return _record == Guid.Empty ? 0 : 1;
            }, v => v > 0, "a record of the player's in the storage", 30);

            var orientation = Quaternion.CreateFromRotationMatrix(MatrixD.CreateWorld(Vector3D.Zero, Site.Forward, Site.Up));
            var rock = ground - Site.Up * 15;
            // the planet's physics where the ship goes: what the check finds and leaves in the list
            var box = new BoundingBoxD(rock - 10, rock + 10);
            var sectors = new List<MyEntity>();
            MyGamePruningStructure.GetAllEntitiesInBox(ref box, sectors, MyEntityQueryType.Static);
            var physics = sectors.OfType<MyVoxelBase>().Count(v => v.GetType() == VoxelPhysics && v.RootVoxel is MyPlanet);
            Check(physics > 0, "no voxel physics of the planet at the site (the player is there)");

            MyGridStorageHelper.MyGridLoadFuture Load(Vector3D at) =>
                MyGridStorageHelper.Instance.LoadGrid(_record, orientation, at, at, 0f, Identity, null);

            // 1. into the rock: refused; nothing left in the list
            var first = Load(rock);
            yield return Until(() => first.IsFinished ? 1 : 0, v => v > 0, "the fetch into the rock answered", 30);
            Check(first.Status == MyGridStorageHelper.MyGridLoadStatus.Fail_CannotPlace, "the fetch into the rock: " + first.Status + ", not Fail_CannotPlace");
            var left = Penetrations().Count;
            Check(left == 0, left + " entities left in MyGridStorageHelper.m_potentialPenetrations after a fetch into voxels " +
                             "(a planet's physics sector deleted later crashes the next fetch: GarageVoxelCheck missing)");

            // 2. a deleted physics sector in the list (as the planet leaves it: a parent, no storage), into the rock again
            var dead = DeadSector(planet);
            Check(dead.Storage == null && dead.RootVoxel == planet, "the dead physics sector not made");
            Penetrations().Add(dead);
            var second = Load(rock);
            yield return Until(() => second.IsFinished ? 1 : 0, v => v > 0, "the second fetch into the rock answered", 30);
            Check(second.Status == MyGridStorageHelper.MyGridLoadStatus.Fail_CannotPlace, "the fetch with a dead sector in the list: " + second.Status + ", not Fail_CannotPlace");
            Check(!Penetrations().Contains(dead), "the dead sector still in the list");
            Check(Penetrations().Count == 0, Penetrations().Count + " entities left in the list after the second fetch");

            // 3. into the air: the ship back, the player's
            var grids = new HashSet<long>(MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.EntityId));
            var air = ground + Site.Up * 40 + Site.Right * 30;
            var third = Load(air);
            yield return Until(() => third.IsFinished ? 1 : 0, v => v > 0, "the fetch into the air answered", 30);
            Check(third.Status == MyGridStorageHelper.MyGridLoadStatus.Success, "the fetch into the air: " + third.Status);
            MyCubeGrid back = null;
            yield return Until(() =>
            {
                back = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !grids.Contains(g.EntityId) && g.DisplayName == shipName);
                return back == null ? 0 : 1;
            }, v => v > 0, "the ship back in the world", 30);
            Track(back);
            Check(back.BigOwners.Contains(Identity), "the ship came back not the player's");
            Check(Penetrations().Count == 0, Penetrations().Count + " entities left in the list after the fetch into the air");
            Result($"into the rock refused, the list empty after it ({physics} sectors of the planet there); a dead sector in the list cleared, the fetch refused, no crash; " +
                   $"into the air: back, {back.BlocksCount} blocks, " + F(Vector3D.Distance(back.PositionComp.GetPosition(), air)) + " m from where asked");
        }
    }

    /// <summary>
    /// <c>MyVoxelBase.IsAnyOfPointInside</c> on a voxel without storage (a planet's physics sector deleted: its
    /// BeforeDelete drops the storage) answers "not inside" instead of a NullReferenceException (SentisOptimisations'
    /// GarageVoxelCheck, the prod's crash of 23.11.2026). And only it: a voxel map marked for close keeps its storage and
    /// is still read - the method is everyone's, the guard must not change what it says for live voxels.
    /// </summary>
    public sealed class VoxelDeadStorageCheck : RegressionCheck
    {
        public override string Name => Prefix + "voxel_dead_storage";
        public override int TimeoutSeconds => 60;

        protected override IEnumerator Check()
        {
            // a rock of 32 m, full, at the site
            var size = 32;
            var storage = MyAPIGateway.Session.VoxelMaps.CreateStorage(new Vector3I(size));
            var data = new VRage.Voxels.MyStorageData();
            var min = Vector3I.Zero;
            var max = new Vector3I(size - 1);
            data.Resize(min, max);
            data.ClearContent(VRage.Voxels.MyVoxelConstants.VOXEL_CONTENT_FULL);
            data.ClearMaterials(Sandbox.Definitions.MyDefinitionManager.Static.GetVoxelMaterialDefinitions().First().Index);
            storage.WriteRange(data, VRage.Voxels.MyStorageDataTypeFlags.ContentAndMaterial, min, max);
            var rock = MyAPIGateway.Session.VoxelMaps.CreateVoxelMap(SentisTests.Game.WorldApi.EntityPrefix + "dead-storage-rock", storage, Site.At(-size / 2.0, -size / 2.0, 40), 0) as MyVoxelBase;
            Check(rock != null, "the rock not made");
            Track(rock);
            yield return WaitForTicks(2);
            var centre = rock.PositionComp.WorldAABB.Center;
            Check(rock.IsAnyOfPointInside(new[] { centre }), "the rock's middle not inside it");

            // marked for close, its storage still there: read as before
            rock.Close();
            var marked = rock.MarkedForClose;
            var stillRead = rock.Storage != null && rock.IsAnyOfPointInside(new[] { centre });

            // a deleted physics sector: no storage
            var dead = GridStorageStaleVoxelCheck.DeadSector(MyEntities.GetEntities().OfType<MyPlanet>().FirstOrDefault());
            bool deadInside;
            string threw = null;
            try
            {
                deadInside = dead.IsAnyOfPointInside(new[] { centre });
            }
            catch (Exception e)
            {
                deadInside = false;
                threw = e.GetType().Name;
            }
            Check(threw == null, "IsAnyOfPointInside on a voxel without storage threw " + threw + " (GarageVoxelCheck missing: the prod's crash of 23.11.2026)");
            Check(!deadInside, "a voxel without storage said a point is inside it");
            Check(marked, "the rock not marked for close by Close()");
            Check(stillRead, "a rock marked for close, its storage still there, no longer read as inside (the guard must leave live voxels alone)");
            Result("a voxel without storage: not inside, no exception; a rock marked for close: still read, inside");
        }
    }
}
