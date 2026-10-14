using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A tool, not a test: removes every grid whose centre is more than <see cref="UnderM"/> under the lowest ground of
    /// the planet it is in - what fell through a planet and lies about its centre (test runs left hundreds on the stand,
    /// 09.10.2026). Says what it removed, by name.
    /// </summary>
    public sealed class PurgeUndergroundScenario : TestScenario
    {
        public const string ScenarioName = "purge_underground";
        private const double UnderM = 300;

        public override string Name => ScenarioName;

        public override IEnumerator Run()
        {
            var removed = 0;
            var blocks = 0;
            var names = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
            {
                if (grid.Closed || grid.MarkedForClose) continue;
                var at = grid.PositionComp.WorldAABB.Center;
                var planet = MyGamePruningStructure.GetClosestPlanet(at);
                if (planet == null) continue;
                var depth = planet.MinimumRadius - (at - planet.PositionComp.GetPosition()).Length();
                if (depth < UnderM) continue;
                var name = System.Text.RegularExpressions.Regex.Replace(grid.DisplayName ?? "?", @"_\d+$", "_N");
                names[name] = names.TryGetValue(name, out var n) ? n + 1 : 1;
                blocks += grid.BlocksCount;
                Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                grid.Close();
                removed++;
                if (removed % 20 == 0) yield return null;
            }
            Note("PURGE UNDERGROUND | removed " + removed + " grids (" + blocks + " blocks) more than " + UnderM + " m under the lowest ground: " +
                 string.Join(", ", names.OrderByDescending(p => p.Value).Select(p => p.Key + " x" + p.Value)));
            if (removed == 0) yield break;
            // saved at once: the stand is stopped without a save, and the next start loaded them all back (10.10.2026)
            var task = SentisTestsPlugin.TorchInstance.Save();
            Check(task != null, "Torch refused to start a save");
            while (!task.IsCompleted) yield return null;
            Note("PURGE UNDERGROUND | world saved: " + task.Result);
        }
    }
}
