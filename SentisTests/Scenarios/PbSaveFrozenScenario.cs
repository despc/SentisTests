using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using Torch.Managers.PatchManager;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A world save runs the Save() of a script on a frozen grid on the game thread when that Save() has code in it, and
    /// leaves a grid whose scripts have an empty Save() (the template's) or none on the save's workers
    /// (SentisOptimisations ParallelEntitySave, ScriptSaveCode).
    ///
    /// A frozen grid's builder is made on a worker, and a programmable block's builder calls the script's Save(): a
    /// script that does something there (moves items, switches a connector, writes a screen) did it off the game
    /// thread (production, 06.10.2026: "PB ... run off the game thread" at the save of a world's unload).
    ///
    /// Three grids with a programmable block each - a Save() that writes its Storage, the empty Save() of the template,
    /// no Save() at all - are left without a player until the freezer takes them, and the world is saved at once:
    /// before the builders of frozen grids are made ahead on the game thread (FrozenGridSaveCache waits 300 frames of
    /// frozen), so the workers make them. What is checked: what the plugin says of each script as the game compiled it,
    /// and the thread the first one's Save() ran on.
    /// </summary>
    public sealed class PbSaveFrozenScenario : TestScenario
    {
        public const string ScenarioName = "pb_save_frozen";
        private const string Prefix = "pbfrozen-";
        private const int Filler = 40;
        private const double HeightM = 200;
        private const double FreezeTimeoutSeconds = 90;
        private const double SaveTimeoutSeconds = 180;

        private static readonly (string Name, string Program, bool Code)[] Scripts =
        {
            ("code", "public void Main(string argument) { }\npublic void Save() { Storage = \"saved \" + DateTime.Now.Ticks; }", true),
            ("empty", "public void Main(string argument) { }\npublic void Save() {\n    // Called when the program needs to save its state.\n}", false),
            ("none", "public void Main(string argument) { }", false),
        };

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        // the probe: every UpdateStorage of the watched blocks, and whether it ran on the game thread
        private static readonly Dictionary<long, int[]> Watched = new Dictionary<long, int[]>();
        private static volatile bool _watching;
        private static bool _probeInstalled;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 420;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);
            var torch = SentisTestsPlugin.TorchInstance;
            Check(torch != null, "Torch instance is not available to the test plugin");
            InstallProbe((PatchManager)torch.Managers.GetManager(typeof(PatchManager)));
            var optimisations = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SentisOptimisations");
            Check(optimisations != null, "SentisOptimisations is not loaded");
            var saveHasCode = optimisations.GetType("SentisOptimisationsPlugin.PBFix", true).GetMethod("SaveHasCode", BindingFlags.Public | BindingFlags.Static)
                              ?? throw new MissingMethodException("PBFix", "SaveHasCode");
            var scriptSaves = optimisations.GetType("Optimizer.Optimizations.ParallelEntitySave", true).GetMethod("ScriptSaves", BindingFlags.Public | BindingFlags.Static)
                              ?? throw new MissingMethodException("ParallelEntitySave", "ScriptSaves");

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var forward = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var site = anchorM.Translation + up * HeightM;

            var grids = new List<MyCubeGrid>();
            for (var s = 0; s < Scripts.Length; s++)
            {
                var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + Scripts[s].Name, MyCubeSize.Large, true, site + forward * (s * 15), new[]
                {
                    new BlockSpec("LargeBlockBatteryBlock", new Vector3I(0, 0, 0)),
                    new BlockSpec("LargeProgrammableBlock", new Vector3I(1, 0, 0)),
                }, forward, up);
                foreach (var block in ob.CubeBlocks)
                    if (block is MyObjectBuilder_MyProgrammableBlock pbOb) pbOb.Program = Scripts[s].Program;
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                grids.Add(grid);
            }
            for (var i = 0; i < Filler; i++)
                Track(WorldApi.SpawnGrid(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "filler-" + i, MyCubeSize.Large, true,
                    site + forward * (60 + i * 12), new[] { new BlockSpec("LargeBlockArmorBlock", new Vector3I(0, 0, 0)) }, forward, up)));
            // a player beside them while the scripts compile: nothing freezes
            FakeClients.Add(1, Network, i => (site + up * 5, 0, 0), withCharacters: true);
            yield return WaitForTicks(60);

            var instance = typeof(MyProgrammableBlock).GetField("m_instance", BindingFlags.Instance | BindingFlags.NonPublic)
                           ?? throw new MissingFieldException("MyProgrammableBlock", "m_instance");
            var pbs = new List<MyProgrammableBlock>();
            foreach (var grid in grids)
            {
                WorldApi.ChargeBatteries(grid);
                var pb = Require<MyProgrammableBlock>(WorldApi.FindFunctional<MyProgrammableBlock>(grid), "the programmable block of " + grid.DisplayName);
                pb.Enabled = true;
                pbs.Add(pb);
            }
            var compileWait = DateTime.UtcNow;
            while (pbs.Any(pb => instance.GetValue(pb) == null))
            {
                if ((DateTime.UtcNow - compileWait).TotalSeconds > 30)
                    throw new ScenarioFailedException("a script did not compile: " + string.Join(" | ", pbs.Where(pb => instance.GetValue(pb) == null)
                        .Select(pb => pb.CubeGrid.DisplayName + ": " + ((pb as Sandbox.ModAPI.IMyTerminalBlock).DetailedInfo ?? "").Replace("\n", " "))));
                yield return null;
            }

            // what the plugin reads in the scripts as the game compiled them (its counters are in every method)
            var said = new List<string>();
            for (var s = 0; s < Scripts.Length; s++)
            {
                var block = (bool)saveHasCode.Invoke(null, new object[] { pbs[s] });
                var grid = (bool)scriptSaves.Invoke(null, new object[] { grids[s] });
                said.Add(Scripts[s].Name + ": Save() has code " + block + ", the grid is the game thread's " + grid);
                Check(block == Scripts[s].Code, "the script \"" + Scripts[s].Name + "\": Save() has code read as " + block + ", it is " + Scripts[s].Code);
                Check(grid == Scripts[s].Code, "the grid of the script \"" + Scripts[s].Name + "\": for the game thread read as " + grid + ", it is " + Scripts[s].Code);
            }

            // the player leaves: the freezer takes the grids
            FakeClients.RemoveAll();
            var freezeWait = Stopwatch.StartNew();
            while (!grids.All(g => RuntimePluginControls.IsGridFrozen(g.EntityId)))
            {
                if (freezeWait.Elapsed.TotalSeconds > FreezeTimeoutSeconds)
                    throw new ScenarioFailedException("the grids did not freeze in " + FreezeTimeoutSeconds + " s: frozen " +
                                                      string.Join(", ", grids.Select(g => g.DisplayName + " " + RuntimePluginControls.IsGridFrozen(g.EntityId))));
                yield return null;
            }

            lock (Watched)
            {
                Watched.Clear();
                foreach (var pb in pbs) Watched[pb.EntityId] = new int[2];
            }
            _watching = true;
            var watch = Stopwatch.StartNew();
            var task = torch.Save();
            Check(task != null, "Torch refused to start a save");
            while (!task.IsCompleted)
            {
                if (watch.Elapsed.TotalSeconds > SaveTimeoutSeconds) throw new ScenarioFailedException("the save did not finish");
                yield return null;
            }
            _watching = false;
            var stillFrozen = grids.All(g => RuntimePluginControls.IsGridFrozen(g.EntityId));
            int[] Of(int s) { lock (Watched) return Watched[pbs[s].EntityId]; }
            Note("PB SAVE FROZEN RESULT | " + string.Join("; ", said) + " | builders made on the game thread / on workers: " +
                 string.Join(", ", Scripts.Select((script, s) => script.Name + " " + Of(s)[0] + "/" + Of(s)[1])) +
                 " | frozen " + freezeWait.Elapsed.TotalSeconds.ToString("F1") + " s after the player left, still frozen after the save " + stillFrozen +
                 " | save " + watch.Elapsed.TotalSeconds.ToString("F1") + " s");
            Check(stillFrozen, "a grid thawed during the save: nothing is shown of frozen grids");
            Check(Of(0)[0] + Of(0)[1] > 0, "the builder of the block with code in its Save() was not made during the save");
            Check(Of(0)[1] == 0, "the Save() with code in it ran off the game thread " + Of(0)[1] + " time(s)");
            // the two without code are the workers' as before: if they were not, the save waited for the builders made
            // ahead and the check above shows nothing
            Check(Of(1)[1] + Of(2)[1] > 0, "no builder of the frozen grids was made on a worker: the save did not take the parallel path, the check above shows nothing");
        }

        private static void InstallProbe(PatchManager patchManager)
        {
            if (_probeInstalled) return;
            var ctx = patchManager.AcquireContext();
            var target = typeof(MyProgrammableBlock).GetMethod("UpdateStorage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                         ?? throw new MissingMethodException("MyProgrammableBlock", "UpdateStorage");
            ctx.GetPattern(target).Prefixes.Add(typeof(PbSaveFrozenScenario).GetMethod(nameof(UpdateStoragePrefix), BindingFlags.Static | BindingFlags.NonPublic));
            patchManager.Commit();
            _probeInstalled = true;
        }

        private static void UpdateStoragePrefix(MyProgrammableBlock __instance)
        {
            if (!_watching) return;
            lock (Watched)
            {
                if (!Watched.TryGetValue(__instance.EntityId, out var counts)) return;
                counts[MySandboxGame.Static?.UpdateThread == Thread.CurrentThread ? 0 : 1]++;
            }
        }

        public override void Cleanup()
        {
            _watching = false;
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
