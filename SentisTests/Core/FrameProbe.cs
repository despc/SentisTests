using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Torch.Managers.PatchManager;

namespace SentisTests.Core
{
    /// <summary>
    /// Measures real simulation work per server frame: the time spent inside
    /// <c>MySandboxGame.Update</c>, which excludes the loop's sleep to 60 Hz (TickMetrics' gap
    /// metric includes that sleep, so it cannot tell jitter from load). Frames over the 16.67 ms
    /// budget are attributed to a few hooked sections so a spike can be traced to its source.
    /// </summary>
    public static class FrameProbe
    {
        public const double BudgetMs = 1000.0 / 60.0;
        private const int WorstKept = 12;
        // Breakdowns are kept for frames above half the budget, so the tail is explained before it
        // turns into a missed frame.
        private const double ReportAboveMs = BudgetMs / 2;

        private enum Section { Tools, ProjectorBuild, Physics, Harness, Bridge, Count }

        private static readonly string[] SectionNames = { "tools10", "projector.Build", "physics", "harness", "bridge" };
        private static readonly long[] _sectionTicks = new long[(int)Section.Count];
        private static readonly long[] _sectionStart = new long[(int)Section.Count];
        private static readonly int[] _sectionDepth = new int[(int)Section.Count];

        private static readonly List<double> _busyMs = new List<double>();
        private static readonly List<string> _worst = new List<string>();
        private static readonly List<double> _worstMs = new List<double>();
        private static readonly List<double> _buildMs = new List<double>();
        private static readonly List<string> _slowBuilds = new List<string>();
        private static long _buildStart;
        private static readonly int[] _gcAtFrameStart = new int[3];
        private static long _frameStart;
        private static long _frameIndex;
        private static int _gameThreadId;
        private static bool _installed;

        public static void Install(PatchManager patchManager)
        {
            if (_installed || patchManager == null) return;
            var ctx = patchManager.AcquireContext();
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                     BindingFlags.NonPublic;

            Hook(ctx, typeof(MySandboxGame).GetMethod("Update", any, null, Type.EmptyTypes, null),
                nameof(FramePrefix), nameof(FrameSuffix));
            Hook(ctx, typeof(MyShipToolBase).GetMethod("UpdateAfterSimulation10", any, null, Type.EmptyTypes, null),
                nameof(ToolsPrefix), nameof(ToolsSuffix));
            Hook(ctx, typeof(MyProjectorBase).GetMethod("Build", any),
                nameof(BuildPrefix), nameof(BuildSuffix));
            Hook(ctx, typeof(MyPhysics).GetMethod("Simulate", any, null, Type.EmptyTypes, null),
                nameof(PhysicsPrefix), nameof(PhysicsSuffix));
            patchManager.Commit();
            _installed = true;
        }

        private static void Hook(PatchContext ctx, MethodInfo target, string prefix, string suffix)
        {
            if (target == null)
            {
                SentisTestsPlugin.Log.Warn("FrameProbe: hook target not found for " + prefix);
                return;
            }
            var pattern = ctx.GetPattern(target);
            pattern.Prefixes.Add(typeof(FrameProbe).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic));
            pattern.Suffixes.Add(typeof(FrameProbe).GetMethod(suffix, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void FramePrefix()
        {
            _gameThreadId = Thread.CurrentThread.ManagedThreadId;
            Array.Clear(_sectionTicks, 0, _sectionTicks.Length);
            for (var g = 0; g < 3; g++) _gcAtFrameStart[g] = GC.CollectionCount(g);
            _frameStart = Stopwatch.GetTimestamp();
        }

        private static void FrameSuffix()
        {
            if (_frameStart == 0) return;
            var ms = (Stopwatch.GetTimestamp() - _frameStart) * 1000.0 / Stopwatch.Frequency;
            _frameStart = 0;
            _frameIndex++;
            _busyMs.Add(ms);
            if (ms <= ReportAboveMs) return;
            if (_worstMs.Count >= WorstKept && ms <= _worstMs.Min()) return;

            var sb = new StringBuilder();
            sb.Append("#").Append(_frameIndex).Append(' ').Append(ms.ToString("F2")).Append("ms [");
            double attributed = 0;
            for (var i = 0; i < (int)Section.Count; i++)
            {
                var sectionMs = _sectionTicks[i] * 1000.0 / Stopwatch.Frequency;
                // Build is nested in tools10, bridge in harness
                if (i != (int)Section.ProjectorBuild && i != (int)Section.Bridge) attributed += sectionMs;
                sb.Append(SectionNames[i]).Append('=').Append(sectionMs.ToString("F2")).Append(' ');
            }
            sb.Append("other=").Append(Math.Max(0, ms - attributed).ToString("F2"))
                .Append(" gcDelta0/1/2=").Append(GC.CollectionCount(0) - _gcAtFrameStart[0]).Append('/')
                .Append(GC.CollectionCount(1) - _gcAtFrameStart[1]).Append('/')
                .Append(GC.CollectionCount(2) - _gcAtFrameStart[2]).Append(']');

            if (_worstMs.Count >= WorstKept)
            {
                var min = _worstMs.IndexOf(_worstMs.Min());
                _worstMs.RemoveAt(min);
                _worst.RemoveAt(min);
            }
            _worstMs.Add(ms);
            _worst.Add(sb.ToString());
        }

        private static void Begin(Section s)
        {
            if (Thread.CurrentThread.ManagedThreadId != _gameThreadId) return;
            if (_sectionDepth[(int)s]++ == 0) _sectionStart[(int)s] = Stopwatch.GetTimestamp();
        }

        private static void End(Section s)
        {
            if (Thread.CurrentThread.ManagedThreadId != _gameThreadId) return;
            if (_sectionDepth[(int)s] == 0) return;
            if (--_sectionDepth[(int)s] == 0)
                _sectionTicks[(int)s] += Stopwatch.GetTimestamp() - _sectionStart[(int)s];
        }

        private static void ToolsPrefix() => Begin(Section.Tools);
        private static void ToolsSuffix() => End(Section.Tools);
        private static void BuildPrefix(MySlimBlock cubeBlock)
        {
            Begin(Section.ProjectorBuild);
            _buildStart = Stopwatch.GetTimestamp();
        }

        private static void BuildSuffix(MySlimBlock cubeBlock)
        {
            End(Section.ProjectorBuild);
            if (Thread.CurrentThread.ManagedThreadId != _gameThreadId || _buildStart == 0) return;
            var ms = (Stopwatch.GetTimestamp() - _buildStart) * 1000.0 / Stopwatch.Frequency;
            _buildStart = 0;
            _buildMs.Add(ms);
            if (ms > 5.0 && _slowBuilds.Count < 20)
                _slowBuilds.Add("#" + (_frameIndex + 1) + " n=" + _buildMs.Count + " " + ms.ToString("F2") + "ms " +
                                cubeBlock?.BlockDefinition?.Id.SubtypeName + "@" + cubeBlock?.Position);
        }
        private static void PhysicsPrefix() => Begin(Section.Physics);
        private static void PhysicsSuffix() => End(Section.Physics);
        public static void HarnessBegin() => Begin(Section.Harness);
        public static void HarnessEnd() => End(Section.Harness);
        public static void BridgeBegin() => Begin(Section.Bridge);
        public static void BridgeEnd() => End(Section.Bridge);

        /// <summary>Formats statistics for frames since the last call and clears the window.</summary>
        public static string Take()
        {
            var frames = _busyMs.ToArray();
            var worst = _worst.Zip(_worstMs, (text, ms) => new { text, ms })
                .OrderByDescending(x => x.ms).Select(x => x.text).ToList();
            var builds = _buildMs.ToArray();
            var slowBuilds = string.Join("; ", _slowBuilds);
            _buildMs.Clear();
            _slowBuilds.Clear();
            _busyMs.Clear();
            _worst.Clear();
            _worstMs.Clear();
            if (!_installed) return "sim-work: probe not installed";
            if (frames.Length == 0) return "sim-work: no frames";

            Array.Sort(frames);
            Func<double, double> pct = p => frames[(int)Math.Min(frames.Length - 1, Math.Floor(p * (frames.Length - 1)))];
            var over = frames.Count(f => f > BudgetMs);
            var over33 = frames.Count(f => f > 2 * BudgetMs);
            var sb = new StringBuilder();
            sb.AppendFormat("sim-work frames={0} avg={1:F2}ms p50={2:F2} p95={3:F2} p99={4:F2} p99.9={5:F2} max={6:F2}ms over16.7/33.3={7}/{8}",
                frames.Length, frames.Average(), pct(0.5), pct(0.95), pct(0.99), pct(0.999), frames[frames.Length - 1],
                over, over33);
            if (builds.Length > 0)
            {
                Array.Sort(builds);
                sb.AppendFormat(" | builds={0} avg={1:F3}ms p99={2:F3} max={3:F2}ms >5ms: {4}", builds.Length,
                    builds.Average(), builds[(int)Math.Floor(0.99 * (builds.Length - 1))], builds[builds.Length - 1],
                    slowBuilds.Length == 0 ? "none" : slowBuilds);
            }
            if (worst.Count > 0)
                sb.Append(" | worst: ").Append(string.Join("; ", worst));
            return sb.ToString();
        }
    }
}
