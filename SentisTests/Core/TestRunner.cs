using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NLog;
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace SentisTests.Core
{
    public enum TestVerdict
    {
        Passed,
        Failed,
        Error,
        Timeout,
        Aborted,
    }

    public class TestResult
    {
        public string Scenario;
        public TestVerdict Verdict;
        public double DurationSeconds;
        public string Message;
        public string Metrics;
        public List<string> Notes = new List<string>();

        public override string ToString()
        {
            return string.Format("{0} {1} ({2:F1}s) {3}", Verdict.ToString().ToUpperInvariant(), Scenario,
                DurationSeconds, string.IsNullOrEmpty(Message) ? "" : "- " + Message);
        }
    }

    /// <summary>
    /// Runs scenarios as game-thread coroutines. A scenario yields to wait for the next tick;
    /// yielding another IEnumerator runs it as a nested coroutine (so helpers can be composed).
    /// An assertion failure throws <see cref="ScenarioFailedException"/>; a timeout is enforced
    /// against the wall clock. Cleanup always runs, on every outcome.
    /// </summary>
    public static class TestRunner
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static readonly List<TestResult> _history = new List<TestResult>();
        private static readonly object _historyLock = new object();
        private static readonly Queue<string> _queue = new Queue<string>();
        private static readonly Stack<IEnumerator> _stack = new Stack<IEnumerator>();

        private static TestScenario _active;
        private static Stopwatch _stopwatch;

        public static TestScenario Active { get { return _active; } }
        public static string ReportDirectory;

        public static IReadOnlyList<TestResult> Snapshot()
        {
            lock (_historyLock)
            {
                return new List<TestResult>(_history);
            }
        }

        /// <summary>
        /// When set, the next started scenario builds its structures around this point instead of
        /// the scenario default (used when an admin runs a test from the field: 30 m in front of
        /// the character's face). Consumed by Start().
        /// </summary>
        public static Vector3D? PendingOrigin;

        /// <summary>
        /// False when the run was requested interactively by a live admin: test entities stay in
        /// the world until "!test cleanup" removes them.
        /// </summary>
        public static bool PendingAutoCleanup = true;

        /// <summary>Origin for the currently active run (null = scenario default). Set by Start().</summary>
        public static Vector3D? RunOrigin { get; private set; }

        public static bool RunAutoCleanup { get; private set; } = true;

        public static void Enqueue(IEnumerable<string> names, Vector3D? origin = null, bool autoCleanup = true)
        {
            PendingOrigin = origin;
            PendingAutoCleanup = autoCleanup;
            foreach (var n in names)
                _queue.Enqueue(n);
        }

        public static void EnqueueAll(Vector3D? origin = null, bool autoCleanup = true)
        {
            PendingOrigin = origin;
            PendingAutoCleanup = autoCleanup;
            foreach (var name in ScenarioRegistry.Names)
                _queue.Enqueue(name);
        }

        public static void StopActive(string reason)
        {
            if (_active == null && _queue.Count == 0)
                return;
            _queue.Clear();
            if (_active != null)
            {
                Finish(TestVerdict.Aborted, reason);
            }
        }

        private static readonly List<KeyValuePair<IMyEntity, DateTime>> _cleanupQueue =
            new List<KeyValuePair<IMyEntity, DateTime>>();

        // entities of interactively-run scenarios: kept until an admin calls "!test cleanup"
        private static readonly List<IMyEntity> _held = new List<IMyEntity>();

        /// <summary>Remove every held/queued test entity right now. Returns how many were removed.</summary>
        public static int CleanupNow()
        {
            var count = 0;
            foreach (var entity in _held.Concat(_cleanupQueue.Select(kvp => kvp.Key)).ToList())
            {
                try
                {
                    if (entity == null || entity.MarkedForClose)
                        continue;
                    MyAPIGateway.Entities.RemoveEntity(entity);
                    entity.Close();
                    count++;
                }
                catch (Exception e)
                {
                    Log.Warn("cleanup: cannot remove {0}: {1}", entity == null ? 0 : entity.EntityId, e.Message);
                }
            }
            _held.Clear();
            _cleanupQueue.Clear();
            Log.Info("manual cleanup removed {0} test entities", count);
            return count;
        }

        /// <summary>Call once per game tick from the plugin Update().</summary>
        public static void Tick()
        {
            try
            {
                ProcessCleanupQueue();
                if (_active == null)
                {
                    if (_queue.Count > 0)
                    {
                        var name = _queue.Dequeue();
                        try
                        {
                            Start(name);
                        }
                        catch (Exception e)
                        {
                            Log.Error(e, "[TEST] cannot start " + name);
                        }
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                if (_stopwatch.Elapsed.TotalSeconds > _active.TimeoutSeconds)
                {
                    Finish(TestVerdict.Timeout,
                        string.Format("timeout after {0:F0}s at: {1}", _active.TimeoutSeconds, _active.Progress));
                    return;
                }

                    if (!Advance())
                        Finish(TestVerdict.Passed, _active.Progress);
                }
            }
            catch (ScenarioFailedException e)
            {
                Finish(TestVerdict.Failed, e.Message);
            }
            catch (Exception e)
            {
                var crashed = _active != null ? _active.Name : "?";
                Finish(TestVerdict.Error, e.GetType().Name + ": " + e.Message);
                Log.Error(e, "scenario " + crashed + " crashed");
            }
        }

        private static bool Advance()
        {
            while (_stack.Count > 0)
            {
                var top = _stack.Peek();
                bool moved;
                try
                {
                    moved = top.MoveNext();
                }
                catch (ScenarioFailedException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    throw new ScenarioFailedException("step threw at [" + _active.Progress + "]: " + e.Message);
                }

                if (!moved)
                {
                    _stack.Pop();
                    continue;
                }

                _active.TicksRun++;
                var current = top.Current;
                if (current is IEnumerator nested)
                {
                    _stack.Push(nested);
                    continue;
                }

                return true; // null/other yield: resume next tick
            }

            return false;
        }

        private static void ProcessCleanupQueue()
        {
            if (_cleanupQueue.Count == 0)
                return;

            var now = DateTime.UtcNow;
            for (var i = _cleanupQueue.Count - 1; i >= 0; i--)
            {
                if (_cleanupQueue[i].Value > now)
                    continue;

                var entity = _cleanupQueue[i].Key;
                _cleanupQueue.RemoveAt(i);
                try
                {
                    if (entity == null || entity.MarkedForClose)
                        continue;
                    MyAPIGateway.Entities.RemoveEntity(entity);
                    entity.Close();
                }
                catch (Exception e)
                {
                    Log.Warn("deferred cleanup: cannot remove {0}: {1}",
                        entity == null ? 0 : entity.EntityId, e.Message);
                }
            }
        }

        public static TickMetrics.Snapshot BaselineMetrics;

        private static void Finish(TestVerdict verdict, string message)
        {
            var scenario = _active;
            var during = TickMetrics.Take();
            var result = new TestResult
            {
                Scenario = scenario.Name,
                Verdict = verdict,
                Metrics = "baseline: " + (BaselineMetrics != null ? BaselineMetrics.Format() : "n/a") +
                          " || during: " + during.Format(),
                DurationSeconds = _stopwatch == null ? 0 : _stopwatch.Elapsed.TotalSeconds,
                Message = message,
            };

            // hand the spawned entities to the delayed cleanup queue so admins can inspect them;
            // interactive admin runs hold them indefinitely until "!test cleanup"
            if (!RunAutoCleanup)
            {
                foreach (var entity in scenario.TakeTracked())
                    if (entity != null) _held.Add(entity);
                Log.Info("{0} left its {1} entities in the world for inspection; run !test cleanup when done",
                    scenario.Name, _held.Count);
            }
            else if (SentisTestsPlugin.Config == null || SentisTestsPlugin.Config.CleanupAfterTests)
            {
                var delay = SentisTestsPlugin.Config?.CleanupDelaySeconds ?? 0;
                var due = DateTime.UtcNow.AddSeconds(Math.Max(0, delay));
                foreach (var entity in scenario.TakeTracked())
                    _cleanupQueue.Add(new KeyValuePair<IMyEntity, DateTime>(entity, due));
                Log.Info("cleanup of {0} scheduled in {1}s ({2} entities remain in the world meanwhile)",
                    scenario.Name, delay, _cleanupQueue.Count);
            }
            else
            {
                Log.Info("cleanup skipped by config; entities of " + scenario.Name + " remain in the world");
            }

            foreach (var progress in scenario.ProgressLog)
                result.Notes.Add(progress);

            _active = null;
            _stack.Clear();
            _stopwatch = null;

            lock (_historyLock)
            {
                _history.Add(result);
            }

            var line = "[TEST] " + result;
            if (verdict == TestVerdict.Passed)
                Log.Info(line);
            else
                Log.Error(line);
            WriteReport(result);
        }

        public static void Start(string name)
        {
            if (_active != null)
                throw new ScenarioFailedException("scenario already running: " + _active.Name);

            var scenario = ScenarioRegistry.Create(name);
            RunOrigin = PendingOrigin;
            RunAutoCleanup = PendingAutoCleanup;
            PendingOrigin = null;
            BaselineMetrics = TickMetrics.Take();
            _active = scenario;
            _stopwatch = Stopwatch.StartNew();
            _stack.Clear();
            _stack.Push(scenario.Run());
            Log.Info("[TEST] started: " + name);
        }

        private static void WriteReport(TestResult result)
        {
            try
            {
                if (string.IsNullOrEmpty(ReportDirectory))
                    return;
                Directory.CreateDirectory(ReportDirectory);
                var sb = new StringBuilder();
                sb.AppendLine("# " + result.Scenario + " - " + result.Verdict);
                sb.AppendLine("- time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("- duration: " + result.DurationSeconds.ToString("F1") + " s");
                if (!string.IsNullOrEmpty(result.Metrics))
                    sb.AppendLine("- main thread: " + result.Metrics);
                if (!string.IsNullOrEmpty(result.Message))
                    sb.AppendLine("- message: " + result.Message);
                foreach (var note in result.Notes)
                    sb.AppendLine("- note: " + note);

                var file = Path.Combine(ReportDirectory,
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + result.Scenario + ".md");
                File.WriteAllText(file, sb.ToString());
                File.WriteAllText(Path.Combine(ReportDirectory, "latest.md"), sb.ToString());
            }
            catch (Exception e)
            {
                Log.Error(e, "cannot write test report");
            }
        }
    }
}
