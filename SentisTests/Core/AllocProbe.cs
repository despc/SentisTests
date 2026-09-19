using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Torch.Managers.PatchManager;
using VRage.Game.Components;
using VRage.Game.Entity;

namespace SentisTests.Core
{
    /// <summary>
    /// Allocations and time per type of everything the game updates each frame, for one window.
    ///
    /// FrameProbe only sees the methods it was given hooks for, so most of what a frame allocates
    /// inside session components and entity updates stays unexplained ("session.components 505 MB").
    /// This probe hooks, for the length of a window, the update methods of every entity, block,
    /// game logic and session component type present in the world, and attributes game-thread
    /// allocations and time to type and method, exclusive of nested hooked calls. The hooks are
    /// installed and removed at runtime; they cost a little on every update, so the window's frame
    /// times are not representative - only the attribution is.
    /// </summary>
    public static class AllocProbe
    {
        private static readonly string[] MethodNames =
        {
            "UpdateBeforeSimulation", "UpdateBeforeSimulation10", "UpdateBeforeSimulation100",
            "UpdateAfterSimulation", "UpdateAfterSimulation10", "UpdateAfterSimulation100",
            "UpdateOnceBeforeFrame", "Simulate",
        };

        private sealed class Stat
        {
            public long Calls;
            public long AllocBytes;
            public long Ticks;
        }

        private struct Frame
        {
            public Type Type;
            public int Method;
            public long AllocStart;
            public long TickStart;
            public long ChildAlloc;
            public long ChildTicks;
        }

        private static readonly Dictionary<(Type, int), Stat> Stats = new Dictionary<(Type, int), Stat>();
        private static readonly Frame[] Stack = new Frame[256];
        private static int _depth;
        private static int _gameThreadId = -1;
        private static PatchManager _patchManager;
        private static PatchContext _context;
        private static long _hooked;
        private static long _skippedDepth;
        private static readonly FieldInfo CompositeComponents =
            typeof(MyCompositeGameLogicComponent).GetField("m_logicComponents", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>Remembers the patch manager; nothing is hooked until <see cref="Start"/>.</summary>
        public static void Init(PatchManager patchManager) => _patchManager = patchManager;

        public static bool Running => _context != null;

        /// <summary>Hooks the update methods of every type in the world. Call on the game thread.</summary>
        public static string Start()
        {
            if (_patchManager == null) return "no patch manager";
            if (_context != null) return "already running";
            _gameThreadId = Thread.CurrentThread.ManagedThreadId;
            Stats.Clear();
            _depth = 0;
            _skippedDepth = 0;

            var types = new HashSet<Type>();
            foreach (var entity in MyEntities.GetEntities())
            {
                Collect(entity, types);
                if (entity is MyCubeGrid grid)
                    foreach (var block in grid.GetFatBlocks())
                        Collect(block, types);
            }
            // The components actually updated each frame, grouped by update order.
            var sessionComponents = typeof(MySession).GetField("m_sessionComponentsForUpdate", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(MySession.Static) as IDictionary;
            if (sessionComponents != null)
                foreach (IEnumerable group in sessionComponents.Values)
                    foreach (var component in group)
                        if (component != null) types.Add(component.GetType());

            var methods = new HashSet<MethodInfo>();
            foreach (var type in types)
                for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                    for (var m = 0; m < MethodNames.Length; m++)
                    {
                        var method = t.GetMethod(MethodNames[m], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                                                 BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        if (method == null || method.IsAbstract || method.IsGenericMethodDefinition || method.GetMethodBody() == null) continue;
                        methods.Add(method);
                    }

            _context = _patchManager.AcquireContext();
            foreach (var method in methods)
            {
                var index = Array.IndexOf(MethodNames, method.Name);
                var pattern = _context.GetPattern(method);
                // Hooking re-emits the body, so keep leaves where they point (see LeaveTargets).
                pattern.Transpilers.Add(LeaveTargets.KeepLeavesMethod);
                pattern.Prefixes.Add(typeof(AllocProbe).GetMethod("Pre" + index, BindingFlags.Static | BindingFlags.NonPublic));
                pattern.Suffixes.Add(typeof(AllocProbe).GetMethod("Post" + index, BindingFlags.Static | BindingFlags.NonPublic));
            }
            _hooked = methods.Count;
            var watch = Stopwatch.StartNew();
            _patchManager.Commit();
            return "hooked " + methods.Count + " update methods of " + types.Count + " types in " + watch.ElapsedMilliseconds + " ms, " +
                   LeaveTargets.Kept + " leaves kept from the re-emit bug";
        }

        /// <summary>Removes the hooks and reports the window, heaviest allocators first.</summary>
        public static string Stop(int top = 30)
        {
            if (_context == null) return "not running";
            _patchManager.FreeContext(_context);
            _context = null;
            _patchManager.Commit();
            _gameThreadId = -1;

            var totalAlloc = Stats.Values.Sum(s => s.AllocBytes);
            var toMs = 1000.0 / Stopwatch.Frequency;
            var sb = new StringBuilder();
            sb.Append("ALLOC BY TYPE (exclusive) | hooked methods=").Append(_hooked)
                .Append(" attributed=").Append(totalAlloc / (1024 * 1024)).Append("MB");
            if (_skippedDepth > 0) sb.Append(" (").Append(_skippedDepth).Append(" calls deeper than the stack were not counted)");
            foreach (var pair in Stats.OrderByDescending(p => p.Value.AllocBytes).Take(top))
            {
                var s = pair.Value;
                sb.Append(" | ").Append(pair.Key.Item1.Name).Append('.').Append(MethodNames[pair.Key.Item2])
                    .Append('=').Append((s.AllocBytes / 1024.0 / 1024.0).ToString("F1")).Append("MB/")
                    .Append(s.Calls).Append("calls/").Append((s.Ticks * toMs).ToString("F0")).Append("ms");
            }
            sb.Append(" | by time:");
            foreach (var pair in Stats.OrderByDescending(p => p.Value.Ticks).Take(15))
            {
                var s = pair.Value;
                sb.Append(' ').Append(pair.Key.Item1.Name).Append('.').Append(MethodNames[pair.Key.Item2])
                    .Append('=').Append((s.Ticks * toMs).ToString("F0")).Append("ms");
            }
            Stats.Clear();
            return sb.ToString();
        }

        private static void Collect(MyEntity entity, HashSet<Type> types)
        {
            if (entity == null) return;
            types.Add(entity.GetType());
            var logic = entity.GameLogic;
            if (logic == null) return;
            types.Add(logic.GetType());
            // Mods usually sit inside a composite logic component.
            if (logic is MyCompositeGameLogicComponent)
            {
                var inner = CompositeComponents?.GetValue(logic) as IEnumerable;
                if (inner != null)
                    foreach (var component in inner)
                        if (component != null) types.Add(component.GetType());
            }
        }

        private static void Enter(object instance, int method)
        {
            if (Thread.CurrentThread.ManagedThreadId != _gameThreadId || instance == null) return;
            if (_depth >= Stack.Length)
            {
                _skippedDepth++;
                return;
            }
            Stack[_depth++] = new Frame
            {
                Type = instance.GetType(),
                Method = method,
                AllocStart = GC.GetAllocatedBytesForCurrentThread(),
                TickStart = Stopwatch.GetTimestamp(),
            };
        }

        private static void Exit(object instance, int method)
        {
            if (Thread.CurrentThread.ManagedThreadId != _gameThreadId || instance == null) return;
            var type = instance.GetType();
            // A hooked method that threw left its frame behind: drop frames until this one.
            while (_depth > 0 && (Stack[_depth - 1].Type != type || Stack[_depth - 1].Method != method)) _depth--;
            if (_depth == 0) return;
            var frame = Stack[--_depth];
            var alloc = GC.GetAllocatedBytesForCurrentThread() - frame.AllocStart;
            var ticks = Stopwatch.GetTimestamp() - frame.TickStart;
            if (!Stats.TryGetValue((frame.Type, frame.Method), out var stat))
                Stats[(frame.Type, frame.Method)] = stat = new Stat();
            stat.Calls++;
            stat.AllocBytes += alloc - frame.ChildAlloc;
            stat.Ticks += ticks - frame.ChildTicks;
            if (_depth > 0)
            {
                Stack[_depth - 1].ChildAlloc += alloc;
                Stack[_depth - 1].ChildTicks += ticks;
            }
        }

        // One prefix/suffix pair per method name, so the method is known without allocating.
        private static void Pre0(object __instance) => Enter(__instance, 0);
        private static void Post0(object __instance) => Exit(__instance, 0);
        private static void Pre1(object __instance) => Enter(__instance, 1);
        private static void Post1(object __instance) => Exit(__instance, 1);
        private static void Pre2(object __instance) => Enter(__instance, 2);
        private static void Post2(object __instance) => Exit(__instance, 2);
        private static void Pre3(object __instance) => Enter(__instance, 3);
        private static void Post3(object __instance) => Exit(__instance, 3);
        private static void Pre4(object __instance) => Enter(__instance, 4);
        private static void Post4(object __instance) => Exit(__instance, 4);
        private static void Pre5(object __instance) => Enter(__instance, 5);
        private static void Post5(object __instance) => Exit(__instance, 5);
        private static void Pre6(object __instance) => Enter(__instance, 6);
        private static void Post6(object __instance) => Exit(__instance, 6);
        private static void Pre7(object __instance) => Enter(__instance, 7);
        private static void Post7(object __instance) => Exit(__instance, 7);
    }
}
