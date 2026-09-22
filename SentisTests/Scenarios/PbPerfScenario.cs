using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRage;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Programmable blocks that cost real time, to see what the plugin makes of them.
    ///
    /// Two kinds of offender are built, because they are what the two thresholds are for:
    /// <list type="bullet">
    /// <item><see cref="FrequentBlocks"/> scripts on Update1 doing a little work every frame - each
    /// run is far under the per-run limit, but together they own a chunk of every frame;</item>
    /// <item><see cref="RareBlocks"/> scripts on Update100 doing a lot of work in one go - over the
    /// per-run limit, while their cost per frame is small.</item>
    /// </list>
    /// The work is <c>GridTerminalSystem.GetBlocks</c> over a station of a few hundred terminal
    /// blocks, repeated: cheap in script instructions (the sandbox limit is on those), expensive in
    /// time. Afterwards the scenario reads what the plugin reports about each block and checks the
    /// server log actually names them.
    /// </summary>
    public sealed class PbPerfScenario : TestScenario
    {
        public const string ScenarioName = "pb_perf";
        private const string Prefix = "pb-";
        private const int FrequentBlocks = 6;
        private const int RareBlocks = 2;
        private const int FrequentPasses = 120;
        private const int RarePasses = 600;
        private const int Batteries = 240;
        private const int Side = 16;
        private const double OffsetM = 6000;
        private const double SettleSeconds = 10;
        private const double RunSeconds = 45;
        private const double LogEverySeconds = 20;
        private const float MaxRunMs = 2f;
        private const float MaxMsPerFrame = 0.5f;
        private const int OverrunsBeforePunish = 3;

        /// <summary>
        /// How long a block may keep loading the server after punishment is switched on. A script on
        /// Update1 is over its budget on every run, so it takes the four runs of
        /// <see cref="OverrunsBeforePunish"/> - a fraction of a second; one on Update100 needs the
        /// same four runs, which is four hundred frames.
        /// </summary>
        private const double FrequentPunishDeadline = 5;

        private const double RarePunishDeadline = 30;

        /// <summary>How long the log is given to catch up with what just happened.</summary>
        private const double LogFlushSeconds = 15;

        /// <summary>The plugin keeps quiet about scripts until the world has been up this long.</summary>
        private const ulong FramesBeforePunish = 10800;

        private readonly ConfigOverride _config = new ConfigOverride();
        private int _punishLogFrom;
        private MyCubeGrid _station;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(RunSeconds + RarePunishDeadline + 900);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            Check(MySession.Static.EnableIngameScripts, "in-game scripts are disabled in this world");

            _config.Set("EnableScriptsPunish", false); // measure and report, do not destroy the blocks
            _config.Set("ScriptsMaxExecTime", MaxRunMs);
            _config.Set("ScriptsMaxMsPerFrame", MaxMsPerFrame);
            _config.Set("ScriptOvertimeExecTimesBeforePunish", OverrunsBeforePunish);

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            _station = BuildStation(anchorM.Translation + up * OffsetM, up);

            var settle = WaitForSeconds(SettleSeconds, "station settles");
            while (settle.MoveNext()) yield return settle.Current;

            var blocks = WorldApi.FindFunctionals<MyProgrammableBlock>(_station);
            Check(blocks.Count == FrequentBlocks + RareBlocks,
                "expected " + (FrequentBlocks + RareBlocks) + " programmable blocks, got " + blocks.Count);
            var running = blocks.Count(b => b.IsWorking);
            Note(blocks.Count + " programmable blocks (" + FrequentBlocks + " on Update1, " + RareBlocks +
                 " on Update100), " + running + " working, " + WorldApi.CountBlocks(_station) + " blocks on the station: " +
                 WorldApi.DescribePower(_station));

            // Nothing is reported before the world has settled; on a freshly started server that wait
            // is part of the test, not a failure.
            while (MySandboxGame.Static.SimulationFrameCounter <= FramesBeforePunish)
            {
                if (MySandboxGame.Static.SimulationFrameCounter % 600 == 0)
                    Note("waiting for frame " + FramesBeforePunish + " (now " +
                         MySandboxGame.Static.SimulationFrameCounter + "): the plugin is quiet until then");
                yield return WaitForTicks(60);
            }

            var logFrom = LogLineCount();
            TickMetrics.Take();
            FrameProbe.Take();
            var started = DateTime.UtcNow;
            var lastLog = started;
            while ((DateTime.UtcNow - started).TotalSeconds < RunSeconds)
            {
                if ((DateTime.UtcNow - lastLog).TotalSeconds >= LogEverySeconds)
                {
                    lastLog = DateTime.UtcNow;
                    Note("running " + (DateTime.UtcNow - started).TotalSeconds.ToString("F0") + "s: " +
                         RuntimePluginControls.PbTop(3));
                }

                yield return null;
            }

            var metrics = TickMetrics.Take();
            var probe = FrameProbe.Take();
            var loads = blocks.ToDictionary(b => b, b => RuntimePluginControls.PbLoadMsPerFrame(b));
            var top = RuntimePluginControls.PbTop(8);
            var noise = LogLinesSince(logFrom).Count(l => l.Contains("Script over budget"));

            Note("PB RESULT | " + blocks.Count + " programmable blocks | loads: " +
                 string.Join(", ", blocks.Select(b => b.CustomName.ToString() + " " + loads[b].ToString("F2") + " ms/frame")) +
                 " | plugin top: " + top +
                 " | " + metrics.Format() + " | " + probe);

            Check(loads.Values.Any(load => load > 0), "the plugin measured no load at all");
            foreach (var pb in blocks.Where(b => b.CustomName.ToString().Contains("frequent")))
            {
                Check(loads[pb] > 0, "no load measured for " + pb.CustomName);
                Check(top.Contains(pb.CustomName.ToString()),
                    pb.CustomName + " loads the server but the plugin does not report it");
            }

            // While nothing is being punished the log stays quiet: what each script costs belongs in
            // the GUI, not in a line per run.
            Check(noise == 0, noise + " log lines about scripts over budget while nothing was punished");

            var punish = PunishPhase(blocks);
            while (punish.MoveNext()) yield return punish.Current;
        }

        /// <summary>
        /// Switches punishment on and waits for the blocks to be taken out of service, timing each
        /// one: a block that loads the server has to stop doing so promptly, not eventually.
        /// </summary>
        private IEnumerator PunishPhase(List<MyProgrammableBlock> blocks)
        {
            _punishLogFrom = LogLineCount();
            _config.Set("EnableScriptsPunish", true);
            Note("punishment on: waiting for the blocks to be taken out of service");

            var started = DateTime.UtcNow;
            var punishedAt = new Dictionary<MyProgrammableBlock, double>();
            var deadline = RarePunishDeadline + 10;
            while ((DateTime.UtcNow - started).TotalSeconds < deadline && punishedAt.Count < blocks.Count)
            {
                foreach (var pb in blocks)
                {
                    if (punishedAt.ContainsKey(pb) || pb.Enabled) continue;
                    punishedAt[pb] = (DateTime.UtcNow - started).TotalSeconds;
                    Note("out of service after " + punishedAt[pb].ToString("F1") + "s: " + pb.CustomName +
                         ", functional=" + pb.IsFunctional +
                         ", integrity " + (pb.SlimBlock.Integrity / pb.SlimBlock.MaxIntegrity).ToString("F2") +
                         " of critical " + pb.BlockDefinition.CriticalIntegrityRatio.ToString("F2"));
                }

                yield return null;
            }

            var missing = blocks.Where(b => !punishedAt.ContainsKey(b)).Select(b => b.CustomName.ToString()).ToList();

            // The log is written by NLog on its own schedule, so give it a moment to catch up before
            // reading what it says about the blocks that were just taken out of service.
            var logged = new List<string>();
            var until = DateTime.UtcNow.AddSeconds(LogFlushSeconds);
            while (DateTime.UtcNow < until)
            {
                logged = LogLinesSince(_punishLogFrom).Where(l => l.Contains("PB deconstructed for load")).ToList();
                if (blocks.All(b => logged.Any(l => l.Contains(b.CustomName.ToString())))) break;
                yield return WaitForTicks(30);
            }

            foreach (var line in logged.Take(3)) Note("log: " + Tail(line));
            Note("PB PUNISH RESULT | out of service: " + punishedAt.Count + "/" + blocks.Count +
                 " | " + string.Join(", ", blocks.Where(punishedAt.ContainsKey)
                     .Select(b => b.CustomName + " " + punishedAt[b].ToString("F1") + "s")) +
                 (missing.Count == 0 ? "" : " | still running: " + string.Join(", ", missing)) +
                 " | load left " + PbLoad_Total().ToString("F2") + " ms/frame");

            Check(missing.Count == 0, "still loading the server after " + deadline.ToString("F0") + "s: " + string.Join(", ", missing));
            foreach (var pb in blocks)
            {
                var kind = pb.CustomName.ToString().Contains("frequent") ? "frequent" : "rare";
                var limit = kind == "frequent" ? FrequentPunishDeadline : RarePunishDeadline;
                Check(punishedAt[pb] <= limit,
                    pb.CustomName + " kept loading the server for " + punishedAt[pb].ToString("F1") + "s, limit " + limit + "s");
                Check(!pb.IsFunctional, pb.CustomName + " is disabled but still functional: it can simply be switched back on");
                Check(pb.SlimBlock.Integrity / pb.SlimBlock.MaxIntegrity < pb.BlockDefinition.CriticalIntegrityRatio,
                    pb.CustomName + " was not damaged below its critical integrity");
                Check(logged.Any(l => l.Contains(pb.CustomName.ToString())),
                    "nothing in the log about deconstructing " + pb.CustomName);
            }
        }

        private static double PbLoad_Total()
        {
            try { return RuntimePluginControls.PbTotalMsPerFrame(); }
            catch (Exception) { return 0; }
        }

        // ------------------------------------------------------------------ the station

        private MyCubeGrid BuildStation(Vector3D position, Vector3D up)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            var owner = WorldApi.PlayerIdentityId();

            void Add(MyObjectBuilder_CubeBlock block, Vector3I min)
            {
                block.Min = new SerializableVector3I(min.X, min.Y, min.Z);
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
                blocks.Add(block);
            }

            // A plate of armour, and batteries on top of it: they power the station and, being
            // terminal blocks, they are what the scripts walk over and over.
            for (var x = 0; x < Side; x++)
            for (var z = 0; z < Side; z++)
                Add(WorldApi.MakeBlockOb("LargeBlockArmorBlock"), new Vector3I(x, 0, z));

            var batteries = 0;
            for (var x = 0; x < Side && batteries < Batteries; x++)
            for (var z = 0; z < Side && batteries < Batteries; z++)
            {
                Add(WorldApi.MakeBlockOb("LargeBlockBatteryBlock"), new Vector3I(x, 1, z));
                batteries++;
            }

            var index = 0;
            for (var i = 0; i < FrequentBlocks; i++)
                Add(ProgrammableBlock("frequent-" + i, Script("Update1", FrequentPasses)), new Vector3I(index++, 2, 0));
            for (var i = 0; i < RareBlocks; i++)
                Add(ProgrammableBlock("rare-" + i, Script("Update100", RarePasses)), new Vector3I(index++, 2, 0));

            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "station",
                DisplayName = WorldApi.EntityPrefix + Prefix + "station",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(
                    MatrixD.CreateWorld(position, Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up)), up)),
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
            var station = WorldApi.SpawnGrid(ob);
            Track(station);
            WorldApi.ChargeBatteries(station);
            return station;
        }

        private static MyObjectBuilder_CubeBlock ProgrammableBlock(string name, string program)
        {
            var ob = (Sandbox.Common.ObjectBuilders.MyObjectBuilder_MyProgrammableBlock)
                WorldApi.MakeBlockOb("LargeProgrammableBlock");
            ob.CustomName = WorldApi.EntityPrefix + Prefix + name;
            ob.Program = program;
            ob.Enabled = true;
            return ob;
        }

        /// <summary>
        /// A script that keeps the terminal system busy: one GetBlocks over a few hundred blocks is
        /// a single instruction to the sandbox counter but real work for the server.
        /// </summary>
        private static string Script(string frequency, int passes) =>
            "List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();\n" +
            "public Program() { Runtime.UpdateFrequency = UpdateFrequency." + frequency + "; }\n" +
            "public void Main(string argument, UpdateType source)\n" +
            "{\n" +
            "    for (int i = 0; i < " + passes + "; i++) GridTerminalSystem.GetBlocks(blocks);\n" +
            "}\n";

        // ------------------------------------------------------------------ the server log

        private static string LogFile()
        {
            var dir = Path.Combine(Environment.CurrentDirectory, "Logs");
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "Torch-*.log").OrderByDescending(f => f).FirstOrDefault()
                : null;
        }

        private static int LogLineCount() => ReadLog().Count;

        private static List<string> LogLinesSince(int from)
        {
            var lines = ReadLog();
            return from >= lines.Count ? new List<string>() : lines.GetRange(from, lines.Count - from);
        }

        private static List<string> ReadLog()
        {
            var file = LogFile();
            if (file == null) return new List<string>();
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                var lines = new List<string>();
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
                return lines;
            }
        }

        private static string Tail(string line)
        {
            var at = line.IndexOf("PB deconstructed", StringComparison.Ordinal);
            return at < 0 ? line : line.Substring(at);
        }

        public override void Cleanup()
        {
            try { _config.Restore(); }
            finally { base.Cleanup(); }
        }

        public override void CleanupLeftovers()
        {
            try
            {
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    if (!(grid.Name ?? "").StartsWith(WorldApi.EntityPrefix + Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(grid);
                    grid.Close();
                }
            }
            finally { base.CleanupLeftovers(); }
        }
    }
}
