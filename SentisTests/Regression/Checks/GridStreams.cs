using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A player arriving among grids is streamed them nearest first, no more than two being loaded on its client at once
    /// (SO GridStreamPacing): ten grids of one block around the spot, 40 m to 2.2 km off, made in no order of distance;
    /// a fake player arrives at the spot. The grids' streams are started in the order of their distance (few pairs out of
    /// it) and the client never has more than <see cref="InFlight"/> of them not ready.
    ///
    /// The same then for a second player arriving with the game's own way (no order, no limit), for the comparison in the
    /// result: the order of chance, and as many loading at once as the network brings.
    /// </summary>
    public sealed class GridStreamOrderCheck : RegressionCheck
    {
        public override string Name => Prefix + "grid_stream_order";

        private const int InFlight = 2;
        private static readonly double[] Distances = { 40, 2200, 300, 1500, 90, 900, 600, 1800, 150, 1200 };

        private readonly Scenarios.ConfigOverride _config = new Scenarios.ConfigOverride();
        private FieldInfo _started;

        private sealed class Run
        {
            public readonly List<long> Order = new List<long>();
            public int MostLoading;
        }

        protected override IEnumerator Check()
        {
            Type pacing = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                pacing = pacing ?? assembly.GetType("Optimizer.Optimizations.GridStreamPacing", false);
            if (pacing == null) throw new Core.ScenarioSkippedException("SentisOptimisations' grid stream pacing is not loaded");
            _started = pacing.GetField("StreamStarted", BindingFlags.Public | BindingFlags.Static);
            var inFlightOf = pacing.GetMethod("InFlightOf", BindingFlags.Public | BindingFlags.Static);
            Check(_started != null && inFlightOf != null, "the grid stream pacing's test API is not what this check knows");

            // round the spot every way, the near and the far mixed
            var grids = new Dictionary<long, double>();
            var made = new List<MyCubeGrid>();
            var ways = new[] { Site.Forward, -Site.Forward, Site.Up, -Site.Up, Vector3D.Cross(Site.Forward, Site.Up), -Vector3D.Cross(Site.Forward, Site.Up) };
            for (var i = 0; i < Distances.Length; i++)
            {
                var grid = Build("stream-" + i, Site.Origin + ways[i % ways.Length] * Distances[i], true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 1, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f),
                    new Part("CubeBlock/LargeBlockArmorBlock", 0, 2, 0));
                grids[grid.EntityId] = Distances[i];
                made.Add(grid);
            }
            yield return Seconds(3);
            Check(made.All(g => !g.Closed && g.BlocksCount == 3), "the grids did not stay: " + string.Join(", ", made.Select(g => g.DisplayName + " " + (g.Closed ? "closed" : g.BlocksCount + " blocks"))));

            var runs = new Dictionary<string, Run>();
            foreach (var paced in new[] { true, false })
            {
                _config.Set("GridStreamNearestFirst", paced);
                _config.Set("GridStreamsInFlight", paced ? InFlight : 0);
                var run = runs[paced ? "paced" : "vanilla"] = new Run();
                // every start kept, the player's picked out once its Steam id is known (its first come during the join)
                var events = new List<(ulong Steam, long Grid)>();
                Action<ulong, long> started = (steam, grid) => { if (grids.ContainsKey(grid)) events.Add((steam, grid)); };
                _started.SetValue(null, started);
                var index = -1;
                if (paced)
                {
                    yield return Arrive(Site.Origin);
                    index = Me;
                }
                else
                    yield return Join(Site.Origin, i => index = i);
                var steamId = FakeClients.PlayerOf(index).Id.SteamId;
                void Pick()
                {
                    foreach (var e in events)
                        if (e.Steam == steamId && !run.Order.Contains(e.Grid)) run.Order.Add(e.Grid);
                }
                for (var f = Frames(60); f > 0 && (run.Order.Count < grids.Count || (int)inFlightOf.Invoke(null, new object[] { steamId }) > 0); f--)
                {
                    Pick();
                    run.MostLoading = Math.Max(run.MostLoading, (int)inFlightOf.Invoke(null, new object[] { steamId }));
                    yield return null;
                }
                _started.SetValue(null, null);
                Pick();
                Note((paced ? "paced" : "vanilla") + ": " + run.Order.Count + " of " + grids.Count + " streamed, in the order " +
                     string.Join(", ", run.Order.Select(g => grids[g].ToString("0"))) + " m; at most " + run.MostLoading + " loading at once; on the client: " + FakeClients.Arrivals(made));
            }

            int OutOfOrder(Run r)
            {
                var n = 0;
                for (var i = 0; i < r.Order.Count; i++)
                    for (var j = i + 1; j < r.Order.Count; j++)
                        if (grids[r.Order[i]] > grids[r.Order[j]] + 100) n++;
                return n;
            }
            var pairs = grids.Count * (grids.Count - 1) / 2;
            var p = runs["paced"];
            var v = runs["vanilla"];
            Check(p.Order.Count == grids.Count, $"paced: {p.Order.Count} of {grids.Count} grids streamed to the player in 60 s");
            Check(p.MostLoading <= InFlight, $"paced: {p.MostLoading} grids loading on the client at once ({InFlight} at most)");
            Check(OutOfOrder(p) <= pairs / 10, $"paced: {OutOfOrder(p)} of {pairs} pairs of grids streamed farther first");
            Result($"paced: nearest first ({OutOfOrder(p)} of {pairs} pairs out of order), at most {p.MostLoading} loading at once | " +
                   $"vanilla: {OutOfOrder(v)} of {pairs} pairs out of order, at most {v.MostLoading} loading at once, {v.Order.Count} streamed");
        }

        public override void Cleanup()
        {
            try { _started?.SetValue(null, null); }
            catch (Exception) { }
            try { _config.Restore(); }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
