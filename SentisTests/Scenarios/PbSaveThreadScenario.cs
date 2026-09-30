using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using Torch.Managers.PatchManager;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A world save runs a programmable block's script (its Save()) on the game thread when the grid is not frozen
    /// (SentisOptimisations ParallelEntitySave).
    ///
    /// Making a programmable block's builder runs the player's script: <c>MyProgrammableBlock.UpdateStorage</c> calls its
    /// Save(). The parallel world save made grid builders on its workers, so scripts ran on sixteen threads at once, and
    /// the plugin's script watchdog punished a slow Save() there - damaged the block and remade its Havok bodies off the
    /// game thread. On the old server that corrupted native memory and crashed it seconds after saves (28-29.09.2026).
    ///
    /// This puts a grid with a scripted programmable block beside a player (not frozen) among <see cref="Filler"/> plain
    /// grids, so the parallel path is taken, saves the world, and checks on which thread the script's Save() ran.
    /// </summary>
    public sealed class PbSaveThreadScenario : TestScenario
    {
        public const string ScenarioName = "pb_save_thread";
        private const string Prefix = "pbsave-";
        private const int Filler = 40;
        private const double HeightM = 200;
        private const double SaveTimeoutSeconds = 180;
        private const string Program = "public void Main(string argument) { }\npublic void Save() { Storage = \"saved \" + DateTime.Now.Ticks; }";

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        // the probe: every UpdateStorage of the watched block, and whether it ran on the game thread
        private static long _watched;
        private static int _onGame, _offGame;
        private static bool _probeInstalled;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);
            var torch = SentisTestsPlugin.TorchInstance;
            Check(torch != null, "Torch instance is not available to the test plugin");
            InstallProbe((PatchManager)torch.Managers.GetManager(typeof(PatchManager)));

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var forward = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var site = anchorM.Translation + up * HeightM;

            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "pb", MyCubeSize.Large, true, site, new[]
            {
                new BlockSpec("LargeBlockBatteryBlock", new Vector3I(0, 0, 0)),
                new BlockSpec("LargeProgrammableBlock", new Vector3I(1, 0, 0)),
            }, forward, up);
            foreach (var block in ob.CubeBlocks)
                if (block is MyObjectBuilder_MyProgrammableBlock pbOb) pbOb.Program = Program;
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            for (var i = 0; i < Filler; i++)
                Track(WorldApi.SpawnGrid(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "filler-" + i, MyCubeSize.Large, true,
                    site + forward * (30 + i * 12), new[] { new BlockSpec("LargeBlockArmorBlock", new Vector3I(0, 0, 0)) }, forward, up)));
            FakeClients.Add(1, Network, i => (site + up * 5, 0, 0), withCharacters: true);
            yield return WaitForTicks(60);
            WorldApi.ChargeBatteries(grid);

            var pb = Require<MyProgrammableBlock>(WorldApi.FindFunctional<MyProgrammableBlock>(grid), "the programmable block");
            pb.Enabled = true;
            var started = DateTime.UtcNow;
            var instance = typeof(MyProgrammableBlock).GetField("m_instance", BindingFlags.Instance | BindingFlags.NonPublic)
                           ?? throw new MissingFieldException("MyProgrammableBlock", "m_instance");
            var compileWait = DateTime.UtcNow;
            while (instance.GetValue(pb) == null)
            {
                if ((DateTime.UtcNow - compileWait).TotalSeconds > 30)
                    throw new ScenarioFailedException("the script did not compile: " + ((pb as Sandbox.ModAPI.IMyTerminalBlock).DetailedInfo ?? "").Replace("\n", " "));
                yield return null;
            }
            Check(!RuntimePluginControls.IsGridFrozen(grid.EntityId), "the grid beside the player froze");

            _watched = pb.EntityId;
            _onGame = _offGame = 0;
            var watch = Stopwatch.StartNew();
            var task = torch.Save();
            Check(task != null, "Torch refused to start a save");
            while (!task.IsCompleted)
            {
                if (watch.Elapsed.TotalSeconds > SaveTimeoutSeconds) throw new ScenarioFailedException("the save did not finish");
                yield return null;
            }
            _watched = 0;
            Note("PB SAVE THREAD RESULT | the script's Save() during the save: on the game thread " + _onGame + ", on other threads " + _offGame +
                 " | save " + watch.Elapsed.TotalSeconds.ToString("F1") + " s");
            Check(_onGame + _offGame > 0, "the script's Save() was not called during the save (did the script compile?)");
            Check(_offGame == 0, "the script's Save() ran off the game thread " + _offGame + " time(s)");
        }

        private static void InstallProbe(PatchManager patchManager)
        {
            if (_probeInstalled) return;
            var ctx = patchManager.AcquireContext();
            var target = typeof(MyProgrammableBlock).GetMethod("UpdateStorage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                         ?? throw new MissingMethodException("MyProgrammableBlock", "UpdateStorage");
            ctx.GetPattern(target).Prefixes.Add(typeof(PbSaveThreadScenario).GetMethod(nameof(UpdateStoragePrefix), BindingFlags.Static | BindingFlags.NonPublic));
            patchManager.Commit();
            _probeInstalled = true;
        }

        private static void UpdateStoragePrefix(MyProgrammableBlock __instance)
        {
            if (_watched == 0 || __instance.EntityId != _watched) return;
            if (MySandboxGame.Static?.UpdateThread == Thread.CurrentThread) Interlocked.Increment(ref _onGame);
            else Interlocked.Increment(ref _offGame);
        }

        public override void Cleanup()
        {
            _watched = 0;
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
