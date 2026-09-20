using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The freeze a player causes by connecting next to a planet that has been dug out.
    ///
    /// The storage of a planet is streamed to a connecting client as one compressed blob, and the
    /// blob is built by <c>MyStorageBase.Save</c> on the game thread. The game caches it - and
    /// throws the cache away on <b>every</b> change of the storage, so on a planet people are
    /// digging, the blob is almost always cold, and the first client to ask for it stops the server
    /// for as long as it takes to serialize and compress the whole thing.
    ///
    /// The scenario digs <see cref="Holes"/> craters into the planet, which is what players do over
    /// a few months, and then measures what the next client to connect would pay:
    /// <list type="bullet">
    /// <item><b>cold</b> - right after digging, the blob has to be built;</item>
    /// <item><b>warm</b> - the same call once the blob exists.</item>
    /// </list>
    /// Then it digs again and waits to see whether anything rebuilds the blob in the background
    /// before a client needs it, which is what the fix has to do.
    /// </summary>
    public sealed class VoxelStreamScenario : TestScenario
    {
        public const string ScenarioName = "voxel_stream";
        private const string Prefix = "vox-";
        private const int Holes = 120;
        private const double HoleRadiusM = 14;
        private const double HoleStepM = 45;
        private const double SettleSeconds = 5;
        private const double StreamSeconds = 25;
        private const int Players = 2;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            Check(planet.Storage != null, "the planet has no storage");

            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var east = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));

            Note("planet " + planet.StorageName + ", storage " + planet.Storage.Size + ", blob now " +
                 Mb(BlobSize(planet)) + " MB");

            // ---------------------------------------------------------------- dig
            var dug = 0;
            for (var i = 0; i < Holes; i++)
            {
                var at = planet.PositionComp.GetPosition() +
                         Vector3D.Normalize(up + east * (i * HoleStepM / 6000.0)) *
                         SurfaceDistance(planet, up, east, i);
                if (Dig(planet, at, HoleRadiusM)) dug++;
                if (i % 10 == 0) yield return WaitForTicks(1);
            }

            var settle = WaitForSeconds(SettleSeconds, "voxels settle");
            while (settle.MoveNext()) yield return settle.Current;
            Note(dug + " craters of " + HoleRadiusM + " m dug into the planet");

            // ------------------------------------------------- what building the blob costs at all
            TickMetrics.Take();
            var cold = TimeSave(planet);
            var warm = TimeSave(planet);
            var blob = Mb(BlobSize(planet));
            Note("building the blob costs " + cold.ToString("F0") + " ms for " + blob +
                 " MB (warm: " + warm.ToString("F1") + " ms). That is what a connecting client used to pay " +
                 "inside a frame.");

            // ------------------------------------------------- and what a connecting client costs now
            // Dig once more so the blob is cold again, then let clients arrive: fresh endpoints stream
            // the planet from scratch, which is exactly the join the players complain about.
            Dig(planet, anchorM.Translation + up * 2, HoleRadiusM);

            // Let the frame that holds the measurement above close first: it contains a deliberate
            // build on the game thread, and it would otherwise be counted in the window below.
            yield return WaitForTicks(5);

            var before = RuntimePluginControls.VoxelStreamWork();
            TickMetrics.Take();
            FrameProbe.Take();
            FakeClients.Add(Players, Network, p => (anchorM.Translation + up * (50 + 10 * p), 0, 0), withCharacters: true);

            var streaming = WaitForSeconds(StreamSeconds, "clients stream the planet");
            while (streaming.MoveNext()) yield return streaming.Current;

            var metrics = TickMetrics.Take();
            var probe = FrameProbe.Take();
            var after = RuntimePluginControls.VoxelStreamWork();
            var offloaded = after.Offloaded - before.Offloaded;
            var offloadedMs = after.OffloadedMs - before.OffloadedMs;
            var rebuilt = after.Rebuilt - before.Rebuilt;
            var inline = after.OnGameThread - before.OnGameThread;
            var worst = WorstFrameMs(metrics.Format());

            Note("VOXEL STREAM RESULT | blob " + blob + " MB | building it costs " + cold.ToString("F0") +
                 " ms | while " + Players + " clients streamed the planet: worst frame " + worst.ToString("F0") +
                 " ms, " + offloaded + " blobs compressed off the game thread (" + offloadedMs + " ms), " +
                 rebuilt + " rebuilt by the background cache, " + inline + " still built inside a frame | " + metrics.Format() + " | " + probe);

            Check(cold > 0, "the storage was never serialized");
            Check(offloaded + rebuilt > 0,
                "nothing built the blob off the game thread: the client either got no voxels or the game thread paid");
            Check(inline == 0,
                inline + " blobs were built inside a frame: the compression is still on the game thread");
            Check(worst < cold / 2,
                "a frame of " + worst.ToString("F0") + " ms while clients streamed a blob that takes " +
                cold.ToString("F0") + " ms to build: the work is still on the game thread");
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Times what <c>MyVoxelReplicable.Serialize</c> does on the game thread.</summary>
        private static double TimeSave(MyPlanet planet)
        {
            var started = Stopwatch.GetTimestamp();
            planet.Storage.Save(out _);
            return (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        }

        private static int BlobSize(MyPlanet planet)
        {
            planet.Storage.Save(out var data);
            return data?.Length ?? 0;
        }

        private static string Mb(int bytes) => (bytes / 1024.0 / 1024.0).ToString("F1");

        /// <summary>Whether the game is holding a compressed blob for this storage right now.</summary>
        private static bool AreDataCached(MyPlanet planet)
        {
            var property = planet.Storage.GetType().GetProperty("AreDataCached");
            return property != null && (bool)property.GetValue(planet.Storage);
        }

        private static double SurfaceDistance(MyPlanet planet, Vector3D up, Vector3D east, int index)
        {
            var direction = Vector3D.Normalize(up + east * (index * HoleStepM / 6000.0));
            var surface = planet.GetClosestSurfacePointGlobal(planet.PositionComp.GetPosition() + direction * planet.AverageRadius);
            return (surface - planet.PositionComp.GetPosition()).Length() - 4;
        }

        private bool Dig(MyPlanet planet, Vector3D at, double radius)
        {
            try
            {
                var shape = new MyShapeSphere { Center = at, Radius = (float)radius };
                MyVoxelGenerator.CutOutShapeWithProperties(planet, shape, out var cut, out _, null, updateSync: true);
                return cut > 0;
            }
            catch (Exception e)
            {
                Note("dig failed: " + e.Message);
                return false;
            }
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

        /// <summary>The longest frame the tick metrics saw, in milliseconds.</summary>
        private static double WorstFrameMs(string metrics)
        {
            var m = System.Text.RegularExpressions.Regex.Match(metrics, @"max=([\d.]+)ms");
            return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }

        private static string Summary(string probe)
        {
            string Pick(string name)
            {
                var m = System.Text.RegularExpressions.Regex.Match(probe,
                    System.Text.RegularExpressions.Regex.Escape(name) + @"=(\d+)ms/(\d+)calls");
                return m.Success ? m.Groups[1].Value + " ms/" + m.Groups[2].Value + " calls" : "-";
            }

            return "voxel.save " + Pick("voxel.save") + ", voxel.streamSerialize " + Pick("voxel.streamSerialize") +
                   ", repl.sendStreamingEntry " + Pick("repl.sendStreamingEntry");
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
