using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using RecastDetour;
using Sandbox.Game;
using SentisTests.Core;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Animal navmesh tiles built on several threads at once, as several <c>MyNavmeshManager</c>s (each planet sector with
    /// animals has one) build theirs on ParallelTasks workers. The prod's crash of 25.11.2026: <c>STATUS_HEAP_CORRUPTION</c>
    /// in <c>RecastDetour.dll</c> under <c>MyNavmeshManager.GenerateTile</c>. <c>RecastDetour_CreateNavmeshTile</c> keeps the
    /// build's <c>rcConfig</c> (the agent's radius and height, the border, the width of the tile's grid...) in one static of
    /// the dll and reads it back all through the build: two builds at once with different agents (a wolf, a spider) and
    /// one fills its arrays by the other's width.
    ///
    /// <see cref="Threads"/> threads, each with its own <c>MyRDWrapper</c>, build the same tile of the same ground for
    /// <see cref="RunSeconds"/> s; every other thread has the other agent. First each agent's tile is built alone - what it
    /// must come to (the polygons, the tile's data size); a build that comes to something else read another's config.
    /// Through <c>MyRDWrapper.CreateNavmeshTile</c> (SO RecastBuildLock serializes it): passes when every build matches
    /// and the lock saw the threads wait for each other. Focus <c>raw</c>: the dll's function called directly, past the
    /// lock - what the game does without SO; expected to give wrong tiles or to bring the server down (not in the runs of
    /// all scenarios).
    /// </summary>
    public sealed class RecastRaceScenario : TestScenario
    {
        public const string ScenarioName = "recast_race";
        private const int Threads = 4;
        private const int RunSeconds = 20;
        private const int StallSeconds = 10;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 90;

        private delegate bool RawBuild(IntPtr instance, Vector3D pos, MyRecastOptions options, int tx, int ty, int layer, IntPtr vertices, int vertexCount, IntPtr triangles, int triangleCount);
        private delegate int RawCount(IntPtr instance, Vector3D pos, int layer);

        private static MyRecastOptions Agent(int kind) => new MyRecastOptions
        {
            // the game's GetRecastOptions; a wolf's size, then a big agent's
            cellHeight = MyPlatformGameSettings.NAVMESH_CELL_HEIGHT,
            agentHeight = kind == 0 ? 1.0f : 2.6f,
            agentRadius = kind == 0 ? 0.5f : 1.6f,
            agentMaxClimb = kind == 0 ? 0.6f : 1.2f,
            agentMaxSlope = 85f,
            regionMinSize = 1f,
            regionMergeSize = 10f,
            edgeMaxLen = 50f,
            edgeMaxError = 3f,
            vertsPerPoly = 6f,
            detailSampleDist = 6f,
            detailSampleMaxError = 0.2f / MyPlatformGameSettings.NAVMESH_CELL_HEIGHT,
            partitionType = 1,
        };

        /// <summary>Rolling ground over the tile and its border, with a step and a raised block to make regions.</summary>
        private static (Vector3[] Vertices, int[] Triangles) Ground()
        {
            const int half = 13, n = half * 2;
            var v = new Vector3[(n + 1) * (n + 1)];
            for (var i = 0; i <= n; i++)
            for (var j = 0; j <= n; j++)
            {
                float x = i - half, z = j - half;
                var y = 0.8f * (float)(Math.Sin(x * 0.45) * Math.Cos(z * 0.35));
                if (Math.Abs(x - 3) < 2.5 && Math.Abs(z + 2) < 2.5) y += 2.2f;
                if (x < -5 && z > 4) y += 1.2f;
                v[i * (n + 1) + j] = new Vector3(x, y, z);
            }
            var t = new List<int>();
            for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
            {
                int a = i * (n + 1) + j, b = a + 1, c = a + n + 1, d = c + 1;
                t.AddRange(new[] { a, b, c, b, d, c });
            }
            return (v, t.ToArray());
        }

        private static Type LockType() => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => { try { return a.GetType("SentisOptimisationsPlugin.CrashFix.RecastBuildLock"); } catch { return null; } })
            .FirstOrDefault(t => t != null);

        private static double Stat(Type type, string name) =>
            type?.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is var v && v != null ? Convert.ToDouble(v) : -1;

        public override IEnumerator Run()
        {
            var raw = (TestRunner.Focus ?? "").Contains("raw");
            var native = typeof(MyRDWrapper);
            const BindingFlags any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var handle = native.GetField("m_recastDetour", any);
            var rawBuild = (RawBuild)Delegate.CreateDelegate(typeof(RawBuild), native.GetMethod("RecastDetour_CreateNavmeshTile", any));
            var rawCount = (RawCount)Delegate.CreateDelegate(typeof(RawCount), native.GetMethod("RecastDetour_GetTilePolygonCount", any));
            Check(handle != null, "MyRDWrapper.m_recastDetour not found");

            var (vertices, triangles) = Ground();
            var pinV = GCHandle.Alloc(vertices, GCHandleType.Pinned);
            var pinT = GCHandle.Alloc(triangles, GCHandleType.Pinned);
            var cell = MyPlatformGameSettings.NAVMESH_CELL_SIZE;
            // a manager of 3 x 3 tiles of 16 m, 70 m high (the game's default); the tile in the middle, centred on 0
            var bMin = new[] { -24f, -35f, -24f };
            var bMax = new[] { 24f, 35f, 24f };

            (bool Ok, int Polygons, int Size) Build(MyRDWrapper wrapper, MyRecastOptions options)
            {
                bool ok;
                int polygons;
                if (raw)
                {
                    var instance = (IntPtr)handle.GetValue(wrapper);
                    ok = rawBuild(instance, Vector3D.Zero, options, 1, 1, 0, pinV.AddrOfPinnedObject(), vertices.Length, pinT.AddrOfPinnedObject(), triangles.Length / 3);
                    polygons = ok ? rawCount(instance, Vector3D.Zero, 0) : 0;
                }
                else
                {
                    var list = new List<MyRecastDetourPolygon>();
                    unsafe
                    {
                        ok = wrapper.CreateNavmeshTile(Vector3D.Zero, ref options, list, 1, 1, 0, (float*)pinV.AddrOfPinnedObject(), vertices.Length,
                            (int*)pinT.AddrOfPinnedObject(), triangles.Length / 3);
                    }
                    polygons = list.Count;
                }
                return (ok, polygons, ok ? wrapper.GetTileDataSize(Vector3D.Zero, 0) : 0);
            }

            MyRDWrapper Make()
            {
                var w = new MyRDWrapper();
                w.Init(cell, 16, bMin, bMax);
                return w;
            }

            // what each agent's tile comes to alone
            var expected = new (bool Ok, int Polygons, int Size)[2];
            var alone = new double[2];
            for (var kind = 0; kind < 2; kind++)
            {
                var w = Make();
                var watch = Stopwatch.StartNew();
                expected[kind] = Build(w, Agent(kind));
                alone[kind] = watch.Elapsed.TotalMilliseconds;
                Check(Build(w, Agent(kind)) == expected[kind], $"agent {kind}: the same tile built twice alone came out different");
                w.Clear();
            }
            Note($"RECAST | {(raw ? "raw: the dll's function past the lock" : "through MyRDWrapper.CreateNavmeshTile")}; cell {cell} m. Alone: " +
                 $"wolf {expected[0].Polygons} polygons, {expected[0].Size} B, {alone[0]:0.0} ms; big agent {expected[1].Polygons} polygons, {expected[1].Size} B, {alone[1]:0.0} ms");
            Check(expected[0].Ok && expected[1].Ok && expected[0].Polygons > 0 && expected[1].Polygons > 0, "a tile alone did not build");
            Check(expected[0] != expected[1], "the two agents' tiles are alike: a race would not show");

            var gate = LockType();
            double builds0 = Stat(gate, "Builds"), waited0 = Stat(gate, "Waited");
            var stop = false;
            long builds = 0, wrong = 0;
            string firstWrong = null;
            Exception thrown = null;
            var wrappers = Enumerable.Range(0, Threads).Select(_ => Make()).ToArray();
            var threads = Enumerable.Range(0, Threads).Select(i => new Thread(() =>
            {
                try
                {
                    var kind = i % 2;
                    var options = Agent(kind);
                    while (!Volatile.Read(ref stop))
                    {
                        var got = Build(wrappers[i], options);
                        Interlocked.Increment(ref builds);
                        if (got != expected[kind] && Interlocked.Increment(ref wrong) == 1)
                            firstWrong = $"agent {kind}: {got.Polygons} polygons, {got.Size} B, built {got.Ok} (alone {expected[kind].Polygons}, {expected[kind].Size} B)";
                    }
                }
                catch (Exception e) { thrown = thrown ?? e; }
            }) { IsBackground = true, Name = "recast_race " + i }).ToArray();
            foreach (var t in threads) t.Start();

            var run = Stopwatch.StartNew();
            long last = -1;
            var movedAt = 0.0;
            string stalled = null;
            while (run.Elapsed.TotalSeconds < RunSeconds && thrown == null && stalled == null)
            {
                yield return null;
                var b = Interlocked.Read(ref builds);
                if (b != last) { last = b; movedAt = run.Elapsed.TotalSeconds; }
                if (run.Elapsed.TotalSeconds - movedAt > StallSeconds) stalled = $"no tile built for {StallSeconds} s";
            }
            Volatile.Write(ref stop, true);
            var joined = threads.All(t => t.Join(15000));
            if (joined) foreach (var w in wrappers) w.Clear();
            pinV.Free();
            pinT.Free();

            var waited = Stat(gate, "Waited") - waited0;
            Note($"RECAST | {run.Elapsed.TotalSeconds:0.0} s, {Threads} threads: {builds} tiles, {wrong} unlike the tile built alone" +
                 (firstWrong != null ? $" (first: {firstWrong})" : "") +
                 (gate == null ? "; SO RecastBuildLock not loaded" : $"; RecastBuildLock: {Stat(gate, "Builds") - builds0:0} builds, {waited:0} waited for another, longest wait {Stat(gate, "LongestWaitMs"):0.0} ms") +
                 (stalled != null ? "; " + stalled : "") + (thrown != null ? $"; {thrown.GetType().Name}: {thrown.Message}" : ""));
            Check(thrown == null, $"{thrown?.GetType().Name}: {thrown?.Message}");
            Check(stalled == null && joined, stalled ?? "a thread did not stop");
            Check(builds > Threads * 10, $"only {builds} tiles built");
            Check(wrong == 0, $"{wrong} of {builds} tiles unlike the tile built alone (the dll's static config overwritten by another build): {firstWrong}");
            if (!raw)
            {
                Check(gate != null, "SO RecastBuildLock is not in this server");
                Check(waited > 0, "the builds never met at the lock: the threads did not overlap, nothing was checked");
            }
        }
    }
}
