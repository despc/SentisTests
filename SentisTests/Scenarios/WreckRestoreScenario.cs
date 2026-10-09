using System;
using System.Collections;
using System.Linq;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisGameplayImprovements' fall-through restore, one grid at a time, on the stand world's wrecks deep inside the
    /// planet ("Wrecked Hauler ..."): with AutoRestoreFromVoxel on, the restore of all of them in one slice took the
    /// stand down in a Havok physics job (30.09.2026, Havok+0x6e082c, every start). Each grid goes to the log (flushed)
    /// before it is restored and the scenario waits two seconds after, so the last line before a crash names the grid.
    /// </summary>
    public sealed class WreckRestoreScenario : TestScenario
    {
        public const string ScenarioName = "wreck_restore";

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var plugin = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("SentisGameplayImprovements.SentisGameplayImprovementsPlugin")).FirstOrDefault(t => t != null);
            var detector = plugin?.Assembly.GetType("SentisGameplayImprovements.BackgroundActions.BackgroundActionsProcessor")
                ?.GetField("FallInVoxelDetector")?.GetValue(null);
            var restore = detector?.GetType().GetMethod("RestoreOnRequest");
            SkipUnless(restore != null, "SGI's FallInVoxelDetector.RestoreOnRequest not found");

            var wrecks = MyEntities.GetEntities().OfType<MyCubeGrid>()
                .Where(g => !g.MarkedForClose && g.DisplayName != null && g.DisplayName.StartsWith("Wrecked Hauler"))
                .OrderBy(g => g.EntityId).ToList();
            Check(wrecks.Count > 0, "no Wrecked Hauler grids in the world");
            foreach (var grid in wrecks)
            {
                var body = grid.Physics;
                var connectors = grid.GetFatBlocks().OfType<Sandbox.Game.Entities.Cube.MyShipConnector>().ToList();
                Note("RESTORE " + grid.DisplayName + " (" + grid.EntityId + "): " + grid.BlocksCount + " blocks, static " + grid.IsStatic +
                     ", body " + (body == null ? "none" : "enabled " + body.Enabled + ", in world " + body.IsInWorld + ", constraints " + ((MyPhysicsBody)body).Constraints.Count) +
                     ", connectors " + connectors.Count + (connectors.Count == 0 ? "" : " (own body " + string.Join("/", connectors.Select(c =>
                         c.Physics == null ? "none" : "rigid " + (c.Physics.RigidBody != null))) + ")") +
                     ", at " + grid.PositionComp.GetPosition());
                NLog.LogManager.Flush();
                var answer = restore.Invoke(detector, new object[] { grid }) as string;
                Note("  -> " + answer);
                NLog.LogManager.Flush();
                yield return WaitForTicks(120);
            }
            Note("WRECK RESTORE RESULT | " + wrecks.Count + " grids restored one by one, the server is up");
        }
    }
}
