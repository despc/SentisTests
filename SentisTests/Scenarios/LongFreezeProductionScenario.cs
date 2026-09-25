using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
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
    /// </summary>
    public sealed class LongFreezeProductionScenario : TestScenario
    {
        public const string ScenarioName = "freeze_long_production";
        private const string ResourceName = "SentisTests.Resources.ProductionStressGrid.xml";
        private const double FrozenMinutes = 12;
        private const double ApartM = 1000;
        private const int FreezeDistanceM = 300;
        private const double TolerancePercent = 5;
        private const int OreKg = 60000, SideOreKg = 15000, Plates = 20000;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _config = new ConfigOverride();

        private MethodInfo _isFrozen, _peekPending, _peekTaken, _peekApplied;
        private IDictionary<long, DateTime> _wakeUps;
        private object _wakeUpLock;

        public override string Name => ScenarioName;
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
                return r;
            }
        }

        private sealed class Reading
        {
            public double Ore, Ingots, Plates, Other;
            public override string ToString() => $"ore {Ore:F0} kg, ingots {Ingots:F1} kg, plates {Plates:F0}, other components {Other:F0}";
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
            foreach (var site in new[] { running, sleeping }) Stock(site);
            // counted from here, both alike (the far one may be frozen already: a grid whose physics is not
            // stepped freezes at once - its frozen time then starts at its spawn, and is owed from there)
            var start = new[] { running.Read(), sleeping.Read() };
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
            bool Settled(MyCubeBlock b) => Pending(b) == 0 && Frames(_peekTaken, b) > 0 && Frames(_peekTaken, b) == Frames(_peekApplied, b);
            while (!(Settled(sleeping.Refinery) && Settled(sleeping.Assembler)))
            {
                if ((DateTime.UtcNow - woke).TotalSeconds > 60)
                    throw new ScenarioFailedException("the compensation did not settle: refinery pending " + Pending(sleeping.Refinery) +
                        ", taken " + Frames(_peekTaken, sleeping.Refinery) + ", spent " + Frames(_peekApplied, sleeping.Refinery) +
                        "; assembler pending " + Pending(sleeping.Assembler) + ", taken " + Frames(_peekTaken, sleeping.Assembler) +
                        ", spent " + Frames(_peekApplied, sleeping.Assembler) + "; frozen again: " + Frozen(sleeping));
                yield return null;
            }
            // the last portion's production pass: a few frames after it was spent
            yield return WaitForTicks(10);
            var end = new[] { running.Read(), sleeping.Read() };
            var takenSeconds = Frames(_peekTaken, sleeping.Refinery) / 60.0;

            var oreRunning = start[0].Ore - end[0].Ore;
            var oreSleeping = start[1].Ore - end[1].Ore;
            var platesRunning = end[0].Plates - start[0].Plates;
            var platesSleeping = end[1].Plates - start[1].Plates;
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
            Check(Math.Abs(platesSleeping - platesRunning) <= platesRunning * TolerancePercent / 100,
                $"plates built differ: {platesSleeping:F0} against {platesRunning:F0}");
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
        private static void Stock(Site site)
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
            ((Sandbox.ModAPI.Ingame.IMyAssembler)site.Assembler).AddQueueItem(
                MyDefinitionId.Parse("MyObjectBuilder_BlueprintDefinition/SteelPlate"), (MyFixedPoint)Plates);
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
            Check(tracker != null && logic != null, "SentisOptimisations freezer is not loaded");
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
