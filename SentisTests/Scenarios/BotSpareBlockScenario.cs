using System.Collections;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A SentisAi bot takes down a block its base has one too many of. The bots of the production server had built a
    /// second, a third... a seventh O2/H2 generator beside the first (06.10.2026); they build no more of them now, and
    /// what stands spare is to go.
    ///
    /// On the base of a bot that is at home, a second block of a kind the base has one of - an O2/H2 generator, else a
    /// basic assembler or a basic refinery - is put down on top of the base, apart from its conveyors, as built by the
    /// bot. Within <see cref="WatchSeconds"/> the base has to have one of the kind again.
    /// </summary>
    public sealed class BotSpareBlockScenario : TestScenario
    {
        public const string ScenarioName = "bot_spare_block";
        private const int WatchSeconds = 900;
        private const double HomeM = 300;

        private static readonly string[] Kinds = { "MyObjectBuilder_OxygenGenerator/", "MyObjectBuilder_Assembler/BasicAssembler", "MyObjectBuilder_Refinery/Blast Furnace" };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => WatchSeconds + 420;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            MyPlayer bot = null;
            MySlimBlock first = null;
            var found = Wait(() =>
            {
                foreach (var kind in Kinds)
                foreach (var p in MySession.Static.Players.GetOnlinePlayers().Where(p => p.DisplayName.StartsWith("[BOT] ") && p.Character != null && !p.Character.IsDead))
                {
                    var identity = p.Identity.IdentityId;
                    var at = p.Character.PositionComp.GetPosition();
                    // the base it lives at (the one with its survival kit), the bot beside it, one of the kind on it, working
                    first = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.IsStatic && g.BigOwners.Contains(identity) &&
                            Vector3D.Distance(g.PositionComp.GetPosition(), at) < HomeM &&
                            g.GetFatBlocks().Any(b => b.IsWorking && b.BlockDefinition.Id.TypeId.ToString().EndsWith("SurvivalKit")))
                        .Select(g => g.GetBlocks().Where(b => b.BlockDefinition.Id.ToString() == kind).ToList())
                        // (one of the kind, so that the spare is the scenario's own: one a base already has too many of may
                        // be one the base hangs on, which the bot leaves standing)
                        .Where(same => same.Count == 1 && same[0].FatBlock?.IsFunctional == true).Select(same => same[0]).FirstOrDefault();
                    if (first == null) continue;
                    bot = p;
                    return true;
                }
                return false;
            }, $"a bot within {HomeM} m of its base that has one working O2/H2 generator (or basic assembler, or basic refinery)", 600);
            while (found.MoveNext()) yield return found.Current;
            var name = bot.DisplayName;
            var grid = first.CubeGrid;
            var id = first.BlockDefinition.Id;
            var what = first.BlockDefinition.DisplayNameText;
            var definition = MyDefinitionManager.Static.GetCubeBlockDefinition(id);
            int Count() => grid.GetBlocks().Count(b => b.BlockDefinition.Id == id);

            // the second one: on top of a block of the base, away from the first, nothing in its cells
            var up = Base6Directions.GetIntVector(grid.WorldMatrix.GetClosestDirection(Vector3D.Normalize(grid.PositionComp.GetPosition() -
                (MyGamePruningStructure.GetClosestPlanet(grid.PositionComp.GetPosition())?.PositionComp.GetPosition() ?? Vector3D.Zero))));
            MySlimBlock spare = null;
            // (a base that has more than one already - the bots' own doing before - is watched as it stands)
            var had = Count();
            if (had > 1) Note($"{name}: {grid.DisplayName} has {had} {what} already; watched as it stands");
            else foreach (var under in grid.GetBlocks().Where(b => b != first).OrderByDescending(b => (b.Position - first.Position).RectangularLength()))
            {
                var min = under.Position + up;
                var max = min + definition.Size - Vector3I.One;
                var free = true;
                for (var it = new Vector3I_RangeIterator(ref min, ref max); it.IsValid() && free; it.MoveNext())
                    free = grid.GetCubeBlock(it.Current) == null;
                if (!free) continue;
                var ob = (MyObjectBuilder_CubeBlock)VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen.CreateNewObject(id);
                ob.Min = min;
                ob.BuiltBy = bot.Identity.IdentityId;
                ob.Owner = bot.Identity.IdentityId;
                ob.IntegrityPercent = 1;
                ob.BuildPercent = 1;
                ob.EntityId = 0;
                spare = ((IMyCubeGrid)grid).AddBlock(ob, false) as MySlimBlock;
                if (spare != null) break;
            }
            Check(had > 1 || spare != null, $"no place on {grid.DisplayName} for a second {what}");
            yield return WaitForTicks(30);
            if (spare != null) Note($"{name}: a second {what} put on {grid.DisplayName} at {spare.Position} (the first at {first.Position}); {Count()} of the kind on the base, " +
                 $"{name} {Vector3D.Distance(bot.Character.PositionComp.GetPosition(), spare.WorldPosition):0} m from it");
            Check(Count() >= 2, $"the base has {Count()} of the kind, two were meant");

            var goneAt = -1;
            for (var tick = 0; tick < WatchSeconds * 60 && goneAt < 0; tick++)
            {
                if (tick % 60 == 0)
                {
                    if (Count() <= 1) goneAt = tick / 60;
                    if (tick % (60 * 60) == 0)
                        Note($"{tick / 60} s: {Count()} of the kind on the base, {name} " +
                             $"{(bot.Character == null ? "has no body" : $"{Vector3D.Distance(bot.Character.PositionComp.GetPosition(), grid.PositionComp.GetPosition()):0} m from the base")}");
                }
                yield return null;
            }
            Note($"BOT SPARE BLOCK RESULT | {name}: " + (goneAt >= 0 ? $"one {what} left on {grid.DisplayName} after {goneAt} s" : $"still {Count()} {what} on {grid.DisplayName} after {WatchSeconds} s"));
            Check(goneAt >= 0, $"{name} did not take down the spare {what} in {WatchSeconds} s");
            Check(Count() == 1, $"{Count()} {what} left on the base, one was meant to stay");
        }
    }
}
