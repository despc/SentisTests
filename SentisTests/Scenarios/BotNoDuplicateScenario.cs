using System.Collections;
using System.Linq;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A SentisAi bot whose base has a block of the conveyors that does not work (begun, or broken) finishes that one
    /// and builds no other. On the production server a bot's base had seven O2/H2 generators, three assemblers and two
    /// refineries (06.10.2026): a block was "wanted" while none of its kind was on the conveyors the bot counted as the
    /// base's, one standing unfinished was not looked at, and another was saved up for and placed.
    ///
    /// The scenario takes a block of that kind a bot built on its base - an O2/H2 generator, else a basic assembler or
    /// a basic refinery - grinds it down until it no longer works (its components into the base), and watches for
    /// <see cref="WatchSeconds"/>: no other block of the kind may appear on the base, and the one ground down has to
    /// work again.
    /// </summary>
    public sealed class BotNoDuplicateScenario : TestScenario
    {
        public const string ScenarioName = "bot_no_duplicate";
        private const int WatchSeconds = 1200;

        private static readonly string[] Kinds = { "MyObjectBuilder_OxygenGenerator/", "MyObjectBuilder_Assembler/BasicAssembler", "MyObjectBuilder_Refinery/Blast Furnace" };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => WatchSeconds + 420;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            MyPlayer bot = null;
            MySlimBlock block = null;
            var found = Wait(() =>
            {
                foreach (var kind in Kinds)
                foreach (var p in MySession.Static.Players.GetOnlinePlayers().Where(p => p.DisplayName.StartsWith("[BOT] ") && p.Character != null && !p.Character.IsDead))
                {
                    var identity = p.Identity.IdentityId;
                    // (on the base it lives at: the one with its survival kit; and the only one of its kind there, so
                    // that another one appearing is the bot's doing now)
                    var bases = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.IsStatic && g.BigOwners.Contains(identity) &&
                        g.GetFatBlocks().Any(b => b.IsWorking && b.BlockDefinition.Id.TypeId.ToString().EndsWith("SurvivalKit")));
                    block = bases.Select(g => g.GetBlocks().Where(b => b.BlockDefinition.Id.ToString() == kind).ToList())
                        .Where(same => same.Count == 1 && same[0].BuiltBy == identity && same[0].IsFullIntegrity).Select(same => same[0]).FirstOrDefault();
                    if (block == null) continue;
                    bot = p;
                    return true;
                }
                return false;
            }, "a bot with one O2/H2 generator (or basic assembler, or basic refinery) of its own, welded in full, on its base", 300);
            while (found.MoveNext()) yield return found.Current;
            var name = bot.DisplayName;
            var grid = block.CubeGrid;
            var id = block.BlockDefinition.Id;
            var what = block.BlockDefinition.DisplayNameText;
            var store = grid.GetFatBlocks().OfType<MyCargoContainer>().FirstOrDefault()?.GetInventory(0) as MyInventory
                        ?? grid.GetFatBlocks().FirstOrDefault(b => b.BlockDefinition.Id.TypeId.ToString().EndsWith("SurvivalKit"))?.GetInventory(0) as MyInventory;
            Check(store != null, $"no container or survival kit on {grid.DisplayName} for the components");
            int Count() => grid.GetBlocks().Count(b => b.BlockDefinition.Id == id);

            // ground down until it does not work (small steps: the amount is scaled by the block's disassembly ratio),
            // the components into the base
            for (var k = 0; k < 20000 && block.FatBlock?.IsFunctional == true && block.BuildIntegrity > block.MaxIntegrity * 0.05f; k++)
                block.DecreaseMountLevel(block.MaxIntegrity * 0.0005f, store);
            Note($"{name}: the {what} of {grid.DisplayName} ground down to {block.BuildIntegrity / block.MaxIntegrity * 100:0}% (working {block.FatBlock?.IsFunctional}), " +
                 $"its components into {(store.Owner as MyCubeBlock)?.DisplayNameText}; {Count()} of the kind on the base");
            Check(block.FatBlock?.IsFunctional == false, $"the {what} still works: not the case of the production server");

            var worksAt = -1;
            var most = Count();
            for (var tick = 0; tick < WatchSeconds * 60; tick++)
            {
                if (tick % 60 == 0)
                {
                    most = System.Math.Max(most, Count());
                    var there = grid.GetCubeBlock(block.Position) == block && !block.IsDestroyed;
                    if (worksAt < 0 && there && block.FatBlock?.IsFunctional == true) worksAt = tick / 60;
                    if (tick % (60 * 60) == 0)
                        Note($"{tick / 60} s: the {what} {(there ? $"at {block.BuildIntegrity / block.MaxIntegrity * 100:0}%" : "is gone")}, {Count()} of the kind on the base, {name} " +
                             $"{(bot.Character == null ? "has no body" : $"{Vector3D.Distance(bot.Character.PositionComp.GetPosition(), block.WorldPosition):0} m from it")}");
                    // it works again and a minute more showed no other one: enough
                    if (most > 1 || (worksAt >= 0 && tick / 60 - worksAt >= 60)) break;
                }
                yield return null;
            }
            Note($"BOT NO DUPLICATE RESULT | {name}: the {what} " + (worksAt >= 0 ? $"works again after {worksAt} s" : "does not work yet") +
                 $"; the most of the kind on {grid.DisplayName} at once: {most}");
            Check(most <= 1, $"{name} built another {what}: {most} on {grid.DisplayName}");
            Check(worksAt >= 0, $"{name} did not bring its {what} back to work in {WatchSeconds} s");
        }
    }
}
