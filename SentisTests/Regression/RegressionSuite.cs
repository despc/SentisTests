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
    /// All the regression checks at once, each at a site of its own (<see cref="Sites"/>), 8 km apart in space and on the
    /// surface: each in a cluster of the physics of its own. Every check is stepped once a frame as the runner steps a scenario; each one's
    /// verdict goes to the log and the results as a scenario of its own (<c>[TEST] PASSED reg_reactor</c>), and the suite
    /// fails when any of them did. A check that is over is cleaned up at once, while the others go on.
    ///
    /// <c>regression</c> runs them all; <see cref="TestRunner.Focus"/> (DebugBridge's test call, <c>focus</c>) keeps only
    /// the checks whose names contain one of its comma-separated words.
    /// </summary>
    public sealed class RegressionSuite : TestScenario
    {
        public const string ScenarioName = "regression";

        /// <summary>
        /// The same checks with the space ones packed into a few heavy clusters and a few light ones apart
        /// (<see cref="Sites.HeavySpace"/>): for SentisClusters' detaching, which cluster runs at what speed.
        /// </summary>
        public const string HeavyName = "regression_heavy";

        private readonly bool _heavy;

        public RegressionSuite(bool heavy = false) { _heavy = heavy; }

        private readonly List<Running> _runs = new List<Running>();
        private ConfigOverride _gameplay;
        private ConfigOverride _essentials;
        private ConfigOverride _clusters;

        public override string Name => _heavy ? HeavyName : ScenarioName;

        public override int TimeoutSeconds => Math.Max(300, Registry.All.Select(c => c.Make().TimeoutSeconds).DefaultIfEmpty(0).Max() * 4 + 120);

        /// <summary>
        /// Loose items at the sites, left by a run that did not end (the server gone meanwhile, the world saved with them
        /// before the restart), and grids at the space sites, taken away before the checks build there: a wheel was not put on where they lay
        /// (reg_all_blocks_large, "head none"), or, in a cluster detached by hand, put into them (26.10.2026).
        /// </summary>
        private void SweepLooseItems(List<RegressionCheck> checks)
        {
            var origins = checks.Where(c => c.Site != null).Select(c => c.Site.Origin).ToList();
            var swept = 0;
            foreach (var item in Sandbox.Game.Entities.MyEntities.GetEntities().OfType<Sandbox.Game.Entities.MyFloatingObject>().ToList())
            {
                if (item.MarkedForClose) continue;
                var at = item.PositionComp.GetPosition();
                if (!origins.Any(o => VRageMath.Vector3D.DistanceSquared(o, at) < SweepRadius * SweepRadius)) continue;
                item.Close();
                swept++;
            }
            // and grids at the space sites, where only the checks build: a rotor's head broken off ("Subgrid", not named as
            // the checks' grids are) was saved in the world and stood where reg_conveyor_mechanisms put a piston's head,
            // every regression run that gave it that site ("the head on LargePistonBase: not in 20 s", 27.10.2026)
            var space = checks.Where(c => c.Site != null && c.Site.Kind == SiteKind.Space).Select(c => c.Site.Origin).ToList();
            var grids = 0;
            foreach (var grid in Sandbox.Game.Entities.MyEntities.GetEntities().OfType<Sandbox.Game.Entities.MyCubeGrid>().ToList())
            {
                if (grid.MarkedForClose) continue;
                var at = grid.PositionComp.GetPosition();
                if (!space.Any(o => VRageMath.Vector3D.DistanceSquared(o, at) < SweepRadius * SweepRadius)) continue;
                grid.Close();
                grids++;
            }
            if (swept == 0 && grids == 0) return;
            RegressionCheck.DeleteClosed();
            if (swept > 0) Note(swept + " loose items taken away from the sites (left by an earlier run)");
            if (grids > 0) Note(grids + " grids taken away from the space sites (left by an earlier run)");
        }

        private const double SweepRadius = 300;

        // SentisClusters.ClustersApi.WaitForDetachedTicks, when that plugin is loaded
        private static readonly Action WaitForDetachedTicks =
            Integrations.TypeOf("SentisClusters", "SentisClusters.ClustersApi")?.GetMethod("WaitForDetachedTicks") is System.Reflection.MethodInfo m
                ? (Action)Delegate.CreateDelegate(typeof(Action), m)
                : null;

        /// <summary>One check of the suite: its coroutine, stepped as the runner steps one.</summary>
        private sealed class Running
        {
            public RegressionCheck Check;
            public readonly Stack<IEnumerator> Stack = new Stack<IEnumerator>();
            public readonly Stopwatch Watch = new Stopwatch();
            /// <summary><see cref="RegressionCheck.DeletingSeconds"/> when the check started: the cleanups' long frames are not its time.</summary>
            public double DeletingAtStart;
            /// <summary>The game's frame when the check started: its timeout is game time (a loaded server runs slow).</summary>
            public ulong StartFrame;

            /// <summary>
            /// Over its time: <see cref="RegressionCheck.TimeoutSeconds"/> of game time, or four times that by the clock
            /// (less the cleanups' long frames). By the clock alone a safe zone's 120 s of coming up did not fit 240 s
            /// on a server at a fifth of its speed (reg_safe_zone, 25.10.2026).
            /// </summary>
            public bool Over()
            {
                var gameSeconds = ((Sandbox.MySandboxGame.Static?.SimulationFrameCounter ?? StartFrame) - StartFrame) / 60.0;
                var clockSeconds = Watch.Elapsed.TotalSeconds - (RegressionCheck.DeletingSeconds - DeletingAtStart);
                return gameSeconds > Check.TimeoutSeconds || clockSeconds > Check.TimeoutSeconds * 4;
            }
            public TestVerdict? Verdict;
            public string Message;
            /// <summary>With <c>detach=1</c>: the check's cluster detached by SentisClusters' !clusters detach while it runs.</summary>
            public bool Detached;

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
            // detach=1: every check's cluster detached by hand while it runs (SentisClusters), at most 8 at once: give
            // the checks in batches of 8. Not a name to look for
            _detachAll = focus.Contains("detach=1");
            focus = focus.Where(f => f != "detach=1").ToArray();
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
            // limit is out of reach while it runs: BagLimitOff, not -1 - Essentials skips tracking bags at -1 only for bags that come after; the ones it tracks already (the stand runs at 1) then loop to an empty list and throw every frame
            if (Integrations.IsLoaded(ConfigOverride.Essentials))
            {
                _essentials = new ConfigOverride(ConfigOverride.Essentials);
                _essentials.Set("BackpackLimit", RegressionCheck.BagLimitOff);
            }

            // the clusters of the size the sites are laid out for, before any site is built
            _clusters = RegressionCheck.ClustersForTests();
            if (_clusters != null) Note("SentisClusters' clusters " + RegressionCheck.TestClusterSize + " m for the run");

            var made = chosen.Select(entry => entry.Make()).ToList();
            // the heavy layout's light sites are the last indices: the checks that need a cluster of their own go there
            if (_heavy) made = made.Where(c => !c.NeedsOwnCluster).Concat(made.Where(c => c.NeedsOwnCluster)).ToList();
            var spaceCount = made.Count(c => c.Kind == SiteKind.Space);
            var clumps = new int[Sites.HeavyClumps + 1];
            var next = new Dictionary<SiteKind, int>();
            foreach (var check in made)
            {
                next.TryGetValue(check.Kind, out var index);
                next[check.Kind] = index + 1;
                if (_heavy && check.Kind == SiteKind.Space)
                {
                    check.Site = Sites.HeavySpace(index, spaceCount, out var clump);
                    clumps[clump + 1]++;
                }
                else check.Site = Sites.Get(check.Kind, index);
                check.InSuite = true;
                var run = new Running { Check = check };
                run.Stack.Push(check.Run());
                run.DeletingAtStart = RegressionCheck.DeletingSeconds;
                run.StartFrame = Sandbox.MySandboxGame.Static?.SimulationFrameCounter ?? 0;
                run.Watch.Start();
                _runs.Add(run);
            }
            Note(_runs.Count + " checks: " + string.Join(", ", next.Select(p => p.Value + " " + p.Key.ToString().ToLowerInvariant())));
            SweepLooseItems(made);
            if (_heavy)
                Note("heavy layout: " + string.Join(", ", Enumerable.Range(0, Sites.HeavyClumps).Select(i => "clump " + (i + 1) + " " + clumps[i + 1] + " checks at " +
                     Sites.HeavySpace(i, spaceCount, out _).Origin.ToString("F0"))) + "; " + clumps[0] + " light, one a cluster");

            DispatchWatch.Start();
            var watch = Stopwatch.StartNew();
            var told = 0.0;
            var frame = 0;
            while (_runs.Any(r => r.Verdict == null))
            {
                if (_detachAll && frame++ % 30 == 0)
                    foreach (var run in _runs)
                    {
                        if (run.Verdict != null || run.Detached || run.Check.Site == null) continue;
                        var at = run.Check.Site.Origin;
                        var answer = string.Join(" ", ClusterCommand($"!clusters detach {at.X:F0} {at.Y:F0} {at.Z:F0}"));
                        if (answer.Contains("detached and kept"))
                        {
                            run.Detached = true;
                            Note(run.Check.Name + ": " + answer.Split('-')[0].Trim());
                        }
                    }
                // the checks change the game straight from the game thread, as a client's request does: not while a
                // detached cluster's tick runs (SentisClusters makes the requests wait for their entity's tick)
                WaitForDetachedTicks?.Invoke();
                foreach (var run in _runs)
                {
                    if (run.Verdict != null) continue;
                    if (run.Over())
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
                 (_detachAll ? ", " + _runs.Count(r => r.Detached) + " of them in a cluster detached by hand" : "") +
                 (bad.Count > 0 ? " | " + string.Join(" | ", bad.Select(r => r.Check.Name + (r.Detached ? " (detached)" : "") + ": " + r.Message)) : ""));
            Check(bad.Count == 0, bad.Count + " of " + _runs.Count + " checks did not pass: " + string.Join(", ", bad.Select(r => r.Check.Name)));
            Check(thrown.Count == 0, "the entities' update pass threw " + thrown.Count + " kinds of exceptions during the run (see the log): " + string.Join(" | ", thrown.Take(3)));
        }

        private bool _detachAll;

        private static List<string> ClusterCommand(string text)
        {
            var answers = new List<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            commands?.HandleCommandFromServer(text, message => answers.Add(message.Message));
            return answers;
        }

        private List<Running> Bad() => _runs.Where(r => r.Verdict != null && r.Verdict != TestVerdict.Passed && r.Verdict != TestVerdict.Skipped).ToList();

        /// <summary>A check is over: its verdict recorded as a scenario's, what it built taken away.</summary>
        private void Finish(Running run)
        {
            run.Watch.Stop();
            var check = run.Check;
            if (run.Detached)
            {
                // its cluster attached again (and let go as one kept detached by hand) before what it built goes
                var at = check.Site.Origin;
                try { ClusterCommand($"!clusters attach {at.X:F0} {at.Y:F0} {at.Z:F0}"); }
                catch (Exception e) { Log.Warn("attaching the cluster of {0} failed: {1}", check.Name, e.Message); }
            }
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
            if (_detachAll)
                try { ClusterCommand("!clusters attach all"); }
                catch (Exception e) { Log.Warn("attaching the clusters failed: {0}", e.Message); }
            try { _clusters?.Restore(); }
            catch (Exception e) { Log.Warn("restoring SentisClusters' settings failed: {0}", e.Message); }
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
