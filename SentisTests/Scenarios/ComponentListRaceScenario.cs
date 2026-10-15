using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using VRage.Game;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// One <c>MyComponentList</c> filled on one thread and cleared on another, as a projector's clipboard fills its
    /// <c>m_buildComponents</c> on a worker (<c>ChangeClipboardPreview</c> from <c>Activate</c>) while the game thread
    /// removes the projection (<c>Deactivate</c>). From the server (14.10.2026, welding large ships): a worker spent a
    /// whole 13 s profile in one <c>Dictionary.TryGetValue</c> under <c>AddMaterial</c> - a dictionary whose chain of
    /// entries the race left in a loop. Two background threads for <see cref="RunSeconds"/>: one adds every component
    /// kind, again and again, the other clears. A call that does not come back in <see cref="StallSeconds"/> is a
    /// loop; such a thread cannot be stopped and spins until the server restarts. Passes when neither thread stalls
    /// nor throws (SO ComponentListLock).
    /// </summary>
    public sealed class ComponentListRaceScenario : TestScenario
    {
        private const int BlueprintBlocks = 100000;

        public const string ScenarioName = "component_list_race";
        private const int RunSeconds = 10;
        private const int StallSeconds = 2;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 60;

        // (what one ChangeClipboardPreview spends on the count when nothing races it: a blueprint of BlueprintBlocks blocks, every
        // large block kind in turn, all built, counted on the game thread as the worker would)
        private static string BlueprintTiming()
        {
            var kinds = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyCubeBlockDefinition>()
                .Where(d => d.Public && d.CubeSize == MyCubeSize.Large && d.Components != null && d.Components.Length > 0).ToArray();
            var grid = new MyObjectBuilder_CubeGrid { CubeBlocks = new System.Collections.Generic.List<MyObjectBuilder_CubeBlock>(BlueprintBlocks) };
            long adds = 0;
            for (var i = 0; i < BlueprintBlocks; i++)
            {
                var kind = kinds[i % kinds.Length];
                grid.CubeBlocks.Add(new MyObjectBuilder_CubeBlock { SubtypeName = kind.Id.SubtypeName });
                adds += kind.Components.Length;
            }
            // (the builder's type is the block's: the plain one stands for every kind; the lookup takes the kind's own type)
            for (var i = 0; i < BlueprintBlocks; i++)
                grid.CubeBlocks[i] = (MyObjectBuilder_CubeBlock)VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen.CreateNewObject(kinds[i % kinds.Length].Id) ?? grid.CubeBlocks[i];
            var list = new MyComponentList();
            var grids = new System.Collections.Generic.List<MyObjectBuilder_CubeGrid> { grid };
            MyGridClipboard.CalculateItemRequirements(grids, list);
            var watch = Stopwatch.StartNew();
            MyGridClipboard.CalculateItemRequirements(grids, list);
            watch.Stop();
            return $"RACE | without a race: a blueprint of {BlueprintBlocks} blocks ({kinds.Length} kinds, about {adds} AddMaterial) counted in {watch.Elapsed.TotalMilliseconds:0.0} ms, " +
                   $"{watch.Elapsed.TotalMilliseconds * 1000000.0 / Math.Max(1, adds):0} ns an AddMaterial";
        }

        public override IEnumerator Run()
        {
            var ids = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyComponentDefinition>().Select(d => d.Id).ToArray();
            Check(ids.Length > 0, "no component definitions");
            Note(BlueprintTiming());
            var list = new MyComponentList();
            var stop = false;
            long adds = 0, clears = 0;
            Exception thrown = null;
            var adder = new Thread(() =>
            {
                try
                {
                    var i = 0;
                    while (!Volatile.Read(ref stop))
                    {
                        list.AddMaterial(ids[i++ % ids.Length], 3, 1);
                        Interlocked.Increment(ref adds);
                    }
                }
                catch (Exception e) { thrown = thrown ?? e; }
            }) { IsBackground = true, Name = "component_list_race add" };
            var clearer = new Thread(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        list.Clear();
                        Interlocked.Increment(ref clears);
                    }
                }
                catch (Exception e) { thrown = thrown ?? e; }
            }) { IsBackground = true, Name = "component_list_race clear" };
            adder.Start();
            clearer.Start();

            var watch = Stopwatch.StartNew();
            long lastAdds = -1, lastClears = -1;
            var addsMovedAt = 0.0;
            var clearsMovedAt = 0.0;
            string stalled = null;
            while (watch.Elapsed.TotalSeconds < RunSeconds && thrown == null && stalled == null)
            {
                yield return null;
                var now = watch.Elapsed.TotalSeconds;
                var a = Interlocked.Read(ref adds);
                var c = Interlocked.Read(ref clears);
                if (a != lastAdds) { lastAdds = a; addsMovedAt = now; }
                if (c != lastClears) { lastClears = c; clearsMovedAt = now; }
                if (now - addsMovedAt > StallSeconds) stalled = $"AddMaterial did not come back in {StallSeconds} s";
                else if (now - clearsMovedAt > StallSeconds) stalled = $"Clear did not come back in {StallSeconds} s";
            }
            Volatile.Write(ref stop, true);
            Note($"RACE | {watch.Elapsed.TotalSeconds:0.0} s: {Interlocked.Read(ref adds)} adds, {Interlocked.Read(ref clears)} clears; " +
                 (stalled ?? (thrown != null ? $"{thrown.GetType().Name}: {thrown.Message}" : "both threads kept going")));
            Check(stalled == null, stalled + " (the dictionary's entries in a loop: that thread spins until the server restarts)");
            Check(thrown == null, $"{thrown?.GetType().Name}: {thrown?.Message}");
        }
    }
}
