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
        /// <summary>
        /// Every entity this plugin creates carries this prefix; the session-start purge and
        /// The purge keys off it. The random digits make a collision with a player-chosen
        /// name essentially impossible.
        /// </summary>
        public const string EntityPrefix = "ST-47351-";

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
            Vector3D position, IEnumerable<BlockSpec> blocks,
            Vector3? forward = null, Vector3? up = null)
        {
            var f = forward ?? Vector3.Forward;
            var u = up ?? Vector3.Up;
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = name,
                // the admin GUI searches DisplayName, not the internal Name
                DisplayName = name,
                GridSizeEnum = size,
                IsStatic = isStatic,
                PositionAndOrientation = new MyPositionAndOrientation(position, f, u),
                // WITHOUT InScene the entity is added to the server lists only: game-thread logic
                // runs, but the replication/scene layer never tells clients about it.
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>(),
            };

            foreach (var spec in blocks)
            {
                var block = MakeBlockOb(spec.SubtypeId);
                block.Min = new SerializableVector3I(spec.Position.X, spec.Position.Y, spec.Position.Z);
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

        /// <summary>
        /// Creates the block object-builder of the DERIVED type registered for the subtype in the
        /// definition manager (MyObjectBuilder_ShipWelder for LargeShipWelder, etc). The plain base
        /// MyObjectBuilder_CubeBlock is silently dropped for functional blocks during grid spawn, so
        /// prefab ships must use the derived type. Falls back to the base type when the subtype has
        /// no resolvable definition.
        /// </summary>
        public static MyObjectBuilder_CubeBlock MakeBlockOb(string subtypeId)
        {
            var defId = Sandbox.Definitions.MyDefinitionManager.Static
                .GetDefinitionsOfType<Sandbox.Definitions.MyCubeBlockDefinition>()
                .Where(d => string.Equals(d.Id.SubtypeName, subtypeId, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Id)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(defId.SubtypeName))
            {
                var derived = VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen
                    .CreateNewObject(defId) as MyObjectBuilder_CubeBlock;
                if (derived != null)
                    return derived;
            }
            return new MyObjectBuilder_CubeBlock { SubtypeName = subtypeId };
        }

        /// <summary>
        /// Aggregates every component the given blueprint blocks consume (definition component
        /// stacks x instance count x safety multiplier). Maps component subtype -> amount.
        /// </summary>
        public static Dictionary<string, int> ComponentsNeeded(
            IEnumerable<VRage.Game.MyObjectBuilder_CubeBlock> blueprintBlocks, int multiplier = 2)
        {
            var needs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var grp in blueprintBlocks.GroupBy(b => b.SubtypeName))
            {
                var def = Sandbox.Definitions.MyDefinitionManager.Static
                    .GetDefinitionsOfType<Sandbox.Definitions.MyCubeBlockDefinition>()
                    .FirstOrDefault(d => string.Equals(d.Id.SubtypeName, grp.Key, StringComparison.OrdinalIgnoreCase));
                if (def == null || def.Components == null)
                {
                    Log.Warn("no definition/components for blueprint block {0}", grp.Key);
                    continue;
                }
                foreach (var c in def.Components)
                {
                    var name = c.Definition != null ? c.Definition.Id.SubtypeName : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    int add = c.Count * grp.Count() * Math.Max(1, multiplier);
                    int prev;
                    needs.TryGetValue(name, out prev);
                    needs[name] = prev + add;
                }
            }
            return needs;
        }

        /// <summary>
        /// Puts every component of the map into the block's inventory (welder/drill style storage).
        /// Returns a human summary; individual failures are logged, not thrown.
        /// </summary>
        public static string StockComponents(VRage.Game.Entity.MyInventoryBase inventory,
            Dictionary<string, int> components)
        {
            var done = new List<string>();
            foreach (var kvp in components)
            {
                try
                {
                    var content = new VRage.Game.MyObjectBuilder_Component { SubtypeName = kvp.Key };
                    inventory.AddItems(kvp.Value, content);
                    done.Add(kvp.Key + "=" + CountComponent(inventory, kvp.Key) + "/" + kvp.Value);
                }
                catch (Exception e)
                {
                    Log.Warn("cannot stock {0} x{1}: {2}", kvp.Key, kvp.Value, e.Message);
                }
            }
            return string.Join(", ", done);
        }

        public static int CountComponent(VRage.Game.Entity.MyInventoryBase inventory, string subtype)
        {
            int total = 0;
            if (inventory == null) return 0;
            foreach (dynamic item in inventory.GetItems())
            {
                string name = ((string)item.Content.SubtypeName);
                if (string.Equals(name, subtype, StringComparison.OrdinalIgnoreCase))
                    total += (int)(float)item.Amount;
            }
            return total;
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

        /// <summary>Blocks that are fully built (survival builds start as 0 % scaffolds).</summary>
        public static int CountFinished(MyCubeGrid grid)
        {
            if (grid == null || grid.CubeBlocks == null) return 0;
            var n = 0;
            foreach (var b in grid.CubeBlocks)
                if (b.IsFullIntegrity) n++;
            return n;
        }

        // ------------------------------------------------------- ship-tool sensors
        // MyShipToolBase keeps the detector sphere and the activation flag private; the test
        // needs them to respect the welder's real reach instead of teleport-welding.
        private static System.Reflection.FieldInfo FindField(object obj, string name)
        {
            for (var t = obj.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>World-space detector sphere of a ship tool (m_detectorSphere transformed by the grid matrix).</summary>
        public static BoundingSphereD SensorSphere(Sandbox.Game.Entities.MyCubeBlock tool)
        {
            var f = FindField(tool, "m_detectorSphere");
            if (f == null) return new BoundingSphereD(PositionOf(tool), 15);
            var local = (BoundingSphere)f.GetValue(tool);
            var center = Vector3D.Transform((Vector3D)local.Center, tool.CubeGrid.WorldMatrix);
            return new BoundingSphereD(center, local.Radius);
        }

        public static bool ToolIsActivated(Sandbox.Game.Entities.MyCubeBlock tool)
        {
            var f = FindField(tool, "m_isActivated");
            return f != null && (bool)f.GetValue(tool);
        }

        /// <summary>Vanilla StartShooting(): sets m_isActivated so UpdateAfterSimulation10 drives ActivateCommon.</summary>
        public static void ToolStartShooting(Sandbox.Game.Entities.MyCubeBlock tool)
        {
            for (var t = tool.GetType(); t != null; t = t.BaseType)
            {
                var m = t.GetMethod("StartShooting", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (m != null) { m.Invoke(tool, new object[0]); break; }
            }
            tool.NeedsUpdate |= VRage.ModAPI.MyEntityUpdateEnum.EACH_10TH_FRAME;
        }

        /// <summary>MyShipWelder.FindProjectedBlocks(): how many hologram blocks the welder itself sees right now.</summary>
        public static int ProbeProjectedBlocks(Sandbox.Game.Entities.MyCubeBlock tool)
        {
            for (var t = tool.GetType(); t != null; t = t.BaseType)
            {
                var m = t.GetMethod("FindProjectedBlocks", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (m != null)
                {
                    var arr = m.Invoke(tool, new object[0]);
                    return arr == null ? 0 : ((Array)arr).Length;
                }
            }
            return -1;
        }

        /// <summary>
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

        /// <summary>
        /// Adds one real, fully functional block to an existing grid through the engine's live-add
        /// path (private MyCubeGrid.AddBlock). The object-builder spawn path silently strips some
        /// fat blocks (e.g. LargeShipWelder: 18 in OB -> 17 in grid); the live-add path used by
        /// actual in-game construction does not.
        /// </summary>
        public static Sandbox.Game.Entities.MyCubeBlock AddRealBlock(MyCubeGrid grid, string subtypeId,
            Vector3I min, float buildAmount = 1f)
        {
            // the serializer creates the DERIVED object-builder type (e.g. ShipWelder); the
            // base MyObjectBuilder_CubeBlock makes MyCubeBlockFactory produce a thin block
            // resolve the definition to get the DERIVED object-builder type the serializer needs
            var defId = Sandbox.Definitions.MyDefinitionManager.Static
                .GetDefinitionsOfType<Sandbox.Definitions.MyCubeBlockDefinition>()
                .Where(d => string.Equals(d.Id.SubtypeName, subtypeId, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Id)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(defId.SubtypeName))
                throw new InvalidOperationException("no cube-block definition for subtype " + subtypeId);

            var blockOb = VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen.CreateNewObject(defId)
                as VRage.Game.MyObjectBuilder_CubeBlock;
            if (blockOb == null)
                throw new InvalidOperationException("no object-builder type for subtype " + subtypeId);
            blockOb.Min = new VRage.SerializableVector3I(min.X, min.Y, min.Z);
            blockOb.BuiltBy = TestIdentityId();
            // fresh unique block id: reuse MyEntities' private remap helper (blocks are not
            // entity bases, so the public RemapObjectBuilder overload does not accept them)
            var helperField = typeof(Sandbox.Game.Entities.MyEntities).GetField("m_remapHelper",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var remapper = helperField.GetValue(null) as VRage.ModAPI.IMyRemapHelper;
            if (remapper == null)
            {
                // internal Sandbox.Game.Entities.MyEntityIdRemapHelper, lazily created like MyEntities does
                var helperType = typeof(Sandbox.Game.Entities.MyEntities)
                    .Assembly.GetType("Sandbox.Game.Entities.MyEntityIdRemapHelper");
                remapper = Activator.CreateInstance(helperType, nonPublic: true) as VRage.ModAPI.IMyRemapHelper;
                helperField.SetValue(null, remapper);
            }
            blockOb.Remap(remapper);
            remapper.Clear();

            var mi = typeof(MyCubeGrid).GetMethod("AddBlock",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (mi == null)
                throw new InvalidOperationException("MyCubeGrid.AddBlock not found");

            Sandbox.Definitions.MyCubeBlockDefinition dbgDef;
            bool haveDef = Sandbox.Definitions.MyDefinitionManager.Static
                .TryGetCubeBlockDefinition(blockOb.GetId(), out dbgDef);
            bool freeCell = grid.CanAddCubes(min, min);
            Log.Info("live-add pre: obType={0} id={1} def={2} bigOrSmall={3} gridLarge={4} cellFree={5}",
                blockOb.GetType().Name, blockOb.GetId().ToString(), dbgDef != null,
                dbgDef != null ? dbgDef.CubeSize.ToString() : "-", grid.GridSizeEnum, freeCell);

            object slimRaw;
            try
            {
                slimRaw = mi.Invoke(grid, new object[] { blockOb, false });
            }
            catch (Exception e)
            {
                Log.Error(e.InnerException ?? e, "live-add AddBlock threw for {0}", subtypeId);
                return null;
            }
            var slim = slimRaw as Sandbox.Game.Entities.Cube.MySlimBlock;
            if (slim == null)
            {
                // triage: call AddCubeBlock directly (skips UpgradeCubeBlock) and report the step that fails
                try
                {
                    var acb = typeof(MyCubeGrid).GetMethod("AddCubeBlock",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var probe = acb.Invoke(grid, new object[] { blockOb, false, dbgDef });
                    Log.Warn("AddCubeBlock probe returned {0}", probe == null ? "null" : "slim");
                    slim = probe as Sandbox.Game.Entities.Cube.MySlimBlock;
                }
                catch (Exception e2)
                {
                    Log.Error(e2.InnerException ?? e2, "live-add AddCubeBlock probe threw for {0}", subtypeId);
                }
                try
                {
                    var min2 = min;
                    VRageMath.MyBlockOrientation orient = (VRageMath.MyBlockOrientation)blockOb.BlockOrientation;
                    var max2 = min;
                    Sandbox.Game.Entities.Cube.MySlimBlock.ComputeMax(dbgDef, orient, ref min2, out max2);
                    var scratch = new Sandbox.Game.Entities.Cube.MySlimBlock();
                    bool initOk = scratch.Init(blockOb, grid, null);
                    Log.Warn("live-add triage: min={0} max={1} spanFree={2} cellFree={3} slimInit={4} fat={5} defSize={6} gridSize={7}",
                        min2, max2, grid.CanAddCubes(min2, max2), grid.CanAddCubes(min, min), initOk,
                        scratch.FatBlock != null, dbgDef.CubeSize, grid.GridSizeEnum);
                }
                catch (Exception e3)
                {
                    Log.Error(e3.InnerException ?? e3, "live-add triage threw");
                }
                if (slim == null)
                {
                    Log.Warn("live-add {0} at {1} returned no slim block", subtypeId, min);
                    return null;
                }
            }
            if (buildAmount > 0f && !slim.IsFullIntegrity)
                slim.IncreaseMountLevel(buildAmount, TestIdentityId(), null, 1f, false,
                    VRage.Game.MyOwnershipShareModeEnum.None);

            Log.Info("live-added {0} at {1} on {2}: fat={3} integrity={4}", subtypeId, min,
                grid.DisplayName, slim.FatBlock != null, slim.Integrity);
            return slim.FatBlock;
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
