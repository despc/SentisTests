using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// Where a programmable block's script spends its time, method by method (for a check that asks; nothing else uses it).
    /// Every method of the script's assembly with a body of some size is timed on the game thread - its calls, its own time
    /// (its nested timed calls taken off) and its whole time - and the heaviest runs of its Main are kept with their own
    /// breakdown. The script's tiny methods (wrappers of a line) are not timed: the timing would cost more than they do;
    /// their time is their caller's. Patched while asked, unpatched after.
    /// </summary>
    internal static class ScriptProfiler
    {
        private const int MinIlBytes = 40;
        private const int HeaviestRunsKept = 20;

        public sealed class Stat
        {
            public long Calls;
            public long OwnTicks;
            public long TotalTicks;
        }

        public sealed class RunBreakdown
        {
            public double Ms;
            public Dictionary<MethodBase, long> Own = new Dictionary<MethodBase, long>();
        }

        private struct Frame
        {
            public MethodBase Method;
            public long Start;
            public long Child;
        }

        private static readonly Dictionary<MethodBase, Stat> Stats = new Dictionary<MethodBase, Stat>();
        private static readonly List<RunBreakdown> Heaviest = new List<RunBreakdown>();
        private static readonly Stack<Frame> Stack = new Stack<Frame>();
        private static Dictionary<MethodBase, long> _run = new Dictionary<MethodBase, long>();
        private static Thread _gameThread;
        private static Harmony _harmony;
        private static readonly HashSet<Assembly> Patched = new HashSet<Assembly>();
        public static int MethodsTimed;

        /// <summary>Runs before this frame are timed but not kept among the heaviest: a patched method is compiled anew on its first call.</summary>
        public static ulong KeepRunsFromFrame;

        public static IReadOnlyDictionary<MethodBase, Stat> All => Stats;
        public static IReadOnlyList<RunBreakdown> HeaviestRuns => Heaviest;

        /// <summary>Times the methods of the script's assembly (once per assembly: a recompile is a new one). Game thread.</summary>
        public static void Attach(Assembly script)
        {
            _gameThread = Thread.CurrentThread;
            if (_harmony == null) _harmony = new Harmony("SentisTests.ScriptProfiler");
            // the patched methods are compiled on their first calls: those runs are not the script's
            KeepRunsFromFrame = Sandbox.MySandboxGame.Static.SimulationFrameCounter + 300;
            if (!Patched.Add(script)) return;
            var prefix = new HarmonyMethod(typeof(ScriptProfiler).GetMethod(nameof(Enter), BindingFlags.Static | BindingFlags.NonPublic));
            var finalizer = new HarmonyMethod(typeof(ScriptProfiler).GetMethod(nameof(Leave), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var type in script.GetTypes())
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                var body = method.GetMethodBody();
                if (body == null || body.GetILAsByteArray().Length < MinIlBytes) continue;
                try
                {
                    _harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                    MethodsTimed++;
                }
                catch
                {
                    // a method Harmony cannot patch is its caller's time
                }
            }
        }

        public static void Detach()
        {
            _harmony?.UnpatchAll("SentisTests.ScriptProfiler");
            Patched.Clear();
            Stack.Clear();
        }

        public static void Reset()
        {
            Stats.Clear();
            Heaviest.Clear();
            Stack.Clear();
            _run = new Dictionary<MethodBase, long>();
        }

        private static void Enter(MethodBase __originalMethod)
        {
            if (Thread.CurrentThread != _gameThread) return;
            Stack.Push(new Frame { Method = __originalMethod, Start = Stopwatch.GetTimestamp() });
        }

        private static Exception Leave(MethodBase __originalMethod, Exception __exception)
        {
            if (Thread.CurrentThread != _gameThread || Stack.Count == 0) return __exception;
            var now = Stopwatch.GetTimestamp();
            var frame = Stack.Pop();
            var total = now - frame.Start;
            var own = total - frame.Child;
            if (!Stats.TryGetValue(frame.Method, out var stat)) Stats[frame.Method] = stat = new Stat();
            stat.Calls++;
            stat.TotalTicks += total;
            stat.OwnTicks += own;
            _run.TryGetValue(frame.Method, out var inRun);
            _run[frame.Method] = inRun + own;
            if (Stack.Count > 0)
            {
                var parent = Stack.Pop();
                parent.Child += total;
                Stack.Push(parent);
            }
            else if (frame.Method.Name == "Main")
            {
                // a run of the script ends: kept when it is among the heaviest
                var ms = total * 1000.0 / Stopwatch.Frequency;
                if (Sandbox.MySandboxGame.Static.SimulationFrameCounter >= KeepRunsFromFrame &&
                    (Heaviest.Count < HeaviestRunsKept || ms > Heaviest[Heaviest.Count - 1].Ms))
                {
                    Heaviest.Add(new RunBreakdown { Ms = ms, Own = _run });
                    Heaviest.Sort((a, b) => b.Ms.CompareTo(a.Ms));
                    if (Heaviest.Count > HeaviestRunsKept) Heaviest.RemoveAt(Heaviest.Count - 1);
                }
                _run = new Dictionary<MethodBase, long>();
            }
            else _run = new Dictionary<MethodBase, long>(); // Save() or a constructor: not a run
            return __exception;
        }

        public static string Name(MethodBase method) => (method.DeclaringType?.Name ?? "?") + "." + method.Name;

        public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
