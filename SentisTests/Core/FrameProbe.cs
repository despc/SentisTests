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
using Sandbox.Game.GameSystems;
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

        private enum Section { Tools, ProjectorBuild, Physics, Refinery, ConveyorPull, ConveyorPush, RefineryUpdateProduction, RefineryRebuildQueue, RefineryRebuildQueue2, InventoryTransfer, QueueInsert, QueueClear, RefineryProcess, InvTransferOrRemove, InvAddItems, ObCreate, InvFitsBlueprint, QueueRemoveRequest, SinkSetRequired, EntitiesBefore, EntitiesAfter, SessionComponents, Harness, Bridge, Count }

        private static readonly string[] SectionNames = { "tools10", "projector.Build", "physics", "refinery.tick", "conveyor.pull", "conveyor.push", "refinery.updateProduction", "refinery.rebuildQueue", "sgi.rebuildQueue", "inventory.transfer", "queue.insert", "queue.clear", "refinery.process", "inv.transferOrRemove", "inv.addItems", "ob.createNewObject", "inv.fitsBlueprint", "queue.removeRequest", "sink.setRequired", "entities.before", "entities.after", "session.components", "harness", "bridge" };

        // Sections timed inside another section; excluded from the top-level sum behind "other".
        private static readonly bool[] Nested = { false, true, false, false, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, false, true };
        private static readonly long[] _sectionTicks = new long[(int)Section.Count];
        private static readonly long[] _sectionStart = new long[(int)Section.Count];
        private static readonly int[] _sectionDepth = new int[(int)Section.Count];
        // Whole-window totals, so the steady load is explained and not only the worst frames.
        private static readonly long[] _windowTicks = new long[(int)Section.Count];
        private static readonly long[] _windowCalls = new long[(int)Section.Count];
        private static readonly long[] _windowAllocBytes = new long[(int)Section.Count];
        private static readonly long[] _sectionAllocStart = new long[(int)Section.Count];
        private static long _frameAllocStart;
        private static long _windowFrameAllocBytes;
        private static long _emptyPulls;
        // Frames by the highest GC generation collected inside them (-1: none): count and busy ms.
        private static readonly long[] _gcFrames = new long[4];
        private static readonly double[] _gcFrameMs = new double[4];

        private static readonly List<double> _busyMs = new List<double>();
        private static readonly List<string> _worst = new List<string>();
        private static readonly List<double> _worstMs = new List<double>();
        private static readonly List<double> _buildMs = new List<double>();
        private static readonly List<string> _slowBuilds = new List<string>();
        private static long _buildStart;
        private static readonly int[] _gcAtFrameStart = new int[3];
        private static int _refineryTicksThisFrame;
        private static readonly List<int> _refineryTicksPerFrame = new List<int>();
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
            Hook(ctx, typeof(MyRefinery).GetMethod("DoUpdateTimerTick", any, null, Type.EmptyTypes, null),
                nameof(RefineryPrefix), nameof(RefinerySuffix));
            Hook(ctx, typeof(MyGridConveyorSystem).GetMethod("PullItems", any),
                nameof(PullPrefix), nameof(PullSuffix));
            Hook(ctx, typeof(MyGridConveyorSystem).GetMethod("PushAnyRequest", any),
                nameof(PushPrefix), nameof(PushSuffix));
            Hook(ctx, typeof(MyRefinery).GetMethod("UpdateProduction", any),
                nameof(UpdateProductionPrefix), nameof(UpdateProductionSuffix));
            var sgiRebuild = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("SentisGameplayImprovements.RefineryPatchs", false))
                .FirstOrDefault(type => type != null)?.GetMethod("DoUpdateTimerTickPatch", any);
            if (sgiRebuild != null) Hook(ctx, sgiRebuild, nameof(SgiRebuildPrefix), nameof(SgiRebuildSuffix));
            Hook(ctx, typeof(Sandbox.Game.MyInventory).GetMethod("TransferItemsInternal", any),
                nameof(TransferPrefix), nameof(TransferSuffix));
            Hook(ctx, typeof(MyProductionBlock).GetMethod("InsertQueueItemRequest", any, null,
                    new[] { typeof(int), typeof(Sandbox.Definitions.MyBlueprintDefinitionBase), typeof(VRage.MyFixedPoint) }, null),
                nameof(QueueInsertPrefix), nameof(QueueInsertSuffix));
            Hook(ctx, typeof(MyProductionBlock).GetMethod("ClearQueue", any),
                nameof(QueueClearPrefix), nameof(QueueClearSuffix));
            Hook(ctx, typeof(Sandbox.Game.MyInventory).GetMethod("TransferOrRemove", any),
                nameof(TransferOrRemovePrefix), nameof(TransferOrRemoveSuffix));
            Hook(ctx, typeof(Sandbox.Game.MyInventory).GetMethod("AddItems", any, null,
                    new[] { typeof(VRage.MyFixedPoint), typeof(VRage.ObjectBuilders.MyObjectBuilder_Base) }, null),
                nameof(AddItemsPrefix), nameof(AddItemsSuffix));
            Hook(ctx, typeof(VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen).GetMethod("CreateNewObject", any, null,
                    new[] { typeof(VRage.Game.MyDefinitionId) }, null),
                nameof(ObCreatePrefix), nameof(ObCreateSuffix));
            Hook(ctx, typeof(Sandbox.Game.MyInventory).GetMethod("ComputeAmountThatFits", any, null,
                    new[] { typeof(Sandbox.Definitions.MyBlueprintDefinitionBase) }, null),
                nameof(FitsPrefix), nameof(FitsSuffix));
            // Totals only (marked nested): these contain the refinery and physics sections.
            Hook(ctx, typeof(Sandbox.Game.Entities.MyEntities).GetMethod("UpdateBeforeSimulation", BindingFlags.Static | BindingFlags.Public),
                nameof(EntitiesBeforePrefix), nameof(EntitiesBeforeSuffix));
            Hook(ctx, typeof(Sandbox.Game.Entities.MyEntities).GetMethod("UpdateAfterSimulation", BindingFlags.Static | BindingFlags.Public),
                nameof(EntitiesAfterPrefix), nameof(EntitiesAfterSuffix));
            Hook(ctx, typeof(Sandbox.Game.World.MySession).GetMethod("UpdateComponents", any, null, Type.EmptyTypes, null),
                nameof(SessionComponentsPrefix), nameof(SessionComponentsSuffix));
            Hook(ctx, typeof(Sandbox.Game.EntityComponents.MyResourceSinkComponent).GetMethod("SetRequiredInputByType", any),
                nameof(SinkPrefix), nameof(SinkSuffix));
            Hook(ctx, typeof(MyRefinery).GetMethod("ProcessQueueItems", any),
                nameof(ProcessPrefix), nameof(ProcessSuffix));
            Hook(ctx, typeof(MyProductionBlock).GetMethod("RemoveQueueItemRequest", any),
                nameof(QueueRemovePrefix), nameof(QueueRemoveSuffix));
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
            _refineryTicksThisFrame = 0;
            _frameAllocStart = GC.GetAllocatedBytesForCurrentThread();
            _frameStart = Stopwatch.GetTimestamp();
        }

        private static void FrameSuffix()
        {
            if (_frameStart == 0) return;
            var ms = (Stopwatch.GetTimestamp() - _frameStart) * 1000.0 / Stopwatch.Frequency;
            _frameStart = 0;
            _windowFrameAllocBytes += GC.GetAllocatedBytesForCurrentThread() - _frameAllocStart;
            _frameIndex++;
            _busyMs.Add(ms);
            _refineryTicksPerFrame.Add(_refineryTicksThisFrame);
            var gcBucket = 0;
            for (var g = 2; g >= 0; g--)
                if (GC.CollectionCount(g) != _gcAtFrameStart[g]) { gcBucket = g + 1; break; }
            _gcFrames[gcBucket]++;
            _gcFrameMs[gcBucket] += ms;
            if (ms <= ReportAboveMs) return;
            if (_worstMs.Count >= WorstKept && ms <= _worstMs.Min()) return;

            var sb = new StringBuilder();
            sb.Append("#").Append(_frameIndex).Append(' ').Append(ms.ToString("F2")).Append("ms [");
            if (_refineryTicksThisFrame > 0) sb.Append("refineryTicks=").Append(_refineryTicksThisFrame).Append(' ');
            double attributed = 0;
            for (var i = 0; i < (int)Section.Count; i++)
            {
                var sectionMs = _sectionTicks[i] * 1000.0 / Stopwatch.Frequency;
                if (!Nested[i]) attributed += sectionMs;
                if (sectionMs < 0.005) continue;
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
            if (_sectionDepth[(int)s]++ == 0)
            {
                _sectionAllocStart[(int)s] = GC.GetAllocatedBytesForCurrentThread();
                _sectionStart[(int)s] = Stopwatch.GetTimestamp();
            }
        }

        private static void End(Section s)
        {
            if (Thread.CurrentThread.ManagedThreadId != _gameThreadId) return;
            if (_sectionDepth[(int)s] == 0) return;
            if (--_sectionDepth[(int)s] == 0)
            {
                var elapsed = Stopwatch.GetTimestamp() - _sectionStart[(int)s];
                _sectionTicks[(int)s] += elapsed;
                _windowTicks[(int)s] += elapsed;
                _windowCalls[(int)s]++;
                _windowAllocBytes[(int)s] += GC.GetAllocatedBytesForCurrentThread() - _sectionAllocStart[(int)s];
            }
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
        private static void RefineryPrefix()
        {
            if (Thread.CurrentThread.ManagedThreadId == _gameThreadId && _sectionDepth[(int)Section.Refinery] == 0)
                _refineryTicksThisFrame++;
            Begin(Section.Refinery);
        }
        private static void RefinerySuffix() => End(Section.Refinery);
        private static void PullPrefix() => Begin(Section.ConveyorPull);
        private static void PullSuffix(VRage.MyFixedPoint __result)
        {
            if (Thread.CurrentThread.ManagedThreadId == _gameThreadId && __result == 0) _emptyPulls++;
            End(Section.ConveyorPull);
        }
        private static void UpdateProductionPrefix() => Begin(Section.RefineryUpdateProduction);
        private static void UpdateProductionSuffix() => End(Section.RefineryUpdateProduction);
        private static void RebuildQueuePrefix() => Begin(Section.RefineryRebuildQueue);
        private static void RebuildQueueSuffix() => End(Section.RefineryRebuildQueue);
        private static void SgiRebuildPrefix() => Begin(Section.RefineryRebuildQueue2);
        private static void SgiRebuildSuffix() => End(Section.RefineryRebuildQueue2);
        private static void TransferPrefix() => Begin(Section.InventoryTransfer);
        private static void TransferSuffix() => End(Section.InventoryTransfer);
        private static void QueueInsertPrefix() => Begin(Section.QueueInsert);
        private static void QueueInsertSuffix() => End(Section.QueueInsert);
        private static void QueueClearPrefix() => Begin(Section.QueueClear);
        private static void QueueClearSuffix() => End(Section.QueueClear);
        private static void TransferOrRemovePrefix() => Begin(Section.InvTransferOrRemove);
        private static void TransferOrRemoveSuffix() => End(Section.InvTransferOrRemove);
        private static void AddItemsPrefix() => Begin(Section.InvAddItems);
        private static void AddItemsSuffix() => End(Section.InvAddItems);
        private static void ObCreatePrefix() => Begin(Section.ObCreate);
        private static void ObCreateSuffix() => End(Section.ObCreate);
        private static void FitsPrefix() => Begin(Section.InvFitsBlueprint);
        private static void FitsSuffix() => End(Section.InvFitsBlueprint);
        private static void EntitiesBeforePrefix() => Begin(Section.EntitiesBefore);
        private static void EntitiesBeforeSuffix() => End(Section.EntitiesBefore);
        private static void EntitiesAfterPrefix() => Begin(Section.EntitiesAfter);
        private static void EntitiesAfterSuffix() => End(Section.EntitiesAfter);
        private static void SessionComponentsPrefix() => Begin(Section.SessionComponents);
        private static void SessionComponentsSuffix() => End(Section.SessionComponents);
        private static void SinkPrefix() => Begin(Section.SinkSetRequired);
        private static void SinkSuffix() => End(Section.SinkSetRequired);
        private static void ProcessPrefix() => Begin(Section.RefineryProcess);
        private static void ProcessSuffix() => End(Section.RefineryProcess);
        private static void QueueRemovePrefix() => Begin(Section.QueueRemoveRequest);
        private static void QueueRemoveSuffix() => End(Section.QueueRemoveRequest);
        private static void PushPrefix() => Begin(Section.ConveyorPush);
        private static void PushSuffix() => End(Section.ConveyorPush);
        private static void PhysicsPrefix() => Begin(Section.Physics);
        private static void PhysicsSuffix() => End(Section.Physics);
        public static void HarnessBegin() => Begin(Section.Harness);
        public static void HarnessEnd() => End(Section.Harness);
        public static void BridgeBegin() => Begin(Section.Bridge);
        public static void BridgeEnd() => End(Section.Bridge);

        private static Type _gcSchedulerType;

        /// <summary>Scheduled gen0 collections run by SentisOptimisations (measured there, reset here).</summary>
        private static void AppendScheduledGc(StringBuilder sb)
        {
            if (_gcSchedulerType == null)
                _gcSchedulerType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Optimizer.Optimizations.GcScheduler", false))
                    .FirstOrDefault(type => type != null);
            if (_gcSchedulerType == null) return;
            Func<string, object> get = name => _gcSchedulerType.GetField(name)?.GetValue(null);
            var count = Convert.ToInt64(get("Collections") ?? 0L);
            var total = Convert.ToDouble(get("TotalPauseMs") ?? 0.0);
            sb.AppendFormat("| scheduled gen0={0} pause avg={1:F2}ms max={2:F2}ms, frames over 16.7 with pause={3} ",
                count, count > 0 ? total / count : 0, Convert.ToDouble(get("MaxPauseMs") ?? 0.0),
                Convert.ToInt64(get("FramesOverBudgetWithPause") ?? 0L));
            foreach (var name in new[] { "Collections", "FramesOverBudgetWithPause" })
                _gcSchedulerType.GetField(name)?.SetValue(null, 0L);
            foreach (var name in new[] { "TotalPauseMs", "MaxPauseMs" })
                _gcSchedulerType.GetField(name)?.SetValue(null, 0.0);
        }

        /// <summary>Formats statistics for frames since the last call and clears the window.</summary>
        public static string Take()
        {
            var frames = _busyMs.ToArray();
            var worst = _worst.Zip(_worstMs, (text, ms) => new { text, ms })
                .OrderByDescending(x => x.ms).Select(x => x.text).ToList();
            var refineryTicks = _refineryTicksPerFrame.ToArray();
            _refineryTicksPerFrame.Clear();
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
            var tickFrames = refineryTicks.Count(n => n > 0);
            if (tickFrames > 0)
            {
                var sortedTicks = refineryTicks.Where(n => n > 0).OrderBy(n => n).ToArray();
                sb.AppendFormat(" | refinery ticks={0} in {1}/{2} frames, per busy frame p50={3} max={4}",
                    refineryTicks.Sum(), tickFrames, refineryTicks.Length, sortedTicks[sortedTicks.Length / 2],
                    sortedTicks[sortedTicks.Length - 1]);
            }
            if (builds.Length > 0)
            {
                Array.Sort(builds);
                sb.AppendFormat(" | builds={0} avg={1:F3}ms p99={2:F3} max={3:F2}ms >5ms: {4}", builds.Length,
                    builds.Average(), builds[(int)Math.Floor(0.99 * (builds.Length - 1))], builds[builds.Length - 1],
                    slowBuilds.Length == 0 ? "none" : slowBuilds);
            }
            sb.Append(" | totals:");
            for (var i = 0; i < (int)Section.Count; i++)
            {
                if (_windowCalls[i] == 0) continue;
                var totalMs = _windowTicks[i] * 1000.0 / Stopwatch.Frequency;
                sb.AppendFormat(" {0}={1:F0}ms/{2}calls({3:F3}ms each, {4:F2}ms/frame, {5:F0}KB alloc)", SectionNames[i],
                    totalMs, _windowCalls[i], totalMs / _windowCalls[i], totalMs / frames.Length,
                    _windowAllocBytes[i] / 1024.0);
            }
            if (_windowCalls[(int)Section.ConveyorPull] > 0) sb.Append(" emptyPulls=").Append(_emptyPulls);
            sb.AppendFormat(" | GC mode: server={0} latency={1}; frames by GC: ", System.Runtime.GCSettings.IsServerGC,
                System.Runtime.GCSettings.LatencyMode);
            string[] gcNames = { "none", "gen0", "gen1", "gen2" };
            for (var g = 0; g < 4; g++)
                if (_gcFrames[g] > 0)
                    sb.AppendFormat("{0}={1} (avg {2:F2}ms) ", gcNames[g], _gcFrames[g], _gcFrameMs[g] / _gcFrames[g]);
            AppendScheduledGc(sb);
            Array.Clear(_gcFrames, 0, _gcFrames.Length);
            Array.Clear(_gcFrameMs, 0, _gcFrameMs.Length);
            Array.Clear(_windowTicks, 0, _windowTicks.Length);
            Array.Clear(_windowCalls, 0, _windowCalls.Length);
            sb.AppendFormat(" gameThreadAlloc={0:F0}MB", _windowFrameAllocBytes / 1048576.0);
            Array.Clear(_windowAllocBytes, 0, _windowAllocBytes.Length);
            _windowFrameAllocBytes = 0;
            _emptyPulls = 0;
            if (worst.Count > 0)
                sb.Append(" | worst: ").Append(string.Join("; ", worst));
            return sb.ToString();
        }
    }
}
