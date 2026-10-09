using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Several copies of one scenario at once, far apart: each in a cluster of the physics of its own. For
    /// SentisClusters, which updates the entities of different clusters on different threads: the copies run side
    /// by side there, and every copy has to pass as it passes alone - a race between the clusters' threads shows
    /// as one copy losing work, throwing or never finishing. The frame is measured over the whole run.
    ///
    /// The copies share the fake players (<see cref="FakeClients.KeepOnRemoveAll"/> while they run); a copy's
    /// place is its scenario's own plus <see cref="TestScenario.Shift"/>.
    ///
    /// <c>cluster_twins_production</c>: 8 production chains (ore, refinery, assembler) 150 km apart;
    /// <c>cluster_twins_weld</c>: 6 welding ships building a projection, 150 km apart;
    /// <c>cluster_twins_toggle</c>: the production chains while SentisClusters' parallel mode is switched on and off
    /// every 3 s (switching in the middle of the work must lose nothing). Left as it was at the end.
    /// </summary>
    public sealed class ClusterTwinsScenario : TestScenario
    {
        public const string Production = "cluster_twins_production";
        public const string Weld = "cluster_twins_weld";
        public const string Toggle = "cluster_twins_toggle";

        private readonly string _name;
        private readonly Func<TestScenario> _make;
        private readonly int _copies;
        private readonly Vector3D _step;
        private readonly int _timeout;
        private readonly double _toggleSeconds;
        private readonly List<TestScenario> _inner = new List<TestScenario>();

        public ClusterTwinsScenario(string name, Func<TestScenario> make, int copies, Vector3D step, int timeoutSeconds, double toggleSeconds = 0)
        {
            _toggleSeconds = toggleSeconds;
            _name = name;
            _make = make;
            _copies = copies;
            _step = step;
            _timeout = timeoutSeconds;
        }

        public override string Name => _name;
        public override int TimeoutSeconds => _timeout;

        /// <summary>One copy's coroutine, with the nested waits it yields run as the runner runs them.</summary>
        private sealed class Copy
        {
            public TestScenario Scenario;
            public readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
            public Exception Failed;
            public bool Done;
            public double Seconds;

            public void Step()
            {
                while (Stack.Count > 0)
                {
                    var top = Stack.Peek();
                    if (!top.MoveNext())
                    {
                        Stack.Pop();
                        continue;
                    }
                    if (top.Current is IEnumerator nested)
                    {
                        Stack.Push(nested);
                        continue;
                    }
                    return;
                }
                Done = true;
            }
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            FakeClients.KeepOnRemoveAll = true;
            var copies = new List<Copy>();
            for (var i = 0; i < _copies; i++)
            {
                var scenario = _make();
                scenario.Shift = _step * i;
                _inner.Add(scenario);
                var copy = new Copy { Scenario = scenario };
                copy.Stack.Push(scenario.Run());
                copies.Add(copy);
            }
            Note(_copies + " copies of " + copies[0].Scenario.Name + ", " + (_step.Length() / 1000).ToString("F0") + " km apart");
            TickMetrics.Take();
            var watch = Stopwatch.StartNew();
            var lastNote = 0.0;
            var parallel = ParallelSwitch();
            var wasOn = parallel?.Get();
            var lastToggle = 0.0;
            var toggles = 0;
            if (_toggleSeconds > 0) Check(parallel != null, "SentisClusters is not here: nothing to switch");
            while (copies.Any(c => !c.Done))
            {
                if (_toggleSeconds > 0 && watch.Elapsed.TotalSeconds - lastToggle >= _toggleSeconds)
                {
                    lastToggle = watch.Elapsed.TotalSeconds;
                    parallel.Set(!parallel.Get());
                    toggles++;
                }
                foreach (var copy in copies)
                {
                    if (copy.Done) continue;
                    try
                    {
                        copy.Step();
                    }
                    catch (Exception e)
                    {
                        copy.Failed = e;
                        copy.Done = true;
                    }
                    if (copy.Done) copy.Seconds = watch.Elapsed.TotalSeconds;
                }
                if (watch.Elapsed.TotalSeconds - lastNote >= 15)
                {
                    lastNote = watch.Elapsed.TotalSeconds;
                    Note("CLUSTER TWINS | " + lastNote.ToString("F0") + " s: done " + copies.Count(c => c.Done) + " of " + copies.Count +
                         ", failed " + copies.Count(c => c.Failed != null));
                }
                yield return null;
            }
            if (wasOn.HasValue) parallel.Set(wasOn.Value);
            if (toggles > 0) Note("the parallel mode was switched " + toggles + " times");
            var metrics = TickMetrics.Take();
            var failed = copies.Where(c => c.Failed != null).ToList();
            Note("CLUSTER TWINS RESULT | " + copies.Count + " copies of " + copies[0].Scenario.Name + ": passed " + (copies.Count - failed.Count) +
                 ", times " + string.Join(" ", copies.Select(c => c.Seconds.ToString("F0") + (c.Failed != null ? "!" : ""))) + " s | " + metrics.Format() +
                 (failed.Count > 0 ? " | first failure: " + failed[0].Failed.Message : ""));
            Check(failed.Count == 0, failed.Count + " of " + copies.Count + " copies failed: " +
                                     string.Join(" | ", failed.Select(c => "#" + copies.IndexOf(c) + " " + c.Failed.Message).Take(4)));
            // the copies of one scenario take about as long: one far behind is one that lost work
            var times = copies.Select(c => c.Seconds).OrderBy(t => t).ToList();
            Check(times.Last() <= times.First() * 2 + 30, "the copies took very different times: " + string.Join(", ", times.Select(t => t.ToString("F0"))) + " s");
        }

        private sealed class Switch
        {
            public Func<bool> Get;
            public Action<bool> Set;
        }

        /// <summary>SentisClusters' Config.Parallel, by reflection; null without the plugin.</summary>
        private static Switch ParallelSwitch()
        {
            var plugin = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisClusters.SentisClustersPlugin")).FirstOrDefault(t => t != null);
            var config = plugin?.GetProperty("Config")?.GetValue(null);
            var property = config?.GetType().GetProperty("Parallel");
            if (property == null) return null;
            return new Switch { Get = () => (bool)property.GetValue(config), Set = on => property.SetValue(config, on) };
        }

        public override void Cleanup()
        {
            FakeClients.KeepOnRemoveAll = false;
            foreach (var scenario in _inner)
                try { scenario.Cleanup(); }
                catch (Exception e) { Log.Warn(e, "cleanup of a copy failed"); }
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            foreach (var scenario in _inner)
                try { scenario.CleanupLeftovers(); }
                catch (Exception) { }
        }
    }
}
