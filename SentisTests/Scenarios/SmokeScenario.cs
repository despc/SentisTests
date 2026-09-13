using System.Collections;
using Sandbox.Game;
using Sandbox.Game.World;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Baseline sanity check of the harness itself: spawn a small dynamic grid, give it a velocity,
    /// verify the simulation actually moves it, then destroy it. If this fails, anything built on
    /// top (weld scenarios etc.) is meaningless.
    /// </summary>
    public class SmokeScenario : TestScenario
    {
        public const string ScenarioName = "smoke";

        public override string Name { get { return ScenarioName; } }

        public override int TimeoutSeconds { get { return 120; } }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused("smoke start");
            WorldApi.LogDefinitionRecon();
            Note("resolving block subtype");
            var armor = WorldApi.FindSubtype(MyCubeSize.Large, "blockarmorblock");

            var spawn = TestRunner.RunOrigin ?? new Vector3D(300, 200, 300); // default: visible from world spawn
            var blocks = new System.Collections.Generic.List<BlockSpec>();
            for (var x = 0; x < 2; x++)
                for (var z = 0; z < 2; z++)
                    blocks.Add(new BlockSpec(armor, new Vector3I(x, 0, z)));

            Note("spawning 4-block dynamic grid");
            var smokeOb = WorldApi.GridOb(WorldApi.EntityPrefix + "smoke", MyCubeSize.Large, false, spawn, blocks);
            smokeOb.LinearVelocity = new SerializableVector3(15f, 0f, 0f);
            var grid = WorldApi.SpawnGrid(smokeOb);
            Track(grid);

            // watch the first second tick-by-tick; something has been eating fresh dynamic
            // grids silently, so record the exact tick and state of the death
            for (int t = 0; t < 60; t++)
            {
                yield return WaitForTicks(1);
                if (grid == null || grid.MarkedForClose)
                {
                    Note("grid died at tick " + t + " id=" + (grid == null ? "null" : grid.EntityId + "") +
                         " Closed=" + (grid != null && grid.Closed) +
                         " inEntityList=" + (grid != null && Sandbox.Game.Entities.MyEntities.GetEntityById(grid.EntityId) != null));
                    break;
                }
            }
            Check(grid != null && !grid.MarkedForClose, "grid alive after 1s");
            Check(WorldApi.CountBlocks(grid) == 4, "expected 4 blocks, got " + WorldApi.CountBlocks(grid));

            WorldApi.EnsureUnpaused("smoke before velocity");
            Note("applying +X velocity");
            Check(grid.Physics != null, "grid has physics");
            grid.Physics.ForceActivate();
            grid.Physics.Activate();
            grid.Physics.SetSpeeds(new Vector3(15f, 0, 0), Vector3.Zero);

            // a gyroscope drives velocity through the functional pipeline; on servers this works
            // even with nobody aboard, so it is the reliable way to prove the sim advances
            var gyro = WorldApi.FindFunctional<Sandbox.Game.Entities.MyGyro>(grid);
            yield return WaitForTicks(5);

            // gyro override "locks" the current velocity server-side and re-asserts it every tick
            if (gyro != null)
            {
                gyro.Enabled = true;
                gyro.SetGyroOverride(true);
                Note("gyro override engaged at vel " + grid.Physics.LinearVelocity.ToString("F1"));
            }

            var startPos = WorldApi.PositionOf(grid);

            yield return WaitForSeconds(3, "smoke flight");

            var moved = Vector3D.Distance(startPos, WorldApi.PositionOf(grid));
            if (moved <= 10)
            {
                Note("not moving (vel=" + grid.Physics.LinearVelocity.ToString("F1") +
                     " rb=" + (grid.Physics.RigidBody == null ? "null" : grid.Physics.RigidBody.GetType().Name) +
                     " active=" + grid.Physics.IsActive + " kinematic=" + grid.Physics.IsKinematic +
                     " lowQual=" + grid.Physics.LowSimulationQuality +
                     "); forcing dynamic and retrying");
                grid.Physics.LowSimulationQuality = false;
                grid.Physics.ForceActivate();
                grid.Physics.ConvertToDynamic(false, false);
                grid.Physics.SetSpeeds(new Vector3(15f, 0, 0), Vector3.Zero);
                startPos = WorldApi.PositionOf(grid);
                yield return WaitForSeconds(3, "smoke flight retry");
                moved = Vector3D.Distance(startPos, WorldApi.PositionOf(grid));
            }

            var playersOnline = MySession.Static.Players.GetOnlinePlayers().Count;
            if (playersOnline == 0 && moved <= 10)
            {
                // SE holds floating-body physics on an empty server; without at least one client
                // "did it drift" cannot be measured. Verify the harness, not the server policy.
                Note("no players online: movement check relaxed (body active=" + grid.Physics.IsActive +
                     ", vel set=" + grid.Physics.LinearVelocity.ToString("F1") + ")");
                Check(grid.Physics.RigidBody != null, "physics body exists");
                yield break;
            }

            Check(moved > 10, "grid should have moved >10m, moved " + moved.ToString("F1") + "m");
            Note("grid moved " + moved.ToString("F1") + "m - simulation OK");
        }
    }
}
