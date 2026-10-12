using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.Models;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Construction stage models loaded ahead of time keep their collision. SentisOptimisations loads the stage models of
    /// a block on a background thread (GrinderPatches, MyCubeBlockDefinition.PreloadConstructionModels - called when a block
    /// drops below a stage, and for every block type of a grid when grinding of it starts); a model reads its collision
    /// shapes only on a thread Havok knows, and counts as loaded either way. On the stand (12.10.2026) the piston head's
    /// stage models had come out of that thread without collision: a head is made as a frame, and MyPistonBase.CanPlaceTop
    /// read shape [0] of it and threw - the bots' "Add Top" failed every minute for hours. Here: stage models not loaded
    /// yet (the piston head's, if it is still free of them) are preloaded that way and compared with the same files loaded
    /// on the game thread; then a piston without a head gets one through the terminal's "Add Top" (DoRecreateTop, as the
    /// bots send it).
    /// </summary>
    public sealed class ConstructionModelShapesScenario : TestScenario
    {
        public const string ScenarioName = "construction_model_shapes";
        private const int Definitions = 6;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 120;

        private static bool Untouched(MyCubeBlockDefinition d) =>
            d.BuildProgressModels != null && d.BuildProgressModels.Length > 0 &&
            d.BuildProgressModels.All(m => !string.IsNullOrEmpty(m?.File) && MyModels.GetModel(m.File)?.LoadedData != true);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var all = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyCubeBlockDefinition>().Where(d => d.Public).ToList();
            var head = all.FirstOrDefault(d => d.Id.SubtypeName == "LargePistonTop");
            var picked = all.Where(Untouched).Where(d => d != head).Take(Definitions).ToList();
            var headFresh = head != null && Untouched(head);
            if (headFresh) picked.Insert(0, head);
            Note($"definitions with no stage model loaded yet: {picked.Count} ({string.Join(", ", picked.Select(d => d.Id.SubtypeName))}); the piston head's {(headFresh ? "among them" : "loaded already")}");
            Check(picked.Count > 0, "every construction model is loaded already: restart the server first");

            foreach (var definition in picked) MyCubeBlockDefinition.PreloadConstructionModels(definition);
            yield return Wait(() => picked.All(d => d.BuildProgressModels.All(m => MyModels.GetModel(m.File)?.LoadedData == true)), "the stage models are loaded", 30);

            var broken = new List<string>();
            var compared = 0;
            foreach (var definition in picked)
            foreach (var stage in definition.BuildProgressModels)
            {
                var loaded = MyModels.GetModel(stage.File);
                var reference = new MyModel(stage.File);   // the same file, on the game thread
                reference.LoadData();
                var want = reference.HavokCollisionShapes?.Length ?? 0;
                var got = loaded.HavokCollisionShapes?.Length ?? 0;
                reference.UnloadData();
                compared++;
                if (got != want) broken.Add($"{definition.Id.SubtypeName} {stage.BuildRatioUpperBound:0.00}: {got} shapes, the file has {want}");
            }
            Note($"CONSTRUCTION MODELS | {compared} stage models preloaded, without the collision of their file: {broken.Count}{(broken.Count == 0 ? "" : ": " + string.Join("; ", broken))}");

            // a piston without a head: "Add Top", as a bot sends it (its head is made as a frame, from the stage models above)
            var origin = TestRunner.RunOrigin ?? new Vector3D(5000, 3000, -2000);
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (origin + new Vector3D(0, 15, 0), 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            var owner = WorldApi.TestIdentityId();
            var armor = WorldApi.FindSubtype(MyCubeSize.Large, "blockarmorblock");
            var specs = new List<BlockSpec>();
            for (var x = 0; x < 3; x++)
            for (var z = 0; z < 3; z++)
                specs.Add(new BlockSpec(armor, new Vector3I(x, 0, z)));
            specs.Add(new BlockSpec("LargePistonBase", new Vector3I(1, 1, 1)));
            specs.Add(new BlockSpec(WorldApi.FindSubtype(MyCubeSize.Large, "batteryblock"), new Vector3I(0, 1, 0)));
            var grid = WorldApi.SpawnGrid(WorldApi.GridOb(WorldApi.EntityPrefix + "piston-head", MyCubeSize.Large, true, origin, specs));
            Track(grid);
            yield return WaitForTicks(60);
            var piston = WorldApi.FindFunctional<MyPistonBase>(grid);
            Check(piston != null, "no piston");
            if (piston.TopBlock != null) Note("the piston came with a head; it is taken off first");
            piston.TopGrid?.Close();
            yield return WaitForTicks(30);
            Check(piston.TopBlock == null, "the piston still has a head");

            var recreate = typeof(MyMechanicalConnectionBlockBase).GetMethod("DoRecreateTop", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(recreate != null, "MyMechanicalConnectionBlockBase.DoRecreateTop not found");
            var guard = Integrations.TypeOf("SentisOptimisations", "SentisOptimisationsPlugin.CrashFix.PistonTopPlacement");
            long Refused() => Convert.ToInt64(guard?.GetField("Refused")?.GetValue(null) ?? -1L);
            var refused0 = Refused();
            recreate.Invoke(piston, new object[] { owner, Enum.ToObject(recreate.GetParameters()[1].ParameterType, 0), false });
            yield return WaitForTicks(60);
            var headModel = piston.TopBlock?.ModelCollision;
            Note($"CONSTRUCTION MODELS | Add Top: head {(piston.TopBlock == null ? "NONE" : $"made, built {piston.TopBlock.SlimBlock.BuildLevelRatio:0.00}, collision {headModel?.HavokCollisionShapes?.Length ?? 0} shapes")}; head checks that threw {Refused() - refused0}");

            Check(broken.Count == 0, "a preloaded stage model has no collision");
            Check(Refused() - refused0 == 0, "the head check threw");
            Check(piston.TopBlock != null, "Add Top made no head");
        }

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }
    }
}
