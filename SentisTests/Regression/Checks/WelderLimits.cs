using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using SentisTests.Game;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A ship welder keeps to the world's block limits of the player it welds for, as the game's own does:
    /// <list type="bullet">
    /// <item>a frame of the player's welded on to where it would work: the block's PCU (all but the frame's own) has to fit
    /// in what the player has left (<c>MyShipWelder.Activate</c>: <c>CheckLimitsAndNotify(block.BuiltBy, …)</c>). With
    /// too little PCU left the frame stays short of working; with the PCU given back it is finished;</item>
    /// <item>a projection built by the welder: a block of a type limited by tag (drills) is not built past the limit
    /// (<c>MyShipWelder.IsWithinWorldLimits</c> with the definition's limit type, the tag when the world limits by tags).
    /// With one drill left to the limit, of two projected drills one is built.</item>
    /// </list>
    /// Focus <c>part=1</c> or <c>part=2</c>: that part alone. SentisOptimisations does the welders' work itself (WelderOptimization): it asked the limits for the server's local
    /// player (nobody on a dedicated server) and by the block's pair name, not its tag - its welders built past both.
    /// </summary>
    public sealed class WelderLimitsCheck : RegressionCheck
    {
        public override string Name => Prefix + "welder_limits";
        public override int TimeoutSeconds => 240;

        private int _pcuTaken;

        protected override IEnumerator Check()
        {
            var session = MySession.Static;
            SkipUnless(session.BlockLimitsEnabled != MyBlockLimitsEnabledEnum.NONE && session.TotalPCU != 0, "no PCU limit in this world");
            yield return Arrive(Site.At(0, 10, 12));
            var limits = session.Players.TryGetIdentity(Identity).BlockLimits;
            var report = new List<string>();

            // focus part=1 / part=2: one part alone
            var part = System.Text.RegularExpressions.Regex.Match(Core.TestRunner.Focus ?? "", @"part=(\d)") is var m && m.Success ? int.Parse(m.Groups[1].Value) : 0;
            if (part != 2)
            {
            // 1. a frame welded on with too little PCU left
            const string reactor = "Reactor/LargeBlockSmallGenerator";
            var definition = Definition(reactor);
            var frameGrid = Build("wl-frame", Site.Origin, true, new[] { new Part(reactor, 0, 0, 0).At(0.02f) });
            var frame = frameGrid.GetCubeBlock(Vector3I.Zero);
            Check(frame != null && !frame.IsFullIntegrity && !frame.ComponentStack.IsFunctional, "the reactor's frame came out working");
            var rig = ToolRig("wl-welder", "ShipWelder/LargeShipWelder", Site.Origin - Site.Forward * 1.5, Site.Forward);
            var welder = Block<MyShipWelder>(rig);
            foreach (var component in definition.Components)
                Put(Inventory(welder), "Component/" + component.Definition.Id.SubtypeName, component.Count * 2);
            yield return Seconds(1);
            // the player's PCU left short of the reactor's (the frame's own counted already)
            var need = definition.PCU - Sandbox.Definitions.MyCubeBlockDefinition.PCU_CONSTRUCTION_STAGE_COST;
            Check(need > 1, "the reactor's PCU is " + definition.PCU + ": nothing to hold back");
            AddPcu(limits, need - 1 - limits.PCU);
            var leftBefore = limits.PCU;
            Switch(welder, true);
            yield return Seconds(15);
            var integrityShort = frame.Integrity / frame.MaxIntegrity;
            var functionalShort = frame.ComponentStack.IsFunctional;
            Note($"with {leftBefore} PCU left of the {need} the reactor needs: integrity {F(integrityShort * 100)} %, working {functionalShort}, PCU left {limits.PCU}");
            Check(!functionalShort, $"the welder finished the player's reactor past their PCU: {leftBefore} left, it needs {need}; now {limits.PCU} left");
            // given back: finished
            AddPcu(limits, _pcuTaken);
            yield return Until(() => frame.ComponentStack.IsFunctional ? 1 : 0, v => v > 0, "the reactor working with the PCU given back", 30, () => State(welder));
            report.Add($"a frame short of {need} PCU ({leftBefore} left) welded to {F(integrityShort * 100)} %, not working; with the PCU back it works");
            Switch(welder, false);
            }
            if (part == 1)
            {
                Result(string.Join("; ", report));
                yield break;
            }

            // 2. a projection of drills past the tag's limit
            var drill = Definition("Drill/LargeBlockDrill");
            var tag = drill.GetLimitType();
            var tagLimit = session.GetBlockTypeLimit(tag);
            if (tagLimit < 0 || tagLimit > 200)
            {
                report.Add("no limit of " + tag + " to build to (" + tagLimit + "): the projection's part not run");
                Result(string.Join("; ", report));
                yield break;
            }
            Func<int> drills = () => limits.BlockTypeBuilt.TryGetValue(tag, out var built) ? built.BlocksBuilt : 0;
            // the player's drills up to one short of the limit: small ones, in rows
            var fill = tagLimit - 1 - drills();
            if (fill > 0)
            {
                var small = Definition("Drill/SmallBlockDrill").Size;
                var parts = Enumerable.Range(0, fill).Select(i => new Part("Drill/SmallBlockDrill", i % 10 * small.X, 0, i / 10 * small.Z)).ToList();
                Build("wl-drills", Site.Origin + Site.Right * 40, true, parts, MyCubeSize.Small);
                yield return Seconds(1);
            }
            Check(drills() == tagLimit - 1, "the player's " + tag + " are " + drills() + ", not one short of the limit " + tagLimit);

            var size = drill.Size;
            var blueprint = new MyObjectBuilder_CubeGrid
            {
                GridSizeEnum = MyCubeSize.Large,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>
                {
                    new Sandbox.Common.ObjectBuilders.MyObjectBuilder_Drill { SubtypeName = drill.Id.SubtypeName, Min = new Vector3I(0, 0, 0) },
                    new Sandbox.Common.ObjectBuilders.MyObjectBuilder_Drill { SubtypeName = drill.Id.SubtypeName, Min = new Vector3I(size.X, 0, 0) },
                },
            };
            var station = Build("wl-projector", Site.Origin + Site.Up * 20, true, new[] {
                new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                new Part("MyObjectBuilder_Projector/LargeProjector", 1, 0, 0).With(b =>
                {
                    var projector = (MyObjectBuilder_ProjectorBase)b;
                    projector.ProjectedGrids = new List<MyObjectBuilder_CubeGrid> { blueprint };
                    projector.ProjectionOffset = new Vector3I(1, 0, 0);
                    projector.KeepProjection = true;
                    projector.ProjectionsRemaining = 0;
                    projector.Scale = 1;
                }) });
            Put(Inventory(Block<MyReactor>(station)), "Ingot/Uranium", 5);
            var shown = Block<MyProjectorBase>(station);
            yield return Until(() => shown.IsWorking && shown.ProjectedGrid != null ? 1 : 0, v => v > 0, "the projection up", 30);
            yield return Until(() => shown.ProjectedGrid != null ? Vector3D.Distance(shown.ProjectedGrid.PositionComp.WorldAABB.Center, shown.PositionComp.GetPosition()) : 1e9,
                v => v < 30, "the hologram by the projector, m", 10);
            Func<int> buildableNow = () => shown.ProjectedGrid?.CubeBlocks.Count(b => shown.CanBuild(b, true) == Sandbox.ModAPI.BuildCheckResult.OK) ?? 0;
            // moved as a player moves it in the terminal until a drill of it can be built: by the projector, not in its grid
            var offsets = new List<Vector3I>();
            for (var d = 1; d <= 4; d++)
                foreach (var axis in new[] { Vector3I.Up, Vector3I.Down, Vector3I.Left, Vector3I.Right, Vector3I.Forward, Vector3I.Backward })
                    offsets.Add(axis * d);
            foreach (var offset in offsets)
            {
                if (buildableNow() > 0) break;
                Request(shown, "OnOffsetChangedSuccess", offset, Vector3I.Zero, 1f, false);
                yield return Seconds(1);
            }
            var hologram = shown.ProjectedGrid;
            var buildable = buildableNow();
            Note("projected drills: " + hologram.CubeBlocks.Count + ", buildable now " + buildable + " (" +
                 string.Join(", ", hologram.CubeBlocks.Select(b => b.Position + " " + shown.CanBuild(b, true))) + ")");
            Check(buildable > 0, "nothing of the projection can be built: " + string.Join(", ", hologram.CubeBlocks.Select(b => shown.CanBuild(b, true).ToString())));

            // a welder over each projected drill, with what a drill is made of
            var welders = new List<MyShipWelder>();
            var index = 0;
            foreach (var block in hologram.CubeBlocks.ToList())
            {
                // the sphere over the drill's top, the tool clear of its cells (where it stands the game places nothing)
                var top = Vector3D.Transform(new BoundingBoxD(block.Min * hologram.GridSize - hologram.GridSize / 2, block.Max * hologram.GridSize + hologram.GridSize / 2).Center, hologram.WorldMatrix);
                var half = (block.Max - block.Min + Vector3I.One) * hologram.GridSize / 2;
                var wm = hologram.WorldMatrix;
                var reach = Math.Abs(Vector3D.Dot(wm.Right, Site.Up)) * half.X + Math.Abs(Vector3D.Dot(wm.Up, Site.Up)) * half.Y + Math.Abs(Vector3D.Dot(wm.Backward, Site.Up)) * half.Z;
                var at = top + Site.Up * (reach + 1.5);
                var tool = Block<MyShipWelder>(ToolRig("wl-proj-welder-" + index++, "ShipWelder/LargeShipWelder", at, -Site.Up));
                foreach (var component in drill.Components)
                    Put(Inventory(tool), "Component/" + component.Definition.Id.SubtypeName, component.Count * 2);
                welders.Add(tool);
            }
            Func<int> builtDrills = () => station.GetFatBlocks().Count(b => b.BlockDefinition.Id == drill.Id);
            foreach (var tool in welders) Switch(tool, true);
            yield return Until(() => builtDrills(), v => v >= 1, "a projected drill built", 30, () => WelderCounts() + "; " + string.Join("; ", welders.Select(w => State(w))));
            // the second one has had its chance: the first one is there to build it against
            yield return Seconds(10);
            foreach (var tool in welders) Switch(tool, false);
            var built = builtDrills();
            Note(WelderCounts());
            Note($"projected drills built {built} of 2; the player's {tag} {drills()} of {tagLimit}");
            Check(built == 1 && drills() <= tagLimit,
                $"the welders built {built} of the 2 projected drills with one left to the limit of {tag}: the player's {tag} now {drills()} of {tagLimit}");
            report.Add($"with one {tag} left to the limit {tagLimit}, 1 of 2 projected drills built ({drills()} of {tagLimit})");
            Result(string.Join("; ", report));
        }

        /// <summary>SentisOptimisations' counts of its welders' work (WelderDiagnostics), if it is there.</summary>
        private static string WelderCounts()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Optimizer.Optimizations.WelderDiagnostics", false)).FirstOrDefault(t => t != null);
            return type == null ? "no SentisOptimisations welders" : "SentisOptimisations' welders: " + type.GetMethod("Snapshot")?.Invoke(null, null);
        }

        /// <summary>PCU added to (or taken from) the player's room, as the game passes PCU between players; given back at the end.</summary>
        private void AddPcu(MyBlockLimits limits, int amount)
        {
            if (amount == 0) return;
            var add = typeof(MyBlockLimits).GetMethod("AddPCU", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                      ?? throw new Core.ScenarioFailedException("MyBlockLimits.AddPCU is not in this game");
            add.Invoke(limits, new object[] { amount });
            _pcuTaken -= amount;
        }

        public override void Cleanup()
        {
            try
            {
                if (_pcuTaken != 0 && Me >= 0) AddPcu(MySession.Static.Players.TryGetIdentity(Identity).BlockLimits, _pcuTaken);
            }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
