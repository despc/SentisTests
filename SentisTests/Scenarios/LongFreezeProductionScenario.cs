using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A production grid asleep for as long as grids sleep on the server between wake-ups (10-15 minutes), measured
    /// against an identical one that keeps running.
    ///
    /// Two copies of the production stress fixture (large refinery, large assembler, reactor, large container), both
    /// given the same heaps of ore and the same long queue of steel plates, so neither runs out in the window. A fake
    /// player stands at one; the other, a kilometre off, freezes. Its next wake-up is put <see cref="FrozenMinutes"/>
    /// ahead in the freezer's own schedule, and from there it goes the freezer's way: the wake-up, the frozen frames
    /// handed out in portions, the freeze again. Once all of them have been taken and spent, both grids are read at
    /// the same moment: the ore refined and the plates built must be the same within <see cref="TolerancePercent"/>.
    ///
    /// The repeat variant (<see cref="RepeatName"/>, 3 minutes asleep) has both assemblers in repeat with a short queue
    /// (<see cref="RepeatPlates"/> plates): the game puts each item made back at the end of the queue. A thawed assembler
    /// in repeat once held the game thread past Torch's watchdog (SO FreezerPatches, 21.10.2026): here the longest frame
    /// of the compensation must stay under <see cref="LongestFrameMs"/>, both queues keep what they had at the start, and
    /// the slept site builds as many as the running one.
    ///
    /// The repeat disassembly variant (<see cref="RepeatDisassemblyName"/>) has both assemblers taking plates apart in
    /// repeat (<see cref="DisassemblyPlates"/> plates in the assembler to take apart): the slept one takes as many apart as
    /// the running one, each queue is the plates it still holds (the game's own way in repeat disassembly), no long frame.
    /// </summary>
    public sealed class LongFreezeProductionScenario : TestScenario
    {
        public const string ScenarioName = "freeze_long_production";
        public const string RepeatName = "freeze_repeat_production";
        public const string RepeatDisassemblyName = "freeze_repeat_disassembly";
        private const int DisassemblyPlates = 5000;
        private const int RepeatPlates = 10;
        private const double LongestFrameMs = 1000;
        private const string ResourceName = "SentisTests.Resources.ProductionStressGrid.xml";
        private readonly string _name;
        private readonly double FrozenMinutes;
        private readonly bool _repeat, _disassemble;

        public LongFreezeProductionScenario() : this(ScenarioName, 12, false) { }

        public LongFreezeProductionScenario(string name, double frozenMinutes, bool repeat, bool disassemble = false)
        {
            _name = name;
            FrozenMinutes = frozenMinutes;
            _repeat = repeat;
            _disassemble = disassemble;
        }

        private const double ApartM = 1000;
        private const int FreezeDistanceM = 300;
        private const double TolerancePercent = 5;
        private const int OreKg = 60000, SideOreKg = 15000, Plates = 20000;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _config = new ConfigOverride();

        private MethodInfo _isFrozen, _peekPending, _peekTaken, _peekApplied;
        private IDictionary<long, DateTime> _wakeUps;
        private object _wakeUpLock;

        public override string Name => _name;
        public override int TimeoutSeconds => (int)(FrozenMinutes * 60 + 900);

        private sealed class Site
        {
            public string Label;
            public MyCubeGrid Grid;
            public MyCubeBlock Cargo, Refinery, Assembler;

            public Reading Read()
            {
                var r = new Reading();
                foreach (var block in Grid.GetFatBlocks().Where(b => b.HasInventory))
                    for (var i = 0; i < block.InventoryCount; i++)
                        foreach (var item in ((MyInventory)block.GetInventory(i)).GetItems())
                        {
                            var type = item.Content.TypeId.ToString();
                            var subtype = item.Content.SubtypeName;
                            var amount = (double)item.Amount;
                            if (type == "MyObjectBuilder_Ore") r.Ore += amount;
                            else if (type == "MyObjectBuilder_Ingot" && subtype != "Uranium") r.Ingots += amount;
                            else if (type == "MyObjectBuilder_Component" && subtype == "SteelPlate") r.Plates += amount;
                            else if (type == "MyObjectBuilder_Component") r.Other += amount;
                        }
                var assembler = (Sandbox.ModAPI.IMyAssembler)Assembler;
                r.Queued = assembler.IsQueueEmpty ? 0 : assembler.GetQueue().Sum(q => (double)q.Amount);
                return r;
            }
        }

        private sealed class Reading
        {
            public double Ore, Ingots, Plates, Other;
            public double Queued = -1;
            public override string ToString() => $"ore {Ore:F0} kg, ingots {Ingots:F1} kg, plates {Plates:F0}, other components {Other:F0}" +
                                                 (Queued >= 0 ? $", queued {Queued:F0}" : "");
        }

        public override IEnumerator Run()
        {
            ResolveApi();
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            _config.Set("FreezerEnabled", true);
            // short from the start: a group queued for freezing keeps the delay it was queued with
            _config.Set("DelayBeforeFreezeSec", 5);
            _config.Set("FreezeDistanceStatic", FreezeDistanceM);
            _config.Set("FreezeDistanceDynamic", FreezeDistanceM);

            var origin = TestRunner.RunOrigin ?? new Vector3D(5000, 0, 0);
            var running = Spawn("running", origin);
            var sleeping = Spawn("sleeping", origin + new Vector3D(ApartM, 0, 0));
            yield return WaitForTicks(30);
            foreach (var site in new[] { running, sleeping }) Stock(site, _repeat, _disassemble);
            if (_disassemble)
            {
                yield return WaitForTicks(30);
                foreach (var site in new[] { running, sleeping })
                {
                    Check(((MyAssembler)site.Assembler).DisassembleEnabled, site.Label + ": the assembler did not switch to disassembly");
                    Queue(site, _repeat);
                }
                yield return WaitForTicks(10);
            }
            // counted from here, both alike (the far one may be frozen already: a grid whose physics is not
            // stepped freezes at once - its frozen time then starts at its spawn, and is owed from there)
            var start = new[] { running.Read(), sleeping.Read() };
            var startFrame = Sandbox.Game.World.MySession.Static.GameplayFrameCounter;
            Note("start: running " + start[0] + " | sleeping " + start[1]);
            FakeClients.Add(1, Network, _ => (origin + new Vector3D(0, 30, 0), 0, 0), withCharacters: true);

            var settle = WaitForSeconds(20, "both sites start working");
            while (settle.MoveNext()) yield return settle.Current;
            var working = running.Read();
            Check(working.Plates > start[0].Plates || working.Ingots > start[0].Ingots, "the running site does not work: " + working);

            var freezing = Wait(() => Frozen(sleeping), "the far site freezes", 180);
            while (freezing.MoveNext()) yield return freezing.Current;
            Check(!Frozen(running), "the site with the player froze");

            // the next wake-up of the frozen one, FrozenMinutes from now, in the freezer's own schedule
            Monitor.Enter(_wakeUpLock);
            try { _wakeUps[sleeping.Grid.EntityId] = DateTime.Now.AddMinutes(FrozenMinutes); }
            finally { Monitor.Exit(_wakeUpLock); }
            var frozenAt = DateTime.UtcNow;
            Note("the far site froze; it wakes up in " + FrozenMinutes + " minutes");

            var lastNote = DateTime.UtcNow;
            while (Frozen(sleeping))
            {
                if ((DateTime.UtcNow - frozenAt).TotalMinutes > FrozenMinutes + 5)
                    throw new ScenarioFailedException("the far site did not wake up");
                if ((DateTime.UtcNow - lastNote).TotalSeconds >= 60)
                {
                    lastNote = DateTime.UtcNow;
                    Note($"frozen {(DateTime.UtcNow - frozenAt).TotalMinutes:F1} min | running {running.Read()} | sleeping {sleeping.Read()}");
                }
                for (var i = 0; i < 30; i++) yield return null;
            }
            var frozenSeconds = (DateTime.UtcNow - frozenAt).TotalSeconds;
            Note($"woke after {frozenSeconds:F0} s; the frozen frames are handed out");

            // all of it taken and spent: nothing pending, as much spent as taken, on both production blocks
            var woke = DateTime.UtcNow;
            _queueSeen.Clear();
            _lastQueued = -1;
            var longestFrame = 0.0;
            var lastFrame = DateTime.UtcNow;
            bool Settled(MyCubeBlock b) => Pending(b) == 0 && Frames(_peekTaken, b) > 0 && Frames(_peekTaken, b) == Frames(_peekApplied, b);
            while (!(Settled(sleeping.Refinery) && Settled(sleeping.Assembler)))
            {
                if ((DateTime.UtcNow - woke).TotalSeconds > 60)
                    throw new ScenarioFailedException("the compensation did not settle: refinery pending " + Pending(sleeping.Refinery) +
                        ", taken " + Frames(_peekTaken, sleeping.Refinery) + ", spent " + Frames(_peekApplied, sleeping.Refinery) +
                        "; assembler pending " + Pending(sleeping.Assembler) + ", taken " + Frames(_peekTaken, sleeping.Assembler) +
                        ", spent " + Frames(_peekApplied, sleeping.Assembler) + "; frozen again: " + Frozen(sleeping));
                yield return null;
                if (_disassemble) QueueSeen(sleeping);
                longestFrame = Math.Max(longestFrame, (DateTime.UtcNow - lastFrame).TotalMilliseconds);
                lastFrame = DateTime.UtcNow;
            }
            // the last portion's production pass: a few frames after it was spent
            yield return WaitForTicks(10);
            var end = new[] { running.Read(), sleeping.Read() };
            var takenSeconds = Frames(_peekTaken, sleeping.Refinery) / 60.0;

            var oreRunning = start[0].Ore - end[0].Ore;
            var oreSleeping = start[1].Ore - end[1].Ore;
            // taken apart: the plates gone
            var platesRunning = _disassemble ? start[0].Plates - end[0].Plates : end[0].Plates - start[0].Plates;
            var platesSleeping = _disassemble ? start[1].Plates - end[1].Plates : end[1].Plates - start[1].Plates;
            Note($"LONG FREEZE RESULT | frozen {frozenSeconds:F0} s, compensated {takenSeconds:F0} s ({(DateTime.UtcNow - woke).TotalSeconds:F1} s after the wake-up) | " +
                 $"ore refined: running {oreRunning:F0} kg, slept {oreSleeping:F0} kg ({Percent(oreSleeping, oreRunning)}) | " +
                 $"plates built: running {platesRunning:F0}, slept {platesSleeping:F0} ({Percent(platesSleeping, platesRunning)}) | " +
                 $"now running {end[0]} | sleeping {end[1]}");

            Check(takenSeconds >= frozenSeconds - 10, $"less compensated than frozen: {takenSeconds:F0} s of {frozenSeconds:F0} s");
            Check(oreRunning > 1000, "the running site refined too little to compare: " + oreRunning);
            Check(end[0].Ore > 1000 && end[1].Ore > 1000, "a site ran out of ore, so the comparison says nothing: " + end[0] + " | " + end[1]);
            Check(Math.Abs(oreSleeping - oreRunning) <= oreRunning * TolerancePercent / 100,
                $"ore refined differs: {oreSleeping:F0} kg against {oreRunning:F0} kg");
            Check(platesRunning > 10, "the running site built too few plates to compare: " + platesRunning);
            // taking apart, the slept one goes at the recipe's speed; the running one is slower (in repeat the game rebuilds
            // its queue from the output after every item and starts the next one over: SO README, freeze_disassembly): no
            // fewer than the running one, no more than the recipe allows in the game time gone by
            if (_disassemble)
            {
                var blueprint = MyDefinitionManager.Static.GetBlueprintDefinition(MyDefinitionId.Parse("MyObjectBuilder_BlueprintDefinition/SteelPlate"));
                var definition = (MyAssemblerDefinition)((MyAssembler)sleeping.Assembler).BlockDefinition;
                var perItem = blueprint.BaseProductionTimeInSeconds / (Sandbox.Game.World.MySession.Static.AssemblerSpeedMultiplier * (definition.AssemblySpeed + ((MyAssembler)sleeping.Assembler).UpgradeValues["Productivity"]));
                var allowed = (Sandbox.Game.World.MySession.Static.GameplayFrameCounter - startFrame) / 60.0 / perItem;
                Note($"taking apart: the recipe allows {allowed:F0} in the time gone by ({perItem:F3} s a plate)");
                // (the first run after a restart has the running one past the recipe too, both through all they hold: then
                // the running one is the bound)
                Check(platesSleeping >= platesRunning * (1 - TolerancePercent / 100) && platesSleeping <= Math.Max(allowed * 1.02 + 1, platesRunning * (1 + TolerancePercent / 100)),
                    $"plates taken apart: slept {platesSleeping:F0}, running {platesRunning:F0}, the recipe allows {allowed:F0}");
            }
            else
                Check(Math.Abs(platesSleeping - platesRunning) <= platesRunning * TolerancePercent / 100,
                    $"plates built differ: {platesSleeping:F0} against {platesRunning:F0}");
            Note($"the longest frame of the compensation: {longestFrame:F0} ms");
            Check(longestFrame < LongestFrameMs, $"a frame of the compensation took {longestFrame:F0} ms");
            if (_disassemble)
            {
                for (var i = 0; i < 60; i++) { QueueSeen(sleeping); yield return null; }
                Note("the slept assembler's queue after the wake-up, as it changed: " + string.Join(" -> ", _queueSeen));
            }
            if (_disassemble) Note("queues: running " + end[0].Queued + " of " + end[0].Plates + " held, slept " + end[1].Queued + " of " + end[1].Plates + " held");
            // assembling in repeat the queue keeps what it had (the fixture's own queue and the plates added to it); taking
            // apart in repeat the game queues what the output holds, and takes each item apart off the queue: the queue is
            // the plates still held
            if (_repeat)
                for (var i = 0; i < 2; i++)
                {
                    var want = _disassemble ? end[i].Plates : start[i].Queued;
                    Check(Math.Abs(end[i].Queued - want) < 0.01, (i == 0 ? "running" : "sleeping") + ": " + end[i].Queued + " plates queued in repeat, " +
                                                              (_disassemble ? end[i].Plates + " held to take apart" : start[i].Queued + " at the start"));
                }
        }

        private readonly List<string> _queueSeen = new List<string>();
        private double _lastQueued = -1;

        private void QueueSeen(Site site)
        {
            var assembler = (Sandbox.ModAPI.IMyAssembler)site.Assembler;
            var queued = assembler.IsQueueEmpty ? 0 : assembler.GetQueue().Sum(q => (double)q.Amount);
            if (queued == _lastQueued) return;
            _lastQueued = queued;
            var held = (double)((MyAssembler)site.Assembler).OutputInventory.GetItemAmount(MyDefinitionId.Parse("MyObjectBuilder_Component/SteelPlate"));
            _queueSeen.Add("f" + Sandbox.Game.World.MySession.Static.GameplayFrameCounter + " queued " + queued + " (" + (assembler.IsQueueEmpty ? 0 : assembler.GetQueue().Count) + " items) held " + held +
                           " pending " + Pending(site.Assembler));
        }

        private static string Percent(double part, double whole) => whole <= 0 ? "-" : (part / whole * 100).ToString("F1") + "%";

        private Site Spawn(string label, Vector3D at)
        {
            var name = WorldApi.EntityPrefix + "long-freeze-" + label;
            var ob = WorldApi.LoadGridTemplate(ResourceName, name, at, Vector3.Forward, Vector3.Up);
            ob.IsStatic = true;
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            WorldApi.EnsureDistributor(grid);
            var site = new Site { Label = label, Grid = grid };
            foreach (var block in grid.GetFatBlocks())
            {
                var subtype = block.BlockDefinition.Id.SubtypeName;
                if (subtype == "LargeBlockLargeContainer") site.Cargo = block;
                else if (subtype == "LargeRefinery") site.Refinery = block;
                else if (subtype == "LargeAssembler") site.Assembler = block;
            }
            Check(site.Cargo != null && site.Refinery != null && site.Assembler != null, label + ": the fixture lacks a block");
            return site;
        }

        /// <summary>The same heaps and the same queue on both: neither runs out in the window.</summary>
        private static void Stock(Site site, bool repeat, bool disassemble)
        {
            var cargo = (MyInventory)site.Cargo.GetInventory(0);
            void Add(string type, string subtype, double amount) => AddTo(cargo, type, subtype, amount);
            void AddTo(MyInventory inventory, string type, string subtype, double amount)
            {
                var cargo = inventory;
                var ob = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(
                    MyObjectBuilderType.Parse("MyObjectBuilder_" + type), subtype);
                // as much as fits, the same on both sites (the fixture's container is not empty)
                var fits = (double)cargo.ComputeAmountThatFits(ob.GetId());
                var put = Math.Floor(Math.Min(amount, fits * 0.95));
                Check(put > 0 && cargo.AddItems((MyFixedPoint)put, ob), site.Label + ": no room for " + subtype);
            }
            Add("Ore", "Iron", OreKg);
            AddTo((MyInventory)site.Refinery.GetInventory(0), "Ore", "Iron", OreKg);     // the container alone lasts ~9 minutes
            Add("Ingot", "Uranium", 100);
            var assembler = (Sandbox.ModAPI.Ingame.IMyAssembler)site.Assembler;
            if (disassemble)
            {
                // the mode first: switching it empties the queue (the fixture's own one of plates to make with it)
                assembler.Mode = Sandbox.ModAPI.Ingame.MyAssemblerMode.Disassembly;
                // into the assembler's own output: in disassembly it takes apart what it holds (the game drops a queue
                // item it has nothing for); as much as fits, the same on both
                AddTo((MyInventory)((MyAssembler)site.Assembler).OutputInventory, "Component", "SteelPlate", DisassemblyPlates);
            }
            if (!disassemble) Queue(site, repeat);
        }

        /// <summary>The plates queued (and repeat on): for disassembly a few frames after the mode, whose change empties the queue when it comes.</summary>
        private static void Queue(Site site, bool repeat)
        {
            var assembler = (Sandbox.ModAPI.Ingame.IMyAssembler)site.Assembler;
            assembler.AddQueueItem(
                MyDefinitionId.Parse("MyObjectBuilder_BlueprintDefinition/SteelPlate"), (MyFixedPoint)(repeat ? RepeatPlates : Plates));
            if (repeat) assembler.Repeating = true;
        }

        private static bool Frozen(Site site) => RuntimePluginControls.IsGridFrozen(site.Grid.EntityId);

        private uint Pending(MyCubeBlock block)
        {
            var value = _peekPending.Invoke(null, new object[] { block.EntityId });
            return value == null ? 0 : Convert.ToUInt32(value);
        }

        private static ulong Frames(MethodInfo method, MyCubeBlock block)
        {
            var value = method.Invoke(null, new object[] { block.EntityId });
            return value == null ? 0UL : Convert.ToUInt64(value);
        }

        private void ResolveApi()
        {
            Type tracker = null, logic = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                tracker = tracker ?? assembly.GetType("SentisOptimisationsPlugin.Freezer.CompensationTracker", false);
                logic = logic ?? assembly.GetType("SentisOptimisationsPlugin.Freezer.FreezeLogic", false);
            }
            SkipUnless(tracker != null && logic != null, "SentisOptimisations freezer is not loaded");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            _isFrozen = tracker.GetMethod("IsFrozen", flags);
            _peekPending = tracker.GetMethod("PeekPending", flags);
            _peekTaken = tracker.GetMethod("PeekTakenFrames", flags);
            _peekApplied = tracker.GetMethod("PeekAppliedFrames", flags);
            _wakeUps = logic.GetField("WakeUpDatas", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary<long, DateTime>;
            _wakeUpLock = logic.GetField("_wakeUpLock", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            Check(_isFrozen != null && _peekPending != null && _peekTaken != null && _peekApplied != null && _wakeUps != null && _wakeUpLock != null,
                "the freezer's diagnostics API is not what this scenario knows");
        }

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
