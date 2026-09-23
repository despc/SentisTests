using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Torch.Managers.PatchManager;

namespace SentisTests.Core
{
    /// <summary>
    /// Times a handful of game methods, each on its own, for a scenario's window: how many calls, and how long
    /// they took. The methods are named by type and method; every one declared under that name on the type is
    /// hooked. Up to <see cref="Slots"/> methods at once.
    /// </summary>
    public static class MethodTimerProbe
    {
        public const int Slots = 16;

        private static PatchManager _patchManager;
        private static PatchContext _context;
        private static readonly string[] Names = new string[Slots];
        private static readonly long[] Calls = new long[Slots];
        private static readonly long[] Ticks = new long[Slots];
        private static int _used;

        public static void Init(PatchManager patchManager) => _patchManager = patchManager;

        /// <summary>Hooks the methods; returns what it could not find.</summary>
        public static string Start(IEnumerable<(Type Type, string Method)> targets)
        {
            if (_patchManager == null) return "no patch manager";
            Stop();
            Array.Clear(Calls, 0, Slots);
            Array.Clear(Ticks, 0, Slots);
            _used = 0;
            var missing = new List<string>();
            _context = _patchManager.AcquireContext();
            foreach (var (type, method) in targets)
            {
                if (type == null) { missing.Add("? ." + method); continue; }
                var found = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == method && !m.IsAbstract).ToList();
                if (found.Count == 0) { missing.Add(type.Name + "." + method); continue; }
                foreach (var target in found)
                {
                    if (_used >= Slots) { missing.Add(type.Name + "." + method + " (no slot)"); continue; }
                    var slot = SlotType(_used);
                    Names[_used] = type.Name + "." + method;
                    var pattern = _context.GetPattern(target);
                    pattern.Transpilers.Add(LeaveTargets.KeepLeavesMethod);
                    pattern.Prefixes.Add(slot.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic));
                    pattern.Suffixes.Add(slot.GetMethod("Suffix", BindingFlags.Static | BindingFlags.NonPublic));
                    _used++;
                }
            }
            _patchManager.Commit();
            return missing.Count == 0 ? "" : "not found: " + string.Join(", ", missing);
        }

        public static void Stop()
        {
            if (_context == null) return;
            _patchManager.FreeContext(_context);
            _context = null;
            _patchManager.Commit();
        }

        /// <summary>Per method: calls a frame, microseconds a call, milliseconds a frame; the heaviest first.</summary>
        public static string Format(long frames)
        {
            frames = Math.Max(1, frames);
            var rows = new List<(double MsPerFrame, string Line)>();
            for (var i = 0; i < _used; i++)
            {
                var ms = Ticks[i] * 1000.0 / Stopwatch.Frequency;
                var calls = Calls[i];
                rows.Add((ms / frames, Names[i] + " " + (calls / (double)frames).ToString("F1") + " calls/f x " +
                                       (calls == 0 ? 0 : ms * 1000 / calls).ToString("F2") + " us = " + (ms / frames).ToString("F3") + " ms/f"));
            }
            return string.Join("; ", rows.OrderByDescending(r => r.MsPerFrame).Select(r => r.Line));
        }

        internal static void Add(int slot, long ticks)
        {
            Interlocked.Increment(ref Calls[slot]);
            Interlocked.Add(ref Ticks[slot], ticks);
        }

        private static Type SlotType(int i)
        {
            switch (i)
            {
                case 0: return typeof(Slot0); case 1: return typeof(Slot1); case 2: return typeof(Slot2); case 3: return typeof(Slot3);
                case 4: return typeof(Slot4); case 5: return typeof(Slot5); case 6: return typeof(Slot6); case 7: return typeof(Slot7);
                case 8: return typeof(Slot8); case 9: return typeof(Slot9); case 10: return typeof(Slot10); case 11: return typeof(Slot11);
                case 12: return typeof(Slot12); case 13: return typeof(Slot13); case 14: return typeof(Slot14); default: return typeof(Slot15);
            }
        }

        // One class a slot: the hooks are static methods, and each must know whose time it adds up.
        private static class Slot0 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(0, Stopwatch.GetTimestamp() - _at); }
        private static class Slot1 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(1, Stopwatch.GetTimestamp() - _at); }
        private static class Slot2 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(2, Stopwatch.GetTimestamp() - _at); }
        private static class Slot3 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(3, Stopwatch.GetTimestamp() - _at); }
        private static class Slot4 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(4, Stopwatch.GetTimestamp() - _at); }
        private static class Slot5 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(5, Stopwatch.GetTimestamp() - _at); }
        private static class Slot6 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(6, Stopwatch.GetTimestamp() - _at); }
        private static class Slot7 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(7, Stopwatch.GetTimestamp() - _at); }
        private static class Slot8 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(8, Stopwatch.GetTimestamp() - _at); }
        private static class Slot9 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(9, Stopwatch.GetTimestamp() - _at); }
        private static class Slot10 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(10, Stopwatch.GetTimestamp() - _at); }
        private static class Slot11 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(11, Stopwatch.GetTimestamp() - _at); }
        private static class Slot12 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(12, Stopwatch.GetTimestamp() - _at); }
        private static class Slot13 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(13, Stopwatch.GetTimestamp() - _at); }
        private static class Slot14 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(14, Stopwatch.GetTimestamp() - _at); }
        private static class Slot15 { [ThreadStatic] private static long _at; private static void Prefix() => _at = Stopwatch.GetTimestamp(); private static void Suffix() => Add(15, Stopwatch.GetTimestamp() - _at); }
    }
}
