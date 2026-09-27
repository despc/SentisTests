using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Torch.Managers.PatchManager;

namespace SentisTests.Core
{
    /// <summary>
    /// What a slow game event is made of: a few "root" methods (a block placed, a player respawned) and the methods
    /// they call, each timed; when a root takes longer than <see cref="SlowMs"/> the tree of its calls goes to the log,
    /// "BuildBlocksRequest 22.1 ms [BuildBlocksSuccess 18.0 [...], ...]". Game thread only; the stand only.
    /// </summary>
    public static class EventTimer
    {
        public const double SlowMs = 5;
        private const int Slots = @N@;

        /// <summary>Type, method, and whether it is a root (an event that is logged when slow).</summary>
        private static readonly (string Type, string Method, bool Root)[] Targets =
        {
@TARGETS@
        };

        private static readonly string[] Names = new string[Slots];
        private static readonly bool[] IsRoot = new bool[Slots];

        // the calls open now, and the finished ones of the root under way: (slot, depth, ticks)
        private static int _gc0, _gc1, _gc2;
        private static readonly List<(int Slot, long Start)> Open = new List<(int, long)>();
        private static readonly List<(int Slot, int Depth, long Ticks)> Done = new List<(int, int, long)>();

        private static int _used;
        private static PatchManager _patchManager;

        /// <summary>The handlers an event has now, timed as calls of whatever raises it (after the world is loaded).</summary>
        public static void TimeHandlers(Delegate handlers)
        {
            if (_patchManager == null || handlers == null || !Installed) return;
            var ctx = _patchManager.AcquireContext();
            var names = new List<string>();
            foreach (var handler in handlers.GetInvocationList())
            {
                var target = handler.Method;
                if (_used >= Slots || target.IsAbstract || target.GetMethodBody() == null) continue;
                Names[_used] = target.DeclaringType?.Name + "." + target.Name;
                var slot = SlotType(_used);
                var pattern = ctx.GetPattern(target);
                pattern.Prefixes.Add(slot.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic));
                pattern.Suffixes.Add(slot.GetMethod("Suffix", BindingFlags.Static | BindingFlags.NonPublic));
                names.Add(Names[_used]);
                _used++;
            }
            _patchManager.Commit();
            SentisTestsPlugin.Log.Info("EventTimer: handlers timed: " + string.Join(", ", names));
        }

        /// <summary>The patches are in (<see cref="Install"/> ran).</summary>
        public static bool Installed { get; private set; }

        public static void Install(PatchManager patchManager)
        {
            if (patchManager == null || Installed) return;
            Installed = true;
            _patchManager = patchManager;
            var ctx = patchManager.AcquireContext();
            var used = 0;
            var missing = new List<string>();
            foreach (var (typeName, method, root) in Targets)
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName, false)).FirstOrDefault(t => t != null);
                if (type == null) { missing.Add(typeName); continue; }
                var found = method == ".ctor"
                    ? type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Cast<MethodBase>().ToList()
                    : type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(m => m.Name == method && !m.IsAbstract && !m.IsGenericMethodDefinition).Cast<MethodBase>().ToList();
                if (found.Count == 0) { missing.Add(type.Name + "." + method); continue; }
                foreach (var target in found)
                {
                    if (used >= Slots) { missing.Add(type.Name + "." + method + " (no slot)"); continue; }
                    Names[used] = type.Name + "." + method;
                    IsRoot[used] = root;
                    var slot = SlotType(used);
                    var pattern = ctx.GetPattern(target);
                    pattern.Transpilers.Add(LeaveTargets.KeepLeavesMethod);
                    pattern.Prefixes.Add(slot.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic));
                    pattern.Suffixes.Add(slot.GetMethod("Suffix", BindingFlags.Static | BindingFlags.NonPublic));
                    used++;
                }
            }
            patchManager.Commit();
            _used = used;
            SentisTestsPlugin.Log.Info($"EventTimer: {used} methods timed" + (missing.Count > 0 ? "; not found: " + string.Join(", ", missing) : ""));
        }

        private static void Enter(int slot)
        {
            if (Sandbox.MySandboxGame.Static?.UpdateThread != Thread.CurrentThread) return;
            if (Open.Count == 0 && !IsRoot[slot]) return;           // only inside a root
            if (Open.Count == 0)
            {
                Done.Clear();
                _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            }
            Open.Add((slot, Stopwatch.GetTimestamp()));
        }

        private static void Exit(int slot)
        {
            if (Open.Count == 0 || Sandbox.MySandboxGame.Static?.UpdateThread != Thread.CurrentThread) return;
            // (a call that threw left no exit: closed with the one that exits now)
            var at = Open.FindLastIndex(o => o.Slot == slot);
            if (at < 0) return;
            var now = Stopwatch.GetTimestamp();
            for (var i = Open.Count - 1; i >= at; i--)
            {
                Done.Add((Open[i].Slot, i, now - Open[i].Start));
                Open.RemoveAt(i);
            }
            if (Open.Count > 0) return;
            var ms = Done[Done.Count - 1].Ticks * 1000.0 / Stopwatch.Frequency;
            // the whole game frame is a root too: only its long ones
            var slow = Names[Done[Done.Count - 1].Slot] == "MySandboxGame.Update" ? 15 : SlowMs;
            if (ms >= slow && SentisTestsPlugin.Config?.EventTimerLogs == true)
            {
                var gc = (GC.CollectionCount(0) - _gc0) + "/" + (GC.CollectionCount(1) - _gc1) + "/" + (GC.CollectionCount(2) - _gc2);
                SentisTestsPlugin.Log.Info("EventTimer: " + Tree() + (gc == "0/0/0" ? "" : " gc " + gc));
            }
            Done.Clear();
        }

        private static string Ms(long ticks) =>
            (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms";

        /// <summary>The finished calls as a tree: a call's children are the calls one deeper finished before it, after its previous sibling.</summary>
        private static string Tree()
        {
            var sb = new StringBuilder();
            // what calls made many times were made of, summed over all of them, all the way down
            void Summed(List<int> members)
            {
                var inner = new List<int>();
                foreach (var member in members)
                    for (var j = member - 1; j >= 0 && Done[j].Depth > Done[member].Depth; j--)
                        if (Done[j].Depth == Done[member].Depth + 1) inner.Add(j);
                if (inner.Count == 0) return;
                sb.Append(" [");
                var firstInner = true;
                foreach (var g in inner.GroupBy(c => Done[c].Slot).OrderByDescending(g => g.Sum(c => Done[c].Ticks)))
                {
                    if (!firstInner) sb.Append(", ");
                    firstInner = false;
                    var part = g.ToList();
                    sb.Append(Names[g.Key]).Append(part.Count > 1 ? " x" + part.Count : "").Append(' ').Append(Ms(part.Sum(c => Done[c].Ticks)));
                    Summed(part);
                }
                sb.Append(']');
            }

            void Write(int index)
            {
                var (slot, depth, ticks) = Done[index];
                sb.Append(Names[slot]).Append(' ').Append(Ms(ticks));
                var children = new List<int>();
                for (var j = index - 1; j >= 0 && Done[j].Depth > depth; j--)
                    if (Done[j].Depth == depth + 1) children.Add(j);
                if (children.Count == 0) return;
                children.Reverse();
                sb.Append(" [");
                var first = true;
                // the same method called many times: summed, one entry
                foreach (var group in children.GroupBy(c => Done[c].Slot))
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    var list = group.ToList();
                    if (list.Count == 1) Write(list[0]);
                    else
                    {
                        sb.Append(Names[group.Key]).Append(" x").Append(list.Count).Append(' ').Append(Ms(list.Sum(c => Done[c].Ticks)));
                        Summed(list);
                    }
                }
                sb.Append(']');
            }
            Write(Done.Count - 1);
            return sb.ToString();
        }

        private static Type SlotType(int i)
        {
            switch (i) { @CASES@ default: throw new ArgumentOutOfRangeException(nameof(i)); }
        }

        // one class a slot: a hook is a static method, and each must know which method it times
@SLOTS@
    }
}
