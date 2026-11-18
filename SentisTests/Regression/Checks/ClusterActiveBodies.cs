using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Havok;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A physics world's set of active bodies changed from two threads at once (HkWorld.RigidBodyActivated and
    /// RigidBodyDeactivated, a plain HashSet), as SentisClusters' threads do it when they update the grids of one world side
    /// by side (SplitHeavy): a body is woken in the call that wakes it. On prod the set broke and the game thread's step of
    /// the world threw in HashSet.SetCapacity, a fatal exception (18.11.2026 20:53). SentisClusters' ActiveBodiesLock makes
    /// the changes one at a time.
    ///
    /// 60 small ships of one world, two threads waking and putting to sleep their own halves of them for
    /// <see cref="RunSeconds"/> s, the way Havok tells the world; then: nothing thrown, no thread stuck, the set whole
    /// (every body in it once, its count right). Skipped without the lock of SentisClusters (the set would be broken for
    /// the rest of the run); focus <c>force</c> runs it all the same.
    /// </summary>
    public sealed class ClusterActiveBodiesCheck : RegressionCheck
    {
        public override string Name => Prefix + "cluster_active_bodies";
        private const int Ships = 60;
        private const double RunSeconds = 3;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        protected override IEnumerator Check()
        {
            var lockType = HarmonyLib.AccessTools.TypeByName("SentisClusters.Run.ActiveBodiesLock");
            var locked = lockType?.GetField("_active", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) != null;
            long LockChanges() => lockType?.GetField("Changes", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) is long n ? n : -1;
            SkipUnless(locked || (TestRunner.Focus ?? "").Contains("force"), "SentisClusters' lock of the active bodies is not in this server");
            yield return Arrive(Site.At(0, 0, -12));
            var grids = new List<MyCubeGrid>();
            for (var i = 0; i < Ships; i++)
                grids.Add(Build("active-bodies-" + i, Site.At(i % 10 * 3, 2 + i / 10 * 3, 0), false, new[] { new Part("CubeBlock/SmallBlockArmorBlock", 0, 0, 0) }, MyCubeSize.Small));
            yield return Seconds(1);
            var world = (grids[0].Physics as Sandbox.Engine.Physics.MyPhysicsBody)?.HavokWorld;
            Check(world != null, "the ships have no physics world");
            var bodies = grids.Select(g => g.Physics?.RigidBody).Where(b => b != null).ToList();
            Check(bodies.Count == Ships && grids.All(g => ReferenceEquals((g.Physics as Sandbox.Engine.Physics.MyPhysicsBody)?.HavokWorld, world)),
                "the ships are not all in one world (" + bodies.Count + " bodies)");
            var set = (HashSet<HkRigidBody>)typeof(HkWorld).GetField("m_activeRigidBodies", Any).GetValue(world);
            var activated = typeof(HkWorld).GetMethod("RigidBodyActivated", Any, null, new[] { typeof(HkEntity) }, null);
            var deactivated = typeof(HkWorld).GetMethod("RigidBodyDeactivated", Any, null, new[] { typeof(HkEntity) }, null);
            Check(activated != null && deactivated != null, "HkWorld has no RigidBodyActivated or RigidBodyDeactivated");

            // one change of each kind here first: through SentisClusters' lock or not
            var probe = LockChanges();
            deactivated.Invoke(world, new object[] { bodies[0] });
            var afterDeactivate = LockChanges();
            activated.Invoke(world, new object[] { bodies[0] });
            Note("through SentisClusters' lock: a deactivation " + (afterDeactivate - probe) + ", an activation " + (LockChanges() - afterDeactivate) +
                 " (the lock " + (locked ? "on" : "off") + ")");
            var thrown = 0;
            string firstError = null;
            long changes = 0;
            var stop = Stopwatch.StartNew();
            var threads = new List<Thread>();
            for (var t = 0; t < 2; t++)
            {
                var mine = bodies.Where((b, i) => i % 2 == t).ToList();
                var thread = new Thread(() =>
                {
                    while (stop.Elapsed.TotalSeconds < RunSeconds)
                        foreach (var body in mine)
                            try
                            {
                                deactivated.Invoke(world, new object[] { body });
                                activated.Invoke(world, new object[] { body });
                                Interlocked.Add(ref changes, 2);
                            }
                            catch (Exception e)
                            {
                                if (Interlocked.Increment(ref thrown) == 1) firstError = (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message;
                            }
                }) { IsBackground = true, Name = "active-bodies-" + t };
                threads.Add(thread);
            }
            var lockedBefore = LockChanges();
            // the game thread waits for them: no step of the world, nor its walk of the set, while they change it
            foreach (var thread in threads) thread.Start();
            var stuck = threads.Count(t => !t.Join(TimeSpan.FromSeconds(RunSeconds + 10)));
            var lockedChanges = LockChanges() - lockedBefore;
            Check(stuck == 0, stuck + " threads stuck in the set after " + (RunSeconds + 10) + " s: a broken HashSet going round (" + changes +
                              " changes made, " + lockedChanges + " of them through SentisClusters' lock)");

            // the set whole: each body in it, counted once
            var missing = bodies.Count(b => !set.Contains(b));
            var counted = 0;
            var walkThrew = (string)null;
            try { foreach (var _ in set) if (++counted > set.Count + 1000) break; }
            catch (Exception e) { walkThrew = e.GetType().Name; }
            // the bodies awake again as Havok has them
            foreach (var body in bodies.Where(b => b.IsActive && !set.Contains(b))) activated.Invoke(world, new object[] { body });
            Check(thrown == 0, thrown + " changes of the set threw: " + firstError);
            Check(walkThrew == null && counted == set.Count, "the set is broken: walked " + counted + " of " + set.Count + (walkThrew != null ? ", " + walkThrew : ""));
            Check(missing == 0, missing + " of " + bodies.Count + " bodies lost from the set");
            Result(changes + " changes of the active set from two threads in " + RunSeconds + " s (" + lockedChanges + " through SentisClusters' lock), the set whole (" + set.Count + " bodies)");
        }
    }
}
