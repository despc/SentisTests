using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Puts the world's piston rigs back the way the operator built them: every piston driving
    /// out, and waits until they are all at their upper limit. For after a piston_perf run was cut
    /// short and the world was saved with the pistons going the other way.
    /// </summary>
    public sealed class PistonExtendScenario : TestScenario
    {
        public const string ScenarioName = "piston_extend";

        private readonly ConfigOverride _config = new ConfigOverride();

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 240;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _config.Set("FreezerEnabled", false);

            var pistons = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose)
                .SelectMany(g => g.GetFatBlocks().OfType<MyPistonBase>()).ToList();
            Check(pistons.Count > 0, "there are no pistons in the world");
            foreach (var p in pistons)
            {
                var piston = (Sandbox.ModAPI.IMyPistonBase)p;
                piston.Velocity = Math.Abs(piston.Velocity) > 0.001f ? Math.Abs(piston.Velocity) : 0.5f;
            }

            var extended = Wait(() => pistons.All(p => ((Sandbox.ModAPI.IMyPistonBase)p).CurrentPosition >= p.MaxLimit - 0.01f),
                "every piston at its upper limit", 200);
            while (extended.MoveNext()) yield return extended.Current;
            Note("PISTONS EXTENDED | " + pistons.Count + " pistons at their upper limit");
        }

        public override void Cleanup()
        {
            try { _config.Restore(); }
            finally { base.Cleanup(); }
        }
    }
}
