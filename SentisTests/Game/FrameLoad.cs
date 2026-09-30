using System.Diagnostics;
using System.Reflection;
using Sandbox.Game.Entities;
using Torch.Managers.PatchManager;

namespace SentisTests.Game
{
    /// <summary>
    /// A frame made heavy on purpose: <see cref="SpinMs"/> of busy work right before the entities' after-simulation
    /// updates (the blocks' 10- and 100-frame updates run there), so what the blocks see is a frame that has already
    /// done that much. For the frame budgets of SentisOptimisations. Off (0) unless a scenario sets it.
    /// </summary>
    public static class FrameLoad
    {
        public static double SpinMs;
        private static bool _installed;

        public static void Install(PatchManager patchManager)
        {
            if (_installed || patchManager == null) return;
            var ctx = patchManager.AcquireContext();
            var target = typeof(MyEntities).GetMethod(nameof(MyEntities.UpdateAfterSimulation), BindingFlags.Static | BindingFlags.Public);
            ctx.GetPattern(target).Prefixes.Add(typeof(FrameLoad).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            patchManager.Commit();
            _installed = true;
        }

        private static void Prefix()
        {
            var ms = SpinMs;
            if (ms <= 0) return;
            var until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
            while (Stopwatch.GetTimestamp() < until) { }
        }
    }
}
