using System;
using System.Collections;
using Sandbox.Game.Entities;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A grid one of whose updates throws once goes on being updated (prod 06.11.2026 23:12: a ship's thrust threw, the
    /// grid was left "being dispatched" - MyCubeGrid.Dispatch has no finally - and every frame after, each of its updates
    /// threw "An update queue is already being dispatched for this entity" and stopped the entities' pass; the players
    /// said it froze). An update of the grid in the parallel pass and one on the game thread each throw once; another
    /// update of the same grid counts its calls: they go on in the frames after, with SentisOptimisations' GridUpdateStuck.
    /// </summary>
    public sealed class GridUpdateThrowsCheck : RegressionCheck
    {
        public override string Name => Prefix + "grid_update_throws";

        private const string Marker = "reg_grid_update_throws";

        protected override IEnumerator Check()
        {
            // what it throws on purpose is not the regression's exception of the pass
            DispatchWatch.Excuse(Marker);
            yield return Arrive(Site.At(0, 6, -8));
            var grid = Build("update-throws", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            yield return WaitForTicks(2);
            foreach (var parallel in new[] { true, false })
            {
                var calls = 0;
                var thrown = false;
                Action counting = () => System.Threading.Interlocked.Increment(ref calls);
                Action throwing = () =>
                {
                    if (thrown) return;
                    thrown = true;
                    throw new InvalidOperationException(Marker + ": an update that throws once");
                };
                // the throwing one first, the counting one after it in the same queue
                grid.Schedule(MyCubeGrid.UpdateQueue.BeforeSimulation, throwing, 0, parallel);
                grid.Schedule(MyCubeGrid.UpdateQueue.BeforeSimulation, counting, 1, parallel);
                yield return WaitForTicks(3);
                var before = calls;
                yield return WaitForTicks(30);
                var after = calls;
                grid.DeSchedule(MyCubeGrid.UpdateQueue.BeforeSimulation, throwing);
                grid.DeSchedule(MyCubeGrid.UpdateQueue.BeforeSimulation, counting);
                var where = parallel ? "the parallel pass" : "the game thread";
                Note($"{where}: thrown {thrown}; the grid's other update ran {before} times in the 3 frames after, {after - before} in the 30 after that");
                Check(thrown, $"the throwing update never ran in {where}");
                Check(after - before >= 25, $"the grid's updates stopped after one of them threw in {where}: {after - before} of 30 frames");
                yield return WaitForTicks(2);
            }
            Result("a grid's update that threw once, in the parallel pass and on the game thread: the grid's updates went on");
        }
    }
}
