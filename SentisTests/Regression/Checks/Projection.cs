using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A projector shows a blueprint with a subgrid: a block and a rotor with its head. A welder over the hologram builds
    /// the block and the rotor, and the head comes onto the rotor as a grid of its own. (Blocks on the head's grid are not
    /// built from a projection in the game itself.)
    /// </summary>
    public sealed class SubgridProjectionCheck : RegressionCheck
    {
        public override string Name => Prefix + "projector_subgrid";
        public override int TimeoutSeconds => 240;

        private static readonly string[] Components = { "SteelPlate", "Construction", "LargeTube", "Motor", "Computer", "MetalGrid", "InteriorPlate", "SmallTube" };

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, 10));
            // the blueprint taken off a real rotor with its head (the head's grid made by the game), as a player copies it
            var source = Build("subgrid-source", Site.At(0, 60, 0), true,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("MotorStator/LargeStator", 1, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 1, 0).With(b => Motion.Charged(b)));
            var stator = Block<MyMotorStator>(source);
            yield return AddHead(stator);
            var mainOb = (MyObjectBuilder_CubeGrid)source.GetObjectBuilder(true);
            mainOb.CubeBlocks.RemoveAll(b => b.SubtypeName == "LargeBlockBatteryBlock");
            var headGrid = stator.TopGrid;
            var headOb = (MyObjectBuilder_CubeGrid)headGrid.GetObjectBuilder(true);
            headGrid.Close();
            source.Close();
            yield return null;

            var grid = Build("projector-subgrid", Site.Origin, true,
                new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                new Part("MyObjectBuilder_Projector/LargeProjector", 1, 0, 0).With(b =>
                {
                    var projector = (MyObjectBuilder_ProjectorBase)b;
                    projector.ProjectedGrids = new List<MyObjectBuilder_CubeGrid> { mainOb, headOb };
                    projector.ProjectionOffset = new Vector3I(1, 0, 0);
                    projector.ProjectionsRemaining = 1;
                    projector.Scale = 1;
                }));
            Put(Inventory(Block<MyReactor>(grid)), "Ingot/Uranium", 5);
            var projectorBlock = Block<MyProjectorBase>(grid);
            yield return Until(() => projectorBlock.IsWorking && projectorBlock.ProjectedGrid != null ? 1 : 0, v => v > 0, "the projection up", 30);
            var previews = Previews(projectorBlock);
            Check(previews.Count == 2, "the projector shows " + previews.Count + " grids of the blueprint's 2");
            yield return Until(() => Vector3D.Distance(projectorBlock.ProjectedGrid.PositionComp.WorldAABB.Center, projectorBlock.PositionComp.GetPosition()), v => v < 20,
                "the hologram next to the projector, m", 10);
            Func<List<Vector3I>> cellsNow = () =>
            {
                var shown = projectorBlock.ProjectedGrid;
                return shown == null ? new List<Vector3I>() : shown.CubeBlocks.Select(b => grid.WorldToGridInteger(shown.GridIntegerToWorld(b.Position))).OrderBy(c => c.X).ToList();
            };
            // the hologram's first block right next to the projector, as a player moves it (ProjectorCheck)
            var cells = cellsNow();
            var want = projectorBlock.Position + new Vector3I(1, 0, 0);
            for (var attempt = 0; attempt < 2 && cells.Count > 0 && cells[0] != want; attempt++)
            {
                var step = want - cells[0];
                Request(projectorBlock, "OnOffsetChangedSuccess", projectorBlock.ProjectionOffset + (attempt == 0 ? step : -step), Vector3I.Zero, 1f, false);
                yield return Seconds(2);
                cells = cellsNow();
            }
            Check(cells.Count == 2 && cells[0] == want, "the hologram is not next to the projector: at cells " + string.Join(" ", cells));

            // a welder under the main part (the head's part is above it)
            var middle = (grid.GridIntegerToWorld(cells[0]) + grid.GridIntegerToWorld(cells[1])) / 2;
            var rigs = new List<MyCubeGrid> { ToolRig("subgrid-welder", "ShipWelder/LargeShipWelder", middle, Site.Up) };
            foreach (var rig in rigs)
            {
                var welder = Block<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(rig);
                foreach (var component in Components) Put(Inventory(welder), "Component/" + component, 200);
                Switch(welder, true);
            }
            Func<MyMotorStator> rotor = () => Blocks<MyMotorStator>(grid).FirstOrDefault();
            yield return Until(() => rotor()?.SlimBlock.IsFullIntegrity == true && rotor().TopGrid != null ? 1 : 0, v => v > 0, "the rotor built, with its head on", 120,
                () => "the rotor " + (rotor() == null ? "not built" : F(rotor().SlimBlock.Integrity) + " of " + F(rotor().SlimBlock.MaxIntegrity) + ", head " + (rotor().TopGrid != null) +
                                       ", missing " + Missing(rotor().SlimBlock)) + "; " + string.Join("; ", rigs.Select(r => State(Block<SpaceEngineers.Game.Entities.Blocks.MyShipWelder>(r)))));
            var top = rotor().TopGrid;
            Track(top);
            Check(cells.All(c => grid.GetCubeBlock(c)?.IsFullIntegrity == true), "the main grid's blocks of the blueprint are not all built");
            Check(((Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock)rotor()).IsAttached, "the head is not attached to the rotor");
            Result("the blueprint with a subgrid welded: the block and the rotor on the projector's grid, the head on the rotor (" + top.BlocksCount + " block on its grid)");
        }

        private static string Missing(MySlimBlock block)
        {
            var missing = new Dictionary<string, int>();
            block.GetMissingComponents(missing);
            return missing.Count == 0 ? "nothing" : string.Join(", ", missing.Select(m => m.Key + " " + m.Value));
        }

        private static readonly System.Reflection.FieldInfo Clipboard =
            typeof(MyProjectorBase).GetField("m_clipboard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>The grids the projector shows, the blueprint's first grid first.</summary>
        private static List<MyCubeGrid> Previews(MyProjectorBase projector)
        {
            var clipboard = Clipboard?.GetValue(projector) ?? throw new Core.ScenarioFailedException("MyProjectorBase.m_clipboard is not in this game");
            return ((IEnumerable<MyCubeGrid>)clipboard.GetType().GetProperty("PreviewGrids").GetValue(clipboard)).ToList();
        }
    }
}
