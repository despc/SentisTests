using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.Voxels;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisGameplayImprovements' asteroid fields (!sgi spawnfield, AsteroidFieldSpawner), high above
    /// the planet, around a small grid that stands in the middle of the field:
    ///
    ///  - every asteroid is placed, inside the field, touching neither the grid nor another asteroid;
    ///  - a generated asteroid holds no ore but the ones asked for (and stone);
    ///  - every asteroid is saved with the world, with an id and a storage name of its own;
    ///  - while the asteroids are generated the plugin's delayed actions still run (the generation used
    ///    to hold their only thread for seconds);
    ///  - the predefined "Field" asteroids (spawnfield2) are placed the same way, where the world has any.
    ///
    /// The asteroids are closed at the end.
    /// </summary>
    public sealed class AsteroidFieldScenario : TestScenario
    {
        public const string ScenarioName = "asteroid_field";
        private const string Prefix = "asteroids-";
        private const double AltitudeM = 20000;
        private const int FieldSize = 600;
        private const int Generated = 4;
        private const int SizeMin = 60, SizeMax = 100;
        private const int Predefined = 2;
        private const double SpawnSeconds = 120;
        private static readonly string[] Ores = { "Iron_02", "Nickel_01" };
        private static readonly string[] Stone = { "Stone_01", "Stone_02", "Stone_03", "Stone_04", "Stone_05" };

        private readonly List<MyVoxelBase> _spawned = new List<MyVoxelBase>();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 360;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetType("SentisGameplayImprovements.AsteroidFieldSpawner", false) != null);
            Check(assembly != null, "SentisGameplayImprovements with AsteroidFieldSpawner is not loaded");
            var spawner = assembly.GetType("SentisGameplayImprovements.AsteroidFieldSpawner");
            var extent = spawner.GetMethod("Extent", BindingFlags.Static | BindingFlags.Public);
            var clearance = (double)spawner.GetField("Clearance").GetRawConstantValue();

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var centre = anchorM.Translation + up * AltitudeM;
            var obstacle = SpawnObstacle(centre, up, side);

            // ------------------------------------------------------------- generated asteroids
            var delayedRan = new Stopwatch();
            var watch = Stopwatch.StartNew();
            TickMetrics.Take();
            var task = (Task)spawner.GetMethod("SpawnGenerated").Invoke(null, new object[]
            {
                centre, FieldSize, Generated, Ores, SizeMin, SizeMax, (Action<string>)(outcome => Note("generated: " + outcome)), _spawned
            });
            ScheduleDelayed(assembly, () => delayedRan.Start());
            while (!task.IsCompleted && watch.Elapsed.TotalSeconds < SpawnSeconds) yield return null;
            var frames = TickMetrics.Take();
            Check(task.IsCompleted, "the field was not made in " + SpawnSeconds + " s");
            Check(!task.IsFaulted, "the field failed: " + task.Exception?.GetBaseException().Message);
            Note("generated " + _spawned.Count + " of " + Generated + " in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s | " + frames.Format());
            Check(delayedRan.IsRunning, "a delayed action of the plugin did not run while the field was generated");
            Check(_spawned.Count == Generated, "placed " + _spawned.Count + " of " + Generated + " asteroids");
            CheckPlacement(_spawned, obstacle, centre, extent, clearance);

            var allowed = new HashSet<byte>(Ores.Concat(Stone)
                .Select(m => MyDefinitionManager.Static.TryGetVoxelMaterialDefinition(m, out var d) ? d : null)
                .Where(d => d != null).Select(d => d.Index));
            foreach (var voxel in _spawned)
            {
                var (solid, foreign) = Materials(voxel, allowed);
                Note(voxel.StorageName + ": " + solid + " solid voxels sampled, " + foreign + " of another ore");
                Check(solid > 0, voxel.StorageName + " is empty");
                Check(foreign == 0, voxel.StorageName + " holds " + foreign + " voxels of an ore not asked for");
            }

            // ------------------------------------------------------------- predefined asteroids
            var definitions = (IList)spawner.GetMethod("FieldDefinitions").Invoke(null, null);
            if (definitions.Count == 0)
            {
                Note("the world has no predefined \"Field\" asteroid: spawnfield2 not tried");
                yield break;
            }
            var before = _spawned.Count;
            watch.Restart();
            task = (Task)spawner.GetMethod("SpawnPredefined").Invoke(null, new object[]
            {
                centre, FieldSize, Predefined, definitions, (Action<string>)(outcome => Note("predefined: " + outcome)), _spawned
            });
            while (!task.IsCompleted && watch.Elapsed.TotalSeconds < SpawnSeconds) yield return null;
            Check(task.IsCompleted && !task.IsFaulted, "the predefined field failed");
            Check(_spawned.Count - before == Predefined, "placed " + (_spawned.Count - before) + " of " + Predefined + " predefined asteroids");
            CheckPlacement(_spawned, obstacle, centre, extent, clearance);
        }

        /// <summary>Inside the field, clear of the grid and of each other, saved, ids and names their own.</summary>
        private void CheckPlacement(List<MyVoxelBase> voxels, MyCubeGrid obstacle, Vector3D centre, MethodInfo extent, double clearance)
        {
            var spheres = voxels.Select(v => new BoundingSphereD(v.PositionComp.WorldAABB.Center,
                (double)extent.Invoke(null, new object[] { v.Storage.Size.AbsMax() }))).ToList();
            for (var i = 0; i < voxels.Count; i++)
            {
                var v = voxels[i];
                Check(!v.MarkedForClose, v.StorageName + " was closed");
                Check(v.Save, v.StorageName + " is not saved with the world");
                Check(Vector3D.Distance(spheres[i].Center, centre) <= FieldSize + 1, v.StorageName + " is outside the field");
                Check(!obstacle.PositionComp.WorldAABB.Intersects(spheres[i]), v.StorageName + " touches the grid in the field");
                for (var j = 0; j < i; j++)
                    Check(Vector3D.Distance(spheres[i].Center, spheres[j].Center) >= spheres[i].Radius + spheres[j].Radius - 1,
                        v.StorageName + " overlaps " + voxels[j].StorageName);
            }
            Check(voxels.Select(v => v.EntityId).Distinct().Count() == voxels.Count, "two asteroids share an id");
            Check(voxels.Select(v => v.StorageName).Distinct().Count() == voxels.Count, "two asteroids share a storage name");
            Note("placement: " + voxels.Count + " asteroids clear of the grid and of each other, clearance " + clearance + " m");
        }

        /// <summary>Solid voxels of a sample (every other voxel), and how many of them are of a material not allowed.</summary>
        private static (int Solid, int Foreign) Materials(MyVoxelBase voxel, HashSet<byte> allowed)
        {
            var storage = voxel.Storage;
            var data = new MyStorageData();
            var chunk = Vector3I.Min(new Vector3I(64), storage.Size);
            data.Resize(chunk);
            int solid = 0, foreign = 0;
            Vector3I block;
            for (block.Z = 0; block.Z < storage.Size.Z; block.Z += chunk.Z)
            for (block.Y = 0; block.Y < storage.Size.Y; block.Y += chunk.Y)
            for (block.X = 0; block.X < storage.Size.X; block.X += chunk.X)
            {
                storage.ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, 0, block, block + chunk - 1);
                Vector3I p;
                for (p.Z = 0; p.Z < chunk.Z; p.Z += 2)
                for (p.Y = 0; p.Y < chunk.Y; p.Y += 2)
                for (p.X = 0; p.X < chunk.X; p.X += 2)
                {
                    if (data.Content(ref p) < 128) continue;
                    solid++;
                    if (!allowed.Contains(data.Material(ref p))) foreign++;
                }
            }
            return (solid, foreign);
        }

        /// <summary>Queues an action on the plugin's delayed actions, due now.</summary>
        private static void ScheduleDelayed(Assembly assembly, Action action)
        {
            var type = assembly.GetType("SentisGameplayImprovements.DelayedLogic.DelayedProcessor");
            var instance = type?.GetField("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            if (instance == null) throw new InvalidOperationException("DelayedProcessor.Instance is not there");
            type.GetMethod("AddDelayedAction").Invoke(instance, new object[] { DateTime.Now, action });
        }

        private MyCubeGrid SpawnObstacle(Vector3D at, Vector3D up, Vector3D side)
        {
            var block = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "grid",
                DisplayName = WorldApi.EntityPrefix + Prefix + "grid",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, side, up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock> { block },
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        public override void Cleanup()
        {
            try
            {
                lock (_spawned)
                    foreach (var voxel in _spawned.Where(v => !v.MarkedForClose))
                        voxel.Close();
            }
            finally { base.Cleanup(); }
        }
    }
}
