using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;

namespace SentisTests.Game
{
    public class BlockSpec
    {
        public string SubtypeId;
        public Vector3I Position;
        public int Orientation;
        /// <summary>Amount of SteelPlate to place into the block's own inventory (0 = none).</summary>
        public int SteelPlates;
        /// <summary>Construction state of the block: 1 = fully built, 0 = unbuilt scaffold.</summary>
        public float BuildPercent = 1f;
        /// <summary>Override for the inventory object builder (takes precedence over SteelPlates).</summary>
        public MyObjectBuilder_Inventory Inventory;

        public BlockSpec(string subtypeId, Vector3I position, int orientation = 0)
        {
            SubtypeId = subtypeId;
            Position = position;
            Orientation = orientation;
        }

        public BlockSpec WithSteel(int plates)
        {
            SteelPlates = plates;
            return this;
        }
    }

    /// <summary>
    /// Server-side world manipulation used by scenarios: block-definition lookup, grid spawning,
    /// physics steering, inventory filling. Everything here must be called on the game thread.
    /// </summary>
    public static class WorldApi
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static readonly Dictionary<string, string> SubtypeCache = new Dictionary<string, string>();

        // ------------------------------------------------------------- definitions

        /// <summary>
        /// Finds a cube-block subtype for the given grid size whose SubtypeName contains ALL the
        /// tokens (case-insensitive). Cached. Throws ScenarioFailedException with the candidate
        /// list when nothing matches, so tuning is obvious from the report.
        /// </summary>
        public static string FindSubtype(MyCubeSize size, params string[] tokens)
        {
            var key = size + "|" + string.Join(",", tokens);
            string cached;
            if (SubtypeCache.TryGetValue(key, out cached))
                return cached;

            var lowered = tokens.Select(t => t.ToLowerInvariant()).ToArray();
            var matches = MyDefinitionManager.Static
                .GetDefinitionsOfType<MyCubeBlockDefinition>()
                .Where(d => d.CubeSize == size && d.Id.SubtypeName != null)
                .Where(d => lowered.All(t => d.Id.SubtypeName.ToLowerInvariant().Contains(t)))
                // prefer plain (non-DLC/prefab) subtypes: shortest name usually wins
                .OrderBy(d => d.Id.SubtypeName.Length)
                .Select(d => d.Id.SubtypeName)
                .ToList();

            if (matches.Count == 0)
            {
                // log names matching ANY single token, they are far more useful than a head sample
                var sample = MyDefinitionManager.Static
                    .GetDefinitionsOfType<MyCubeBlockDefinition>()
                    .Where(d => d.CubeSize == size && d.Id.SubtypeName != null)
                    .Where(d => lowered.Any(t => d.Id.SubtypeName.ToLowerInvariant().Contains(t)))
                    .Select(d => d.Id.SubtypeName)
                    .Take(50);
                Log.Error("no subtype for {0} [{1}]; candidates: {2}", size, key, string.Join(", ", sample));
                throw new Core.ScenarioFailedException(
                    "no block subtype matches " + size + " [" + string.Join("+", tokens) + "]");
            }

            Log.Info("subtype {0} [{1}] matched {2}: {3}", size, key, matches.Count, string.Join(", ", matches.Take(40)));

            var chosen = matches[0];
            Log.Info("subtype for {0} [{1}] -> {2}", size, string.Join("+", tokens), chosen);
            SubtypeCache[key] = chosen;
            return chosen;
        }

        // ------------------------------------------------------------------ grids

        /// <summary>
        /// The dedicated server keeps running its main loop while the WORLD is paused, so a paused
        /// session looks exactly like "physics broken". Tests must not run against a paused world.
        /// </summary>
        public static void EnsureUnpaused(string context)
        {
            if (Sandbox.MySandboxGame.IsPaused)
            {
                Log.Warn("session is PAUSED ({0}); resuming", context);
                Sandbox.MySandboxGame.IsPaused = false;
            }
        }

        public static void LogDefinitionRecon()
        {
            var names = MyDefinitionManager.Static
                .GetDefinitionsOfType<MyCubeBlockDefinition>()
                .Where(d => d.Id.SubtypeName != null &&
                            (d.Id.SubtypeName.ToLowerInvariant().Contains("eact") ||
                             d.Id.SubtypeName.ToLowerInvariant().Contains("solar") ||
                             d.Id.SubtypeName.ToLowerInvariant().Contains("engine")))
                .Select(d => d.CubeSize + ":" + d.Id.SubtypeName)
                .Distinct().OrderBy(x => x);
            Log.Info("power-related subtypes: {0}", string.Join(", ", names));
        }

        private static long? _identityId;

        /// <summary>A dedicated identity that owns all test constructs (projectors require one).</summary>
        public static long TestIdentityId()
        {
            if (_identityId.HasValue)
                return _identityId.Value;

            var players = Sandbox.Game.World.MySession.Static.Players;
            foreach (var identity in players.GetAllIdentities())
            {
                if (identity.DisplayName == "SentisTests")
                {
                    _identityId = identity.IdentityId;
                    return _identityId.Value;
                }
            }

            var created = players.CreateNewIdentity("SentisTests");
            _identityId = created.IdentityId;
            Log.Info("created test identity {0} ({1})", created.DisplayName, created.IdentityId);
            return _identityId.Value;
        }

        public static MyObjectBuilder_CubeGrid GridOb(string name, MyCubeSize size, bool isStatic,
            Vector3D position, IEnumerable<BlockSpec> blocks)
        {
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = name,
                GridSizeEnum = size,
                IsStatic = isStatic,
                PositionAndOrientation = new MyPositionAndOrientation(
                    position, Vector3.Forward, Vector3.Up),
                // WITHOUT InScene the entity is added to the server lists only: game-thread logic
                // runs, but the replication/scene layer never tells clients about it.
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>(),
            };

            foreach (var spec in blocks)
            {
                var block = new MyObjectBuilder_CubeBlock
                {
                    SubtypeName = spec.SubtypeId,
                    Min = new SerializableVector3I(spec.Position.X, spec.Position.Y, spec.Position.Z),
                };
                block.BuiltBy = TestIdentityId();
                if (spec.Orientation != 0)
                    block.BlockOrientation.Forward = (VRageMath.Base6Directions.Direction)spec.Orientation;

                if (spec.BuildPercent < 1f)
                    block.BuildPercent = spec.BuildPercent;

                if (spec.SteelPlates > 0)
                {
                    block.ConstructionInventory = new MyObjectBuilder_Inventory
                    {
                        Items = new List<MyObjectBuilder_InventoryItem>
                        {
                            new MyObjectBuilder_InventoryItem
                            {
                                Amount = spec.SteelPlates,
                                Content = new MyObjectBuilder_Component { SubtypeName = "SteelPlate" },
                            },
                        },
                    };
                }

                ob.CubeBlocks.Add(block);
            }

            return ob;
        }

        public static MyObjectBuilder_Inventory SteelInventory(int amount)
        {
            return new MyObjectBuilder_Inventory
            {
                Items = new List<MyObjectBuilder_InventoryItem>
                {
                    new MyObjectBuilder_InventoryItem
                    {
                        Amount = amount,
                        Content = new MyObjectBuilder_Component { SubtypeName = "SteelPlate" },
                    },
                },
            };
        }

        /// <summary>
        /// Creates the grid and adds it to the world. Synchronous variant of
        /// MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd; the entity is fully inited on return.
        /// </summary>
        public static MyCubeGrid SpawnGrid(MyObjectBuilder_CubeGrid ob)
        {
            var entity = MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(ob);
            var grid = entity as MyCubeGrid;
            if (grid == null)
                throw new Core.ScenarioFailedException("spawned entity is not a cube grid: " +
                                                       (entity == null ? "null" : entity.GetType().Name));
            Log.Info("spawned grid '{0}' id={1} blocks={2} static={3}", ob.Name, grid.EntityId,
                ob.CubeBlocks.Count, ob.IsStatic);
            return grid;
        }

        public static int CountBlocks(MyCubeGrid grid)
        {
            return grid == null || grid.CubeBlocks == null ? 0 : grid.CubeBlocks.Count;
        }

        // -------------------------------------------------------------- utilities

        public static IEnumerable<T> Functionals<T>(MyCubeGrid grid) where T : class
        {
            foreach (var cube in grid.GetBlocks())
            {
                var fat = cube.FatBlock;
                if (fat is T typed && !fat.MarkedForClose)
                    yield return typed;
            }
        }

        public static T FindFunctional<T>(MyCubeGrid grid) where T : class
        {
            if (grid?.CubeBlocks == null)
                return null;
            foreach (var slim in grid.CubeBlocks)
            {
                var block = slim?.FatBlock;
                var typed = block as T;
                if (typed != null)
                    return typed;
            }

            return null;
        }

        public static List<T> FindFunctionals<T>(MyCubeGrid grid) where T : class
        {
            var result = new List<T>();
            if (grid?.CubeBlocks == null)
                return result;
            foreach (var slim in grid.CubeBlocks)
            {
                var typed = slim?.FatBlock as T;
                if (typed != null)
                    result.Add(typed);
            }

            return result;
        }

        public static Vector3D PositionOf(IMyEntity entity)
        {
            return entity.GetPosition();
        }

        public static double DistanceTo(IMyEntity entity, Vector3D target)
        {
            return Vector3D.Distance(PositionOf(entity), target);
        }

        /// <summary>
        /// Velocity-controlled steering: pushes the grid's velocity toward the target point.
        /// Doubles as gravity compensation on the dedicated server (empty world has gravity),
        /// which is exactly what a player holding thrust would achieve with controllers.
        /// </summary>
        public static void SteerToward(MyCubeGrid grid, Vector3D target, double speed, double responsiveness = 3.0,
            double arrivalRadius = 2.0)
        {
            var physics = grid?.Physics;
            if (physics == null)
                return;

            var current = physics.LinearVelocity;
            var to = target - PositionOf(grid);
            var distance = to.Length();

            Vector3D desired;
            if (distance <= arrivalRadius)
            {
                desired = Vector3D.Zero;
            }
            else
            {
                desired = to / distance * speed;
                // slow down on final approach
                if (distance < speed * 2)
                    desired *= distance / (speed * 2);
            }

            var dt = 1.0 / 60.0;
            var blend = Math.Min(1.0, responsiveness * dt);
            physics.LinearVelocity = new Vector3(
                current.X + (float)((desired.X - current.X) * blend),
                current.Y + (float)((desired.Y - current.Y) * blend),
                current.Z + (float)((desired.Z - current.Z) * blend));
        }

        /// <summary>Kills all velocity (station-keeping).</summary>
        public static void Hold(MyCubeGrid grid)
        {
            SteerToward(grid, PositionOf(grid), 0);
        }

        public static List<MyCubeGrid> GridsInSphere(Vector3D center, double radius)
        {
            var result = new List<MyCubeGrid>();
            IEnumerable<IMyEntity> found;
            var sphere = new BoundingSphereD(center, radius);
            try
            {
                found = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            }
            catch (Exception e)
            {
                Log.Warn("GetTopMostEntitiesInSphere failed: {0}", e.Message);
                return result;
            }

            if (found == null)
                return result;

            foreach (var entity in found)
            {
                var grid = entity as MyCubeGrid;
                if (grid != null && !grid.MarkedForClose)
                    result.Add(grid);
            }

            return result;
        }

        public static string DescribePower(MyCubeGrid grid)
        {
            try
            {
                var producers = FindFunctionals<MyReactor>(grid);
                var working = producers.Count(p => p.IsWorking);
                return "reactors=" + producers.Count + " working=" + working;
            }
            catch (Exception e)
            {
                return "power info unavailable: " + e.Message;
            }
        }
    }
}
