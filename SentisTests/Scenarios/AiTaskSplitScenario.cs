using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// An AI task block broken off its grid while the grid is in combat, then deleted, and the grid's combat over and
    /// begun again (production fell so on 14.10.2026, 22:19: NullReferenceException in
    /// <c>MyAiBlockSystem.DeactivateComponent</c> out of <c>SetCombatState(true)</c>, when an offensive combat block found a
    /// target). In combat the grid keeps its task component among the active ones unless the block is marked for close;
    /// one that left by a split stays there, and once deleted has no entity. A grid in the air: battery, armor, basic task
    /// block, armor in a row (seven blocks and more a side); the task activated, combat on, the armor razed (the task block goes to a grid of its own), that grid
    /// closed, combat off and on. Passes when nothing throws and the first grid holds no task component (SO
    /// AiTaskBlockLeftGrid).
    /// </summary>
    public sealed class AiTaskSplitScenario : TestScenario
    {
        public const string ScenarioName = "ai_task_split";
        private const string Prefix = "AiTaskSplit_";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 60;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var planet = MyEntities.GetEntities().OfType<MyPlanet>().OrderByDescending(x => x.MaximumRadius).FirstOrDefault();
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var probe = centre + Vector3D.Normalize(new Vector3D(-0.4, 0.6, 0.7)) * planet.MaximumRadius;
            var ground = planet.GetClosestSurfacePointGlobal(ref probe);
            var up = Vector3D.Normalize(ground - centre);
            var origin = ground + up * 300;

            var owner = WorldApi.PlayerIdentityId();
            // in a row: battery and seven armor blocks, the armor block that is razed, the task block and six armor blocks
            // (the larger piece stays the grid; each of at least seven: the world's trash removal closes grids of fewer than five blocks)
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x <= 15; x++)
            {
                var b = WorldApi.MakeBlockOb(x == 0 ? "LargeBlockBatteryBlock" : x == 9 ? "LargeBasicMission" : "LargeBlockArmorBlock");
                b.Min = new SerializableVector3I(x, 0, 0);
                b.Owner = owner;
                b.BuiltBy = owner;
                blocks.Add(b);
            }
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "grid", DisplayName = WorldApi.EntityPrefix + Prefix + "grid",
                GridSizeEnum = MyCubeSize.Large, IsStatic = false,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(origin, Vector3D.CalculatePerpendicularVector(up), up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            var taskBlock = grid.GetFatBlocks().FirstOrDefault(b => b.BlockDefinition.Id.SubtypeName == "LargeBasicMission");
            Check(taskBlock != null, "the task block was not made");
            Check(taskBlock.Components.TryGet<MyAiBlockComponent>(out var component), "the task block has no AI component");
            var system = grid.GridSystems.AiBlockSystem;

            component.IsActivated = true;
            Check(system.GetActiveAiComponent(MyAiBlockType.Task) == component, "the task did not become the grid's active one");
            system.SetCombatState(true);
            Note($"AI | task active, combat on: active task {(system.GetActiveAiComponent(MyAiBlockType.Task) == component ? "kept" : "gone")}");

            grid.RazeBlock(new Vector3I(8, 0, 0));
            var split = WaitForSeconds(2, "the grid splits");
            while (split.MoveNext()) yield return split.Current;
            Note($"AI | armor razed: {grid.BlocksCount} blocks left on the first grid, the task block on {(taskBlock.CubeGrid == grid ? "it" : "another")}{(taskBlock.MarkedForClose ? ", marked for close" : "")}");
            Check(!taskBlock.MarkedForClose && taskBlock.CubeGrid != grid, "the task block did not go to a grid of its own");
            var broken = taskBlock.CubeGrid;
            Track(broken);
            var kept = system.GetActiveAiComponent(MyAiBlockType.Task) != null;
            Note($"AI | task block broken off to its own grid: the first grid's active task {(kept ? "still that block" : "gone")}");

            broken.Close();
            var closed = WaitForSeconds(1, "the broken-off grid is deleted");
            while (closed.MoveNext()) yield return closed.Current;
            Note($"AI | broken-off grid deleted: component's block {(component.Entity == null ? "none" : "still there")}");

            Exception thrown = null;
            try
            {
                system.SetCombatState(false);
                system.SetCombatState(true);
            }
            catch (Exception e) { thrown = e; }
            Note($"AI | combat off and on: {(thrown == null ? "no throw" : thrown.GetType().Name + ": " + thrown.Message)}");
            Check(thrown == null, $"combat on threw {thrown?.GetType().Name}: {thrown?.Message}");
            Check(system.GetActiveAiComponent(MyAiBlockType.Task) == null, "the first grid still holds the task component of a deleted block");
        }
    }
}
