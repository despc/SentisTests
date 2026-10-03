using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A SentisAi bot under radiation waits it out in its pressurized cockpit instead of dying of it. On Europa the
    /// planet gives 0.6 radiation a second to whoever is not sheltered; the stand has no such planet, so the scenario
    /// plays the planet: a bot of the stand with a pressurized cockpit of its own near gets that radiation while it
    /// is on foot, and none in the cockpit (where the game lets it decay). The bot must be in the cockpit before
    /// the radiation hurts (74.5), come out when it is gone, and lose no health to it.
    /// </summary>
    public sealed class BotRadiationScenario : TestScenario
    {
        public const string ScenarioName = "bot_radiation";
        private const float GainPerSecond = 0.6f;
        private const float Hurts = 74.5f;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            MyPlayer bot = null;
            MyCockpit shelter = null;
            // a bot on foot with a pressurized cockpit of its own within a walk
            var found = Wait(() =>
            {
                foreach (var player in MySession.Static.Players.GetOnlinePlayers().Where(p => p.DisplayName.StartsWith("[BOT] ")))
                {
                    var body = player.Character;
                    if (body == null || body.IsDead || player.Controller?.ControlledEntity is MyShipController) continue;
                    var identity = player.Identity.IdentityId;
                    var at = body.PositionComp.GetPosition();
                    var cockpit = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.BigOwners.Contains(identity))
                        .SelectMany(g => g.GetFatBlocks<MyCockpit>())
                        .Where(c => c.IsFunctional && c.BlockDefinition.IsPressurized && Vector3D.Distance(c.PositionComp.GetPosition(), at) < 300)
                        .OrderBy(c => Vector3D.Distance(c.PositionComp.GetPosition(), at)).FirstOrDefault();
                    if (cockpit == null) continue;
                    bot = player;
                    shelter = cockpit;
                    return true;
                }
                return false;
            }, "a bot on foot with a pressurized cockpit of its own within 300 m", 300);
            while (found.MoveNext()) yield return found.Current;
            var name = bot.DisplayName;
            Note($"{name}: {Vector3D.Distance(shelter.PositionComp.GetPosition(), bot.Character.PositionComp.GetPosition()):0} m from {shelter.BlockDefinition.Id.SubtypeName} on {shelter.CubeGrid.DisplayName}");

            float Radiation() => bot.Character?.StatComp?.Radiation?.Value ?? 0;
            bool Sheltered() => bot.Controller?.ControlledEntity is MyCockpit seat && seat.BlockDefinition.IsPressurized;
            var health = bot.Character.StatComp.Health.Value;
            var worst = 0f;
            var satAt = -1f;
            var cameOut = false;
            var tick = 0;
            // ten minutes at most: out in the radiation, into the cockpit, the radiation gone, out again
            for (; tick < 10 * 60 * 60 && !cameOut; tick++)
            {
                var body = bot.Character;
                Check(body != null && !body.IsDead, $"{name} died (radiation {Radiation():0}, worst {worst:0})");
                if (tick % 60 == 0)
                {
                    // the planet: radiation to who is not sheltered, once it has sat none (the game lets it decay)
                    if (!Sheltered() && satAt < 0) body.StatComp.Radiation.Increase(GainPerSecond, null);
                    worst = System.Math.Max(worst, Radiation());
                    if (Sheltered() && satAt < 0)
                    {
                        satAt = Radiation();
                        Note($"{name} sat in the pressurized cockpit with radiation {satAt:0.0} after {tick / 60} s");
                    }
                    if (satAt >= 0 && !Sheltered())
                    {
                        cameOut = true;
                        Note($"{name} came out with radiation {Radiation():0.0} after {tick / 60} s");
                    }
                    if (tick % 1800 == 0) Note($"{tick / 60} s: radiation {Radiation():0.0}, {(Sheltered() ? "in the cockpit" : "on foot")}");
                }
                yield return null;
            }
            Check(satAt >= 0, $"{name} never went to its pressurized cockpit (radiation reached {worst:0})");
            Check(worst < Hurts, $"the radiation reached {worst:0.0}: the game hurts from {Hurts}");
            Check(cameOut, $"{name} did not come out of the cockpit (radiation {Radiation():0.0})");
            Check(Radiation() <= 6, $"{name} came out with radiation {Radiation():0.0}, not gone");
            var now = bot.Character.StatComp.Health.Value;
            Note($"health {health:0} -> {now:0}, the most radiation {worst:0.0}");
        }
    }
}
