using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// What the clusters of the physics share, changed from all of them at once (SentisClusters runs each cluster's
    /// blocks on a thread of its own). <see cref="Rigs"/> rigs 8 km apart along the world's axis, each a cluster with a
    /// fake player by it, for <see cref="RunSeconds"/> s:
    /// <list type="bullet">
    /// <item>a projector shows a slab of armour, a welder builds it and a grinder grinds it off again - blocks made and
    /// closed (entities by id, the builder's PCU, grids parted by the grinding) on the clusters' threads;</item>
    /// <item>a rotor and a hinge part from their heads and join them again every second (the game's logical and physical
    /// groups of grids, one for the process).</item>
    /// </list>
    /// Then: no exception in the clusters' threads; every grid of the rigs and every block entity of them found by id on
    /// the game thread and on another; no closed entity left in the game's stores of entities (more than before); each
    /// mechanism in its head's groups exactly while attached; the PCU the game counts for the builder as the blocks they
    /// built add up. Focus: <c>rigs=5 seconds=120 detach=1</c> (each rig's cluster detached, SentisClusters'
    /// <c>!clusters detach</c>). Not part of the regression: it takes the freezer off and detaches clusters.
    /// </summary>
    public sealed class ClusterStressCheck : RegressionCheck
    {
        public const string ScenarioName = "cluster_stress";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;
        private const double Apart = 8000;
        private int Rigs = 5;
        private int RunSeconds = 120;

        private readonly Scenarios.ConfigOverride _config = new Scenarios.ConfigOverride();

        private sealed class Rig
        {
            public int Index;
            public Vector3D At;
            public MyCubeGrid Station;
            public MyProjectorBase Projector;
            public SpaceEngineers.Game.Entities.Blocks.MyShipWelder Welder;
            public Sandbox.Game.Weapons.MyShipGrinder Grinder;
            public readonly List<MyMechanicalConnectionBlockBase> Mechanisms = new List<MyMechanicalConnectionBlockBase>();
            public long Toggles, Added, Removed;
        }

        protected override IEnumerator Check()
        {
            Check(Integrations.IsLoaded("SentisClusters") || Command("!clusters status").Any(), "SentisClusters is not loaded");
            var focus = TestRunner.Focus ?? "";
            int Arg(string name, int otherwise) => Regex.Match(focus, name + @"=(\d+)") is var m && m.Success ? int.Parse(m.Groups[1].Value) : otherwise;
            Rigs = Math.Max(1, Arg("rigs", 5));
            RunSeconds = Math.Max(10, Arg("seconds", 120));
            var detach = Arg("detach", 0) == 1;
            _config.Set("FreezerEnabled", false);
            yield return Arrive(Site.At(0, 10, -15));

            var before = Exceptions();
            var closedBefore = ClosedInStores();
            var errorsAt = LogMark();

            // the slab the projectors show: a 3 x 3 plate of light armour
            var slabParts = new List<Part>();
            for (var x = 0; x < 3; x++)
                for (var z = 0; z < 3; z++)
                    slabParts.Add(new Part("CubeBlock/LargeBlockArmorBlock", x, 0, z));
            var source = Build("stress-slab", Site.At(0, 80, 0), true, slabParts);
            var slabOb = (MyObjectBuilder_CubeGrid)source.GetObjectBuilder(true);
            source.Close();
            yield return null;

            var rigs = new List<Rig>();
            for (var k = 0; k < Rigs; k++)
            {
                var rig = new Rig { Index = k, At = Site.Origin + Site.Up * (Apart * k) };
                rigs.Add(rig);
                if (k > 0)
                {
                    var at = rig.At + Site.Forward * -30 + Site.Up * 10;
                    yield return Join(at, i => { });
                }
                rig.Station = Build("stress-" + k, rig.At, true, new[]
                {
                    new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                    new Part("MyObjectBuilder_Projector/LargeProjector", 1, 0, 0).With(b =>
                    {
                        var projector = (MyObjectBuilder_ProjectorBase)b;
                        projector.ProjectedGrids = new List<MyObjectBuilder_CubeGrid> { (MyObjectBuilder_CubeGrid)slabOb.Clone() };
                        projector.ProjectionOffset = new Vector3I(2, 0, 0);
                        projector.KeepProjection = true;
                        projector.ProjectionsRemaining = 0;
                        projector.Scale = 1;
                    }),
                    new Part("BatteryBlock/LargeBlockBatteryBlock", -1, 0, 0).With(b => Motion.Charged(b)),
                    new Part("MotorStator/LargeStator", -2, 0, 0),
                    new Part("MotorAdvancedStator/LargeHinge", -4, 0, 0),
                });
                Put(Inventory(Block<MyReactor>(rig.Station)), "Ingot/Uranium", 20);
                rig.Projector = Block<MyProjectorBase>(rig.Station);
                foreach (var mechanism in Blocks<MyMechanicalConnectionBlockBase>(rig.Station))
                {
                    yield return AddHead(mechanism);
                    rig.Mechanisms.Add(mechanism);
                    if (mechanism.TopGrid != null) Track(mechanism.TopGrid);
                }
            }
            foreach (var rig in rigs)
                yield return Until(() => rig.Projector.IsWorking && rig.Projector.ProjectedGrid != null ? 1 : 0, v => v > 0, "rig " + rig.Index + ": the projection up", 30, () => State(rig.Projector));

            // the PCU taken with the projections up: the game counts the blocks a projector shows to its owner as well
            var pcuBefore = PcuOf(Identity);

            // the tools over the slab, a welder and a grinder, both on the slab's middle
            foreach (var rig in rigs)
            {
                var shown = rig.Projector.ProjectedGrid;
                var middle = shown.PositionComp.WorldAABB.Center;
                var weldRig = ToolRig("stress-welder-" + rig.Index, "ShipWelder/LargeShipWelder", middle, -Site.Forward);
                var grindRig = ToolRig("stress-grinder-" + rig.Index, "ShipGrinder/LargeShipGrinder", middle, Site.Forward);
                rig.Welder = Block<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(weldRig);
                rig.Grinder = Block<Sandbox.Game.Weapons.MyShipGrinder>(grindRig);
                Put(Inventory(rig.Welder), "Component/SteelPlate", 3000);
                Switch(rig.Welder, true);
            }
            // the blocks the welder adds to the station and the grinder takes off it, counted
            foreach (var rig in rigs)
            {
                var counted = rig;
                rig.Station.OnBlockAdded += b => Interlocked.Increment(ref counted.Added);
                rig.Station.OnBlockRemoved += b => Interlocked.Increment(ref counted.Removed);
            }
            yield return Seconds(3);
            foreach (var rig in rigs) Switch(rig.Grinder, true);

            var clusterIds = rigs.Select(r => ClusterId(r.Mechanisms.FirstOrDefault()?.TopGrid ?? (MyEntity)r.Welder.CubeGrid)).ToList();
            Note("rigs " + Rigs + " in clusters " + string.Join(", ", clusterIds));
            if (detach)
                foreach (var rig in rigs)
                {
                    var at = rig.At;
                    var answer = string.Join(" ", Command($"!clusters detach {at.X:F0} {at.Y:F0} {at.Z:F0}"));
                    Note("rig " + rig.Index + ": " + answer);
                }

            // the run: the mechanisms parted and joined every second, the tools on
            var clock = Stopwatch.StartNew();
            var lastToggle = Stopwatch.StartNew();
            var attachNext = false;
            while (clock.Elapsed.TotalSeconds < RunSeconds)
            {
                if (lastToggle.Elapsed.TotalSeconds >= 1)
                {
                    lastToggle.Restart();
                    foreach (var rig in rigs)
                        foreach (var mechanism in rig.Mechanisms)
                        {
                            ((Sandbox.ModAPI.IMyTerminalBlock)mechanism).GetActionWithName(attachNext ? "Attach" : "Detach")?.Apply(mechanism);
                            rig.Toggles++;
                        }
                    attachNext = !attachNext;
                    // the grinder's and the welder's stock kept: the grinder's components to the welder
                    foreach (var rig in rigs)
                    {
                        var grinderInventory = (Sandbox.Game.MyInventory)rig.Grinder.GetInventory(0);
                        if (grinderInventory != null && (double)grinderInventory.CurrentVolume > 0) grinderInventory.Clear();
                        if (Amount(Inventory(rig.Welder), "Component/SteelPlate") < 500) Put(Inventory(rig.Welder), "Component/SteelPlate", 2000);
                    }
                }
                yield return null;
            }

            // stopped, all attached again, the world left to settle
            foreach (var rig in rigs)
            {
                Switch(rig.Welder, false);
                Switch(rig.Grinder, false);
                foreach (var mechanism in rig.Mechanisms)
                    ((Sandbox.ModAPI.IMyTerminalBlock)mechanism).GetActionWithName("Attach")?.Apply(mechanism);
            }
            if (detach) Command("!clusters attach all");
            yield return Seconds(3);

            var report = new List<string>();
            var failures = new List<string>();

            // 1. the clusters' threads
            var thrown = Exceptions() - before;
            report.Add("exceptions in the clusters' threads " + thrown);
            if (thrown > 0) failures.Add(thrown + " exceptions in the clusters' threads");

            // 2. every grid of the rigs and every block entity of them found by id, here and from another thread
            var entities = new List<MyEntity>();
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => !g.MarkedForClose && rigs.Any(r => Vector3D.Distance(g.PositionComp.GetPosition(), r.At) < 500)))
            {
                entities.Add(grid);
                entities.AddRange(grid.GetFatBlocks());
            }
            var missedHere = entities.Count(e => !ReferenceEquals(MyEntities.GetEntityById(e.EntityId), e));
            var missedThere = 0;
            var thread = new Thread(() => missedThere = entities.Count(e => !ReferenceEquals(MyEntities.GetEntityById(e.EntityId), e)));
            thread.Start();
            thread.Join();
            report.Add(entities.Count + " entities of the rigs, not found by id: on the game thread " + missedHere + ", on another " + missedThere);
            if (missedHere + missedThere > 0) failures.Add("entities not found by id: " + missedHere + " on the game thread, " + missedThere + " on another");

            // 3. no closed entities left in the stores
            var closedAfter = ClosedInStores();
            report.Add("closed entities in the stores " + closedBefore + " -> " + closedAfter);
            if (closedAfter > closedBefore + 5) failures.Add("closed entities left in the stores of entities: " + closedBefore + " -> " + closedAfter);

            // 4. the mechanisms in their heads' groups exactly when attached
            var wrongGroups = new List<string>();
            foreach (var rig in rigs)
                foreach (var mechanism in rig.Mechanisms)
                {
                    var attached = ((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)mechanism).IsAttached;
                    var head = mechanism.TopGrid;
                    var logical = head != null && MyCubeGridGroups.Static.Logical.HasSameGroup(mechanism.CubeGrid, head);
                    var physical = head != null && MyCubeGridGroups.Static.Physical.HasSameGroup(mechanism.CubeGrid, head);
                    if (attached != logical || attached != physical)
                        wrongGroups.Add("rig " + rig.Index + " " + mechanism.BlockDefinition.Id.SubtypeName + ": attached " + attached + ", logical " + logical + ", physical " + physical);
                }
            var attachedCount = rigs.Sum(r => r.Mechanisms.Count(m => ((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)m).IsAttached));
            report.Add("mechanisms attached at the end " + attachedCount + " of " + rigs.Sum(r => r.Mechanisms.Count) + ", toggled " + rigs.Sum(r => r.Toggles) +
                       " times, groups wrong " + wrongGroups.Count);
            if (wrongGroups.Count > 0) failures.Add("groups: " + string.Join("; ", wrongGroups));

            // 5. the builder's PCU as the blocks they built add up
            var pcuAfter = PcuOf(Identity);
            report.Add("the builder's PCU counted " + pcuBefore.Counted + " -> " + pcuAfter.Counted + ", in the blocks " + pcuBefore.InBlocks + " -> " + pcuAfter.InBlocks);
            if (pcuAfter.Counted - pcuAfter.InBlocks != pcuBefore.Counted - pcuBefore.InBlocks)
                failures.Add("the builder's PCU drifted from their blocks: counted " + pcuAfter.Counted + ", in the blocks " + pcuAfter.InBlocks +
                             " (before " + pcuBefore.Counted + " / " + pcuBefore.InBlocks + ")");

            // 6. the log
            var errors = LogSince(errorsAt).Where(l => l.Contains("[ERROR]") || l.Contains("[FATAL]")).Where(l => !l.Contains("urlacl") && !l.Contains("SentisAnticheat")).ToList();
            report.Add("errors in the log " + errors.Count);
            if (errors.Count > 0) failures.Add("errors in the log: " + string.Join(" | ", errors.Take(3).Select(l => l.Length > 300 ? l.Substring(0, 300) : l)));

            report.Add("blocks welded onto the stations " + rigs.Sum(r => r.Added) + ", ground off " + rigs.Sum(r => r.Removed) + " (" +
                       string.Join(", ", rigs.Select(r => r.Added + "/" + r.Removed)) + ")");
            if (rigs.Any(r => r.Added < 5 || r.Removed < 5)) failures.Add("a rig built or ground almost nothing: " + string.Join(", ", rigs.Select(r => r.Added + "/" + r.Removed)));
            Check(failures.Count == 0, string.Join(" || ", failures) + " ||| " + string.Join("; ", report));
            Result((detach ? "detached: " : "") + string.Join("; ", report));
        }

        // the Torch log: where it ends now, and its lines after that
        internal static (string File, long Length) LogMark()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(Torch.TorchBase).Assembly.Location) ?? ".", "Logs");
            var file = System.IO.Directory.GetFiles(dir, "Torch-*.log").OrderBy(p => p).LastOrDefault();
            return (file, file == null ? 0 : new System.IO.FileInfo(file).Length);
        }

        internal static List<string> LogSince((string File, long Length) mark)
        {
            var lines = new List<string>();
            if (mark.File == null) return lines;
            var now = LogMark();
            foreach (var (file, from) in now.File == mark.File ? new[] { (mark.File, mark.Length) } : new[] { (mark.File, mark.Length), (now.File, 0L) })
                using (var stream = new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                {
                    stream.Seek(from, System.IO.SeekOrigin.Begin);
                    using (var reader = new System.IO.StreamReader(stream))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null) lines.Add(line);
                    }
                }
            return lines;
        }

        internal static long Exceptions()
        {
            var status = string.Join("\n", Command("!clusters status"));
            var m = Regex.Match(status, @"exceptions in the clusters' threads: (\d+)");
            return m.Success ? long.Parse(m.Groups[1].Value) : 0;
        }

        /// <summary>Closed entities still in the game's stores of entities (the process' and the game thread's own).</summary>
        private static int ClosedInStores()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.NonPublic;
            var count = 0;
            foreach (var name in new[] { "m_mainData", "m_perThreadData" })
            {
                var store = typeof(MyEntityIdentifier).GetField(name, st)?.GetValue(null);
                if (store == null || name == "m_perThreadData" && ReferenceEquals(store, typeof(MyEntityIdentifier).GetField("m_mainData", st)?.GetValue(null))) continue;
                if (store.GetType().GetField("EntityList")?.GetValue(store) is Dictionary<long, IMyEntity> list)
                    count += list.Values.Count(e => e != null && e.Closed);
            }
            return count;
        }

        /// <summary>The PCU the game counts for a builder, and the PCU of the blocks of the world they built.</summary>
        private static (long Counted, long InBlocks) PcuOf(long identityId)
        {
            var identity = Sandbox.Game.World.MySession.Static.Players.TryGetIdentity(identityId);
            long counted = identity?.BlockLimits?.PCUBuilt ?? -1;
            long inBlocks = 0;
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>())
            {
                if (grid.MarkedForClose || grid.Projector != null) continue;
                foreach (var block in grid.GetBlocks())
                    if (block.BuiltBy == identityId)
                        inBlocks += block.BlockDefinition.PCU;
            }
            return (counted, inBlocks);
        }

        internal static List<string> Command(string text)
        {
            var answers = new List<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            commands?.HandleCommandFromServer(text, message => answers.Add(message.Message));
            return answers;
        }

        public override void Cleanup()
        {
            try { Command("!clusters attach all"); }
            catch (Exception) { }
            try { _config.Restore(); }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
