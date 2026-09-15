using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using NLog;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.ModAPI;

namespace SentisTests.Core
{
    /// <summary>
    /// Base class for a live-server test scenario. <see cref="Run"/> is a coroutine driven one
    /// MoveNext per game tick; use <see cref="Wait"/>, <see cref="Check"/> and friends.
    /// All entities created through <see cref="Track"/> are destroyed in <see cref="Cleanup"/>.
    /// </summary>
    public abstract class TestScenario
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly List<IMyEntity> _tracked = new List<IMyEntity>();
        private readonly List<string> _progressLog = new List<string>();

        public abstract string Name { get; }

        public virtual int TimeoutSeconds
        {
            get { return SentisTestsPlugin.Config?.DefaultTimeoutSeconds ?? 300; }
        }

        public int TicksRun { get; internal set; }

        /// <summary>Human readable position in the scenario, shown in timeouts/failures.</summary>
        public string Progress { get; private set; } = "init";

        public IReadOnlyList<string> ProgressLog { get { return _progressLog; } }

        /// <summary>The entities this run tracked (cleanup skips them, it owns them).</summary>
        internal IReadOnlyList<IMyEntity> Tracked { get { return _tracked; } }

        public abstract IEnumerator Run();

        /// <summary>Moves the tracked list out without deleting anything (deferred cleanup).</summary>
        public List<IMyEntity> TakeTracked()
        {
            var copy = new List<IMyEntity>(_tracked);
            _tracked.Clear();
            return copy;
        }

        public virtual void Cleanup()
        {
            for (var i = _tracked.Count - 1; i >= 0; i--)
            {
                var entity = _tracked[i];
                try
                {
                    if (entity == null || entity.MarkedForClose)
                        continue;
                    MyAPIGateway.Entities.RemoveEntity(entity);
                    entity.Close();
                }
                catch (Exception e)
                {
                    Log.Warn("cleanup: cannot remove {0}: {1}", entity.EntityId, e.Message);
                }
            }

            _tracked.Clear();
        }

        /// <summary>
        /// Removes what <see cref="Cleanup"/> does not cover: debris the test knocked loose
        /// (blocks that flew off and became their own tiny grids) and stale test grids from a
        /// previous crashed/interrupted run. Called by the runner once Run() has finished, on
        /// every verdict (pass, fail, timeout, error). Skipped when the run is kept for
        /// inspection.
        /// </summary>
        public virtual void CleanupLeftovers() { }

        // ---------------------------------------------------------------- helpers

        protected void Track(IMyEntity entity)
        {
            if (entity != null && !_tracked.Contains(entity))
                _tracked.Add(entity);
        }

        protected void Note(string message)
        {
            Progress = message;
            _progressLog.Add("[" + TicksRun + "] " + message);
            Log.Info("[TEST:{0}] {1}", Name, message);
        }

        protected static void Check(bool condition, string what)
        {
            if (!condition)
                throw new ScenarioFailedException(what);
        }

        protected static T Require<T>(object value, string what) where T : class
        {
            var typed = value as T;
            if (typed == null)
                throw new ScenarioFailedException(what + " (got " + (value == null ? "null" : value.GetType().Name) + ")");
            return typed;
        }

        /// <summary>Waits until the condition holds; fails the scenario after maxSeconds.</summary>
        protected IEnumerator Wait(Func<bool> condition, string description, int maxSeconds = 60)
        {
            var started = DateTime.UtcNow;
            while (!SafeInvoke(condition))
            {
                if ((DateTime.UtcNow - started).TotalSeconds > maxSeconds)
                    throw new ScenarioFailedException("condition not met in " + maxSeconds + "s: " + description);
                yield return null;
            }
        }

        /// <summary>Waits a fixed number of ticks.</summary>
        protected IEnumerator WaitForTicks(int ticks)
        {
            for (var i = 0; i < ticks; i++)
                yield return null;
        }

        /// <summary>Waits real time while logging progress every ~5 seconds.</summary>
        protected IEnumerator WaitForSeconds(double seconds, string description)
        {
            var started = DateTime.UtcNow;
            double lastLogged = -10;
            while ((DateTime.UtcNow - started).TotalSeconds < seconds)
            {
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                if (elapsed - lastLogged > 5)
                {
                    Note(description + " (" + elapsed.ToString("F0") + "/" + seconds.ToString("F0") + "s)");
                    lastLogged = elapsed;
                }

                yield return null;
            }
        }

        /// <summary>
        /// Waits while polling a value until stop(value) returns true or timeout.
        /// Returns the last observed value.
        /// </summary>
        protected IEnumerator<double> WaitValue<T>(Func<T> read, Func<T, bool> stop, Func<T, string> render,
            int maxSeconds = 120)
        {
            var started = DateTime.UtcNow;
            T last = default(T);
            while (true)
            {
                last = SafeRead(read);
                if (stop(last))
                    break;
                if ((DateTime.UtcNow - started).TotalSeconds > maxSeconds)
                    throw new ScenarioFailedException("value condition not met in " + maxSeconds + "s, last: " + render(last));
                yield return 0.0;
            }

            yield return Convert.ToDouble(last);
        }

        private static bool SafeInvoke(Func<bool> f)
        {
            try
            {
                return f();
            }
            catch (Exception e)
            {
                Log.Warn("condition threw (treated as false): {0}", e.Message);
                return false;
            }
        }

        private static T SafeRead<T>(Func<T> f)
        {
            try
            {
                return f();
            }
            catch (Exception e)
            {
                Log.Warn("read threw (keeping previous): {0}", e.Message);
                return default(T);
            }
        }
    }
}
