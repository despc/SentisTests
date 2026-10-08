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
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// An assembler taking components apart while its grid is frozen: the freezer makes up for the frozen time with
    /// batches of its own, and those must give what the game gives - the ingots of the items taken apart, no more.
    ///
    /// Three copies of the production fixture, each with an assembler in disassembly mode:
    ///  - "running", thruster components, with a fake player beside it: the game's own disassembly, the measure;
    ///  - "sleeping", a kilometre off: the same heap and the same queue, frozen for <see cref="FrozenMinutes"/> - it must
    ///    have taken apart no fewer than the running one (within <see cref="TolerancePercent"/>) and no more than the
    ///    time gone by allows at the recipe's speed. (The running one is slower than the recipe: a component of 1.33 s
    ///    is finished every 2 s on the stand.)
    ///  - "short-1" and "short-2", further off: one computer and two, and a queue of a thousand, put in once they are
    ///    frozen (awake they would be done with them in a second), frozen as long. All
    ///    a site holds is taken apart and gives the ingots of exactly that many. The freezer counted batches by the time gone
    ///    by and by the queue and looked into the inventory for one batch only: on the server an assembler with three
    ///    computers and six queued gave the ingots of six (04.10.2026, SentisWatcher: "took 3 of 6"). Computers, because
    ///    they are quick to take apart: several fit one portion of the frozen time, and that is where it gave too much
    ///    (with three held and a portion of three it gave what is due: hence one and two).
    ///
    /// On every site the books must balance: for each ingot of the recipe, as much gained as the components lost give.
    /// </summary>
    public sealed class FreezeDisassemblyScenario : TestScenario
    {
        public const string ScenarioName = "freeze_disassembly";
        private const string ResourceName = "SentisTests.Resources.ProductionStressGrid.xml";
        private const string SlowBlueprint = "MyObjectBuilder_BlueprintDefinition/ThrustComponent";
        private const string QuickBlueprint = "MyObjectBuilder_BlueprintDefinition/ComputerComponent";
        private const double FrozenMinutes = 2;
        private const double ApartM = 1000;
        private const int FreezeDistanceM = 300;
        private const double TolerancePercent = 5;
        private static readonly int[] ShortHeld = { 1, 2 };
        private const int ShortQueued = 1000;

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _config = new ConfigOverride();

        private MethodInfo _peekPending, _peekTaken, _peekApplied;
        private IDictionary<long, DateTime> _wakeUps;
        private object _wakeUpLock;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(FrozenMinutes * 60 + 600);

        private sealed class Site
        {
            public string Label;
            public int Held;
            public MyCubeGrid Grid;
            public MyAssembler Assembler;
            public Dictionary<MyDefinitionId, double> Start;
            public MyBlueprintDefinitionBase Blueprint;
            public MyDefinitionId Part;
            /// <summary>What one component gives when taken apart, by ingot.</summary>
            public Dictionary<MyDefinitionId, double> Gives;

            /// <summary>Everything the grid holds, by item.</summary>
            public Dictionary<MyDefinitionId, double> Read()
            {
                var held = new Dictionary<MyDefinitionId, double>(MyDefinitionId.Comparer);
                foreach (var block in Grid.GetFatBlocks().Where(b => b.HasInventory))
                    for (var i = 0; i < block.InventoryCount; i++)
                        foreach (var item in ((MyInventory)block.GetInventory(i)).GetItems())
                        {
                            var id = item.Content.GetId();
                            held.TryGetValue(id, out var was);
                            held[id] = was + (double)item.Amount;
                        }
                return held;
            }

            public double Change(Dictionary<MyDefinitionId, double> now, MyDefinitionId id)
            {
                now.TryGetValue(id, out var after);
                Start.TryGetValue(id, out var before);
                return after - before;
            }
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

            var blueprint = MyDefinitionManager.Static.GetBlueprintDefinition(MyDefinitionId.Parse(SlowBlueprint));
            var quick = MyDefinitionManager.Static.GetBlueprintDefinition(MyDefinitionId.Parse(QuickBlueprint));
            Check(blueprint != null && blueprint.Results.Length == 1, "no blueprint of a thruster component");
            Check(quick != null && quick.Results.Length == 1, "no blueprint of a computer");

            var origin = TestRunner.RunOrigin ?? new Vector3D(5000, 0, 0);
            var running = Spawn("running", origin);
            var sleeping = Spawn("sleeping", origin + new Vector3D(ApartM, 0, 0));
            var scarce = ShortHeld.Select((held, i) => Spawn("short-" + held, origin + new Vector3D((2 + i) * ApartM, 0, 0))).ToArray();
            for (var i = 0; i < scarce.Length; i++) scarce[i].Held = ShortHeld[i];
            yield return WaitForTicks(30);

            // a heap that outlasts the run with half as much to spare: what one component takes, from the assembler itself
            var speed = MySession.Static.AssemblerSpeedMultiplier * ((MyAssemblerDefinition)running.Assembler.BlockDefinition).AssemblySpeed;
            var secondsEach = blueprint.BaseProductionTimeInSeconds / Math.Max(speed, 0.001f);
            var heap = (int)Math.Ceiling((FrozenMinutes * 60 + 240) / secondsEach * 1.5);
            Recipe(running, blueprint);
            Recipe(sleeping, blueprint);
            foreach (var site in scarce) Recipe(site, quick);
            Stock(running, heap, heap * 10);
            Stock(sleeping, heap, heap * 10);
            Note($"a thruster component is taken apart in {secondsEach:F2} s and gives " +
                 string.Join(", ", running.Gives.Select(g => g.Key.SubtypeName + " " + g.Value)) + $"; the heap is {heap}; " +
                 $"a computer in {quick.BaseProductionTimeInSeconds / Math.Max(speed, 0.001f):F2} s");
            var sites = new[] { running, sleeping }.Concat(scarce).ToArray();
            var asleep = new[] { sleeping }.Concat(scarce).ToArray();
            foreach (var site in sites) site.Start = site.Read();
            var began = DateTime.UtcNow;
            void Pace(string when) => Note($"{when}, {(DateTime.UtcNow - began).TotalSeconds:F0} s in: taken apart " +
                string.Join(", ", sites.Select(x => $"{x.Label} {-x.Change(x.Read(), x.Part):0.###} ({x.Assembler.CurrentState}, progress {x.Assembler.CurrentProgress:0.##})")));
            FakeClients.Add(1, Network, _ => (origin + new Vector3D(0, 30, 0), 0, 0), withCharacters: true);

            var settle = WaitForSeconds(20, "the running site starts taking components apart");
            while (settle.MoveNext()) yield return settle.Current;
            Pace("working");
            Check(running.Change(running.Read(), running.Part) < 0, "the running site takes nothing apart");

            var freezing = Wait(() => asleep.All(Frozen), "the far sites freeze", 180);
            while (freezing.MoveNext()) yield return freezing.Current;
            Check(!Frozen(running), "the site with the player froze");

            // the short ones get their computers and their queues asleep: awake they would take them apart in a second,
            // and it is the frozen time that must not give more than is held
            foreach (var site in scarce)
            {
                Stock(site, site.Held, ShortQueued);
                site.Start = site.Read();
            }

            // the next wake-up of the frozen ones, FrozenMinutes from now, in the freezer's own schedule
            Monitor.Enter(_wakeUpLock);
            try
            {
                foreach (var site in asleep) _wakeUps[site.Grid.EntityId] = DateTime.Now.AddMinutes(FrozenMinutes);
            }
            finally { Monitor.Exit(_wakeUpLock); }
            var frozenAt = DateTime.UtcNow;
            Pace("frozen");
            Note("the far sites froze; they wake up in " + FrozenMinutes + " minutes");

            while (asleep.Any(Frozen))
            {
                if ((DateTime.UtcNow - frozenAt).TotalMinutes > FrozenMinutes + 5)
                    throw new ScenarioFailedException("the far sites did not wake up");
                for (var i = 0; i < 30; i++) yield return null;
            }
            var frozenSeconds = (DateTime.UtcNow - frozenAt).TotalSeconds;
            Pace("awake");
            Note($"woke after {frozenSeconds:F0} s; the frozen frames are handed out");

            // all of it taken and spent: nothing pending, as much spent as taken
            var woke = DateTime.UtcNow;
            bool Settled(MyCubeBlock b) => Pending(b) == 0 && Frames(_peekTaken, b) > 0 && Frames(_peekTaken, b) == Frames(_peekApplied, b);
            while (!asleep.All(x => Settled(x.Assembler)))
            {
                if ((DateTime.UtcNow - woke).TotalSeconds > 60)
                    throw new ScenarioFailedException("the compensation did not settle: " + string.Join("; ", asleep.Select(s =>
                        $"{s.Label} pending {Pending(s.Assembler)}, taken {Frames(_peekTaken, s.Assembler)}, spent {Frames(_peekApplied, s.Assembler)}, frozen again {Frozen(s)}")));
                yield return null;
            }
            // the last portion's production pass: a few frames after it was spent
            yield return WaitForTicks(10);

            var elapsed = (DateTime.UtcNow - began).TotalSeconds;
            var apart = new Dictionary<Site, double>();
            foreach (var site in sites)
            {
                var now = site.Read();
                var part = site.Part;
                var gives = site.Gives;
                var lost = -site.Change(now, part);
                apart[site] = lost;
                Note($"DISASSEMBLY RESULT | {site.Label}: {lost:0.###} components taken apart, {heldOf(now, part):0.###} left | " +
                     string.Join(", ", gives.Select(g => $"{g.Key.SubtypeName} +{site.Change(now, g.Key):0.######} (for {lost:0.###}: {lost * g.Value:0.######})")));
                // the books: each ingot of the recipe, as much as the components lost give (a millionth lost a component to rounding)
                foreach (var give in gives)
                {
                    var got = site.Change(now, give.Key);
                    var due = lost * give.Value;
                    Check(Math.Abs(got - due) <= Math.Max(0.001, due * 0.0001),
                        $"{site.Label}: {give.Key.SubtypeName} +{got:0.######} for {lost:0.###} components taken apart, {due:0.######} is due");
                }
            }
            double heldOf(Dictionary<MyDefinitionId, double> held, MyDefinitionId id) => held.TryGetValue(id, out var amount) ? amount : 0;

            foreach (var site in scarce)
                Check(apart[site] == site.Held, $"{site.Label} took apart {apart[site]:0.###} of the {site.Held} computers it held");
            Check(apart[running] >= 10, "the running site took too few apart to compare: " + apart[running]);
            Check(heldOf(running.Read(), running.Part) > 0 && heldOf(sleeping.Read(), sleeping.Part) > 0, "a site ran out of components, so the comparison says nothing");
            // no fewer than the one that was awake all the time, no more than the time allows at the recipe's speed
            var most = elapsed / secondsEach + 1;
            Note($"asleep {apart[sleeping]:0.###}, running {apart[running]:0.###}, the recipe's speed allows {most:0.#} in {elapsed:F0} s");
            Check(apart[sleeping] >= apart[running] - Math.Max(3, apart[running] * TolerancePercent / 100),
                $"the frozen site took apart fewer than the running one: {apart[sleeping]:0.###} against {apart[running]:0.###}");
            Check(apart[sleeping] <= most,
                $"the frozen site took apart more than {elapsed:F0} s allow: {apart[sleeping]:0.###}, at most {most:0.#}");
        }

        private Site Spawn(string label, Vector3D at)
        {
            var name = WorldApi.EntityPrefix + "freeze-apart-" + label;
            var ob = WorldApi.LoadGridTemplate(ResourceName, name, at, Vector3.Forward, Vector3.Up);
            ob.IsStatic = true;
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            WorldApi.EnsureDistributor(grid);
            var site = new Site { Label = label, Grid = grid, Assembler = grid.GetFatBlocks().OfType<MyAssembler>().FirstOrDefault() };
            Check(site.Assembler != null, label + ": the fixture has no assembler");
            // (the fixture's refinery has ore to work on: its ingots are not the assembler's)
            foreach (var refinery in grid.GetFatBlocks().OfType<MyRefinery>()) refinery.Enabled = false;
            return site;
        }

        /// <summary>The components to take apart in the assembler itself (nothing comes by conveyor), and the queue.</summary>
        private static void Recipe(Site site, MyBlueprintDefinitionBase blueprint)
        {
            var factor = (MyFixedPoint)(1f / site.Assembler.GetEfficiencyMultiplierForBlueprint(blueprint));
            site.Blueprint = blueprint;
            site.Part = blueprint.Results[0].Id;
            site.Gives = blueprint.Prerequisites.ToDictionary(p => p.Id, p => (double)(p.Amount * factor), MyDefinitionId.Comparer);
        }

        private void Stock(Site site, int held, int queued)
        {
            var part = site.Part;
            var blueprint = site.Blueprint;
            var cargo = site.Grid.GetFatBlocks().FirstOrDefault(b => b.BlockDefinition.Id.SubtypeName == "LargeBlockLargeContainer");
            Check(cargo != null, site.Label + ": the fixture has no container");
            ((MyInventory)cargo.GetInventory(0)).AddItems(100, new MyObjectBuilder_Ingot { SubtypeName = "Uranium" });

            var assembler = (Sandbox.ModAPI.Ingame.IMyAssembler)site.Assembler;
            assembler.UseConveyorSystem = false;
            assembler.ClearQueue();
            assembler.Mode = Sandbox.ModAPI.Ingame.MyAssemblerMode.Disassembly;
            var output = (MyInventory)site.Assembler.OutputInventory;
            var ob = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(part);
            Check((double)output.ComputeAmountThatFits(part) >= held, $"{site.Label}: {held} components do not fit the assembler");
            Check(output.AddItems(held, ob), site.Label + ": the components were not put in");
            assembler.AddQueueItem(blueprint.Id, (MyFixedPoint)queued);
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
            _peekPending = tracker.GetMethod("PeekPending", flags);
            _peekTaken = tracker.GetMethod("PeekTakenFrames", flags);
            _peekApplied = tracker.GetMethod("PeekAppliedFrames", flags);
            _wakeUps = logic.GetField("WakeUpDatas", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary<long, DateTime>;
            _wakeUpLock = logic.GetField("_wakeUpLock", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            Check(_peekPending != null && _peekTaken != null && _peekApplied != null && _wakeUps != null && _wakeUpLock != null,
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
