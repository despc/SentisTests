using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A SentisAi bot with an empty suit fills it with hydrogen at its base: the base's O2/H2 generator makes it from
    /// ice, and the suit takes it at the base's survival kit, which stands on the generator's conveyors. The scenario
    /// finds a bot whose base has a working kit joined by conveyors to a working generator (ice is put into it when it
    /// holds little), empties the bot's hydrogen, and
    /// waits: the bot must come to the kit and leave with a full suit - alive, without the respawn it falls back on
    /// when there is no hydrogen to be had. Needs such a base on the stand; up to 12 minutes.
    /// </summary>
    public sealed class BotRefuelScenario : TestScenario
    {
        public const string ScenarioName = "bot_refuel";

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var ice = new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Ice");
            var hydrogen = MyCharacterOxygenComponent.HydrogenId;
            MyPlayer bot = null;
            MyGasGenerator generator = null;
            var found = Wait(() =>
            {
                foreach (var player in MySession.Static.Players.GetOnlinePlayers().Where(p => p.DisplayName.StartsWith("[BOT] ")))
                {
                    var body = player.Character;
                    if (body == null || body.IsDead || player.Controller?.ControlledEntity is MyShipController) continue;
                    var identity = player.Identity.IdentityId;
                    var at = body.PositionComp.GetPosition();
                    foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.BigOwners.Contains(identity) && Vector3D.Distance(g.PositionComp.GetPosition(), at) < 400))
                    {
                        // the kit and the generator joined by conveyors: the suit takes the gas through the kit's own network
                        var kit = grid.GetFatBlocks().FirstOrDefault(b => b.IsWorking && b.BlockDefinition.Id.TypeId.ToString().EndsWith("SurvivalKit"));
                        var withIce = kit == null ? null : grid.GetFatBlocks<MyGasGenerator>().FirstOrDefault(g => g.IsWorking &&
                            kit is Sandbox.Game.GameSystems.Conveyors.IMyConveyorEndpointBlock a && g is Sandbox.Game.GameSystems.Conveyors.IMyConveyorEndpointBlock b &&
                            Sandbox.Game.GameSystems.MyGridConveyorSystem.Reachable(a.ConveyorEndpoint, b.ConveyorEndpoint));
                        if (withIce == null) continue;
                        bot = player;
                        generator = withIce;
                        return true;
                    }
                }
                return false;
            }, "a bot on foot within 400 m of its base with a working survival kit joined by conveyors to a working O2/H2 generator", 400);
            while (found.MoveNext()) yield return found.Current;
            var name = bot.DisplayName;
            var deaths = 0;
            var body0 = bot.Character.EntityId;
            float Hydrogen() => bot.Character?.OxygenComponent?.GetGasFillLevel(hydrogen) ?? 0;
            // the base's stock of ice, if the bot has brought none yet: the scenario is about the suit, not the trip for ice
            if ((double)MyEntityExtensions.GetInventory(generator).GetItemAmount(ice) < 200)
                MyEntityExtensions.GetInventory(generator).AddItems(500, new MyObjectBuilder_Ore { SubtypeName = "Ice" });
            var iceBefore = (double)MyEntityExtensions.GetInventory(generator).GetItemAmount(ice);
            Note($"{name}: hydrogen {Hydrogen() * 100:0}%, the generator of {generator.CubeGrid.DisplayName} holds {iceBefore:0} kg of ice");
            bot.Character.OxygenComponent.UpdateStoredGasLevel(ref hydrogen, 0.03f);
            Note($"{name}: hydrogen set to {Hydrogen() * 100:0}%");

            var filled = false;
            for (var tick = 0; tick < 12 * 60 * 60 && !filled; tick++)
            {
                if (tick % 60 == 0)
                {
                    var body = bot.Character;
                    if (body != null && body.EntityId != body0) { deaths++; body0 = body.EntityId; Note($"{name} has a new body after {tick / 60} s (hydrogen {Hydrogen() * 100:0}%)"); }
                    filled = deaths == 0 && Hydrogen() > 0.9f;
                    if (tick % 1800 == 0)
                    {
                        Note($"{tick / 60} s: hydrogen {Hydrogen() * 100:0}%");
                        // what the suit and the generator are doing: for when it does not fill
                        var sink = body?.OxygenComponent?.CharacterGasSink;
                        var source = generator.Components.Get<Sandbox.Game.EntityComponents.MyResourceSourceComponent>();
                        var kitNow = generator.CubeGrid.GetFatBlocks().FirstOrDefault(b => b.BlockDefinition.Id.TypeId.ToString().EndsWith("SurvivalKit"));
                        Note($"{tick / 60} s: suit joined to {(sink?.TemporaryConnectedEntity as MyCubeBlock)?.DisplayNameText ?? "nothing"}, asks {sink?.RequiredInputByType(hydrogen):0.###}, gets {sink?.CurrentInputByType(hydrogen):0.###}; " +
                             $"generator enabled {generator.Enabled}, working {generator.IsWorking}, can give {source?.MaxOutputByType(hydrogen):0.###}, gives {source?.CurrentOutputByType(hydrogen):0.###}, " +
                             $"ice {(double)MyEntityExtensions.GetInventory(generator).GetItemAmount(ice):0}; kit working {kitNow?.IsWorking}, {(body == null || kitNow == null ? -1 : Vector3D.Distance(body.PositionComp.GetPosition(), kitNow.PositionComp.GetPosition())):0.0} m from the body; " +
                             $"the grid can give {generator.CubeGrid.GridSystems.ResourceDistributor.MaxAvailableResourceByType(hydrogen, generator.CubeGrid):0.###}");
                    }
                }
                yield return null;
            }
            var iceAfter = generator.MarkedForClose ? -1 : (double)MyEntityExtensions.GetInventory(generator).GetItemAmount(ice);
            Note($"{name}: hydrogen {Hydrogen() * 100:0}%, new bodies {deaths}, ice {iceBefore:0} -> {iceAfter:0} kg");
            Check(deaths == 0, $"{name} got a new body {deaths} time(s): the suit was filled by a respawn, not at the kit");
            Check(filled, $"{name} did not fill its suit at the base (hydrogen {Hydrogen() * 100:0}%)");
            Check(iceAfter < iceBefore, "the generator used no ice: the hydrogen did not come from it");
        }
    }
}
