using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Game;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisOptimisations' HeadDetachWatch: a whole mechanism taken out of the world (its stator's grid and its head's,
    /// one after the other, as a cleanup does) is only counted (WholeLeft), not written to the log; the head's grid
    /// alone taken out is still written.
    /// </summary>
    public sealed class HeadLeftWorldCheck : RegressionCheck
    {
        public override string Name => Prefix + "head_left_world";

        private static Type Watch => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("SentisOptimisationsPlugin.CrashFix.HeadDetachWatch", false)).FirstOrDefault(t => t != null);

        private static long Count(string field) => (long)(Watch?.GetField(field, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? -1L);

        protected override IEnumerator Check()
        {
            SkipUnless(Watch?.GetField("WholeLeft") != null, "SentisOptimisations' HeadDetachWatch has no WholeLeft");
            yield return Arrive(Site.At(-10, 4, -10));

            // 1. the whole mechanism closed, the stator's grid first, then the head's
            var whole = Build("head-left-whole", Site.Origin, true, new Part("MotorStator/LargeStator", 0, 0, 0));
            var stator = Block<MyMotorStator>(whole);
            yield return AddHead(stator);
            yield return Seconds(1);
            var headGrid = stator.TopBlock.CubeGrid;
            var before = Count("WholeLeft");
            var detached = Count("Detached");
            whole.Close();
            headGrid.Close();
            yield return Seconds(2);
            var wholeCounted = Count("WholeLeft") - before;
            var wholeDetached = Count("Detached") - detached;
            Note($"whole mechanism: detached {wholeDetached}, counted as left whole {wholeCounted}");
            Check(wholeCounted == wholeDetached && wholeCounted >= 1, $"a whole mechanism leaving: {wholeCounted} of {wholeDetached} detaches counted as left whole");

            // 2. the head's grid alone closed: written (not counted)
            var half = Build("head-left-half", Site.At(6, 0, 0), true, new Part("MotorStator/LargeStator", 0, 0, 0));
            var stator2 = Block<MyMotorStator>(half);
            yield return AddHead(stator2);
            yield return Seconds(1);
            before = Count("WholeLeft");
            detached = Count("Detached");
            stator2.TopBlock.CubeGrid.Close();
            yield return Seconds(2);
            var halfCounted = Count("WholeLeft") - before;
            var halfDetached = Count("Detached") - detached;
            Note($"the head's grid alone: detached {halfDetached}, counted as left whole {halfCounted}");
            Check(halfDetached >= 1 && halfCounted == 0, $"the head's grid alone leaving: counted as a whole mechanism {halfCounted} of {halfDetached}");
            Result($"whole mechanism: {wholeDetached} detach, counted, not written; the head's grid alone: {halfDetached} detach, written");
        }
    }
}
