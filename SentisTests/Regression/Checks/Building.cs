using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;
using BuildCheckResult = Sandbox.ModAPI.BuildCheckResult;

namespace SentisTests.Regression.Checks
{
    internal static class Placing
    {
        public static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static MyCubeGrid.MyBlockVisuals Visuals => new MyCubeGrid.MyBlockVisuals(new Vector3(0, -1, 0).PackHSVToUint(), MyStringHash.NullOrEmpty);
    }

    /// <summary>
    /// A player puts a block on a grid from the build menu (<c>MyCubeGrid.BuildBlocksRequest</c> from the client), in
    /// survival: the block appears as a frame, and its first component leaves the player's inventory.
    /// </summary>
    public sealed class PlaceBlockCheck : RegressionCheck
    {
        public override string Name => Prefix + "place_block";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, 0));
            var floor = Build("place-floor", Site.Origin, true,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            var definition = Definition("CubeBlock/LargeBlockArmorBlock");
            var plate = definition.Components[0].Definition.Id;
            var inventory = Inventory(Body);
            Put(inventory, "Component/SteelPlate", 30);
            var before = (double)inventory.GetItemAmount(plate);
            StandAt(floor.GridIntegerToWorld(new Vector3I(0, 2, 2)), floor.GridIntegerToWorld(new Vector3I(0, 1, 0)));

            var cell = new Vector3I(0, 1, 0);
            var location = new MyCubeGrid.MyBlockLocation(definition.Id, cell, cell, cell, Quaternion.Identity, 0, Identity);
            var request = typeof(MyCubeGrid).GetMethod("BuildBlocksRequest", Placing.Any);
            Check(request != null, "MyCubeGrid.BuildBlocksRequest is not in this game");
            AsMe(() => request.Invoke(floor, new object[] { Placing.Visuals, new HashSet<MyCubeGrid.MyBlockLocation> { location }, Body.EntityId, false, Identity }));
            yield return Until(() => floor.GetCubeBlock(cell) != null ? 1 : 0, v => v > 0, "the block placed", 10);
            var block = floor.GetCubeBlock(cell);
            var spent = before - (double)inventory.GetItemAmount(plate);
            Check(spent >= 1, "the block was placed and the player's steel plates are all there (" + before + ")");
            Check(!block.IsFullIntegrity, "a block placed in survival came out finished");
            Check(block.BuiltBy == Identity, "the block is built by " + block.BuiltBy + ", not by the player " + Identity);
            Result("block placed as a frame (" + (block.BuildLevelRatio * 100).ToString("F0") + " %), " + spent + " plates taken from the player");
        }
    }

    /// <summary>
    /// A player puts a block in empty space, making a new grid (<c>MyCubeBuilder.RequestGridSpawn</c> from the client):
    /// the grid appears where it was put, the player's, its block a frame.
    /// </summary>
    public sealed class PlaceGridCheck : RegressionCheck
    {
        public override string Name => Prefix + "place_grid";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 0, -6));
            var definition = Definition("CubeBlock/LargeBlockArmorBlock");
            var inventory = Inventory(Body);
            foreach (var component in definition.Components)
                inventory.AddItems(component.Count, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(component.Definition.Id));

            var builder = typeof(MyCubeBuilder);
            var request = builder.GetMethod("RequestGridSpawn", Placing.Any);
            var dataType = builder.GetNestedType("GridSpawnRequestData", BindingFlags.NonPublic);
            var authorType = builder.GetNestedType("Author", BindingFlags.NonPublic);
            var buildDataType = builder.GetNestedType("BuildData", BindingFlags.NonPublic);
            Check(request != null && dataType != null && authorType != null && buildDataType != null, "MyCubeBuilder.RequestGridSpawn and its data are not in this game");
            var author = Activator.CreateInstance(authorType, Body.EntityId, Identity);
            var position = Activator.CreateInstance(buildDataType);
            var spot = Site.Origin;
            buildDataType.GetField("Position").SetValue(position, spot);
            buildDataType.GetField("Forward").SetValue(position, (Vector3)Site.Forward);
            buildDataType.GetField("Up").SetValue(position, (Vector3)Site.Up);
            buildDataType.GetField("AbsolutePosition").SetValue(position, true);
            var data = Activator.CreateInstance(dataType, author, (DefinitionIdBlit)definition.Id, position, false, false, Placing.Visuals);
            var before = new HashSet<long>(MyEntities.GetEntities().OfType<MyCubeGrid>().Select(g => g.EntityId));
            AsMe(() => request.Invoke(null, new[] { data }));

            MyCubeGrid made = null;
            yield return Until(() =>
            {
                made = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => !before.Contains(g.EntityId) && !g.MarkedForClose &&
                                                                                         Vector3D.Distance(g.PositionComp.WorldAABB.Center, spot) < 5);
                return made != null ? 1 : 0;
            }, v => v > 0, "the new grid", 10);
            Track(made);
            var block = made.CubeBlocks.First();
            Check(block.BlockDefinition.Id == definition.Id, "the new grid's block is " + block.BlockDefinition.Id);
            Check(block.BuiltBy == Identity, "the new grid's block is built by " + block.BuiltBy + ", not the player " + Identity);
            Check(!block.IsFullIntegrity, "a block placed in survival came out finished");
            Result("new grid " + made.EntityId + " of one frame, " + (made.IsStatic ? "static" : "dynamic") + ", " +
                   F(Vector3D.Distance(made.PositionComp.WorldAABB.Center, spot)) + " m from where it was put");
        }
    }

    /// <summary>
    /// A ship welder, switched on in the terminal and powered, welds a frame in its reach to the end with the plates in
    /// its inventory, as a player's welding ship does.
    /// </summary>
    public sealed class ShipWeldCheck : RegressionCheck
    {
        public override string Name => Prefix + "ship_weld";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, 8));
            var target = Build("weld-target", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0).At(0.05f));
            var block = target.GetCubeBlock(Vector3I.Zero);
            Check(!block.IsFullIntegrity, "the frame came out finished");
            var rig = ToolRig("welder", "ShipWelder/LargeShipWelder", Site.Origin - Site.Forward * 1.5, Site.Forward);
            var welder = Block<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(rig);
            Put(Inventory(welder), "Component/SteelPlate", 60);
            var start = block.Integrity / block.MaxIntegrity;
            var plates = Amount(Inventory(welder), "Component/SteelPlate");
            Switch(welder, true);

            yield return Until(() => block.Integrity / block.MaxIntegrity, v => v >= 0.999, "the frame's integrity", 90, () => State(welder));
            var spent = plates - Amount(Inventory(welder), "Component/SteelPlate");
            Check(spent > 0, "the frame was welded and the welder's plates are all there");
            Result("frame welded " + (start * 100).ToString("F0") + " -> 100 %, " + spent + " plates from the welder");
        }
    }

    /// <summary>
    /// A ship grinder, switched on and powered, grinds a block in its reach away and gets back every component it was
    /// built of.
    /// </summary>
    public sealed class ShipGrindCheck : RegressionCheck
    {
        public override string Name => Prefix + "ship_grind";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, 8));
            var target = Build("grind-target", Site.Origin, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            var rig = ToolRig("grinder", "ShipGrinder/LargeShipGrinder", Site.Origin - Site.Forward * 1.0, Site.Forward);
            var grinder = Block<MyShipGrinder>(rig);
            var definition = Definition("CubeBlock/LargeBlockArmorBlock");
            var plates = definition.Components.Where(c => c.Definition.Id.SubtypeName == "SteelPlate").Sum(c => c.Count);
            Switch(grinder, true);

            yield return Until(() => target.MarkedForClose || target.GetCubeBlock(Vector3I.Zero) == null ? 1 : 0, v => v > 0, "the block ground away", 90,
                () => State(grinder) + "; block integrity " + F(target.GetCubeBlock(Vector3I.Zero)?.Integrity ?? 0));
            yield return Seconds(1);
            var got = Amount(Inventory(grinder), "Component/SteelPlate");
            Check(Math.Abs(got - plates) < 0.01, "the grinder got " + got + " steel plates of the block's " + plates);
            Result("block ground away, " + got + " of " + plates + " plates back in the grinder");
        }
    }

    /// <summary>
    /// A projector shows a blueprint of two blocks next to itself; a welder over the hologram builds them: they become
    /// blocks of the projector's grid, finished.
    /// </summary>
    public sealed class ProjectorCheck : RegressionCheck
    {
        public override string Name => Prefix + "projector";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, 10));
            var blueprint = new MyObjectBuilder_CubeGrid
            {
                GridSizeEnum = MyCubeSize.Large,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>
                {
                    new MyObjectBuilder_CubeBlock { SubtypeName = "LargeBlockArmorBlock", Min = new Vector3I(0, 0, 0) },
                    new MyObjectBuilder_CubeBlock { SubtypeName = "LargeBlockArmorBlock", Min = new Vector3I(1, 0, 0) },
                },
            };
            var grid = Build("projector", Site.Origin, true,
                new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                new Part("MyObjectBuilder_Projector/LargeProjector", 1, 0, 0).With(b =>
                {
                    var projector = (MyObjectBuilder_ProjectorBase)b;
                    projector.ProjectedGrids = new List<MyObjectBuilder_CubeGrid> { blueprint };
                    projector.ProjectionOffset = new Vector3I(1, 0, 0);
                    projector.ProjectionsRemaining = 1;
                    projector.Scale = 1;
                }));
            Put(Inventory(Block<MyReactor>(grid)), "Ingot/Uranium", 5);
            var projectorBlock = Block<MyProjectorBase>(grid);
            yield return Until(() => projectorBlock.IsWorking && projectorBlock.ProjectedGrid != null ? 1 : 0, v => v > 0, "the projection up", 30);
            // the hologram where the projector put it (a moment after it comes up it is still at the world's middle)
            Func<List<Vector3I>> cellsNow = () =>
            {
                var shown = projectorBlock.ProjectedGrid;
                return shown == null ? new List<Vector3I>() : shown.CubeBlocks.Select(b => grid.WorldToGridInteger(shown.GridIntegerToWorld(b.Position))).OrderBy(c => c.X).ToList();
            };
            yield return Until(() => projectorBlock.ProjectedGrid != null ? Vector3D.Distance(projectorBlock.ProjectedGrid.PositionComp.WorldAABB.Center, projectorBlock.PositionComp.GetPosition()) : 1e9,
                v => v < 20, "the hologram next to the projector, m", 10);
            var cells = cellsNow();
            // moved the way a player moves it in the terminal, its first block right next to the projector; the offset is
            // along the projector's own axes, so a move the wrong way is turned round
            var want = projectorBlock.Position + new Vector3I(1, 0, 0);
            for (var attempt = 0; attempt < 2 && cells.Count > 0 && cells[0] != want; attempt++)
            {
                var step = want - cells[0];
                var next = projectorBlock.ProjectionOffset + (attempt == 0 ? step : -step);
                Note("the hologram at cells " + string.Join(" ", cells) + "; offset " + projectorBlock.ProjectionOffset + " -> " + next);
                Request(projectorBlock, "OnOffsetChangedSuccess", next, Vector3I.Zero, 1f, false);
                yield return Seconds(2);
                cells = cellsNow();
            }
            var hologram = projectorBlock.ProjectedGrid;
            var buildable = hologram.CubeBlocks.Count(b => projectorBlock.CanBuild(b, true) == BuildCheckResult.OK);
            Note("the hologram is at cells " + string.Join(" ", cells) + " of the projector's grid, " + buildable + " of its blocks buildable now");
            Check(buildable > 0, "nothing of the projection can be built");

            // the welder over the middle of the hologram, facing down at it
            var middle = (grid.GridIntegerToWorld(cells[0]) + grid.GridIntegerToWorld(cells[cells.Count - 1])) / 2;
            var rig = ToolRig("projector-welder", "ShipWelder/LargeShipWelder", middle + Site.Up * 1.5, -Site.Up);
            var welder = Block<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(rig);
            Put(Inventory(welder), "Component/SteelPlate", 80);
            Switch(welder, true);
            yield return Until(() => cells.Count(c => grid.GetCubeBlock(c)?.IsFullIntegrity == true), v => v >= cells.Count,
                "projected blocks built and finished", 120, () => State(welder));
            Result("both projected blocks welded into the projector's grid (" + grid.BlocksCount + " blocks)");
        }
    }
}
