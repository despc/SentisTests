using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Core
{
    /// <summary>
    /// What safe zones cost while a scenario runs, hooked only for that time:
    /// <list type="bullet">
    /// <item>the shape-against-shape tests (<c>MyPhysics.IsPenetratingShapeShape</c>) - how many and how long;
    /// a zone runs one for every body that it is told has left it;</item>
    /// <item>the bodies the zone is told have entered and left (<c>InsertEntityInternal</c>,
    /// <c>RemoveEntityPhantom</c>);</item>
    /// <item>the zone's own update (<c>MySafeZone.UpdateBeforeSimulation</c>);</item>
    /// <item>the motor locks it sets and clears on the mechanical groups (<c>ForceLockGridMotors</c>).</item>
    /// </list>
    /// </summary>
    public static class SafeZoneProbe
    {
        private static PatchManager _patchManager;
        private static PatchContext _context;

        public static long ShapeTests, ShapeTicks, ShapeMaxTicks;
        public static long Enters, Leaves, EnterTicks, LeaveTicks;
        public static long ZoneUpdates, ZoneUpdateTicks;
        public static long Locks, Unlocks;
        public static long TrackerTicks, TrackerTickTicks;

        // what the zone is told enters and leaves it: the entity's type, and whether it is the top of its hierarchy
        private static readonly ConcurrentDictionary<string, long> EnterKinds = new ConcurrentDictionary<string, long>();
        private static readonly ConcurrentDictionary<string, long> LeaveKinds = new ConcurrentDictionary<string, long>();

        /// <summary>The entities the zones told the clients have come in and gone out, in order.</summary>
        public static readonly ConcurrentQueue<long> SentIn = new ConcurrentQueue<long>();
        public static readonly ConcurrentQueue<long> SentOut = new ConcurrentQueue<long>();

        [ThreadStatic] private static long _shapeStart;
        [ThreadStatic] private static long _updateStart;
        [ThreadStatic] private static long _enterStart;
        [ThreadStatic] private static long _leaveStart;
        [ThreadStatic] private static long _trackerStart;

        public static void Init(PatchManager patchManager) => _patchManager = patchManager;

        public static string Start()
        {
            if (_patchManager == null) return "no patch manager";
            if (_context != null) return "already running";
            Reset();
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _context = _patchManager.AcquireContext();

            var shapeTest = typeof(MyPhysics).GetMethod(nameof(MyPhysics.IsPenetratingShapeShape), any, null,
                new[] { typeof(Havok.HkShape), typeof(Vector3D).MakeByRefType(), typeof(Quaternion).MakeByRefType(),
                        typeof(Havok.HkShape), typeof(Vector3D).MakeByRefType(), typeof(Quaternion).MakeByRefType() }, null);
            Hook(shapeTest, nameof(ShapePrefix), nameof(ShapeSuffix));
            Hook(typeof(MySafeZone).GetMethod("InsertEntityInternal", any), nameof(EnterPrefix), nameof(EnterSuffix));
            Hook(typeof(MySafeZone).GetMethod("RemoveEntityPhantom", any), nameof(LeavePrefix), nameof(LeaveSuffix));
            Hook(typeof(MySafeZone).GetMethod(nameof(MySafeZone.UpdateBeforeSimulation), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
                nameof(UpdatePrefix), nameof(UpdateSuffix));
            Hook(typeof(MySafeZone).GetMethod("ForceLockGridMotors", any), nameof(LockPrefix), null);
            Hook(typeof(MySafeZone).GetMethod("SendInsertedEntity", any), nameof(SentInPrefix), null);
            Hook(typeof(MySafeZone).GetMethod("SendRemovedEntity", any), nameof(SentOutPrefix), null);
            // SentisOptimisations' own tracking of the grids in the zones, when it is there
            var tracker = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Optimizer.Optimizations.SafeZoneGridTracking", false))
                .FirstOrDefault(t => t != null);
            if (tracker != null) Hook(tracker.GetMethod("Tick", BindingFlags.Static | BindingFlags.Public), nameof(TrackerPrefix), nameof(TrackerSuffix));
            _patchManager.Commit();
            return "safe zone probe on";
        }

        public static void Stop()
        {
            if (_context == null) return;
            _patchManager.FreeContext(_context);
            _context = null;
            _patchManager.Commit();
        }

        public static void Reset()
        {
            ShapeTests = ShapeTicks = ShapeMaxTicks = 0;
            Enters = Leaves = EnterTicks = LeaveTicks = 0;
            ZoneUpdates = ZoneUpdateTicks = 0;
            Locks = Unlocks = 0;
            TrackerTicks = TrackerTickTicks = 0;
            EnterKinds.Clear();
            while (SentIn.TryDequeue(out _)) { }
            while (SentOut.TryDequeue(out _)) { }
            LeaveKinds.Clear();
        }

        private static string Kind(VRage.ModAPI.IMyEntity entity)
        {
            if (entity == null) return "null";
            var top = entity.GetTopMostParent();
            return entity.GetType().Name + (top == entity ? "" : " in " + top?.GetType().Name);
        }

        private static string Top(ConcurrentDictionary<string, long> kinds) =>
            string.Join(", ", kinds.OrderByDescending(k => k.Value).Take(6).Select(k => k.Key + " " + k.Value));

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        public static string Format() =>
            "shape tests " + ShapeTests + " (" + Ms(ShapeTicks).ToString("F1") + " ms, max " + Ms(ShapeMaxTicks).ToString("F2") + " ms)" +
            ", zone enters " + Enters + " (" + Ms(EnterTicks).ToString("F0") + " ms), leaves " + Leaves + " (" + Ms(LeaveTicks).ToString("F0") + " ms)" +
            ", zone updates " + ZoneUpdates + " (" + Ms(ZoneUpdateTicks).ToString("F1") + " ms)" +
            ", motor locks " + Locks + ", unlocks " + Unlocks +
            ", grid tracking " + TrackerTicks + " frames (" + Ms(TrackerTickTicks).ToString("F1") + " ms)" +
            (Enters + Leaves > 0 ? " [enter: " + Top(EnterKinds) + "; leave: " + Top(LeaveKinds) + "]" : "");

        public static double ShapeMs => Ms(ShapeTicks);

        private static void Hook(MethodInfo target, string prefix, string suffix)
        {
            if (target == null)
            {
                SentisTestsPlugin.Log.Warn("SafeZoneProbe: target not found for " + (prefix ?? suffix));
                return;
            }
            var pattern = _context.GetPattern(target);
            pattern.Transpilers.Add(LeaveTargets.KeepLeavesMethod);
            if (prefix != null) pattern.Prefixes.Add(typeof(SafeZoneProbe).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic));
            if (suffix != null) pattern.Suffixes.Add(typeof(SafeZoneProbe).GetMethod(suffix, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void ShapePrefix() => _shapeStart = Stopwatch.GetTimestamp();

        private static void ShapeSuffix()
        {
            var ticks = Stopwatch.GetTimestamp() - _shapeStart;
            Interlocked.Increment(ref ShapeTests);
            Interlocked.Add(ref ShapeTicks, ticks);
            long max;
            while (ticks > (max = Interlocked.Read(ref ShapeMaxTicks)) &&
                   Interlocked.CompareExchange(ref ShapeMaxTicks, ticks, max) != max)
            {
            }
        }

        private static void EnterSuffix() => Interlocked.Add(ref EnterTicks, Stopwatch.GetTimestamp() - _enterStart);

        private static void LeaveSuffix() => Interlocked.Add(ref LeaveTicks, Stopwatch.GetTimestamp() - _leaveStart);

        private static void EnterPrefix(MyEntity entity)
        {
            _enterStart = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref Enters);
            EnterKinds.AddOrUpdate(Kind(entity), 1, (_, n) => n + 1);
        }

        private static void LeavePrefix(VRage.ModAPI.IMyEntity entity)
        {
            _leaveStart = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref Leaves);
            LeaveKinds.AddOrUpdate(Kind(entity), 1, (_, n) => n + 1);
        }

        private static void UpdatePrefix() => _updateStart = Stopwatch.GetTimestamp();

        private static void UpdateSuffix()
        {
            Interlocked.Increment(ref ZoneUpdates);
            Interlocked.Add(ref ZoneUpdateTicks, Stopwatch.GetTimestamp() - _updateStart);
        }

        private static void TrackerPrefix() => _trackerStart = Stopwatch.GetTimestamp();

        private static void TrackerSuffix()
        {
            Interlocked.Increment(ref TrackerTicks);
            Interlocked.Add(ref TrackerTickTicks, Stopwatch.GetTimestamp() - _trackerStart);
        }

        private static void SentInPrefix(long entityId) => SentIn.Enqueue(entityId);

        private static void SentOutPrefix(long entityId) => SentOut.Enqueue(entityId);

        private static void LockPrefix(bool locked)
        {
            if (locked) Interlocked.Increment(ref Locks);
            else Interlocked.Increment(ref Unlocks);
        }
    }
}
