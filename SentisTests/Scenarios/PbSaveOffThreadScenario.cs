using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A programmable block whose script has a Save() stays on when its grid's builder is made off the game thread.
    ///
    /// SentisOptimisations' ParallelEntitySave makes a frozen grid's builder on a worker; the programmable block's builder
    /// runs the script's Save() there. PBFix's run prefix reported that to MyModWatchdog.ReportIncorrectBehaviour, which
    /// outside a mod's context threw, and PBFix switched the block off - players' blocks went off at every world save
    /// (production, 04.10.2026). A static grid with a battery and a programmable block holding a small script with Save(),
    /// spawned from its builder (the script also has to start although the block came in before its grid had its logical
    /// group); once it runs, the grid's builder is made on a worker, twice; the block has to stay on and its storage be
    /// the one Save() wrote.
    /// </summary>
    public sealed class PbSaveOffThreadScenario : TestScenario
    {
        public const string ScenarioName = "pb_save_offthread";
        private const string Prefix = "pb-save-";

        private const string Script =
            "int runs;\n" +
            "public Program() { Runtime.UpdateFrequency = UpdateFrequency.Update10; }\n" +
            "public void Save() { Storage = \"saved \" + runs; }\n" +
            "public void Main(string argument, UpdateType updateSource) { runs++; Echo(\"runs \" + runs); }\n";

        private readonly ConfigOverride _optimisations = new ConfigOverride(ConfigOverride.Optimisations);

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 90;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            _optimisations.Set("FreezerEnabled", false);
            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchor.Translation - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var at = anchor.Translation + up * 1000 + side * 1700;

            var owner = WorldApi.PlayerIdentityId();
            var battery = WorldApi.MakeBlockOb("LargeBlockBatteryBlock");
            battery.Min = new SerializableVector3I(0, 0, 0);
            if (battery is MyObjectBuilder_BatteryBlock charged)
            {
                charged.CurrentStoredPower = 3f;
                charged.ProducerEnabled = true;
            }
            var pbOb = (MyObjectBuilder_MyProgrammableBlock)WorldApi.MakeBlockOb("LargeProgrammableBlock");
            pbOb.Min = new SerializableVector3I(0, 1, 0);
            pbOb.Program = Script;
            pbOb.Enabled = true;
            var blocks = new List<MyObjectBuilder_CubeBlock> { battery, pbOb };
            foreach (var block in blocks)
            {
                block.Owner = owner;
                block.BuiltBy = owner;
            }
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + "grid",
                DisplayName = WorldApi.EntityPrefix + Prefix + "grid",
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, side, up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            var pb = grid.GetFatBlocks().OfType<MyProgrammableBlock>().FirstOrDefault();
            Check(pb != null, "no programmable block");

            string Echo() => (typeof(MyProgrammableBlock).GetField("m_echoOutput", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(pb) as System.Text.StringBuilder)?.ToString() ?? "";
            var running = Wait(() => Echo().StartsWith("runs "), "the script runs", 20);
            while (running.MoveNext()) yield return running.Current;
            Note($"the script runs: '{Echo().Trim()}', the block {(pb.Enabled ? "on" : "off")}");

            for (var round = 1; round <= 2; round++)
            {
                var build = System.Threading.Tasks.Task.Run(() => (MyObjectBuilder_CubeGrid)grid.GetObjectBuilder());
                while (!build.IsCompleted) yield return null;
                var storage = (build.IsFaulted ? null : build.Result.CubeBlocks.OfType<MyObjectBuilder_MyProgrammableBlock>().FirstOrDefault())?.Storage;
                Note($"builder {round} made on a worker: {(build.IsFaulted ? "failed: " + build.Exception?.GetBaseException().Message : "made")}, " +
                     $"the script's storage '{storage}', the block {(pb.Enabled ? "on" : "off")}");
                Check(pb.Enabled, $"the programmable block was switched off by its Save() run off the game thread (builder {round})");
                var settle = WaitForSeconds(1, "the script runs on");
                while (settle.MoveNext()) yield return settle.Current;
            }
            Note($"after the builders: '{Echo().Trim()}', the block {(pb.Enabled ? "on" : "off")}");
            Check(Echo().StartsWith("runs "), "the script stopped running");
        }

        public override void Cleanup()
        {
            try { _optimisations.Restore(); }
            finally { base.Cleanup(); }
        }
    }
}
