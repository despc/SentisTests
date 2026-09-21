using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// No gravity drives (SentisGameplayImprovements' GravityDrivePatch). In open space, three rigs:
    ///  A. a ship with a gravity generator and artificial mass on it - it must not move;
    ///  B. a ship with a gravity generator and artificial mass on a rotor head (a subgrid) - it must
    ///     not move either;
    ///  C. a station with a gravity generator and, in its field, a separate ship with artificial
    ///     mass - that one the generator still pushes.
    /// </summary>
    internal sealed class GravityDriveScenario : TestScenario
    {
        public const string ScenarioName = "gravity_drive";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 150;

        private const string Prefix = "gdrive-";
        private const double RigSpacingM = 600;
        private const double PushSeconds = 6;
        private const double StillMps = 0.5;     // what counts as not moving
        private const double MovingMps = 2;      // what the pushed ship must reach at least

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private Vector3D _up, _side;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            Check(planet != null, "no planet");
            _up = Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            _side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(_up));

            // open space: no natural gravity, where a generator works at full strength
            Vector3D centre = default;
            var found = false;
            for (var d = 100000.0; d <= 1000000 && !found; d += 50000)
            {
                centre = anchor + _up * d;
                found = MyGravityProviderSystem.CalculateNaturalGravityInPoint(centre).Length() < 0.001f;
            }
            Check(found, "no place without natural gravity found above the planet");

            FakeClients.RemoveAll();
            var player = centre + _up * 80;
            FakeClients.Add(1, Network, p => (player, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            // A: one ship, generator and mass on it
            var shipA = Spawn("ship-a", centre - _side * RigSpacingM, false, new[]
            {
                Armor(-1, 0), Armor(0, 0), Armor(1, 0), Generator(0, 1, 0), Battery(-1, 1, 0), Mass(1, 1, 0), Mass(1, 2, 0), Mass(0, 2, 0),
            });

            // B: generator on the ship, mass on a rotor head
            var shipB = Spawn("ship-b", centre, false, new[]
            {
                Armor(-1, 0), Armor(0, 0), Armor(1, 0), Generator(0, 1, 0), Battery(-1, 1, 0), Stator(1, 1, 0),
            });

            // C: a station with a generator and a separate ship with mass in its field
            var station = Spawn("station-c", centre + _side * RigSpacingM, true, new[]
            {
                Armor(-1, 0), Armor(0, 0), Armor(1, 0), Generator(0, 1, 0), Battery(-1, 1, 0),
            });
            // beside the station, so that it falls past it instead of into it
            var shipC = Spawn("ship-c", centre + _side * (RigSpacingM + 10) + _up * 15, false, new[]
            {
                Armor(0, 0), Battery(0, 1, 0), Mass(1, 1, 0), Mass(-1, 1, 0),
            });

            var settle = WaitForSeconds(2, "the rigs settle");
            while (settle.MoveNext()) yield return settle.Current;

            // the rotor head of B, and the mass on it
            var stator = WorldApi.FindFunctional<MyMotorStator>(shipB);
            Check(stator != null, "ship B has no rotor");
            // what "Add rotor head" does on the server: RecreateTop's own work, built at once
            var recreate = typeof(Sandbox.Game.Entities.Blocks.MyMechanicalConnectionBlockBase).GetMethod("DoRecreateTop",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Check(recreate != null, "MyMechanicalConnectionBlockBase.DoRecreateTop is gone");
            var normalSize = Enum.Parse(recreate.GetParameters()[1].ParameterType, "Normal");
            recreate.Invoke(stator, new object[] { WorldApi.PlayerIdentityId(), normalSize, true });
            var topped = Wait(() => stator.TopGrid != null, "the rotor head is built", 10);
            while (topped.MoveNext()) yield return topped.Current;
            ((Sandbox.ModAPI.Ingame.IMyMotorStator)stator).RotorLock = true;
            var head = stator.TopGrid;
            foreach (var y in new[] { 1, 2 })
            {
                var mass = Mass(0, y, 0);
                ((VRage.Game.ModAPI.IMyCubeGrid)head).AddBlock(mass, false);
            }
            Track(head);
            var built = Wait(() => head.GetFatBlocks().OfType<SpaceEngineers.Game.Entities.Blocks.MyVirtualMass>().Count() == 2, "the mass is on the rotor head", 10);
            while (built.MoveNext()) yield return built.Current;
            Check(GravityDrivePhysicalGroup(shipB, head), "the rotor head is not in ship B's physical group");

            // everything still, then the push
            foreach (var grid in new[] { shipA, shipB, head, shipC })
            {
                grid.Physics.LinearVelocity = Vector3.Zero;
                grid.Physics.AngularVelocity = Vector3.Zero;
            }
            var droppedBefore = Dropped();
            var push = WaitForSeconds(PushSeconds, "the generators push");
            while (push.MoveNext()) yield return push.Current;

            var speedA = shipA.Physics.LinearVelocity.Length();
            var speedB = shipB.Physics.LinearVelocity.Length();
            var speedC = shipC.Physics.LinearVelocity.Length();
            var working = string.Join(", ", new[] { shipA, head, shipC }.SelectMany(g => g.GetFatBlocks().OfType<SpaceEngineers.Game.Entities.Blocks.MyVirtualMass>())
                .Select(m => m.IsWorking ? "on" : "off"));
            Note("after " + PushSeconds + " s: ship A (generator and mass aboard) " + speedA.ToString("F2") + " m/s, ship B (mass on a rotor head) " +
                 speedB.ToString("F2") + " m/s, ship C (mass, the station's generator) " + speedC.ToString("F2") + " m/s; masses " + working +
                 "; pushes dropped " + (Dropped() - droppedBefore));

            Check(speedC >= MovingMps, "the station's generator did not push the separate ship: " + speedC.ToString("F2") + " m/s - the rig does not work");
            Check(speedA < StillMps, "ship A moved by its own gravity drive: " + speedA.ToString("F2") + " m/s");
            Check(speedB < StillMps, "ship B moved by its own gravity drive with the mass on a rotor: " + speedB.ToString("F2") + " m/s");
            Note("GRAVITY DRIVE RESULT: A " + speedA.ToString("F2") + " m/s, B " + speedB.ToString("F2") + " m/s, C " + speedC.ToString("F2") + " m/s");
        }

        private static bool GravityDrivePhysicalGroup(MyCubeGrid a, MyCubeGrid b) =>
            MyCubeGridGroups.Static.Physical.GetGroup(a) == MyCubeGridGroups.Static.Physical.GetGroup(b);

        /// <summary>GravityDrivePatch.Dropped, read by name: the tests do not reference the plugin.</summary>
        private static long Dropped()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("SentisGameplayImprovements.GravityDrivePatch")).FirstOrDefault(t => t != null);
            var field = type?.GetField("Dropped");
            return field == null ? -1 : (long)field.GetValue(null);
        }

        // ------------------------------------------------------------------ the rigs

        private MyCubeGrid Spawn(string name, Vector3D at, bool isStatic, IEnumerable<MyObjectBuilder_CubeBlock> blocks)
        {
            var owner = WorldApi.PlayerIdentityId();
            var list = blocks.ToList();
            foreach (var block in list)
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
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, _side, _up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = list,
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        private static MyObjectBuilder_CubeBlock At(MyObjectBuilder_CubeBlock block, int x, int y, int z)
        {
            block.Min = new SerializableVector3I(x, y, z);
            return block;
        }

        private static MyObjectBuilder_CubeBlock Armor(int x, int z) => At(WorldApi.MakeBlockOb("LargeBlockArmorBlock"), x, 0, z);

        private static MyObjectBuilder_CubeBlock Mass(int x, int y, int z) => At(WorldApi.MakeBlockOb("VirtualMassLarge"), x, y, z);

        private static MyObjectBuilder_CubeBlock Stator(int x, int y, int z) => At(WorldApi.MakeBlockOb("LargeStator"), x, y, z);

        private static MyObjectBuilder_CubeBlock Battery(int x, int y, int z)
        {
            var battery = (MyObjectBuilder_BatteryBlock)WorldApi.MakeBlockOb("LargeBlockBatteryBlock");
            battery.CurrentStoredPower = 3f;
            battery.Enabled = true;
            return At(battery, x, y, z);
        }

        /// <summary>The large gravity generator: its subtype is empty, so it is made by type. Gravity pulls "down" the ship.</summary>
        private static MyObjectBuilder_CubeBlock Generator(int x, int y, int z)
        {
            var generator = (MyObjectBuilder_GravityGenerator)MyObjectBuilderSerializerKeen.CreateNewObject(
                new MyDefinitionId(typeof(MyObjectBuilder_GravityGenerator), ""));
            generator.FieldSize = new Vector3(150, 150, 150);
            generator.GravityAcceleration = 9.81f;
            generator.Enabled = true;
            return At(generator, x, y, z);
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
            }
            finally { base.Cleanup(); }
        }
    }
}
