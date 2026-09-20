using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The same question as <see cref="VoxelStreamScenario"/>, asked about a huge grid: does a client
    /// loading it stop the server?
    ///
    /// A grid is streamed the same way a planet is, and <c>MyCubeGridReplicable.Serialize</c> builds
    /// the grid's object builder <b>on the game thread</b> before handing the writing to a worker.
    /// For a ship of tens of thousands of blocks that is the expensive part, and vanilla repeats it
    /// for every client that comes into range.
    ///
    /// The scenario builds a grid of about <see cref="TargetBlocks"/> blocks, lets clients arrive
    /// from scratch, and measures the frames and what the streaming spent inside them. Measured:
    /// 27000 blocks cost 15 ms of one frame, once for both clients, because the plugin keeps the
    /// builder - nothing like the seconds a dug planet used to cost. What is left on a join is
    /// <c>repl.addForClient</c>, the server registering every replicable for the new client, and
    /// that is the same whether a big grid is there or not.
    /// </summary>
    public sealed class GridStreamScenario : TestScenario
    {
        public const string ScenarioName = "grid_stream";
        private const string Prefix = "gridstream-";
        private const int Side = 30;
        private const int TargetBlocks = Side * Side * Side;
        private const double OffsetM = 4000;
        private const double SettleSeconds = 10;
        private const double StreamSeconds = 30;
        private const int Players = 2;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private MyCubeGrid _grid;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());

            _grid = BuildGrid(anchorM.Translation + up * OffsetM, up);
            var settle = WaitForSeconds(SettleSeconds, "the grid settles");
            while (settle.MoveNext()) yield return settle.Current;

            var blocks = WorldApi.CountBlocks(_grid);
            Note("grid of " + blocks + " blocks spawned; streaming it is what a client loads");

            // One builder on the game thread, measured the way the streaming does it.
            yield return WaitForTicks(5);
            TickMetrics.Take();
            FrameProbe.Take();
            var build = TimeBuilder(_grid);
            yield return WaitForTicks(5);
            FrameProbe.Take();
            TickMetrics.Take();
            Note("building the object builder of this grid costs " + build.ToString("F0") + " ms on the game thread");

            // Now let clients arrive: fresh endpoints stream the grid from scratch.
            var beforeBuilders = RuntimePluginControls.TakeGridStreamBuilderStats();
            FakeClients.Add(Players, Network, p => (_grid.PositionComp.GetPosition() + up * (80 + 10 * p), 0, 0), withCharacters: true);

            var streaming = WaitForSeconds(StreamSeconds, "clients stream the grid");
            while (streaming.MoveNext()) yield return streaming.Current;

            var metrics = TickMetrics.Take();
            var probe = FrameProbe.Take();
            var builders = RuntimePluginControls.TakeGridStreamBuilderStats();
            var worst = WorstFrameMs(metrics.Format());
            var builderMs = Ms(probe, "grid.getObjectBuilder");
            var builderCalls = Calls(probe, "grid.getObjectBuilder");

            Note("GRID STREAM RESULT | " + blocks + " blocks | one builder costs " + build.ToString("F0") +
                 " ms | while " + Players + " clients streamed it: worst frame " + worst.ToString("F0") +
                 " ms, grid.getObjectBuilder " + builderMs + " ms in " + builderCalls + " calls | " + builders +
                 " | " + metrics.Format() + " | " + probe);

            Check(blocks >= TargetBlocks / 2, "the grid is far smaller than asked for: " + blocks + " blocks");
            Check(build > 0, "the object builder was never built");

            // The heavy half of streaming a grid - turning the builder into bytes - is on a worker in
            // vanilla already, and the builder itself is cached per grid, so the game thread should
            // build it once however many clients arrive.
            Check(builderCalls <= Players,
                "the object builder was built " + builderCalls + " times for " + Players +
                " clients: the builder cache is not holding");
            Check(builderMs < 3 * build + 10,
                "grid.getObjectBuilder took " + builderMs + " ms of frames while one build costs " +
                build.ToString("F0") + " ms: the grid is being rebuilt per client");
        }

        /// <summary>Armour cube: the cheapest way to a grid with a great many blocks.</summary>
        private MyCubeGrid BuildGrid(Vector3D position, Vector3D up)
        {
            var blocks = new List<BlockSpec>();
            for (var x = 0; x < Side; x++)
            for (var y = 0; y < Side; y++)
            for (var z = 0; z < Side; z++)
                blocks.Add(new BlockSpec("LargeBlockArmorBlock", new Vector3I(x, y, z)));

            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "ship", MyCubeSize.Large, true, position, blocks,
                Vector3.Forward, (Vector3)up);
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        private static double TimeBuilder(MyCubeGrid grid)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            grid.GetObjectBuilder();
            return (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static double WorstFrameMs(string metrics)
        {
            var m = System.Text.RegularExpressions.Regex.Match(metrics, @"max=([\d.]+)ms");
            return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }

        private static int Calls(string probe, string section)
        {
            var m = System.Text.RegularExpressions.Regex.Match(probe,
                System.Text.RegularExpressions.Regex.Escape(section) + @"=\d+ms/(\d+)calls");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        private static int Ms(string probe, string section)
        {
            var m = System.Text.RegularExpressions.Regex.Match(probe,
                System.Text.RegularExpressions.Regex.Escape(section) + @"=(\d+)ms/");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!(grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                    grid.Close();
                }
            }
            finally { base.CleanupLeftovers(); }
        }
    }
}
