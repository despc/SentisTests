using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A large grid built on block by block, as a ship welded from a projection (prod, dotTrace 02.11.2026): every block
    /// with a conveyor port makes the grid work its conveyor graph out again, and when that is done each conveyor line asks
    /// the power distributor for the state - a full recompute of the grid's distribution on the game thread whenever a
    /// block with a power sink came in meanwhile.
    ///
    /// A static station of <see cref="Lights"/> lights (power sinks), reactors and a conveyor line; for
    /// <see cref="PhaseSeconds"/> s a conveyor junction and a light are added every other frame (a fleet of welders), first with
    /// SentisOptimisations' PowerRecomputePace off (factor 0), then on (factor 8). Measured: the plugin's own count of
    /// the distributors' recomputes run and put off and the milliseconds they took (no patch of the scenario's on the
    /// method: a Harmony patch beside the plugin's Torch one would leave only one of them running), and the frames'
    /// length. Passes when with the pace the recomputes take at most half the time they do without, and every added
    /// light ends up powered.
    /// </summary>
    public class PowerRecomputePaceScenario : TestScenario
    {
        public const string ScenarioName = "power_recompute_pace";
        private const int Side = 100;
        private const int Lights = Side * Side;
        private const double PhaseSeconds = 10;
        private const int EveryFrames = 2;

        private ConfigOverride _config;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 300;
        public override string[] Requires => new[] { Integrations.Optimisations };

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + "prp-anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var at = anchor + Vector3D.Normalize(new Vector3D(-0.4, 0.8, 0.45)) * 300000;
            var grid = Station(at);
            yield return WaitForTicks(120);
            var distributor = grid.GridSystems.ResourceDistributor;
            Note("station: " + grid.BlocksCount + " blocks, power " + distributor.ResourceStateByType(MyResourceDistributorComponent.ElectricityId));
            // what one recompute of the station costs (the pace off for it)
            var probe = new ConfigOverride();
            probe.Set("PowerRecomputePauseFactor", 0);
            var electricity = MyResourceDistributorComponent.ElectricityId;
            var costs = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var w = Stopwatch.StartNew();
                distributor.RecomputeResourceDistribution(ref electricity);
                costs.Add(w.Elapsed.TotalMilliseconds);
            }
            probe.Restore();
            Note("one recompute of the station: " + string.Join(", ", costs.Select(c => c.ToString("F1"))) + " ms");

            var pace = Integrations.TypeOf(Integrations.Optimisations, "Optimizer.Optimizations.PowerRecomputePace");
            Check(pace != null, "SentisOptimisations has no PowerRecomputePace");
            pace.GetField("Watched").SetValue(null, distributor);
            long Count(string field) => Convert.ToInt64(pace.GetField(field).GetValue(null));
            double TotalMs() => Convert.ToDouble(pace.GetProperty("WatchedMs").GetValue(null));

            _config = new ConfigOverride();
            var added = new List<IMySlimBlock>();
            var sums = new Dictionary<int, Phase>();
            var next = 0;
            var conveyor = grid.GridSystems.ConveyorSystem;
            var recomputing = typeof(MyGridConveyorSystem).GetField("m_isRecomputingGraph", BindingFlags.Instance | BindingFlags.NonPublic);
            // a warm-up first (not counted), then each factor twice, in the order 0, 8, 8, 0: neither gets the warmer or the colder start
            foreach (var factor in new[] { -1, 0, 8, 8, 0 })
            {
                _config.Set("PowerRecomputePauseFactor", Math.Max(0, factor));
                var phase = new Phase();
                var runs0 = Count("WatchedRuns");
                var putOff0 = Count("WatchedPutOffs");
                var ms0 = TotalMs();
                var frames = (int)(PhaseSeconds * 60);
                var watch = Stopwatch.StartNew();
                var last = 0.0;
                var wasRecomputing = false;
                for (var f = 0; f < frames; f++)
                {
                    var now = watch.Elapsed.TotalMilliseconds;
                    if (f > 0) phase.Frame(now - last);
                    last = now;
                    var busy = (bool)recomputing.GetValue(conveyor);
                    if (busy && !wasRecomputing) phase.GraphRuns++;
                    wasRecomputing = busy;
                    if (f % EveryFrames == 0)
                    {
                        // along the conveyor line: a junction, a light beside it
                        var x = 2 + next++;
                        var inRuns0 = Count("WatchedRuns");
                        var inMs0 = TotalMs();
                        var aw = Stopwatch.StartNew();
                        AddBlock(grid, new MyObjectBuilder_Conveyor { SubtypeName = "LargeBlockConveyor" }, new Vector3I(x, 1, -2));
                        var light = AddBlock(grid, new MyObjectBuilder_InteriorLight { SubtypeName = "SmallLight" }, new Vector3I(x, 2, -2));
                        phase.AddMs += aw.Elapsed.TotalMilliseconds;
                        phase.InAddRuns += Count("WatchedRuns") - inRuns0;
                        phase.InAddMs += TotalMs() - inMs0;
                        if (light != null) added.Add(light);
                    }
                    yield return null;
                }
                yield return WaitForTicks(90);
                phase.Runs = Count("WatchedRuns") - runs0;
                phase.Ms = TotalMs() - ms0;
                phase.PutOff = Count("WatchedPutOffs") - putOff0;
                Note((factor < 0 ? "warm-up" : "factor " + factor) + ": " + phase);
                if (factor < 0) continue;
                if (!sums.TryGetValue(factor, out var sum)) sums[factor] = sum = new Phase();
                sum.Add(phase);
            }
            var without = sums[0];
            var with = sums[8];
            Note("in all, without: " + without);
            Note("in all, with: " + with);
            yield return WaitForTicks(120);
            var unpowered = added.Count(b => b.FatBlock is IMyFunctionalBlock fb && fb.Enabled && !fb.IsWorking);
            Note(added.Count + " lights added, " + unpowered + " of them not working");
            Check(unpowered == 0, unpowered + " of the added lights are not powered after the phase");
            Check(without.Ms > 0, "no recompute of the station was seen without the pace: the scenario does not reach the case");
            Check(with.PutOff > 0, "with the pace no recompute of the station was put off");
            Check(with.Ms <= without.Ms / 2, "with the pace the station's recomputes took " + with.Ms.ToString("F0") + " ms, without " + without.Ms.ToString("F0") + " ms");
            Check(with.Excess <= without.Excess, "with the pace the frames ran " + with.Excess.ToString("F0") + " ms over 16.7 ms, without " + without.Excess.ToString("F0") + " ms");
            Check(with.Over50 <= without.Over50, "with the pace " + with.Over50 + " frames over 50 ms, without " + without.Over50);
            Check(with.GraphRuns <= without.GraphRuns * 1.1 + 2, "with the pace the conveyor graph was worked out " + with.GraphRuns + " times, without " + without.GraphRuns);
            Note("PASS: with the pace " + with.Ms.ToString("F0") + " ms of recomputes against " + without.Ms.ToString("F0") + ", frames over 16.7 ms by " +
                 with.Excess.ToString("F0") + " ms against " + without.Excess.ToString("F0") + ", over 50 ms " + with.Over50 + " against " + without.Over50);
        }

        /// <summary>What a phase measured (or the sum of phases).</summary>
        private sealed class Phase
        {
            public long Runs, PutOff, InAddRuns;
            public double Ms, AddMs, InAddMs, Excess, Longest;
            public int Over33, Over50, GraphRuns;

            public void Frame(double ms)
            {
                Longest = Math.Max(Longest, ms);
                if (ms > 16.7) Excess += ms - 16.7;
                if (ms > 33.4) Over33++;
                if (ms > 50) Over50++;
            }

            public void Add(Phase p)
            {
                Runs += p.Runs; PutOff += p.PutOff; InAddRuns += p.InAddRuns;
                Ms += p.Ms; AddMs += p.AddMs; InAddMs += p.InAddMs; Excess += p.Excess; Longest = Math.Max(Longest, p.Longest);
                Over33 += p.Over33; Over50 += p.Over50; GraphRuns += p.GraphRuns;
            }

            public override string ToString() =>
                "the station's power recomputed " + Runs + " times, " + Ms.ToString("F0") + " ms (" + (Runs > 0 ? (Ms / Runs).ToString("F2") : "-") + " ms each), " +
                PutOff + " put off; the conveyor graph worked out " + GraphRuns + " times; frames: " + Excess.ToString("F0") + " ms over 16.7 ms, " + Over33 + " over 33 ms, " +
                Over50 + " over 50 ms, the longest " + Longest.ToString("F0") + " ms; adding the blocks " + AddMs.ToString("F0") + " ms, of it " + InAddRuns + " recomputes " +
                InAddMs.ToString("F0") + " ms";
        }

        private static IMySlimBlock AddBlock(MyCubeGrid grid, MyObjectBuilder_CubeBlock block, Vector3I at)
        {
            block.Min = at;
            block.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
            block.IntegrityPercent = 1;
            block.BuildPercent = 1;
            return ((IMyCubeGrid)grid).AddBlock(block, false);
        }

        /// <summary>A plate of lights on armor, eight reactors with uranium, a cargo container with a conveyor line out of it.</summary>
        private MyCubeGrid Station(Vector3D at)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < Side; x++)
            for (var z = 0; z < Side; z++)
            {
                blocks.Add(new MyObjectBuilder_CubeBlock { SubtypeName = "LargeBlockArmorBlock", Min = new SerializableVector3I(x, 0, z) });
                blocks.Add(new MyObjectBuilder_InteriorLight { SubtypeName = "SmallLight", Min = new SerializableVector3I(x, 1, z), Enabled = true });
            }
            for (var i = 0; i < 8; i++)
                blocks.Add(new MyObjectBuilder_Reactor
                {
                    SubtypeName = "LargeBlockLargeGenerator", Min = new SerializableVector3I(i * 3, -3, 0), Enabled = true,
                    Inventory = new MyObjectBuilder_Inventory { Items = new List<MyObjectBuilder_InventoryItem> { new MyObjectBuilder_InventoryItem { Amount = 100, PhysicalContent = new MyObjectBuilder_Ingot { SubtypeName = "Uranium" } } } }
                });
            blocks.Add(new MyObjectBuilder_CargoContainer { SubtypeName = "LargeBlockSmallContainer", Min = new SerializableVector3I(0, 1, -2) });
            blocks.Add(new MyObjectBuilder_Conveyor { SubtypeName = "LargeBlockConveyor", Min = new SerializableVector3I(1, 1, -2) });
            foreach (var b in blocks)
            {
                b.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
                b.IntegrityPercent = 1;
                b.BuildPercent = 1;
            }
            var builder = new MyObjectBuilder_CubeGrid
            {
                GridSizeEnum = MyCubeSize.Large, IsStatic = true, DisplayName = WorldApi.EntityPrefix + "power-pace", CreatePhysics = true,
                PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.Enabled,
                PositionAndOrientation = new MyPositionAndOrientation(at, Vector3.Forward, Vector3.Up), CubeBlocks = blocks
            };
            MyAPIGateway.Entities.RemapObjectBuilder(builder);
            var grid = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(builder);
            Track(grid);
            return grid;
        }

        public override void Cleanup()
        {
            _config?.Restore();
            Integrations.TypeOf(Integrations.Optimisations, "Optimizer.Optimizations.PowerRecomputePace")?.GetField("Watched")?.SetValue(null, null);
            base.Cleanup();
        }
    }
}
