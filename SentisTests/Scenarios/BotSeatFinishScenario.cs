using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.Game;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A SentisAi bot finishes a block of its base that works but is not welded in full. On the live server bots left
    /// passenger seats half welded with all the components at the base: once a block worked it was no longer "unfinished"
    /// to the bot, and a welding broken off (the next component not in its pockets) was never gone back to. The scenario
    /// takes a passenger seat a bot built on its base, grinds it down to 60% (still working) with the components into a
    /// container of that base, and waits up to eight minutes for the seat to be welded in full again.
    /// </summary>
    public sealed class BotSeatFinishScenario : TestScenario
    {
        public const string ScenarioName = "bot_seat_finish";
        private const int WatchSeconds = 480;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => WatchSeconds + 120;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            MyPlayer bot = null;
            MySlimBlock seat = null;
            var found = Wait(() =>
            {
                foreach (var p in MySession.Static.Players.GetOnlinePlayers().Where(p => p.DisplayName.StartsWith("[BOT] ") && p.Character != null && !p.Character.IsDead))
                {
                    var identity = p.Identity.IdentityId;
                    // (on the base it lives at: the one with its survival kit - an old base of its stood without one)
                    seat = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.IsStatic && g.BigOwners.Contains(identity) &&
                                                                                    g.GetFatBlocks().Any(b => b.IsWorking && b.BlockDefinition.Id.TypeId.ToString().EndsWith("SurvivalKit")))
                        .SelectMany(g => g.GetBlocks())
                        .FirstOrDefault(b => b.BlockDefinition.Id.SubtypeName == "PassengerSeatLarge" && b.BuiltBy == identity && b.IsFullIntegrity);
                    if (seat == null) continue;
                    bot = p;
                    return true;
                }
                return false;
            }, "a bot with a passenger seat of its own, welded in full, on a base", 300);
            while (found.MoveNext()) yield return found.Current;
            var name = bot.DisplayName;
            var grid = seat.CubeGrid;
            var store = grid.GetFatBlocks().OfType<MyCargoContainer>().FirstOrDefault()?.GetInventory(0) as MyInventory
                        ?? grid.GetFatBlocks().FirstOrDefault(b => b.BlockDefinition.Id.TypeId.ToString().EndsWith("SurvivalKit"))?.GetInventory(0) as MyInventory;
            Check(store != null, $"no container or survival kit on {grid.DisplayName} for the seat's components");

            // ground down to 60%, still working, the components into the base
            // (small steps: the amount is scaled by the block's disassembly ratio - at 2% a step the seat went to 0 at once)
            for (var k = 0; k < 5000 && seat.BuildIntegrity > seat.MaxIntegrity * 0.6f; k++)
                seat.DecreaseMountLevel(seat.MaxIntegrity * 0.0005f, store);
            Note($"{name}: the Passenger Seat of {grid.DisplayName} ground down to {seat.BuildIntegrity / seat.MaxIntegrity * 100:0}% (working {seat.IsFunctional}), " +
                 $"its components into {(store.Owner as MyCubeBlock)?.DisplayNameText}");
            Check(seat.IsFunctional, "the seat stopped working at 60%: not the case of the live server");

            var doneAt = -1;
            for (var tick = 0; tick < WatchSeconds * 60 && doneAt < 0; tick++)
            {
                if (seat.IsDestroyed || seat.CubeGrid.GetCubeBlock(seat.Position) != seat) { Note("the seat is gone"); break; }
                if (tick % 60 == 0)
                {
                    if (seat.IsFullIntegrity) doneAt = tick / 60;
                    if (tick % (30 * 60) == 0)
                        Note($"{tick / 60} s: the seat at {seat.BuildIntegrity / seat.MaxIntegrity * 100:0}%, {name} " +
                             $"{(bot.Character == null ? "has no body" : $"{Vector3D.Distance(bot.Character.PositionComp.GetPosition(), seat.WorldPosition):0} m from it")}");
                }
                yield return null;
            }
            Note(doneAt >= 0 ? $"{name}: the seat welded in full after {doneAt} s" : $"{name}: the seat still at {seat.BuildIntegrity / seat.MaxIntegrity * 100:0}% after {WatchSeconds} s");
            Check(doneAt >= 0, $"{name} did not finish its working Passenger Seat in {WatchSeconds} s");
        }
    }
}
