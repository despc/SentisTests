using System.Collections;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A SentisAi bot brings a block of its base that stands apart from the conveyors onto them: with a conveyor block
    /// or two where that does it, else by taking the block down and placing it again port to port. On the production
    /// server the bots built a second block of the kind instead (seven O2/H2 generators on one base, 06.10.2026).
    ///
    /// On the base of a bot that is at home, the one block of a kind - an O2/H2 generator, else a basic assembler or a
    /// basic refinery - is removed and put down again on top of the base, away from where it stood, as built by the
    /// bot. Within <see cref="WatchSeconds"/> the base has to have one block of the kind, working, that the survival
    /// kit reaches through the conveyors; never more than one.
    /// </summary>
    public sealed class BotMoveApartScenario : TestScenario
    {
        public const string ScenarioName = "bot_move_apart";
        private const int WatchSeconds = 1500;
        private const double HomeM = 300;

        private static readonly string[] Kinds = { "MyObjectBuilder_OxygenGenerator/", "MyObjectBuilder_Assembler/BasicAssembler", "MyObjectBuilder_Refinery/Blast Furnace" };

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => WatchSeconds + 720;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            MyPlayer bot = null;
            MySlimBlock first = null;
            MyCubeBlock kit = null;
            var said = false;
            var found = Wait(() =>
            {
                // (what the bases have, once: which of them the scenario can take and why not the others)
                if (!said)
                {
                    said = true;
                    foreach (var p in MySession.Static.Players.GetOnlinePlayers().Where(p => p.DisplayName.StartsWith("[BOT] ") && p.Character != null))
                    foreach (var g in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.IsStatic && g.BigOwners.Contains(p.Identity.IdentityId)))
                    {
                        var itsKit = g.GetFatBlocks().FirstOrDefault(b => b.IsWorking && b.BlockDefinition.Id.TypeId.ToString().EndsWith("SurvivalKit"));
                        Note($"{p.DisplayName} {Vector3D.Distance(g.PositionComp.GetPosition(), p.Character.PositionComp.GetPosition()):0} m from {g.DisplayName} (kit {(itsKit == null ? "none working" : "working")}): " +
                             string.Join("; ", Kinds.Select(k => k.Split('/')[1] + " " + string.Join(",", g.GetBlocks().Where(b => b.BlockDefinition.Id.ToString() == k)
                                 .Select(b => $"{b.Position} {(b.FatBlock?.IsFunctional == true ? "works" : "broken")} {(itsKit != null && Joined(itsKit, b.FatBlock) ? "joined" : "apart")}")))));
                    }
                }
                foreach (var kind in Kinds)
                foreach (var p in MySession.Static.Players.GetOnlinePlayers().Where(p => p.DisplayName.StartsWith("[BOT] ") && p.Character != null && !p.Character.IsDead))
                {
                    var identity = p.Identity.IdentityId;
                    var at = p.Character.PositionComp.GetPosition();
                    foreach (var g in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.IsStatic && g.BigOwners.Contains(identity) &&
                                 Vector3D.Distance(g.PositionComp.GetPosition(), at) < HomeM))
                    {
                        var same = g.GetBlocks().Where(b => b.BlockDefinition.Id.ToString() == kind).ToList();
                        var itsKit = g.GetFatBlocks().FirstOrDefault(b => b.IsWorking && b.BlockDefinition.Id.TypeId.ToString().EndsWith("SurvivalKit"));
                        // (one of the kind, working, on the kit's conveyors now: what comes after is the scenario's doing)
                        if (same.Count != 1 || itsKit == null || same[0].FatBlock?.IsFunctional != true || !Joined(itsKit, same[0].FatBlock)) continue;
                        // (and nothing else on the base that does its work - a basic assembler beside an assembler is not
                        // brought back, it is taken down as one too many)
                        if (g.GetFatBlocks().Count(b => b.BlockDefinition.Id.TypeId == same[0].BlockDefinition.Id.TypeId) != 1) continue;
                        first = same[0];
                        kit = itsKit;
                        bot = p;
                        return true;
                    }
                }
                return false;
            }, $"a bot within {HomeM} m of its base that has one working O2/H2 generator (or basic assembler, or basic refinery) on the survival kit's conveyors", 600);
            while (found.MoveNext()) yield return found.Current;
            var name = bot.DisplayName;
            var grid = first.CubeGrid;
            var id = first.BlockDefinition.Id;
            var what = first.BlockDefinition.DisplayNameText;
            var was = first.Position;
            var definition = MyDefinitionManager.Static.GetCubeBlockDefinition(id);
            System.Collections.Generic.List<MySlimBlock> All() => grid.GetBlocks().Where(b => b.BlockDefinition.Id == id).ToList();

            // away from the conveyors: on top of the block of the base furthest from where it stood, nothing in its cells
            var up = Base6Directions.GetIntVector(grid.WorldMatrix.GetClosestDirection(Vector3D.Normalize(grid.PositionComp.GetPosition() -
                (MyGamePruningStructure.GetClosestPlanet(grid.PositionComp.GetPosition())?.PositionComp.GetPosition() ?? Vector3D.Zero))));
            grid.RazeBlock(was);
            yield return WaitForTicks(30);
            MySlimBlock apart = null;
            foreach (var under in grid.GetBlocks().OrderByDescending(b => (b.Position - was).RectangularLength()))
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
                apart = ((IMyCubeGrid)grid).AddBlock(ob, false) as MySlimBlock;
                if (apart?.FatBlock != null && Joined(kit, apart.FatBlock)) { grid.RazeBlock(apart.Position); apart = null; continue; }
                if (apart != null) break;
            }
            Check(apart != null, $"no place on {grid.DisplayName} for the {what} apart from the conveyors");
            yield return WaitForTicks(30);
            var put = apart.Position;
            Note($"{name}: the {what} of {grid.DisplayName} moved from {was} to {put}, apart from the conveyors; {name} " +
                 $"{Vector3D.Distance(bot.Character.PositionComp.GetPosition(), apart.WorldPosition):0} m from it");

            var joinedAt = -1;
            var most = 1;
            var how = "";
            for (var tick = 0; tick < WatchSeconds * 60 && joinedAt < 0; tick++)
            {
                if (tick % 60 == 0)
                {
                    var all = All();
                    most = System.Math.Max(most, all.Count);
                    if (kit.Closed || !kit.IsWorking) kit = grid.GetFatBlocks().FirstOrDefault(b => b.IsWorking && b.BlockDefinition.Id.TypeId.ToString().EndsWith("SurvivalKit")) ?? kit;
                    var on = all.FirstOrDefault(b => b.FatBlock?.IsFunctional == true && !kit.Closed && Joined(kit, b.FatBlock));
                    if (on != null && all.Count == 1)
                    {
                        joinedAt = tick / 60;
                        how = on.Position == put ? "joined where it stood" : $"taken down and placed again at {on.Position}";
                    }
                    if (tick % (60 * 60) == 0)
                        Note($"{tick / 60} s: {all.Count} of the kind on the base ({string.Join(", ", all.Select(b => $"{b.Position} {b.BuildIntegrity / b.MaxIntegrity * 100:0}%"))}), {name} " +
                             $"{(bot.Character == null ? "has no body" : $"{Vector3D.Distance(bot.Character.PositionComp.GetPosition(), grid.PositionComp.GetPosition()):0} m from the base")}");
                    if (most > 1) break;
                }
                yield return null;
            }
            Note($"BOT MOVE APART RESULT | {name}: the {what} of {grid.DisplayName} " + (joinedAt >= 0 ? $"on the conveyors after {joinedAt} s, {how}" : $"not on the conveyors after {WatchSeconds} s") +
                 $"; the most of the kind at once: {most}");
            Check(most <= 1, $"{name} built another {what}: {most} on {grid.DisplayName}");
            Check(joinedAt >= 0, $"{name} did not bring the {what} onto the conveyors in {WatchSeconds} s");
        }

        /// <summary>Whether the conveyors join two blocks of a grid.</summary>
        private static bool Joined(MyCubeBlock a, MyCubeBlock b) =>
            a is IMyConveyorEndpointBlock from && b is IMyConveyorEndpointBlock to && from.ConveyorEndpoint != null && to.ConveyorEndpoint != null &&
            MyGridConveyorSystem.Reachable(from.ConveyorEndpoint, to.ConveyorEndpoint);
    }
}
