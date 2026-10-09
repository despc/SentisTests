using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What a player's script costs the server, as SentisOptimisations' PBFix measures and judges it: a blueprint with a
    /// programmable block (a player's base, its script stored in the block) spawned static a kilometre up, its blocks
    /// switched on as built, the block's script run for <see cref="WatchSeconds"/> s. Every run PBFix measures is
    /// collected - how long, how often over the limit of one run, the share of every frame - and the moment noted when
    /// PBFix, with punishing on (as on production), would take the block apart. A probe: it passes when the script ran.
    ///
    /// <see cref="IimName"/>: Isy's Inventory Manager 2.9.5 on a base of 93 blocks, burned on the production server.
    /// </summary>
    public sealed class PbScriptLoadScenario : TestScenario
    {
        public const string IimName = "pb_iim";
        /// <summary>The same with punishing off: every run over the limit noted with what the script was doing, for the whole watch.</summary>
        public const string IimWatchName = "pb_iim_watch";
        /// <summary>
        /// Where the scripted base stands as a blueprint - <c>IimBlueprintPath</c> in SentisTests.cfg.
        /// Without it the scenario is skipped: the script it measures belongs to somebody's world.
        /// </summary>
        private static string IimBlueprint => SentisTestsPlugin.Config?.IimBlueprintPath ?? "";
        private const string Prefix = "pb-script-";
        private const double WatchSeconds = 180;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly ConfigOverride _optimisations = new ConfigOverride(ConfigOverride.Optimisations);
        private readonly string _name;
        private readonly string _blueprint;

        private readonly bool _punish;

        public PbScriptLoadScenario(string name, string blueprint, bool punish)
        {
            _name = name;
            _blueprint = blueprint;
            _punish = punish;
        }

        public static PbScriptLoadScenario Iim() => new PbScriptLoadScenario(IimName, IimBlueprint, true);
        public static PbScriptLoadScenario IimWatch() => new PbScriptLoadScenario(IimWatchName, IimBlueprint, false);

        public override string Name => _name;
        public override int TimeoutSeconds => (int)WatchSeconds + 90;

        public override IEnumerator Run()
        {
            RequireFile(_blueprint, "the scripted base this test spawns (SentisTests.cfg: IimBlueprintPath)");
            WorldApi.EnsureUnpaused(Name);
            // (no freezing: a grid a kilometre up with the player 300 m off was frozen, out of its groups, and its script never ran)
            _optimisations.Set("FreezerEnabled", false);
            // punishing on, as on the production server: the block is taken apart when PBFix judges it too heavy
            _optimisations.Set("EnableScriptsPunish", _punish);
            // (looked for in the loaded assemblies: Type.GetType with the assembly's name went to Torch's resolver, which threw)
            var so = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations");
            var load = so?.GetType("SentisOptimisationsPlugin.PbLoad");
            SkipUnless(load != null, "SentisOptimisations' PbLoad not found");
            T Call<T>(string method, params object[] args) => (T)load.GetMethod(method, BindingFlags.Static | BindingFlags.Public).Invoke(null, args);
            var config = so.GetType("SentisOptimisationsPlugin.SentisOptimisationsPlugin")?.GetProperty("Config", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            double Setting(string name, double fallback) => Convert.ToDouble(config?.GetType().GetProperty(name)?.GetValue(config) ?? fallback);
            var maxRun = Setting("ScriptsMaxExecTime", 2);
            var maxLoad = Setting("ScriptsMaxMsPerFrame", 0.5);
            var before = (int)Setting("ScriptOvertimeExecTimesBeforePunish", 3);

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchor.Translation - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var centre = anchor.Translation + up * 1000 - side * 1400;

            FakeClients.RemoveAll();
            FakeClients.Add(1, Network, p => (centre + side * 300 + up * 30, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            var obs = WorldApi.LoadBlueprintFile(_blueprint, WorldApi.EntityPrefix + Prefix + "grid");
            var shift = centre - (Vector3D)obs[0].PositionAndOrientation.Value.Position;
            foreach (var ob in obs)
            {
                var po = ob.PositionAndOrientation.Value;
                po.Position = (Vector3D)po.Position + shift;
                ob.PositionAndOrientation = po;
            }
            var grids = obs.Select(WorldApi.SpawnGrid).ToList();
            foreach (var grid in grids) Track(grid);
            var pb = grids.SelectMany(g => g.GetFatBlocks().OfType<MyProgrammableBlock>()).FirstOrDefault();
            Check(pb != null, "the blueprint has no programmable block");
            Note($"{grids.Count} grids, {grids.Sum(g => g.BlocksCount)} blocks; the programmable block '{pb.CustomName}', its script {((Sandbox.ModAPI.IMyProgrammableBlock)pb).ProgramData?.Length ?? 0} chars; " +
                 $"limits: one run {maxRun} ms, {maxLoad} ms of every frame, punished past {before} of 20 runs over");
            // pasted with the block off, as saved; switched on a few seconds later, as the player does in the terminal
            // (the longest frame after each noted: a compile on the game thread held it 1.5 s)
            var frameWatch = System.Diagnostics.Stopwatch.StartNew();
            var longestAfterPaste = 0.0;
            for (var f = 0; f < 3 * 60; f++)
            {
                var frameStart = frameWatch.Elapsed.TotalMilliseconds;
                yield return null;
                longestAfterPaste = Math.Max(longestAfterPaste, frameWatch.Elapsed.TotalMilliseconds - frameStart);
            }
            Note($"the longest frame in 3 s after the paste: {longestAfterPaste:0} ms");
            ((Sandbox.ModAPI.IMyFunctionalBlock)pb).Enabled = true;
            Note($"the programmable block switched on; its grid's logical group {(Sandbox.Game.Entities.MyCubeGridGroups.Static.Logical.GetGroup(pb.CubeGrid) != null ? "present" : "none")}");
            var burnedAt = -1.0;

            var runs = new List<double>();
            var lastMs = -1.0;
            var peakLoad = 0.0;
            var peakOverruns = 0;
            var punishedAt = -1.0;
            var started = DateTime.UtcNow;
            var longestAfterOn = 0.0;
            var lastFrameAt = frameWatch.Elapsed.TotalMilliseconds;
            var nextNote = 10.0;
            for (var tick = 0; tick < WatchSeconds * 60; tick++)
            {
                var ms = Call<double>("LastMs", pb);
                if (ms > 0 && ms != lastMs)
                {
                    runs.Add(ms);
                    lastMs = ms;
                    if (runs.Count <= 15 || ms > maxRun)
                    {
                        var info = pb.DetailedInfo.ToString();
                        var task = info.Split((char)10).FirstOrDefault(l => l.StartsWith("Task:")) ?? "";
                        var step = info.Split((char)10).FirstOrDefault(l => l.StartsWith("Script step:")) ?? "";
                        Note($"run {runs.Count} at {(DateTime.UtcNow - started).TotalSeconds:0.0} s: {ms:0.000} ms, then {Call<double>("LoadMsPerFrame", pb):0.000} ms of every frame, " +
                             $"{Call<int>("Overruns", pb)} of 20 over; {task.Trim()} {step.Trim()}");
                    }
                }
                var perFrame = Call<double>("LoadMsPerFrame", pb);
                var overruns = Call<int>("Overruns", pb);
                peakLoad = Math.Max(peakLoad, perFrame);
                peakOverruns = Math.Max(peakOverruns, overruns);
                var seconds = (DateTime.UtcNow - started).TotalSeconds;
                var nowMs = frameWatch.Elapsed.TotalMilliseconds;
                if (seconds < 5) longestAfterOn = Math.Max(longestAfterOn, nowMs - lastFrameAt);
                else if (seconds < 5.1 && longestAfterOn > 0) { Note($"the longest frame in 5 s after switching it on: {longestAfterOn:0} ms"); longestAfterOn = -1; }
                lastFrameAt = nowMs;
                if (burnedAt < 0 && (!pb.IsFunctional || pb.MarkedForClose))
                {
                    burnedAt = seconds;
                    Note($"{seconds:0} s: the block burned: functional {pb.IsFunctional}, integrity {pb.SlimBlock.Integrity / pb.SlimBlock.MaxIntegrity * 100:0}%, {runs.Count} runs, last {ms:0.00} ms, {perFrame:0.00} ms of every frame, {overruns} of 20 over");
                    break;
                }
                if (punishedAt < 0 && overruns > before)
                {
                    punishedAt = seconds;
                    Note($"{seconds:0} s: PBFix would take the block apart now - {overruns} of the last 20 runs over, last run {ms:0.00} ms, {perFrame:0.00} ms of every frame");
                }
                if (seconds >= nextNote)
                {
                    nextNote += 15;
                    var echo = (typeof(MyProgrammableBlock).GetField("m_echoOutput", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(pb) as System.Text.StringBuilder)?.ToString() ?? "";
                    const BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    object Field(string f) => typeof(MyProgrammableBlock).GetField(f, any)?.GetValue(pb);
                    var assembly = typeof(MyProgrammableBlock).GetProperty("CurrentAssembly", any)?.GetValue(pb);
                    Note($"{seconds:0} s: the block: assembly {(assembly != null ? "compiled" : "none")}, instance {(Field("m_instance") != null ? "made" : "none")}, " +
                         $"to instantiate {Field("m_needsInstantiation")}, logical group {(Sandbox.Game.Entities.MyCubeGridGroups.Static.Logical.GetGroup(pb.CubeGrid) != null ? "present" : "none")}, in scene {pb.CubeGrid.InScene}, stopped by {Field("m_terminationReason")}, info: {pb.DetailedInfo.ToString().Replace('\n', ' ').Substring(0, Math.Min(300, pb.DetailedInfo.Length))}");
                    Note($"{seconds:0} s: {runs.Count} runs, last {ms:0.00} ms, {perFrame:0.00} ms of every frame, {overruns} of 20 over; enabled {pb.Enabled}, working {pb.IsWorking}; " +
                         $"echo: {string.Join(" | ", echo.Split('\n').Where(l => l.Trim().Length > 0).Take(6))}");
                }
                yield return null;
            }
            runs.Sort();
            double At(double q) => runs.Count == 0 ? 0 : runs[Math.Min(runs.Count - 1, (int)(q * runs.Count))];
            Note($"after {WatchSeconds:0} s: {runs.Count} runs; one run median {At(0.5):0.000} ms, 95% {At(0.95):0.000} ms, 99% {At(0.99):0.000} ms, max {(runs.Count == 0 ? 0 : runs[runs.Count - 1]):0.000} ms; " +
                 $"{runs.Count(r => r > maxRun)} runs over {maxRun} ms; peak share of every frame {peakLoad:0.000} ms; most runs over in a window {peakOverruns} of 20; " +
                 (punishedAt < 0 ? "PBFix would not have punished it" : $"PBFix would have taken it apart after {punishedAt:0} s") +
                (burnedAt < 0 ? "; it did not burn" : $"; it burned after {burnedAt:0} s"));
            Note("PBFix's top blocks: " + Call<string>("Report", 5).Replace("\n", " | "));
            Check(runs.Count > 0, "the script never ran");
            // the grid's builder made on a worker, as a parallel world save does for a frozen grid: the block's builder
            // runs the script's Save() off the game thread - PBFix switched such a block off (production, 04.10.2026)
            if (pb.IsFunctional && !pb.MarkedForClose)
            {
                var build = System.Threading.Tasks.Task.Run(() => pb.CubeGrid.GetObjectBuilder());
                while (!build.IsCompleted) yield return null;
                Note($"the grid's builder made on a worker: {(build.IsFaulted ? "failed: " + build.Exception?.GetBaseException().Message : "made")}; the block {(pb.Enabled ? "still on" : "switched off")}");
                Check(pb.Enabled, "the programmable block was switched off by its script's Save() run off the game thread");
            }
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); _optimisations.Restore(); }
            finally { base.Cleanup(); }
        }
    }
}
