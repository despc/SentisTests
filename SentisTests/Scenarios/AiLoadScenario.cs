using System;
using System.Collections;
using System.Linq;
using SentisTests.Core;
using SentisTests.Game;

namespace SentisTests.Scenarios
{
    /// <summary>Read-only five-minute observation of the existing bot cohort, with actual manager CPU cost and server frame tails.</summary>
    public sealed class AiLoadScenario : TestScenario
    {
        public const string ScenarioName = "ai_load";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 420;
        public override bool KeepsNewGrids => true;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var manager = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisAi.Bots.BotManager", false)).FirstOrDefault(t => t != null);
            SkipUnless(manager != null, "SentisAi is not loaded");
            yield return WaitForTicks(30 * 60);
            var bots = Sandbox.Game.World.MySession.Static.Players.GetOnlinePlayers().Count(p => p.DisplayName.StartsWith("[BOT] "));
            Check(bots > 0, "no live bots to profile");
            var missing = MethodTimerProbe.Start(new[] { (manager, "Tick") });
            Check(string.IsNullOrEmpty(missing), missing);
            FrameProbe.Take();
            TickMetrics.Take();
            Note($"profiling: {bots} existing bots for 18000 simulation frames; no forced activities or injected supplies");
            for (var frame = 0; frame < 18000; frame++)
            {
                if (frame > 0 && frame % 1800 == 0) Note($"profiling: {frame}/18000 frames, {bots} bots");
                yield return null;
            }
            Note("SentisAi CPU (includes lifecycle, connections and tool requests): " + MethodTimerProbe.Format(18000));
            Note("whole server simulation frames: " + FrameProbe.Take());
            Note("frame gaps (includes 60 Hz wait): " + TickMetrics.Take());
            Check(Sandbox.Game.World.MySession.Static.Players.GetOnlinePlayers().Count(p => p.DisplayName.StartsWith("[BOT] ")) == bots,
                "bot cohort changed during the profiling window");
        }

        public override void Cleanup()
        {
            MethodTimerProbe.Stop();
            base.Cleanup();
        }
    }
}
