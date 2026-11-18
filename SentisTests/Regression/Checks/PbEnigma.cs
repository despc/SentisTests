using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using SentisTests.Scenarios;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A player's combat ship with its scripts under the production's limits (only when asked; the blueprint is a player's,
    /// not in the repository: <c>pb_enigma.sbc</c> in the instance folder, else skipped). "[STARFIRE] - Enigma_BG" (vanilla
    /// production, 18.11.2026: 14 grids, 16 000 blocks, 210 cameras, 68 turrets) with its RTAS and its RTAS-Sub, the
    /// player's, the test player in its main cockpit, an enemy grid 1 km ahead, SentisOptimisations' limits as the
    /// production has them (0.3 ms a frame, 8 ms a run) and punishment on: the scripts run 30 s idle, then a minute in a fight
    /// (RTAS told to lock with its cameras, aim the ship and focus its turrets), the RTAS blocks are recompiled twice (the
    /// fight told again), and no block is taken out of service. Each block's load is noted a second.
    /// </summary>
    public sealed class PbEnigmaCheck : RegressionCheck
    {
        private readonly bool _profile;

        /// <param name="profile">
        /// <c>pb_enigma_profile</c>: RTAS's methods timed (<see cref="ScriptProfiler"/>), punishment off (the timing is a
        /// load of its own), where its time and its heaviest runs go noted.
        /// </param>
        public PbEnigmaCheck(bool profile = false) => _profile = profile;

        public override string Name => Prefix + (_profile ? "pb_enigma_profile" : "pb_enigma");
        public override bool OnlyWhenAsked => true;
        public override int TimeoutSeconds => 480;

        private readonly ConfigOverride _config = new ConfigOverride();

        protected override IEnumerator Check()
        {
            var file = Path.Combine(Path.GetDirectoryName(ConfigOverride.RestoreFile) ?? "", "pb_enigma.sbc");
            SkipUnless(File.Exists(file), "no " + file);
            yield return Arrive(Site.At(0, 60, -120));

            var obs = WorldApi.LoadBlueprintFile(file, WorldApi.EntityPrefix + "enigma");
            foreach (var block in obs.SelectMany(g => g.CubeBlocks))
            {
                block.Owner = Identity;
                block.BuiltBy = Identity;
            }
            var shift = Site.Origin - (Vector3D)obs[0].PositionAndOrientation.Value.Position;
            foreach (var ob in obs)
            {
                var po = ob.PositionAndOrientation.Value;
                po.Position = (Vector3D)po.Position + shift;
                ob.PositionAndOrientation = po;
                ob.LinearVelocity = default(VRage.SerializableVector3);
                ob.AngularVelocity = default(VRage.SerializableVector3);
            }
            var grids = obs.Select(WorldApi.SpawnGrid).ToList();
            foreach (var grid in grids) Track(grid);
            var seat = grids.SelectMany(g => g.GetFatBlocks().OfType<MyShipController>()).FirstOrDefault(c => c.CustomName.ToString() == "[Starfire] Main Cockpit/Seat Locked");
            SkipUnless(seat != null, "no '[Starfire] Main Cockpit/Seat Locked' on the ship");
            // the enemy: 5 x 5 x 2 of armour (bigger than the script's least target) 1 km ahead of the main cockpit
            var enemyParts = new List<Part>();
            for (var x = 0; x < 5; x++) for (var y = 0; y < 2; y++) for (var z = 0; z < 5; z++) enemyParts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, z));
            enemyParts.Add(new Part("BatteryBlock/LargeBlockBatteryBlock", 2, 2, 2).With(b => Motion.Charged(b)));
            Build("enigma-enemy", seat.PositionComp.GetPosition() + seat.WorldMatrix.Forward * 1000, true, enemyParts, ownedBy: WorldApi.TestIdentityId());

            var pbs = grids.SelectMany(g => g.GetFatBlocks().OfType<MyProgrammableBlock>()).Where(pb => pb.IsFunctional).ToList();
            Note(grids.Count + " grids, " + grids.Sum(g => g.BlocksCount) + " blocks; programmable blocks: " +
                 string.Join(", ", pbs.Select(pb => "'" + pb.CustomName + "' on " + pb.CubeGrid.DisplayName + " (" + (((Sandbox.ModAPI.IMyProgrammableBlock)pb).ProgramData?.Length ?? 0) + " chars, " + (pb.Enabled ? "on" : "off") + ")")));
            SkipUnless(pbs.Count > 0, "the blueprint has no working programmable block");

            _config.Set("ScriptsMaxMsPerFrame", 0.3f);
            _config.Set("ScriptsMaxExecTime", 8f);
            _config.Set("ScriptOvertimeExecTimesBeforePunish", 3);
            _config.Set("EnableScriptsPunish", !_profile);
            _config.Set("FreezerEnabled", false);
            // the guard punishes only 3 minutes after the world's load
            yield return Until(() => Sandbox.MySandboxGame.Static.SimulationFrameCounter, f => f > 10800 + 60, "the world 3 minutes up (the guard's own wait)", 240);
            foreach (var pb in pbs) pb.Enabled = true;
            yield return Until(() => pbs.Count(Scripts.Ready), n => n == pbs.Count, "the scripts compiled", 60, () => string.Join("; ", pbs.Select(State)));
            var sit = Sit(seat);
            while (sit.MoveNext()) yield return sit.Current;
            var rtasMain = pbs.FirstOrDefault(pb => ((Sandbox.ModAPI.IMyProgrammableBlock)pb).ProgramData?.Length > 50000);
            var instance = typeof(MyProgrammableBlock).GetField("m_instance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            void Profile()
            {
                var program = rtasMain == null ? null : instance.GetValue(rtasMain);
                if (_profile && program != null) ScriptProfiler.Attach(program.GetType().Assembly);
            }
            Profile();
            // the pilot's combat: the cameras search and lock, the ship aims at the target, the turrets on it
            void Fight()
            {
                if (rtasMain == null) return;
                foreach (var arg in new[] { "raycast_switch", "target_align_on", "turret_focus" })
                    Request(rtasMain, "RunProgramRequest", Scripts.Argument(arg), Sandbox.ModAPI.Ingame.UpdateType.Terminal);
            }

            var peaks = pbs.ToDictionary(pb => pb, pb => 0.0);
            var lines = new List<string>();
            IEnumerator Watch(int seconds, string phase)
            {
                for (var s = 0; s < seconds; s++)
                {
                    yield return Seconds(1);
                    var row = new List<string>();
                    foreach (var pb in pbs)
                    {
                        var load = RuntimePluginControls.PbLoadMsPerFrame(pb);
                        peaks[pb] = Math.Max(peaks[pb], load);
                        row.Add(load.ToString("F2") + (pb.IsFunctional && pb.Enabled ? "" : "X"));
                    }
                    lines.Add(phase + s + " " + string.Join("/", row));
                }
            }
            var watch = Watch(30, "idle");
            while (watch.MoveNext()) yield return watch.Current;
            Fight();
            watch = Watch(60, "fight");
            while (watch.MoveNext()) yield return watch.Current;
            var rtas = pbs.Where(pb => ((Sandbox.ModAPI.IMyProgrammableBlock)pb).ProgramData?.Contains("RTAS - Raycasting") == true).ToList();
            for (var compile = 1; compile <= 2; compile++)
            {
                foreach (var pb in rtas.Where(b => b.IsFunctional && b.Enabled)) Request(pb, "Recompile", true);
                yield return Until(() => rtas.Count(Scripts.Ready), n => n == rtas.Count, "RTAS compiled again", 30);
                Profile();
                watch = Watch(5, "re" + compile + "-");
                while (watch.MoveNext()) yield return watch.Current;
                Fight();
                watch = Watch(20, "re" + compile + "fight-");
                while (watch.MoveNext()) yield return watch.Current;
            }

            Note("loads a second, ms a frame (" + string.Join(" / ", pbs.Select(pb => pb.CustomName.ToString())) + "; X - out of service):");
            for (var i = 0; i < lines.Count; i += 10) Note(string.Join("  ", lines.Skip(i).Take(10)));
            foreach (var pb in pbs)
            {
                var info = pb.DetailedInfo.ToString().Replace('\n', ' ');
                Note("'" + pb.CustomName + "': " + (pb.IsFunctional && pb.Enabled ? "running" : "out of service") + ", the highest second " + peaks[pb].ToString("F2") +
                     " ms a frame; " + (info.Length > 300 ? info.Substring(0, 300) : info));
            }
            if (_profile)
            {
                ScriptProfiler.Detach();
                var stats = ScriptProfiler.All.OrderByDescending(p => p.Value.OwnTicks).ToList();
                // the script's names are not ASCII: the report in UTF-8 beside the blueprint (the log's encoding loses them)
                var report = new System.Text.StringBuilder();
                foreach (var p in stats.Take(60))
                    report.AppendLine(ScriptProfiler.Name(p.Key) + "	own " + ScriptProfiler.Ms(p.Value.OwnTicks).ToString("F2") + " ms	total " + ScriptProfiler.Ms(p.Value.TotalTicks).ToString("F2") +
                                      " ms	calls " + p.Value.Calls);
                report.AppendLine("heaviest runs:");
                foreach (var run in ScriptProfiler.HeaviestRuns)
                    report.AppendLine(run.Ms.ToString("F2") + " ms: " + string.Join(", ", run.Own.OrderByDescending(o => o.Value).Take(8)
                        .Select(o => ScriptProfiler.Name(o.Key) + " " + ScriptProfiler.Ms(o.Value).ToString("F2"))));
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(file) ?? "", "pb_enigma_profile.txt"), report.ToString(), new System.Text.UTF8Encoding(false));
                var own = stats.Sum(p => p.Value.OwnTicks);
                Note("RTAS profiled: " + ScriptProfiler.MethodsTimed + " methods timed; their own time " + ScriptProfiler.Ms(own).ToString("F0") + " ms in all");
                foreach (var p in stats.Take(25))
                    Note("  " + ScriptProfiler.Name(p.Key) + ": own " + ScriptProfiler.Ms(p.Value.OwnTicks).ToString("F1") + " ms (" + (100.0 * p.Value.OwnTicks / Math.Max(1, own)).ToString("F0") +
                         "%), with what it calls " + ScriptProfiler.Ms(p.Value.TotalTicks).ToString("F1") + " ms, " + p.Value.Calls + " calls, " +
                         (ScriptProfiler.Ms(p.Value.TotalTicks) * 1000 / Math.Max(1, p.Value.Calls)).ToString("F1") + " µs a call");
                foreach (var run in ScriptProfiler.HeaviestRuns.Take(12))
                    Note("  a run of " + run.Ms.ToString("F2") + " ms: " + string.Join(", ", run.Own.OrderByDescending(o => o.Value).Take(5)
                        .Select(o => ScriptProfiler.Name(o.Key) + " " + ScriptProfiler.Ms(o.Value).ToString("F2"))));
                ScriptProfiler.Reset();
            }
            var lost = pbs.Where(pb => !(pb.IsFunctional && pb.Enabled)).ToList();
            Check(lost.Count == 0, string.Join(", ", lost.Select(pb => "'" + pb.CustomName + "'")) + " taken out of service");
            Result("the ship's " + pbs.Count + " scripts ran 30 s idle, 60 s in a fight (lock, aim, turrets) and through two recompiles of RTAS under 0.3 ms a frame, none taken out of service; their highest seconds " +
                   string.Join(", ", pbs.Select(pb => peaks[pb].ToString("F2"))) + " ms a frame");
        }

        public override void Cleanup()
        {
            if (_profile) ScriptProfiler.Detach();
            _config.Restore();
            base.Cleanup();
        }
    }
}
