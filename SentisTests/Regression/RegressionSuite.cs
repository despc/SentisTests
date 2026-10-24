using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SentisTests.Core;
using SentisTests.Game;
using SentisTests.Scenarios;

namespace SentisTests.Regression
{
    /// <summary>
    /// All the regression checks at once, each at a site of its own (<see cref="Sites"/>): space checks 1 km apart, planet
    /// checks 300 m apart on the surface. Every check is stepped once a frame as the runner steps a scenario; each one's
    /// verdict goes to the log and the results as a scenario of its own (<c>[TEST] PASSED reg_reactor</c>), and the suite
    /// fails when any of them did. A check that is over is cleaned up at once, while the others go on.
    ///
    /// <c>regression</c> runs them all; <see cref="TestRunner.Focus"/> (DebugBridge's test call, <c>focus</c>) keeps only
    /// the checks whose names contain one of its comma-separated words.
    /// </summary>
    public sealed class RegressionSuite : TestScenario
    {
        public const string ScenarioName = "regression";

        private readonly List<Running> _runs = new List<Running>();
        private ConfigOverride _gameplay;
        private ConfigOverride _essentials;

        public override string Name => ScenarioName;

        public override int TimeoutSeconds => Math.Max(300, Registry.All.Select(c => c.Make().TimeoutSeconds).DefaultIfEmpty(0).Max() + 120);

        /// <summary>One check of the suite: its coroutine, stepped as the runner steps one.</summary>
        private sealed class Running
        {
            public RegressionCheck Check;
            public readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
            public readonly Stopwatch Watch = new Stopwatch();
            /// <summary><see cref="RegressionCheck.DeletingSeconds"/> when the check started: the cleanups' long frames are not its time.</summary>
            public double DeletingAtStart;
            public TestVerdict? Verdict;
            public string Message;

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
                    Check.TicksRun++;
                    if (top.Current is IEnumerator nested)
                    {
                        Stack.Push(nested);
                        continue;
                    }
                    return;
                }
                Verdict = TestVerdict.Passed;
                Message = Check.Progress;
            }
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            FakeClients.KeepOnRemoveAll = true;

            var focus = (TestRunner.Focus ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var chosen = Registry.All.Where(c => focus.Length == 0 || focus.Any(f => c.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            Check(chosen.Count > 0, "no regression check matches '" + TestRunner.Focus + "'");

            // the checks that need damage: SentisGameplayImprovements blocks it all for a while after the start
            if (Integrations.Missing(new[] { Integrations.Gameplay }).Count == 0)
            {
                _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
                _gameplay.Set("DisableAnyDamageAfterStartTime", 0);
                _gameplay.Set("MinimumMassForKineticDamage", 1);
                // a fast fall through the ground where the planet has no physics yet happens in the game itself; the
                // server's answer is to bring such a grid back (off in the stand's config)
                _gameplay.Set("AutoRestoreFromVoxel", true);
                // the checks' drops (death bags, stacks for collectors and the cleanup) all at once pass SGI's cap of loose
                // items, and past it SGI closes them at random: the cap is out of reach while the suite runs
                _gameplay.Set("MaxFloatingObjects", RegressionCheck.LooseItemsCap);
            }

            // Essentials' limit of death bags (BackpackLimit, 1 on the stand) counts them by the owner it reads as a bag
            // comes into the world, and the game sets the owner after that: every bag counts as nobody's, and past the
            // limit all but the newest of the whole server are closed 30 s on. The suite's deaths come together, so the
            // limit is out of reach while it runs: int.MaxValue, not -1 - Essentials skips tracking bags at -1 only for bags that come after; the ones it tracks already (the stand runs at 1) then loop to an empty list and throw every frame
            if (Integrations.IsLoaded(ConfigOverride.Essentials))
            {
                _essentials = new ConfigOverride(ConfigOverride.Essentials);
                _essentials.Set("BackpackLimit", int.MaxValue);
            }

            var next = new Dictionary<SiteKind, int>();
            foreach (var entry in chosen)
            {
                var check = entry.Make();
                next.TryGetValue(check.Kind, out var index);
                next[check.Kind] = index + 1;
                check.Site = Sites.Get(check.Kind, index);
                check.InSuite = true;
                var run = new Running { Check = check };
                run.Stack.Push(check.Run());
                run.DeletingAtStart = RegressionCheck.DeletingSeconds;
                run.Watch.Start();
                _runs.Add(run);
            }
            Note(_runs.Count + " checks: " + string.Join(", ", next.Select(p => p.Value + " " + p.Key.ToString().ToLowerInvariant())));

            DispatchWatch.Start();
            var watch = Stopwatch.StartNew();
            var told = 0.0;
            while (_runs.Any(r => r.Verdict == null))
            {
                foreach (var run in _runs)
                {
                    if (run.Verdict != null) continue;
                    if (run.Watch.Elapsed.TotalSeconds - (RegressionCheck.DeletingSeconds - run.DeletingAtStart) > run.Check.TimeoutSeconds)
                    {
                        run.Verdict = TestVerdict.Timeout;
                        run.Message = "timeout after " + run.Check.TimeoutSeconds + " s at: " + run.Check.Progress;
                    }
                    else
                    {
                        try { run.Step(); }
                        catch (ScenarioFailedException e) { run.Verdict = TestVerdict.Failed; run.Message = e.Message; }
                        catch (ScenarioSkippedException e) { run.Verdict = TestVerdict.Skipped; run.Message = e.Message; }
                        catch (Exception e)
                        {
                            var cause = e.GetBaseException();
                            Log.Error(cause, "[TEST] " + run.Check.Name + " step threw at [" + run.Check.Progress + "]");
                            run.Verdict = TestVerdict.Error;
                            run.Message = "step threw at [" + run.Check.Progress + "]: " + cause.GetType().Name + ": " + cause.Message;
                        }
                    }
                    if (run.Verdict != null) Finish(run);
                }
                if (watch.Elapsed.TotalSeconds - told >= 15)
                {
                    told = watch.Elapsed.TotalSeconds;
                    Note("REGRESSION | " + told.ToString("F0") + " s: " + _runs.Count(r => r.Verdict != null) + " of " + _runs.Count + " done, " +
                         Bad().Count + " not passed; running " + string.Join(", ", _runs.Where(r => r.Verdict == null).Select(r => r.Check.Name).Take(12)));
                }
                yield return null;
            }

            DispatchWatch.Stop();
            var thrown = DispatchWatch.Report();
            foreach (var line in thrown) Log.Error("[TEST] REGRESSION | the entities' update pass threw (it stops there for the frame): " + line);
            var bad = Bad();
            var skipped = _runs.Where(r => r.Verdict == TestVerdict.Skipped).ToList();
            Note("REGRESSION RESULT | " + (_runs.Count - bad.Count - skipped.Count) + " passed, " + bad.Count + " not, " + skipped.Count + " skipped of " +
                 _runs.Count + " in " + watch.Elapsed.TotalSeconds.ToString("F0") + " s" +
                 (bad.Count > 0 ? " | " + string.Join(" | ", bad.Select(r => r.Check.Name + ": " + r.Message)) : ""));
            Check(bad.Count == 0, bad.Count + " of " + _runs.Count + " checks did not pass: " + string.Join(", ", bad.Select(r => r.Check.Name)));
            Check(thrown.Count == 0, "the entities' update pass threw " + thrown.Count + " kinds of exceptions during the run (see the log): " + string.Join(" | ", thrown.Take(3)));
        }

        private List<Running> Bad() => _runs.Where(r => r.Verdict != null && r.Verdict != TestVerdict.Passed && r.Verdict != TestVerdict.Skipped).ToList();

        /// <summary>A check is over: its verdict recorded as a scenario's, what it built taken away.</summary>
        private void Finish(Running run)
        {
            run.Watch.Stop();
            var check = run.Check;
            try { check.CleanupLeftovers(); }
            catch (Exception e) { Log.Warn("leftover cleanup of {0} failed: {1}", check.Name, e.Message); }
            foreach (var entity in check.TakeTracked())
            {
                try
                {
                    if (entity == null || entity.MarkedForClose) continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(entity);
                    entity.Close();
                }
                catch (Exception e) { Log.Warn("cleanup of {0}: cannot remove {1}: {2}", check.Name, entity?.EntityId, e.Message); }
            }
            try { check.Cleanup(); }
            catch (Exception e) { Log.Warn("cleanup of {0} failed: {1}", check.Name, e.Message); }
            // what the check built, deleted now: closed grids left waiting stand in the scene with their physics, in the
            // way of the checks still running (RegressionCheck.DeleteClosed)
            try { RegressionCheck.DeleteClosed(); }
            catch (Exception e) { Log.Warn("deleting what {0} closed failed: {1}", check.Name, e.Message); }
            TestRunner.Record(new TestResult
            {
                Scenario = check.Name,
                Verdict = run.Verdict.Value,
                DurationSeconds = run.Watch.Elapsed.TotalSeconds,
                Message = run.Message,
                Notes = check.ProgressLog.ToList(),
            });
        }

        public override void Cleanup()
        {
            FakeClients.KeepOnRemoveAll = false;
            foreach (var run in _runs.Where(r => r.Verdict == null))
            {
                run.Verdict = TestVerdict.Aborted;
                run.Message = "the suite ended";
                Finish(run);
            }
            try { _gameplay?.Restore(); }
            catch (Exception e) { Log.Warn("restoring SentisGameplayImprovements' settings failed: {0}", e.Message); }
            try { _essentials?.Restore(); }
            catch (Exception e) { Log.Warn("restoring Essentials' settings failed: {0}", e.Message); }
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
