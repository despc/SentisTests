using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisOptimisations' NestedProjections (25.11.2026: the prod's save had 76 MB of blueprints of projectors inside the
    /// blueprints of projectors): a blueprint whose grid has a projector holding a blueprint (that one holding another) -
    /// <list type="bullet">
    /// <item>in the builder of a projector made in the world (as a save loads it): the projector keeps its blueprint, the
    /// projector inside it has none;</item>
    /// <item>set into a projector (<c>SetNewBlueprint</c>, what a player's loading comes to): the same;</item>
    /// <item><c>!projections nested</c>: a nested blueprint put back into the live projector's blueprint is counted and left,
    /// with <c>apply</c> taken out, the projector's own blueprint kept.</item>
    /// </list>
    /// </summary>
    public sealed class NestedProjectionsCheck : RegressionCheck
    {
        public override string Name => Prefix + "nested_projections";
        public override int TimeoutSeconds => 90;

        private const BindingFlags Field = BindingFlags.Instance | BindingFlags.NonPublic;

        private static MyObjectBuilder_CubeGrid Grid(params MyObjectBuilder_CubeBlock[] blocks) => new MyObjectBuilder_CubeGrid
        {
            GridSizeEnum = MyCubeSize.Large,
            CubeBlocks = new List<MyObjectBuilder_CubeBlock>(new MyObjectBuilder_CubeBlock[]
                { new MyObjectBuilder_CubeBlock { SubtypeName = "LargeBlockArmorBlock", Min = new Vector3I(0, 0, 0) } }.Concat(blocks)),
        };

        private static MyObjectBuilder_Projector ProjectorHolding(params MyObjectBuilder_CubeGrid[] blueprint) => new MyObjectBuilder_Projector
        {
            SubtypeName = "LargeProjector", Min = new Vector3I(1, 0, 0),
            ProjectedGrids = new List<MyObjectBuilder_CubeGrid>(blueprint),
        };

        /// <summary>A blueprint of one grid: a projector holding a grid whose projector holds another.</summary>
        private static MyObjectBuilder_CubeGrid Blueprint() => Grid(ProjectorHolding(Grid(ProjectorHolding(Grid()))));

        private static List<MyObjectBuilder_CubeGrid> Held(MyProjectorBase p) =>
            typeof(MyProjectorBase).GetField("m_originalGridBuilders", Field).GetValue(p) as List<MyObjectBuilder_CubeGrid>
            ?? typeof(MyProjectorBase).GetField("m_savedProjections", Field).GetValue(p) as List<MyObjectBuilder_CubeGrid>;

        /// <summary>The blueprints of the projectors inside what a projector holds.</summary>
        private static int Inside(MyProjectorBase p) =>
            Held(p)?.SelectMany(g => g.CubeBlocks).OfType<MyObjectBuilder_ProjectorBase>().Sum(b => (b.ProjectedGrids?.Count ?? 0) + (b.ProjectedGrid != null ? 1 : 0)) ?? -1;

        private static List<string> Command(string text)
        {
            var answers = new List<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            commands?.HandleCommandFromServer(text, message => answers.Add(message.Message));
            return answers;
        }

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -10));
            // 1. made from its builder, as a save loads it
            var grid = Build("nested-projector", Site.Origin, true,
                new Part("Reactor/LargeBlockSmallGenerator", 0, 0, 0),
                new Part("MyObjectBuilder_Projector/LargeProjector", 1, 0, 0).With(b =>
                {
                    var projector = (MyObjectBuilder_ProjectorBase)b;
                    projector.ProjectedGrids = new List<MyObjectBuilder_CubeGrid> { Blueprint() };
                    projector.Scale = 1;
                }));
            Put(Inventory(grid.GetFatBlocks<MyReactor>().First()), "Ingot/Uranium", 5);
            var p = grid.GetFatBlocks<MyProjectorBase>().First();
            yield return WaitForTicks(5);
            Check(Held(p)?.Count == 1, "the projector's own blueprint: " + (Held(p)?.Count.ToString() ?? "none"));
            Check(Inside(p) == 0, $"loaded: {Inside(p)} blueprints left in the projector inside the projector's blueprint");

            // 2. set into it, as a player's loading comes to
            var set = typeof(MyProjectorBase).GetMethod("SetNewBlueprint", Field, null, new[] { typeof(List<MyObjectBuilder_CubeGrid>) }, null);
            Check(set != null, "MyProjectorBase.SetNewBlueprint not found");
            var blueprint = Blueprint();
            set.Invoke(p, new object[] { new List<MyObjectBuilder_CubeGrid> { blueprint } });
            yield return WaitForTicks(5);
            Check(Held(p)?.Count == 1, "set: the projector's own blueprint: " + (Held(p)?.Count.ToString() ?? "none"));
            Check(Inside(p) == 0, $"set: {Inside(p)} blueprints left in the projector inside the blueprint set");

            // 3. the command, on one put back into the live projector's blueprint
            Held(p)[0].CubeBlocks.OfType<MyObjectBuilder_ProjectorBase>().First().ProjectedGrids = new List<MyObjectBuilder_CubeGrid> { Grid(), Grid() };
            Check(Inside(p) == 2, "the nested blueprints not put back");
            var dry = string.Join(" ", Command("!projections nested"));
            Check(dry.Contains("Будет убрано") && Inside(p) == 2, $"dry run: {dry}; {Inside(p)} left");
            var applied = string.Join(" ", Command("!projections nested apply"));
            Check(applied.Contains("Убрано") && Inside(p) == 0, $"apply: {applied}; {Inside(p)} left");
            Check(Held(p)?.Count == 1, "apply took the projector's own blueprint too");
            Result("loaded and set: the projector inside the blueprint empty, the blueprint kept; the command: dry run left, apply took 2 | " + applied);
        }
    }
}
