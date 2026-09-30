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
            // a piston without power or switched off cannot move: named, and not waited for
            var idle = pistons.Where(p => !p.IsWorking).ToList();
            if (idle.Count > 0)
                Note(idle.Count + " pistons not working, left as they are: " + string.Join("; ", idle.Take(6).Select(p =>
                    "'" + p.CubeGrid.DisplayName + "' " + p.EntityId + (p.Enabled ? "" : " (off)") + ", owner " + p.OwnerId)));
            pistons = pistons.Where(p => p.IsWorking).ToList();
            foreach (var p in pistons)
            {
                var piston = (Sandbox.ModAPI.IMyPistonBase)p;
                piston.Velocity = Math.Abs(piston.Velocity) > 0.001f ? Math.Abs(piston.Velocity) : 0.5f;
            }

            bool AtLimit(MyPistonBase p) => ((Sandbox.ModAPI.IMyPistonBase)p).CurrentPosition >= p.MaxLimit - 0.01f;
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!pistons.All(p => p.Closed || AtLimit(p)) && waited.Elapsed.TotalSeconds < 200) yield return null;
            var stuck = pistons.Where(p => !p.Closed && !AtLimit(p)).ToList();
            // which ones and why: a piston without power, switched off, without its head, or held by what it pushes
            Check(stuck.Count == 0, stuck.Count + " of " + pistons.Count + " pistons not at their upper limit after 200 s: " +
                string.Join("; ", stuck.Take(6).Select(p => "'" + p.CubeGrid.DisplayName + "' " + p.EntityId +
                    " at " + ((Sandbox.ModAPI.IMyPistonBase)p).CurrentPosition.ToString("F2") + "/" + ((Sandbox.ModAPI.IMyPistonBase)p).MaxLimit.ToString("F2") +
                    (p.Enabled ? "" : ", off") + (p.IsWorking ? "" : ", not working") + (p.TopGrid == null ? ", no head" : "") +
                    ", owner " + p.OwnerId)));
            Note("PISTONS EXTENDED | " + pistons.Count + " pistons at their upper limit");
        }

        public override void Cleanup()
        {
            try { _config.Restore(); }
            finally { base.Cleanup(); }
        }
    }
}
