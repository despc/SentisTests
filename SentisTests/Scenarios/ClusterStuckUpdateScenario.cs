using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisClusters' StuckUpdates: an entity's update that hangs for 10 s is written to the log with the full stacks of
    /// its thread, of the other threads of the clusters updating something, and of the game thread; nothing is stopped.
    ///
    /// Four test sites 8 km apart (each its own physics cluster, a fake player at each): 8 landing gears on the first,
    /// 40 on each of the others - the after/10 loop is shared among the clusters' threads, and the first site, the
    /// lightest, is not the game thread's (it takes the heaviest group). The update of one gear hangs for 14 s, three
    /// times, 35 s apart (one report in 30 s):
    ///  1. a loop that never yields (a spin), on a thread of the clusters;
    ///  2. a wait for a lock another thread holds (one side of a deadlock), on a thread of the clusters;
    ///  3. a spin on whatever thread updates the gear first (the game thread's too).
    /// Checks for each: the log names the gear 10-12 s into the hang, gives its thread's stack with the hanging method in
    /// it, and the game thread's (or says it is the stuck one); the hang lasted its 14 s; the frames go on after it.
    /// </summary>
    public sealed class ClusterStuckUpdateScenario : TestScenario
    {
        public const string ScenarioName = "cluster_stuck_update";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 600;
        public override string[] Requires => new[] { "SentisClusters" };

        private const string HarmonyId = "SentisTests.ClusterStuckUpdate";
        private const int Sites = 4, Gears = 40, FirstGears = 8;
        private const double HangSeconds = 14;

        private enum Mode { None, Spin, Lock, SpinAnywhere }

        private static volatile MyLandingGear _target;
        private static volatile int _mode;
        private static readonly object Held = new object();
        private static long _calls;
        private static volatile string _hungOn;
        private static double _hungSeconds = -1;
        private static DateTime _hungAt;
        private static volatile bool _hanging;

        private Harmony _harmony;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var stuck = Integrations.TypeOf("SentisClusters", "SentisClusters.Run.StuckUpdates");
            Check(stuck != null, "SentisClusters' StuckUpdates not found: a build without it");
            var toldField = stuck.GetField("Told", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Check(toldField != null, "StuckUpdates.Told is not there");
            long Told() => (long)toldField.GetValue(null);
            // the clusters take the loops only once the world has warmed up after the start
            var active = Integrations.TypeOf("SentisClusters", "SentisClusters.Run.ClusterRunner")?.GetProperty("Active", BindingFlags.Static | BindingFlags.Public);
            Check(active != null, "ClusterRunner.Active is not there");
            yield return Wait(() => (bool)active.GetValue(null), "SentisClusters takes the update loops (the world warmed up)", 900);

            var owner = WorldApi.TestIdentityId();
            var origin = TestRunner.RunOrigin ?? new Vector3D(-40000, 25000, 70000);
            var gears = new List<List<MyLandingGear>>();
            var battery = WorldApi.FindSubtype(MyCubeSize.Large, "batteryblock");
            FakeClients.RemoveAll();
            yield return WaitForTicks(10);
            for (var s = 0; s < Sites; s++)
            {
                var at = origin + new Vector3D(8000 * s, 0, 0);
                var specs = new List<BlockSpec> { new BlockSpec(battery, new Vector3I(0, 0, 0)) };
                for (var g = 0; g < (s == 0 ? FirstGears : Gears); g++) specs.Add(new BlockSpec("LargeBlockLandingGear", new Vector3I(4 + g % 10 * 4, 0, g / 10 * 4)));
                var ob = WorldApi.GridOb(WorldApi.EntityPrefix + "stuck-update-" + s, MyCubeSize.Large, false, at, specs);
                foreach (var block in ob.CubeBlocks) block.Owner = owner;
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                gears.Add(grid.GetFatBlocks().OfType<MyLandingGear>().ToList());
            }
            FakeClients.Add(Sites, new FakeClients.NetworkProfile { RttMs = 50 }, i => (origin + new Vector3D(8000 * i, 20, 0), 0, 0), withCharacters: true);
            Check(gears[0].Count == FirstGears && gears.Skip(1).All(g => g.Count == Gears), $"the test sites do not have their landing gears: {string.Join(", ", gears.Select(g => g.Count))}");

            _harmony = new Harmony(HarmonyId);
            var update = typeof(MyLandingGear).GetMethod(nameof(MyLandingGear.UpdateAfterSimulation10), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            Check(update != null, "MyLandingGear.UpdateAfterSimulation10 is not there");
            _harmony.Patch(update, prefix: new HarmonyMethod(typeof(ClusterStuckUpdateScenario).GetMethod(nameof(GearPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            yield return WaitForSeconds(3, "the sites settle into their clusters");

            var modes = new[] { Mode.Spin, Mode.Lock, Mode.SpinAnywhere };
            for (var round = 0; round < modes.Length; round++)
            {
                var mode = modes[round];
                if (round > 0) yield return WaitForSeconds(35, "one report in 30 s: the next hang after it");
                var gear = mode == Mode.SpinAnywhere ? gears[1][0] : gears[0][round];
                Thread holder = null;
                if (mode == Mode.Lock)
                {
                    // another thread holds the lock all through the hang
                    var taken = new ManualResetEventSlim();
                    holder = new Thread(() =>
                    {
                        lock (Held)
                        {
                            taken.Set();
                            Thread.Sleep(TimeSpan.FromSeconds(HangSeconds + 20));
                        }
                    }) { IsBackground = true, Name = "ST stuck update lock holder" };
                    holder.Start();
                    taken.Wait(5000);
                }
                var log = LogEnd();
                var told0 = Told();
                _hungOn = null;
                _hungSeconds = -1;
                _target = gear;
                _mode = (int)mode;
                // the hang comes within 10 frames (the gear's 10-frame update); the game thread waits through it
                yield return Wait(() => _hungSeconds >= 0, $"the gear's update ({mode}) hung and ended", 120);
                _mode = (int)Mode.None;
                var after = MySession.Static.GameplayFrameCounter;
                yield return WaitForSeconds(2, "the frames after the hang");
                var frames = MySession.Static.GameplayFrameCounter - after;
                var told = Told() - told0;

                var lines = LogSince(log.File, log.At);
                var id = gear.EntityId.ToString();
                var heads = lines.Where(l => l.Contains("has been updating") && l.Contains(id) && l.Contains("reading the stacks")).ToList();
                var report = lines.FindIndex(l => l.Contains("has been updating") && l.Contains(id) && !l.Contains("reading the stacks"));
                // the report: its first line and those after it up to the next line of the log (a time stamp)
                var body = report < 0 ? new List<string>() : lines.Skip(report + 1).TakeWhile(l => !Regex.IsMatch(l, @"^\d\d:\d\d:\d\d\.\d{4} ")).ToList();
                var stack = body.SkipWhile(l => !l.StartsWith("Its stack:")).Skip(1).TakeWhile(l => l.StartsWith("   at ")).ToList();
                var hanging = stack.Any(l => l.Contains(nameof(GearPrefix)));
                var waiting = mode != Mode.Lock || stack.Any(l => l.Contains("Monitor"));
                var onGame = _hungOn == "the game thread";
                var gameStack = onGame || body.Any(l => l.StartsWith("The game thread"));
                var tellAt = heads.Count == 0 ? -1 : (Stamp(heads[0]) - _hungAt).TotalSeconds;
                Note($"STUCK UPDATE | {mode}: hung on '{_hungOn}' for {_hungSeconds:F1} s; told {told} time(s), {tellAt:F1} s into the hang; " +
                     $"its stack {stack.Count} frames, the hanging method in it {hanging}, the wait in it {waiting}, the game thread's stack {gameStack}; {frames} frames in 2 s after");
                foreach (var l in body.Take(14)) Note("STUCK UPDATE |   " + l.TrimEnd());
                if (mode != Mode.SpinAnywhere) Check(!onGame, $"{mode}: the gear hung on the game thread, not on a thread of the clusters");
                Check(_hungSeconds >= HangSeconds - 0.5, $"{mode}: the hang lasted {_hungSeconds:F1} s, not its {HangSeconds} s: something cut it short");
                Check(told == 1 && heads.Count == 1, $"{mode}: the stuck update was told {told} time(s), {heads.Count} line(s) naming the gear");
                Check(tellAt >= 9.5 && tellAt <= 12.5, $"{mode}: told {tellAt:F1} s into the hang, not at 10 s");
                Check(hanging && waiting, $"{mode}: the stack in the log does not show where the update hung");
                Check(gameStack, $"{mode}: the log has no stack of the game thread");
                Check(frames > 60, $"{mode}: the server did not go on after the hang: {frames} frames in 2 s");
                holder?.Join(TimeSpan.FromSeconds(HangSeconds + 30));
            }
        }

        private static bool GearPrefix(MyLandingGear __instance)
        {
            if (__instance != _target)
            {
                Interlocked.Increment(ref _calls);
                return true;
            }
            var mode = (Mode)_mode;
            if (mode == Mode.None || _hanging) return true;
            var onGame = Thread.CurrentThread == MySandboxGame.Static?.UpdateThread;
            // the first two on a thread of the clusters, as on production
            if (onGame && mode != Mode.SpinAnywhere) return true;
            _hanging = true;
            _hungOn = onGame ? "the game thread" : Thread.CurrentThread.Name ?? Thread.CurrentThread.ManagedThreadId.ToString();
            var began = DateTime.Now;
            _hungAt = began;
            try
            {
                if (mode == Mode.Lock)
                {
                    if (Monitor.TryEnter(Held, TimeSpan.FromSeconds(HangSeconds))) Monitor.Exit(Held);
                }
                else
                    while ((DateTime.Now - began).TotalSeconds < HangSeconds) Thread.SpinWait(1000);
            }
            finally
            {
                _mode = (int)Mode.None;
                _hungSeconds = (DateTime.Now - began).TotalSeconds;
                _hanging = false;
            }
            return true;
        }

        // ------------------------------------------------------------------ the server log from a point on

        private static DateTime Stamp(string line)
        {
            var m = Regex.Match(line, @"^(\d\d):(\d\d):(\d\d)\.(\d{4})");
            if (!m.Success) return DateTime.MinValue;
            return DateTime.Now.Date.AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value)).AddSeconds(int.Parse(m.Groups[3].Value))
                .AddTicks(int.Parse(m.Groups[4].Value) * 1000L);
        }

        private static string LogFile()
        {
            var dir = Path.Combine(Environment.CurrentDirectory, "Logs");
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "Torch-*.log").OrderByDescending(f => f).FirstOrDefault() : null;
        }

        private static (string File, long At) LogEnd()
        {
            var file = LogFile();
            return (file, file == null ? 0 : new FileInfo(file).Length);
        }

        private static List<string> LogSince(string file, long at)
        {
            var lines = new List<string>();
            if (file == null) return lines;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(Math.Min(at, stream.Length), SeekOrigin.Begin);
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                }
            }
            return lines;
        }

        public override void Cleanup()
        {
            try
            {
                _mode = (int)Mode.None;
                _target = null;
                _harmony?.UnpatchAll(HarmonyId);
                FakeClients.RemoveAll();
            }
            finally
            {
                base.Cleanup();
            }
        }
    }
}
