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

        // what a check throws on purpose (reg_grid_update_throws): an exception carrying one of these is not counted
        private static readonly ConcurrentDictionary<string, byte> Excused = new ConcurrentDictionary<string, byte>();

        /// <summary>Exceptions whose message, or an inner one's, holds <paramref name="marker"/> are not counted.</summary>
        public static void Excuse(string marker) => Excused[marker] = 0;

        private static bool IsExcused(Exception e, int depth = 0)
        {
            if (e == null || depth > 8 || Excused.IsEmpty) return false;
            if (Excused.Keys.Any(m => (e.Message ?? "").Contains(m))) return true;
            if (IsExcused(e.InnerException, depth + 1)) return true;
            // ParallelTasks.TaskException: the exceptions of the task's items
            if (e.GetType().GetProperty("InnerExceptions")?.GetValue(e) is IEnumerable<Exception> inner)
                foreach (var x in inner)
                    if (IsExcused(x, depth + 1)) return true;
            return false;
        }

        /// <summary>Each kind of exception (type, message, the first frames of its stack) with how many times it came.</summary>
        public static List<string> Report() =>
            Seen.OrderByDescending(p => p.Value).Select(p => p.Value + "x " + p.Key).ToList();

        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null || !Recording) return __exception;
            try
            {
                if (IsExcused(__exception)) return __exception;
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
