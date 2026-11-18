using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using SentisTests.Core;
using SentisTests.Game;
using SentisTests.Scenarios;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A heavy combat script under the production's limits (only when asked; the script is a player's, not in the repository:
    /// <c>pb_rtas.cs</c> in the instance folder, else skipped). RTAS (a targeting script, vanilla production, 18.11.2026) runs
    /// on Update1 on a ship with its named cockpit and cameras and its group of turrets, guns, cameras and an LCD, an enemy
    /// 1 km ahead, with SentisOptimisations' limits set as the production has them (0.3 ms a frame) and punishment on: it runs
    /// a minute, is recompiled twice, and is not taken out of service. Its load each second is noted.
    /// </summary>
    public sealed class PbRtasCheck : RegressionCheck
    {
        public override string Name => Prefix + "pb_rtas";
        public override bool OnlyWhenAsked => true;
        public override int TimeoutSeconds => 300;

        private readonly ConfigOverride _config = new ConfigOverride();

        protected override IEnumerator Check()
        {
            var file = Path.Combine(Path.GetDirectoryName(ConfigOverride.RestoreFile) ?? "", "pb_rtas.cs");
            SkipUnless(File.Exists(file), "no " + file);
            var script = File.ReadAllText(file);
            yield return Arrive(Site.At(0, 30, -30));

            var parts = new List<Part>();
            for (var x = 0; x < 16; x++)
            for (var hz = 0; hz < 24; hz++)
            for (var y = 0; y < 3; y++)
                parts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, y, hz));
            var z = 0;
            var cameraNames = new[] { "(HPR) Central aim camera", "(HPR) Side aim camera" };
            var cameras = 0;
            void Row(string id, int count, Action<MyObjectBuilder_CubeBlock> setup = null)
            {
                var size = Definition(id).Size;
                for (var i = 0; i < count; i++)
                {
                    var part = new Part(id, i * (size.X + 1), 3, z);
                    parts.Add(setup == null ? part : part.With(setup));
                }
                z += Math.Max(size.Z, Math.Max(size.X, size.Y)) + 1;
            }
            Row("Cockpit/LargeBlockCockpit", 1, b => ((MyObjectBuilder_TerminalBlock)b).CustomName = "(HPR) Main Cockpit/Seat Locked");
            Row("BatteryBlock/LargeBlockBatteryBlock", 2, b => Motion.Charged(b));
            Row("TextPanel/LargeLCDPanel", 1);
            Row("MyProgrammableBlock/LargeProgrammableBlock", 1, b =>
            {
                ((MyObjectBuilder_MyProgrammableBlock)b).Program = script;
                ((MyObjectBuilder_TerminalBlock)b).CustomName = "(HPR) PB-RTAS M";
            });
            Row("LargeGatlingTurret/", 3);
            Row("SmallMissileLauncher/LargeMissileLauncher", 3);
            Row("CameraBlock/LargeCameraBlock", 4, b => { if (cameras < cameraNames.Length) ((MyObjectBuilder_TerminalBlock)b).CustomName = cameraNames[cameras++]; });
            var ship = Build("rtas-ship", Site.Origin, true, parts);
            BuildFor(WorldApi.TestIdentityId(), "rtas-enemy", Site.Origin + Site.Forward * 1000,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 1, 0).With(b => Motion.Charged(b)));

            // the group, as the terminal makes it: every block of the ship but the armour and the PB
            var members = ship.GetFatBlocks().OfType<MyTerminalBlock>().Where(b => !(b is MyProgrammableBlock)).ToList();
            var builder = new MyObjectBuilder_BlockGroup { Name = "(HPR) RTAS", Blocks = members.Select(b => b.Position).ToList() };
            var group = (MyBlockGroup)Activator.CreateInstance(typeof(MyBlockGroup), true);
            typeof(MyBlockGroup).GetMethod("Init", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(group, new object[] { ship, builder });
            typeof(MyCubeGrid).GetMethod("AddGroup", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(MyBlockGroup), typeof(bool) }, null)
                .Invoke(ship, new object[] { group, true });

            var pb = Block<MyProgrammableBlock>(ship);
            _config.Set("ScriptsMaxMsPerFrame", 0.3f);
            _config.Set("ScriptsMaxExecTime", 8f);
            _config.Set("ScriptOvertimeExecTimesBeforePunish", 3);
            _config.Set("EnableScriptsPunish", true);
            // the guard punishes only 3 minutes after the world's load
            yield return Until(() => Sandbox.MySandboxGame.Static.SimulationFrameCounter, f => f > 10800 + 60, "the world 3 minutes up (the guard's own wait)", 240);
            Request(pb, "Recompile", true);
            yield return Until(() => Scripts.Ready(pb) ? 1 : 0, v => v > 0, "RTAS compiled", 30, () => State(pb));

            var loads = new List<string>();
            var peak = 0.0;
            IEnumerator Watch(int seconds, string phase)
            {
                for (var s = 0; s < seconds && pb.IsFunctional && pb.Enabled; s++)
                {
                    yield return Seconds(1);
                    var load = RuntimePluginControls.PbLoadMsPerFrame(pb);
                    peak = Math.Max(peak, load);
                    loads.Add(phase + s + ":" + load.ToString("F2"));
                }
            }
            var watch = Watch(30, "run");
            while (watch.MoveNext()) yield return watch.Current;
            for (var compile = 1; compile <= 2 && pb.IsFunctional && pb.Enabled; compile++)
            {
                Request(pb, "Recompile", true);
                watch = Watch(15, "re" + compile + "-");
                while (watch.MoveNext()) yield return watch.Current;
            }

            var echo = pb.DetailedInfo.ToString().Split('\n').FirstOrDefault(l => l.Contains("RTAS")) ?? "";
            Note("RTAS's load a second, ms a frame: " + string.Join(" ", loads));
            Note("RTAS: " + (pb.IsFunctional && pb.Enabled ? "running" : "taken out of service") + ", the highest second " + peak.ToString("F2") + " ms a frame; " + echo.Trim());
            Check(pb.IsFunctional && pb.Enabled, "RTAS was taken out of service (" + string.Join(" ", loads.Skip(Math.Max(0, loads.Count - 8))) + ")");
            Result("RTAS ran a minute and was recompiled twice under 0.3 ms a frame and was not taken out of service; its highest second " + peak.ToString("F2") + " ms a frame");
        }

        public override void Cleanup()
        {
            _config.Restore();
            base.Cleanup();
        }
    }
}
