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

    /// <summary>
    /// A grid stream kept from a client's previous connection (production, 13.11.2026: SO GridStreamPacing threw
    /// IndexOutOfRangeException in the game's FastPriorityQueue.Contains on every stream of the client - the entry's queue
    /// index past the new connection's queue). An entry with the index 1000000 (the new queue holds 1024) put among the
    /// player's waiting streams, the player brought among twelve grids: no error of the pacing, the stale entry dropped, all
    /// twelve grids streamed within 60 s.
    /// </summary>
    public sealed class GridStreamStaleEntryCheck : RegressionCheck
    {
        public override string Name => Prefix + "grid_stream_stale_entry";

        private readonly Scenarios.ConfigOverride _config = new Scenarios.ConfigOverride();

        private static int Errors(string log, long from)
        {
            using (var stream = new System.IO.FileStream(log, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
            {
                stream.Seek(from, System.IO.SeekOrigin.Begin);
                var text = new System.IO.StreamReader(stream).ReadToEnd();
                return System.Text.RegularExpressions.Regex.Matches(text, @"GridStreamPacing \((stream|schedule)\)").Count;
            }
        }

        protected override IEnumerator Check()
        {
            Type pacing = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                pacing = pacing ?? assembly.GetType("Optimizer.Optimizations.GridStreamPacing", false);
            if (pacing == null) throw new Core.ScenarioSkippedException("SentisOptimisations' grid stream pacing is not loaded");
            var waitingField = pacing.GetField("Waiting", BindingFlags.NonPublic | BindingFlags.Static);
            var flights = pacing.GetField("Flights", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            Check(waitingField != null && flights != null, "the grid stream pacing is not what this check knows");
            _config.Set("GridStreamNearestFirst", true);
            _config.Set("GridStreamsInFlight", 2);

            var made = new List<MyCubeGrid>();
            var ways = new[] { Site.Forward, -Site.Forward, Site.Up, -Site.Up, Vector3D.Cross(Site.Forward, Site.Up), -Vector3D.Cross(Site.Forward, Site.Up) };
            for (var i = 0; i < 12; i++)
                // powered: the stand's trash removal takes grids of a few blocks with no power far from every player, and the
                // player waits 20 km off first
                made.Add(Build("stale-stream-" + i, Site.Origin + ways[i % ways.Length] * (60 + 40 * i), true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 1, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f)));
            yield return Seconds(2);

            // the player far off first: none of the grids streamed to it yet
            var index = -1;
            yield return Join(Site.Origin + Site.Up * 20000, i => index = i);
            yield return Seconds(2);
            // a live replicable for the stale entry's owner (the old connection's grid would do as well): the pacing asks only that it be valid
            var replicable = Sandbox.Game.Replication.MyExternalReplicable.FindByObject(made[0]) ??
                             Sandbox.Game.Replication.MyExternalReplicable.FindByObject(FakeClients.Character(index));
            Check(replicable != null, "no replicable of the first grid nor of the player's character");
            var stale = new VRage.Network.MyStateDataEntry(replicable, default(VRage.Network.NetworkId), null);
            var queueIndex = typeof(VRage.Network.MyStateDataEntry).BaseType.GetField("QueueIndex", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            queueIndex.SetValue(stale, 1000000);
            var waiting = (IDictionary)waitingField.GetValue(null);
            var ours = new HashSet<object>(made.Select(g => (object)Sandbox.Game.Replication.MyExternalReplicable.FindByObject(g)).Where(r => r != null));
            HashSet<VRage.Network.MyStateDataEntry> Ours()
            {
                lock (flights)
                    foreach (DictionaryEntry pair in waiting)
                        if (pair.Value is HashSet<VRage.Network.MyStateDataEntry> set && set.Any(e => e != stale && (ours.Contains(e.Owner) || made.Any(g => e.Owner?.GetType().GetProperty("Instance")?.GetValue(e.Owner) == g))))
                            return set;
                return null;
            }

            var folder = System.IO.Path.Combine(Environment.CurrentDirectory, "Logs");
            var log = System.IO.Directory.GetFiles(folder, "Torch-*.log").OrderByDescending(f => f).First();
            var from = new System.IO.FileInfo(log).Length;

            // among the grids: their streams queue up and start, the pacing looks for the nearest waiting one; the stale entry
            // put among the player's waiting streams as soon as they are there
            FakeClients.MoveTo(index, Site.Origin);
            HashSet<VRage.Network.MyStateDataEntry> theirs = null;
            for (var f = Frames(20); f > 0 && (theirs = Ours()) == null; f--) yield return null;
            if (theirs == null)
                lock (flights)
                    Note("the waiting streams: " + string.Join("; ", waiting.Values.Cast<HashSet<VRage.Network.MyStateDataEntry>>()
                        .Select(set => set.Count + " [" + string.Join(",", set.Take(4).Select(e => e.Owner?.GetType().Name + " " + (e.Owner?.GetType().GetProperty("Instance")?.GetValue(e.Owner) as VRage.Game.Entity.MyEntity)?.DisplayName)) + "]")));
            Check(theirs != null, "no stream of the grids waited for the player");
            lock (flights) theirs.Add(stale);
            int Arrived() => made.Count(g => FakeClients.ArrivedFor(index, g));
            for (var f = Frames(60); f > 0 && Arrived() < made.Count; f--) yield return null;
            yield return Seconds(1);
            var errors = Errors(log, from);
            var arrived = Arrived();
            bool kept;
            lock (flights) kept = theirs.Contains(stale);
            object Count(string name) => pacing.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            Note("errors of the pacing " + errors + ", the stale entry " + (kept ? "kept" : "dropped") + ", grids streamed " + arrived + " of " + made.Count +
                 "; pacing counts: started " + Count("Started") + ", swapped " + Count("Swapped") + ", held " + Count("Held"));
            Check(errors == 0, errors + " errors of GridStreamPacing with a stale entry among the waiting streams");
            Check(!kept, "the stale entry was not dropped from the waiting streams");
            Check(made.All(g => !g.Closed), "grids of the check were removed: " + string.Join(", ", made.Where(g => g.Closed).Select(g => g.DisplayName)));
            Check(arrived == made.Count, arrived + " of " + made.Count + " grids streamed to the player in 60 s");
            Result("a stale entry (index 1000000) among the waiting streams: dropped, no error of the pacing; all " + made.Count + " grids streamed");
        }

        public override void Cleanup()
        {
            try { _config.Restore(); }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
