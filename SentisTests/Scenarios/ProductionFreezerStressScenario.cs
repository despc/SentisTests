using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Ingame;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI.Ingame;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Live stress test for SentisOptimisations Freezer compensation. Spawns 64 independent
    /// production grids from a fully serialized fixture (no runtime inventory/queue mutation),
    /// observes every grid entering and leaving the freezer, observes CompensationTracker pending
    /// work being consumed, and requires exact production output on every grid.
    /// </summary>
    public sealed class ProductionFreezerStressScenario : TestScenario
    {
        public const string ScenarioName = "production_freezer_stress";
        private const string ResourceName = "SentisTests.Resources.ProductionStressGrid.xml";
        private const int GridCount = 64;
        private const int WantedPerComponent = 25;
        private const int InitialIronOre = 5000;
        private const int InitialNickelOre = 1250;
        private const int InitialSiliconOre = 500;
        private const int InitialUraniumIngot = 10;
        private const double MinimumFrozenSeconds = 30;

        private static Type _compensationTrackerType;
        private static MethodInfo _isFrozenMethod;
        private static MethodInfo _peekPendingMethod;
        private static MethodInfo _peekTakenMethod;
        private static MethodInfo _peekAppliedMethod;

        private sealed class Probe
        {
            public int Index;
            public MyCubeGrid Grid;
            public MySlimBlock Cargo;
            public MySlimBlock Refinery;
            public MySlimBlock Assembler;
            public MySlimBlock Reactor;
            public IMyProductionBlock Production;
            public MyAssembler AssemblerBlock;
            public List<IMyInventory> Inventories;
            public Snapshot InitialSnapshot;
            public bool RefinerySeenFrozen;
            public bool AssemblerSeenFrozen;
            public bool WasFrozenLastTick;
            public bool SeenUnfrozenAfterFreeze;
            public bool ChangedWhileFrozen;
            public Snapshot FrozenChangedFrom;
            public Snapshot FrozenChangedTo;
            public bool RefineryPendingSeen;
            public bool AssemblerPendingSeen;
            public ulong RefineryTakenFrames;
            public ulong AssemblerTakenFrames;
            public ulong RefineryAppliedFrames;
            public ulong AssemblerAppliedFrames;
            public bool SeenRefinedIngot;
            public DateTime FrozenAt;
            public double LongestFrozenSeconds;
            public Snapshot FrozenSnapshot;
        }

        private struct Snapshot
        {
            public decimal IronOre;
            public decimal NickelOre;
            public decimal SiliconOre;
            public decimal UraniumIngot;
            public decimal IronIngot;
            public decimal NickelIngot;
            public decimal SiliconIngot;
            public decimal Steel;
            public decimal Motors;
            public decimal Computers;
            public decimal QueueSteel;
            public decimal QueueMotors;
            public decimal QueueComputers;
            public int QueueEntries;
            public int UnexpectedQueueEntries;
            public float AssemblerProgress;

            // Reactor fuel may be consumed fractionally to power the static grid. It is checked in
            // the serialized fixture but is not refinery/assembler production progress.
            public bool ProductionEquals(Snapshot other)
            {
                return IronOre == other.IronOre && NickelOre == other.NickelOre &&
                       SiliconOre == other.SiliconOre &&
                       IronIngot == other.IronIngot && NickelIngot == other.NickelIngot &&
                       SiliconIngot == other.SiliconIngot && Steel == other.Steel &&
                       Motors == other.Motors && Computers == other.Computers &&
                       QueueSteel == other.QueueSteel && QueueMotors == other.QueueMotors &&
                       QueueComputers == other.QueueComputers && QueueEntries == other.QueueEntries &&
                       UnexpectedQueueEntries == other.UnexpectedQueueEntries &&
                       AssemblerProgress.Equals(other.AssemblerProgress);
            }

            public override string ToString()
            {
                return "ore=" + IronOre + "/" + NickelOre + "/" + SiliconOre +
                       ", ingot=" + IronIngot + "/" + NickelIngot + "/" + SiliconIngot +
                       ", component=" + Steel + "/" + Motors + "/" + Computers +
                       ", queue=" + QueueSteel + "/" + QueueMotors + "/" + QueueComputers +
                       ", progress=" + AssemblerProgress.ToString("F6");
            }
        }

        public override string Name { get { return ScenarioName; } }
        public override int TimeoutSeconds { get { return 1380; } }     // a frozen group wakes 600-1200 s after it froze

        public override IEnumerator Run()
        {
            ResolveFreezerApi();
            var probes = new List<Probe>(GridCount);
            var origin = TestRunner.RunOrigin ?? new Vector3D(5000, 0, 0);
            Note("spawning " + GridCount + " independent 5x production grids");

            for (var i = 0; i < GridCount; i++)
            {
                var row = i / 8;
                var column = i % 8;
                var position = origin + new Vector3D(column * 50, 0, row * 50);
                var name = WorldApi.EntityPrefix + "production-freezer-" + i.ToString("D2");
                var ob = WorldApi.LoadGridTemplate(ResourceName, name, position, Vector3.Forward, Vector3.Up);
                ob.IsStatic = true;
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                WorldApi.EnsureDistributor(grid);
                probes.Add(BuildProbe(i, grid));

                // Avoid one giant spawn-frame spike.
                if ((i + 1) % 4 == 0)
                    yield return null;
            }

            yield return WaitForTicks(30);
            foreach (var probe in probes)
                ValidateInitialState(probe);

            Note("all 64 fixtures validated; waiting for freeze -> unfreeze -> compensation");
            var started = DateTime.UtcNow;
            var lastLog = DateTime.MinValue;

            var sampleTick = 0;
            while (true)
            {
                // Sampling every 10 game ticks catches the 120-frame pending window while avoiding
                // hundreds of whole-grid scans on every frame.
                sampleTick++;
                if (sampleTick % 10 != 0)
                {
                    yield return null;
                    continue;
                }

                var frozen = 0;
                var unfrozenAfterFreeze = 0;
                var refineryPending = 0;
                var assemblerPending = 0;
                var refineryApplied = 0;
                var assemblerApplied = 0;
                var complete = 0;

                foreach (var probe in probes)
                {
                    var refineryFrozen = IsFrozen(probe.Refinery.FatBlock.EntityId);
                    var assemblerFrozen = IsFrozen(probe.Assembler.FatBlock.EntityId);
                    Check(refineryFrozen == assemblerFrozen,
                        "grid " + probe.Index + " freezer split-brain: refinery=" + refineryFrozen +
                        ", assembler=" + assemblerFrozen);

                    var current = ReadSnapshot(probe);
                    if (current.IronIngot > 0 || current.NickelIngot > 0 || current.SiliconIngot > 0)
                        probe.SeenRefinedIngot = true;

                    if (refineryFrozen)
                    {
                        frozen++;
                        probe.RefinerySeenFrozen = true;
                        probe.AssemblerSeenFrozen = true;
                        if (!probe.WasFrozenLastTick)
                        {
                            probe.FrozenAt = DateTime.UtcNow;
                            probe.FrozenSnapshot = current;
                        }
                        else if (!current.ProductionEquals(probe.FrozenSnapshot))
                        {
                            probe.ChangedWhileFrozen = true;
                            probe.FrozenChangedFrom = probe.FrozenSnapshot;
                            probe.FrozenChangedTo = current;
                        }
                    }
                    else if (probe.WasFrozenLastTick)
                    {
                        if (probe.FrozenAt != DateTime.MinValue)
                        {
                            probe.LongestFrozenSeconds = Math.Max(probe.LongestFrozenSeconds,
                                (DateTime.UtcNow - probe.FrozenAt).TotalSeconds);
                            probe.FrozenAt = DateTime.MinValue;
                        }
                        probe.SeenUnfrozenAfterFreeze = true;
                    }
                    probe.WasFrozenLastTick = refineryFrozen;

                    if (HasPending(probe.Refinery.FatBlock.EntityId))
                        probe.RefineryPendingSeen = true;
                    if (HasPending(probe.Assembler.FatBlock.EntityId))
                        probe.AssemblerPendingSeen = true;
                    probe.RefineryTakenFrames = GetFrames(_peekTakenMethod,
                        probe.Refinery.FatBlock.EntityId);
                    probe.AssemblerTakenFrames = GetFrames(_peekTakenMethod,
                        probe.Assembler.FatBlock.EntityId);
                    probe.RefineryAppliedFrames = GetFrames(_peekAppliedMethod,
                        probe.Refinery.FatBlock.EntityId);
                    probe.AssemblerAppliedFrames = GetFrames(_peekAppliedMethod,
                        probe.Assembler.FatBlock.EntityId);

                    if (probe.SeenUnfrozenAfterFreeze) unfrozenAfterFreeze++;
                    if (probe.RefineryPendingSeen) refineryPending++;
                    if (probe.AssemblerPendingSeen) assemblerPending++;
                    if (probe.RefineryTakenFrames > 60 &&
                        probe.RefineryAppliedFrames == probe.RefineryTakenFrames) refineryApplied++;
                    if (probe.AssemblerTakenFrames > 60 &&
                        probe.AssemblerAppliedFrames == probe.AssemblerTakenFrames) assemblerApplied++;

                    Check(!probe.ChangedWhileFrozen,
                        "grid " + probe.Index + " changed exact inventory/queue/progress while frozen: " +
                        probe.FrozenChangedFrom + " -> " + probe.FrozenChangedTo);
                    Check(current.Steel <= WantedPerComponent && current.Motors <= WantedPerComponent &&
                          current.Computers <= WantedPerComponent,
                        "grid " + probe.Index + " overproduced: SteelPlate=" + current.Steel +
                        ", Motor=" + current.Motors + ", Computer=" + current.Computers);

                    if (current.Steel == WantedPerComponent && current.Motors == WantedPerComponent &&
                        current.Computers == WantedPerComponent && current.QueueEntries == 0)
                        complete++;
                }

                if (complete == GridCount && refineryApplied == GridCount && assemblerApplied == GridCount)
                {
                    foreach (var probe in probes)
                        ValidateFinalState(probe);
                    Note("stress verified: 64/64 grids froze both production blocks and completed both " +
                         "compensation passes; exact totals SteelPlate=1600, Motor=1600, Computer=1600");
                    yield break;
                }

                if ((DateTime.UtcNow - started).TotalSeconds > 1320)
                    throw new ScenarioFailedException("stress timed out: complete=" + complete + "/" + GridCount +
                        ", frozen=" + frozen + ", woke=" + unfrozenAfterFreeze +
                        ", pending refinery/assembler=" + refineryPending + "/" + assemblerPending +
                        ", applied refinery/assembler=" + refineryApplied + "/" + assemblerApplied);

                if ((DateTime.UtcNow - lastLog).TotalSeconds >= 10)
                {
                    Note("stress progress: frozen=" + frozen + "/64, woke=" + unfrozenAfterFreeze +
                         "/64, pending R/A=" + refineryPending + "/" + assemblerPending +
                         ", applied R/A=" + refineryApplied + "/" + assemblerApplied +
                         ", complete=" + complete + "/64");
                    lastLog = DateTime.UtcNow;
                }

                yield return null;
            }
        }

        private static Probe BuildProbe(int index, MyCubeGrid grid)
        {
            MySlimBlock cargo = null;
            MySlimBlock refinery = null;
            MySlimBlock assembler = null;
            MySlimBlock reactor = null;
            var inventories = new List<IMyInventory>();
            foreach (var slim in grid.GetBlocks())
            {
                var subtype = slim.BlockDefinition.Id.SubtypeName;
                if (subtype == "LargeBlockLargeContainer") cargo = slim;
                else if (subtype == "LargeRefinery") refinery = slim;
                else if (subtype == "LargeAssembler") assembler = slim;
                else if (subtype == "LargeBlockSmallGenerator") reactor = slim;

                var owner = slim.FatBlock as IMyInventoryOwner;
                if (owner == null || !owner.HasInventory) continue;
                for (var inventoryIndex = 0; inventoryIndex < owner.InventoryCount; inventoryIndex++)
                    inventories.Add(owner.GetInventory(inventoryIndex));
            }

            Check(cargo != null && refinery != null && assembler != null && reactor != null,
                "stress fixture " + index + " is missing a production block");
            var production = Require<IMyProductionBlock>(assembler.FatBlock,
                "grid " + index + " assembler has no production interface");
            var assemblerBlock = Require<MyAssembler>(assembler.FatBlock,
                "grid " + index + " assembler has no concrete MyAssembler");
            var probe = new Probe
            {
                Index = index,
                Grid = grid,
                Cargo = cargo,
                Refinery = refinery,
                Assembler = assembler,
                Reactor = reactor,
                Production = production,
                AssemblerBlock = assemblerBlock,
                Inventories = inventories,
            };
            // Capture the serialized contract immediately, before vanilla has a chance to consume
            // fuel, move ore, refine or advance the queue.
            probe.InitialSnapshot = ReadSnapshot(probe);
            return probe;
        }

        private static void ValidateInitialState(Probe probe)
        {
            var cargoInv = Inventory(probe.Cargo, 0);
            Check(cargoInv.IsConnectedTo(Inventory(probe.Refinery, 0)),
                "grid " + probe.Index + " cargo-refinery disconnected after distributor initialization");
            Check(cargoInv.IsConnectedTo(Inventory(probe.Assembler, 0)),
                "grid " + probe.Index + " cargo-assembler disconnected after distributor initialization");
            Check(cargoInv.IsConnectedTo(Inventory(probe.Reactor, 0)),
                "grid " + probe.Index + " cargo-reactor disconnected after distributor initialization");

            var snapshot = probe.InitialSnapshot;
            Check(snapshot.IronOre == InitialIronOre && snapshot.NickelOre == InitialNickelOre &&
                  snapshot.SiliconOre == InitialSiliconOre,
                "grid " + probe.Index + " wrong serialized ore: Fe=" + snapshot.IronOre +
                ", Ni=" + snapshot.NickelOre + ", Si=" + snapshot.SiliconOre);
            Check(snapshot.UraniumIngot == InitialUraniumIngot,
                "grid " + probe.Index + " wrong serialized uranium fuel: " + snapshot.UraniumIngot);
            Check(snapshot.Steel == 0 && snapshot.Motors == 0 && snapshot.Computers == 0,
                "grid " + probe.Index + " starts with target components");
            Check(snapshot.QueueEntries == 3 && snapshot.UnexpectedQueueEntries == 0,
                "grid " + probe.Index + " queue identities are not exactly SteelPlate/MotorComponent/ComputerComponent");
            Check(snapshot.QueueSteel == WantedPerComponent && snapshot.QueueMotors == WantedPerComponent &&
                  snapshot.QueueComputers == WantedPerComponent,
                "grid " + probe.Index + " wrong exact queue amounts: " + snapshot);
        }

        private static void ValidateFinalState(Probe probe)
        {
            var final = ReadSnapshot(probe);
            Check(probe.RefinerySeenFrozen, "grid " + probe.Index + " refinery never entered freezer");
            Check(probe.AssemblerSeenFrozen, "grid " + probe.Index + " assembler never entered freezer");
            Check(probe.SeenUnfrozenAfterFreeze, "grid " + probe.Index + " never woke after freezing");
            Check(probe.RefineryPendingSeen, "grid " + probe.Index + " refinery never exposed pending compensation");
            Check(probe.AssemblerPendingSeen, "grid " + probe.Index + " assembler never exposed pending compensation");
            Check(probe.RefineryTakenFrames > 60 &&
                  probe.RefineryAppliedFrames == probe.RefineryTakenFrames,
                "grid " + probe.Index + " refinery compensation ledger mismatch: taken=" +
                probe.RefineryTakenFrames + ", applied=" + probe.RefineryAppliedFrames);
            Check(probe.AssemblerTakenFrames > 60 &&
                  probe.AssemblerAppliedFrames == probe.AssemblerTakenFrames,
                "grid " + probe.Index + " assembler compensation ledger mismatch: taken=" +
                probe.AssemblerTakenFrames + ", applied=" + probe.AssemblerAppliedFrames);
            Check(probe.LongestFrozenSeconds >= MinimumFrozenSeconds,
                "grid " + probe.Index + " frozen interval too short to prove catch-up: " +
                probe.LongestFrozenSeconds.ToString("F1") + "s");
            Check(!probe.ChangedWhileFrozen, "grid " + probe.Index + " produced while frozen");
            Check(probe.SeenRefinedIngot, "grid " + probe.Index + " never produced a refined ingot");
            Check(final.IronOre < InitialIronOre && final.NickelOre < InitialNickelOre &&
                  final.SiliconOre < InitialSiliconOre,
                "grid " + probe.Index + " did not consume every ore type");
            var steel = final.Steel;
            var motors = final.Motors;
            var computers = final.Computers;
            Check(steel == WantedPerComponent && motors == WantedPerComponent &&
                  computers == WantedPerComponent,
                "grid " + probe.Index + " wrong final components: SteelPlate=" + steel +
                ", Motor=" + motors + ", Computer=" + computers);
            Check(final.QueueEntries == 0 && final.UnexpectedQueueEntries == 0,
                "grid " + probe.Index + " completed output but queue.Count == " + final.QueueEntries);
        }

        private static Snapshot ReadSnapshot(Probe probe)
        {
            var snapshot = new Snapshot();
            foreach (var inventoryInterface in probe.Inventories)
            {
                var inventory = (MyInventoryBase)(object)inventoryInterface;
                foreach (dynamic item in inventory.GetItems())
                {
                    var amount = (decimal)(double)item.Amount;
                    var type = item.Content.TypeId.ToString();
                    var subtype = (string)item.Content.SubtypeName;
                    if (type == "MyObjectBuilder_Ore")
                    {
                        if (subtype == "Iron") snapshot.IronOre += amount;
                        else if (subtype == "Nickel") snapshot.NickelOre += amount;
                        else if (subtype == "Silicon") snapshot.SiliconOre += amount;
                    }
                    else if (type == "MyObjectBuilder_Ingot")
                    {
                        if (subtype == "Uranium") snapshot.UraniumIngot += amount;
                        else if (subtype == "Iron") snapshot.IronIngot += amount;
                        else if (subtype == "Nickel") snapshot.NickelIngot += amount;
                        else if (subtype == "Silicon") snapshot.SiliconIngot += amount;
                    }
                    else if (type == "MyObjectBuilder_Component")
                    {
                        if (subtype == "SteelPlate") snapshot.Steel += amount;
                        else if (subtype == "Motor") snapshot.Motors += amount;
                        else if (subtype == "Computer") snapshot.Computers += amount;
                    }
                }
            }

            var queue = new List<MyProductionItem>();
            probe.Production.GetQueue(queue);
            snapshot.QueueEntries = queue.Count;
            foreach (var item in queue)
            {
                var amount = (decimal)(double)item.Amount;
                var blueprint = item.BlueprintId.SubtypeName;
                if (blueprint == "SteelPlate") snapshot.QueueSteel += amount;
                else if (blueprint == "MotorComponent") snapshot.QueueMotors += amount;
                else if (blueprint == "ComputerComponent") snapshot.QueueComputers += amount;
                else snapshot.UnexpectedQueueEntries++;
            }
            snapshot.AssemblerProgress = probe.AssemblerBlock.CurrentProgress;
            return snapshot;
        }

        private static void ResolveFreezerApi()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                _compensationTrackerType = assembly.GetType(
                    "SentisOptimisationsPlugin.Freezer.CompensationTracker", false);
                if (_compensationTrackerType != null) break;
            }
            Check(_compensationTrackerType != null &&
                  _compensationTrackerType.Assembly.GetName().Name == "SentisOptimisations",
                "SentisOptimisations CompensationTracker is not loaded from the expected assembly");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            _isFrozenMethod = _compensationTrackerType.GetMethod("IsFrozen", flags, null,
                new[] { typeof(long) }, null);
            _peekPendingMethod = _compensationTrackerType.GetMethod("PeekPending", flags, null,
                new[] { typeof(long) }, null);
            _peekTakenMethod = _compensationTrackerType.GetMethod("PeekTakenFrames", flags, null,
                new[] { typeof(long) }, null);
            _peekAppliedMethod = _compensationTrackerType.GetMethod("PeekAppliedFrames", flags, null,
                new[] { typeof(long) }, null);
            Check(_isFrozenMethod != null && _isFrozenMethod.ReturnType == typeof(bool) &&
                  _peekPendingMethod != null && _peekPendingMethod.ReturnType == typeof(uint?) &&
                  _peekTakenMethod != null && _peekTakenMethod.ReturnType == typeof(ulong?) &&
                  _peekAppliedMethod != null && _peekAppliedMethod.ReturnType == typeof(ulong?),
                "SentisOptimisations CompensationTracker diagnostics API has an incompatible signature");
        }

        private static bool IsFrozen(long blockId)
        {
            return (bool)_isFrozenMethod.Invoke(null, new object[] { blockId });
        }

        private static bool HasPending(long blockId)
        {
            var value = _peekPendingMethod.Invoke(null, new object[] { blockId });
            return value != null && Convert.ToUInt32(value) > 0;
        }

        private static ulong GetFrames(MethodInfo method, long blockId)
        {
            var value = method.Invoke(null, new object[] { blockId });
            return value == null ? 0UL : Convert.ToUInt64(value);
        }

        private static IMyInventory Inventory(MySlimBlock slim, int index)
        {
            var owner = slim.FatBlock as IMyInventoryOwner;
            Check(owner != null && owner.HasInventory, slim.BlockDefinition.Id.SubtypeName + " has no inventory");
            return owner.GetInventory(index);
        }

        public override void CleanupLeftovers()
        {
            var entities = new HashSet<VRage.ModAPI.IMyEntity>();
            Sandbox.ModAPI.MyAPIGateway.Entities.GetEntities(entities, e =>
                e is MyCubeGrid && (e.Name ?? "").StartsWith(
                    WorldApi.EntityPrefix + "production-freezer-", StringComparison.OrdinalIgnoreCase));
            foreach (var entity in entities)
            {
                var tracked = false;
                foreach (var own in Tracked)
                    if (own == entity) { tracked = true; break; }
                if (tracked) continue;
                Sandbox.ModAPI.MyAPIGateway.Entities.RemoveEntity(entity);
                entity.Close();
            }
        }
    }
}
