using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using BuildCheckResult = Sandbox.ModAPI.BuildCheckResult;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A probe of the user's "Шасси под проектор" (12.10.2026): its projector and the "Атмосферный бур" it projects - what of
    /// the projection can be welded now, where, and which block of the chassis each of those stands against. Read only:
    /// what the bots need to know to weld the flying drill from it, and to tell it from the chassis after.
    /// </summary>
    public sealed class DrillPrintProbeScenario : TestScenario
    {
        public const string ScenarioName = "drill_print_probe";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 30;

        public override IEnumerator Run()
        {
            // (the bots' chassis first, built by a bot; else the user's)
            var chassis = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.DisplayName == "Шасси под проектор")
                .OrderByDescending(g => g.GetBlocks().Any(b => Sandbox.Game.World.MySession.Static.Players.TryGetIdentity(b.BuiltBy)?.DisplayName?.StartsWith("[BOT]") == true)).FirstOrDefault();
            if (chassis != null)
                foreach (var power in chassis.GetFatBlocks().OfType<Sandbox.Game.Entities.MyBatteryBlock>())
                    Note($"DRILL PRINT | battery {power.CurrentStoredPower:0.000} of {power.MaxStoredPower:0.000} MWh, working {power.IsWorking}");
            Check(chassis != null, "no grid 'Шасси под проектор'");
            var projector = chassis.GetFatBlocks().OfType<MyProjectorBase>().FirstOrDefault();
            Check(projector != null, "no projector on the chassis");
            // what of the chassis and the drill the bots may weld (the world has research on)
            var research = Sandbox.Game.SessionComponents.MySessionComponentResearch.Static;
            foreach (var bot in new[] { "[BOT] Cedar", "[BOT] Alder" })
            {
                var identity = Sandbox.Game.World.MySession.Static.Players.GetAllIdentities().FirstOrDefault(i => i.DisplayName == bot);
                if (identity == null || research == null) continue;
                var kinds = chassis.GetBlocks().Select(b => b.BlockDefinition.Id).Concat(projector.ProjectedGrid?.CubeBlocks.Select(b => b.BlockDefinition.Id) ?? Enumerable.Empty<VRage.Game.MyDefinitionId>()).Distinct().ToList();
                var locked = kinds.Where(k => !research.CanUse(identity.IdentityId, k)).Select(k => k.SubtypeName).ToList();
                Note($"DRILL PRINT | {bot}: of {kinds.Count} kinds of block of the chassis and the drill, not researched {locked.Count}: {string.Join(", ", locked)}");
                // the world's limits for one more small armour block on a grid of its own
                var armour = Sandbox.Definitions.MyDefinitionManager.Static.GetCubeBlockDefinition(VRage.Game.MyDefinitionId.Parse("MyObjectBuilder_CubeBlock/SmallBlockArmorBlock"));
                var limit = Sandbox.Game.World.MySession.Static.IsWithinWorldLimits(out var failed, identity.IdentityId, armour.BlockPairName, armour.PCU, 1);
                var limits = Sandbox.Game.World.MySession.Static.Players.TryGetIdentity(identity.IdentityId)?.BlockLimits;
                Note($"DRILL PRINT | {bot}: one more small armour block: {limit} {failed}; PCU {limits?.PCUBuilt} of {limits?.MaxBlocks}, blocks {limits?.BlocksBuilt}; settings: total PCU {Sandbox.Game.World.MySession.Static.Settings.TotalPCU}, max grid blocks {Sandbox.Game.World.MySession.Static.Settings.MaxGridSize}, max blocks per player {Sandbox.Game.World.MySession.Static.Settings.MaxBlocksPerPlayer}, limits {Sandbox.Game.World.MySession.Static.Settings.BlockLimitsEnabled}");
            }
            var preview = projector.ProjectedGrid;
            Note($"DRILL PRINT | chassis {chassis.BlocksCount} blocks, {(chassis.IsStatic ? "static" : "dynamic")}, size {chassis.GridSizeEnum}; projector working {projector.IsWorking}, " +
                 $"enabled {projector.Enabled}, projection {(preview == null ? "NONE" : preview.DisplayName + ", " + preview.CubeBlocks.Count + " blocks")}");
            if (preview == null) yield break;
            // the cells the drill takes, in the chassis's cells from its landing gear's first one: where a chassis put down
            // anywhere has its drill (SentisAi's Blueprints/DrillCells.txt is made from this)
            var gear = chassis.GetBlocks().FirstOrDefault(b => b.BlockDefinition.Id.SubtypeName == "SmallBlockLandingGear");
            if (gear != null)
            {
                var cells = new System.Collections.Generic.SortedSet<string>();
                foreach (var block in preview.CubeBlocks)
                    for (var x = block.Min.X; x <= block.Max.X; x++)
                    for (var y = block.Min.Y; y <= block.Max.Y; y++)
                    for (var z = block.Min.Z; z <= block.Max.Z; z++)
                    {
                        var cell = chassis.WorldToGridInteger(preview.GridIntegerToWorld(new VRageMath.Vector3I(x, y, z))) - gear.Min;
                        cells.Add($"{cell.X} {cell.Y} {cell.Z}");
                    }
                var file = System.IO.Path.Combine(Sandbox.Game.World.MySession.Static.CurrentPath, "..", "..", "DrillCells.txt");
                System.IO.File.WriteAllLines(file, cells);
                Note($"DRILL PRINT | {cells.Count} cells of the drill from the landing gear written to {System.IO.Path.GetFullPath(file)}");
                // the test SentisAi asks of each cell for rock (a corner of a cube a little smaller than the cell inside the
                // voxel): a cube 3 m under the landing gear and one 3 m over it
                var foot = chassis.GridIntegerToWorld(gear.Position);
                var up = -VRageMath.Vector3D.Normalize(Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(foot));
                var half = new VRageMath.Vector3D(chassis.GridSize * 0.42);
                var local = new VRageMath.BoundingBoxD(-half, half);
                foreach (var depth in new[] { -3.0, 3.0 })
                {
                    var cube = chassis.WorldMatrix;
                    cube.Translation = foot + up * depth;
                    var box = new VRageMath.BoundingBoxD(cube.Translation - 1, cube.Translation + 1);
                    var voxels = new System.Collections.Generic.List<MyVoxelBase>();
                    MyGamePruningStructure.GetAllVoxelMapsInBox(ref box, voxels);
                    Note($"DRILL PRINT | rock test {depth:+0;-0} m from the landing gear: {voxels.Any(v => v.IsAnyAabbCornerInside(ref cube, local))} ({voxels.Count} voxel maps)");
                }
            }
            var counts = preview.CubeBlocks.GroupBy(b => projector.CanBuild(b, true)).Select(g => $"{g.Key} {g.Count()}");
            Note("DRILL PRINT | blocks by state: " + string.Join(", ", counts));
            foreach (var block in preview.CubeBlocks.Where(b => projector.CanBuild(b, true) == BuildCheckResult.OK).Take(12))
            {
                var at = preview.GridIntegerToWorld(block.Position);
                var cell = chassis.WorldToGridInteger(at);
                var touching = chassis.GetBlocks().Where(c => (c.Position - cell).RectangularLength() == 1).Select(c => $"{c.BlockDefinition.Id.SubtypeName}@{c.Position}");
                Note($"DRILL PRINT | buildable {block.BlockDefinition.Id.SubtypeName} at chassis cell {cell}, against {string.Join(" ", touching)}");
            }
            yield break;
        }
    }
}
