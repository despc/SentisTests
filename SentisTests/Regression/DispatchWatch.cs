using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;

namespace SentisTests.Regression
{
    /// <summary>
    /// Exceptions out of the game's pass over the entities' updates (<c>MyParallelEntityUpdateOrchestrator</c>
    /// <c>DispatchBeforeSimulation</c>, <c>DispatchAfterSimulation</c>), counted while the regression runs. An exception
    /// there stops the pass: every entity after the one that threw misses that update - a grid's damage, its scheduled
    /// work. SentisOptimisations swallows them (CrashFixPatch) and writes them to the log only with its diagnostic logs
    /// on; here they are seen whatever the logs. A finalizer of the first priority, so it sees the exception before
    /// anything swallows it; it leaves it as it is.
    /// </summary>
    internal static class DispatchWatch
    {
        private static readonly Harmony Harmony = new Harmony("SentisTests.DispatchWatch");
        private static readonly ConcurrentDictionary<string, int> Seen = new ConcurrentDictionary<string, int>();
        private static bool _patched;
        public static volatile bool Recording;

        public static void Start()
        {
            Seen.Clear();
            Recording = true;
            if (_patched) return;
            _patched = true;
            var finalizer = new HarmonyMethod(typeof(DispatchWatch).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
            foreach (var name in new[] { "DispatchBeforeSimulation", "DispatchAfterSimulation" })
            {
                var method = typeof(MyParallelEntityUpdateOrchestrator).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                             ?? throw new MissingMethodException("MyParallelEntityUpdateOrchestrator." + name);
                Harmony.Patch(method, finalizer: finalizer);
            }
        }

        public static void Stop() => Recording = false;

        /// <summary>Each kind of exception (type, message, the first frames of its stack) with how many times it came.</summary>
        public static List<string> Report() =>
            Seen.OrderByDescending(p => p.Value).Select(p => p.Value + "x " + p.Key).ToList();

        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null || !Recording) return __exception;
            try
            {
                var e = __exception.GetBaseException();
                var frames = (e.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(6);
                Seen.AddOrUpdate(e.GetType().Name + ": " + e.Message + " @ " + string.Join(" <- ", frames), 1, (k, n) => n + 1);
            }
            catch
            {
                // a finalizer must not throw
            }
            return __exception;
        }
    }
}
