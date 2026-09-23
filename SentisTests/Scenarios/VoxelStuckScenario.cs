using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
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
    /// What SentisGameplayImprovements does to a grid stuck in voxels (Voxels.Unstick), for each kind of
    /// place:
    ///
    ///  1. a ship in space is moved 1 km, to a place where it touches nothing, and stopped;
    ///  2. a rotor head of a static base in space is made static - moving it would move the base too -
    ///     and the base stays where it was;
    ///  3. a ship in gravity is made static where it is.
    ///
    /// When a grid counts as stuck (seconds in a row of voxel contacts) is covered by the unit tests.
    /// </summary>
    public sealed class VoxelStuckScenario : TestScenario
    {
        public const string ScenarioName = "voxel_stuck";
        private const string Prefix = "stuck-";

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 90;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetType("SentisGameplayImprovements.Assholes.Voxels", false) != null);
            Check(assembly != null, "SentisGameplayImprovements with Voxels is not loaded");
            var voxels = assembly.GetType("SentisGameplayImprovements.Assholes.Voxels");
            var unstick = voxels.GetMethod("Unstick", BindingFlags.Static | BindingFlags.Public);
            var distance = (double)voxels.GetField("TeleportDistance").GetRawConstantValue();
            var isFree = assembly.GetType("SentisGameplayImprovements.AsteroidFieldSpawner")
                .GetMethod("IsFree", new[] { typeof(BoundingSphereD), typeof(ICollection<long>) });

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));

            // out of the planet's gravity
            var space = anchorM.Translation;
            for (var km = 20; km <= 600 && !NoGravity(space); km += 20) space = anchorM.Translation + up * km * 1000;
            Check(NoGravity(space), "found no place without gravity above the planet");
            Note("space: " + (Vector3D.Distance(space, anchorM.Translation) / 1000).ToString("0") + " km above the site");

            // ------------------------------------------------------------- 1. a ship in space
            var ship = SpawnShip("ship", space, up, side, isStatic: false);
            yield return null;
            ship.Physics.LinearVelocity = side * 5;
            var from = Centre(ship);
            unstick.Invoke(null, new object[] { ship, 999L });
            yield return null;
            var to = Centre(ship);
            var moved = Vector3D.Distance(from, to);
            var sphere = new BoundingSphereD(to, ship.PositionComp.WorldAABB.HalfExtents.Length());
            var free = (bool)isFree.Invoke(null, new object[] { sphere, new List<long> { ship.EntityId } });
            Note("ship in space: moved " + moved.ToString("0.0") + " m, speed " + ship.Physics.LinearVelocity.Length().ToString("0.00") +
                 " m/s, static " + ship.IsStatic + ", place free " + free);
            Check(!ship.IsStatic, "the ship in space was made static, not moved");
            Check(Math.Abs(moved - distance) < 1, "the ship in space moved " + moved.ToString("0.0") + " m, not " + distance);
            Check(ship.Physics.LinearVelocity.Length() < 0.1f, "the moved ship still moves");
            Check(free, "the ship was moved into something");

            // ------------------------------------------------------------- 2. a rotor head of a static base
            var basePoint = space + side * 5000;
            var station = SpawnShip("base", basePoint, up, side, isStatic: true, withStator: true);
            yield return null;
            var stator = station.GetFatBlocks().OfType<MyMotorStator>().First();
            var recreate = typeof(MyMechanicalConnectionBlockBase).GetMethod("DoRecreateTop", BindingFlags.Instance | BindingFlags.NonPublic);
            recreate.Invoke(stator, new object[] { WorldApi.PlayerIdentityId(), Enum.Parse(recreate.GetParameters()[1].ParameterType, "Normal"), true });
            var topped = Wait(() => stator.TopGrid != null, "the rotor head is built", 10);
            while (topped.MoveNext()) yield return topped.Current;
            var head = stator.TopGrid;
            head.DisplayName = WorldApi.EntityPrefix + Prefix + "head";
            head.Name = head.DisplayName;
            Track(head);
            Check(!head.IsStatic, "the rotor head is static from the start");
            var baseAt = station.PositionComp.GetPosition();
            var headAt = head.PositionComp.GetPosition();
            unstick.Invoke(null, new object[] { head, 999L });
            yield return null;
            Note("rotor head of a static base in space: static " + head.IsStatic + ", head moved " +
                 Vector3D.Distance(headAt, head.PositionComp.GetPosition()).ToString("0.0") + " m, base moved " +
                 Vector3D.Distance(baseAt, station.PositionComp.GetPosition()).ToString("0.0") + " m");
            Check(head.IsStatic, "the rotor head of a static base was not made static");
            Check(Vector3D.Distance(baseAt, station.PositionComp.GetPosition()) < 0.1, "the base was moved");
            Check(Vector3D.Distance(headAt, head.PositionComp.GetPosition()) < 5, "the rotor head was moved away");

            // ------------------------------------------------------------- 3. a ship in gravity
            var low = anchorM.Translation + up * 1000 + side * 2500;
            Check(!NoGravity(low), "no gravity 1 km above the site");
            var lander = SpawnShip("lander", low, up, side, isStatic: false);
            yield return null;
            var landerAt = lander.PositionComp.GetPosition();
            unstick.Invoke(null, new object[] { lander, 999L });
            yield return null;
            Note("ship in gravity: static " + lander.IsStatic + ", moved " + Vector3D.Distance(landerAt, lander.PositionComp.GetPosition()).ToString("0.0") + " m");
            Check(lander.IsStatic, "the ship in gravity was not made static");
            Check(Vector3D.Distance(landerAt, lander.PositionComp.GetPosition()) < 5, "the ship in gravity was moved");
        }

        private static bool NoGravity(Vector3D at) => Vector3.IsZero(MyGravityProviderSystem.CalculateNaturalGravityInPoint(at));

        private static Vector3D Centre(MyCubeGrid grid) => grid.PositionComp.WorldAABB.Center;

        private MyCubeGrid SpawnShip(string name, Vector3D at, Vector3D up, Vector3D side, bool isStatic, bool withStator = false)
        {
            var owner = WorldApi.PlayerIdentityId();
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 3; x++)
            {
                var armor = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                armor.Min = new SerializableVector3I(x, 0, 0);
                blocks.Add(armor);
            }
            if (withStator)
            {
                var stator = WorldApi.MakeBlockOb("LargeStator");
                stator.Min = new SerializableVector3I(1, 1, 0);
                blocks.Add(stator);
            }
            foreach (var block in blocks)
            {
                block.Owner = owner;
                block.BuiltBy = owner;
            }
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = isStatic,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, side, up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }
    }
}
