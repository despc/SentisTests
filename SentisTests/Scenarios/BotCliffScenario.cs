using System;
using System.Collections;
using System.Linq;
using Sandbox.Engine.Voxels;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A SentisAi bot on foot, no hydrogen, does not walk over a cliff. Bots walking home from a mountain went over its edge
    /// at 76-80 m/s and died (three in two days, 04-05.10.2026). The scenario cuts a crater 13 m deep between a bot and its
    /// base, puts the bot 45 m from the base with its pockets full of stone (it heads home) and no hydrogen, and watches: it
    /// must come within 20 m of the base in four minutes, in the same body, with no fall - never faster than 12 m/s, its
    /// health not down. The ground is put back as generated afterwards.
    /// <c>bot_cliff_h2</c>: the same with half a tank of hydrogen - it may go down into the crater, but the jetpack brakes
    /// the fall (the bots kept it off on foot and fell free: one went down at 79 m/s with 29% hydrogen).
    /// </summary>
    public sealed class BotCliffScenario : TestScenario
    {
        public const string ScenarioName = "bot_cliff", WithHydrogenName = "bot_cliff_h2";
        private readonly bool _withHydrogen;

        public BotCliffScenario(bool withHydrogen = false) => _withHydrogen = withHydrogen;
        private const double CraterRadiusM = 10, CraterSunkM = 3;
        private const int WatchSeconds = 240;

        public override string Name => _withHydrogen ? WithHydrogenName : ScenarioName;
        public override int TimeoutSeconds => WatchSeconds + 120;

        private MyPlanet _planet;
        private Vector3D _crater;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            MyPlayer bot = null;
            MyCubeGrid home = null;
            var found = Wait(() =>
            {
                foreach (var p in MySession.Static.Players.GetOnlinePlayers().Where(p => p.DisplayName.StartsWith("[BOT] ") && p.Character != null && !p.Character.IsDead &&
                                                                                         !(p.Controller?.ControlledEntity is MyShipController) && !BotPitScenario.BusyCharging(p)))
                {
                    var at = p.Character.PositionComp.GetPosition();
                    if (MyGamePruningStructure.GetClosestPlanet(at) == null) continue;
                    var identity = p.Identity.IdentityId;
                    home = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.IsStatic && g.BigOwners.Contains(identity) &&
                                                                                    g.GetFatBlocks().Any(b => b.BlockDefinition.Id.TypeId.ToString().EndsWith("SurvivalKit")))
                        .OrderBy(g => Vector3D.Distance(g.PositionComp.GetPosition(), at)).FirstOrDefault();
                    if (home == null) continue;
                    bot = p;
                    return true;
                }
                return false;
            }, "a bot on foot on a planet with a base of its own", 300);
            while (found.MoveNext()) yield return found.Current;
            var settling = bot.Character.EntityId;
            yield return WaitForTicks(30 * 60);
            Check(bot.Character != null && bot.Character.EntityId == settling && !bot.Character.IsDead, $"{bot.DisplayName} respawned while the scenario waited for it to settle");
            var name = bot.DisplayName;

            var basePos = home.PositionComp.GetPosition();
            var planet = MyGamePruningStructure.GetClosestPlanet(basePos);
            _planet = planet;
            var ground = new PlanetGround(planet);
            var up = ground.Up(basePos);
            var away = bot.Character.PositionComp.GetPosition() - basePos;
            away -= up * Vector3D.Dot(away, up);
            if (away.LengthSquared() < 1) away = Vector3D.CalculatePerpendicularVector(up);
            away.Normalize();
            var reach = home.PositionComp.WorldAABB.HalfExtents.Length();
            // the crater between: its near rim 15 m clear of the base's blocks
            var craterTop = ground.Ground(basePos + away * (reach + 15 + CraterRadiusM));
            _crater = craterTop - up * CraterSunkM;
            MyVoxelGenerator.CutOutShapeWithProperties(planet, new MyShapeSphere { Center = _crater, Radius = (float)CraterRadiusM }, out _, out _, null, updateSync: true);
            var start = ground.Ground(basePos + away * (reach + 15 + CraterRadiusM * 2 + 8)) + up * 0.5;
            yield return WaitForTicks(10);

            var body0 = bot.Character.EntityId;
            var matrix = MatrixD.CreateWorld(start, -away, up);
            bot.Character.PositionComp.SetWorldMatrix(ref matrix);
            if (bot.Character.Physics != null) bot.Character.Physics.LinearVelocity = Vector3.Zero;
            var pockets = MyEntityExtensions.GetInventory(bot.Character);
            var stone = new VRage.Game.MyDefinitionId(typeof(VRage.Game.MyObjectBuilder_Ore), "Stone");
            var room = pockets.ComputeAmountThatFits(stone);
            if (room > 1) pockets.AddItems(room * 0.95f, new VRage.Game.MyObjectBuilder_Ore { SubtypeName = "Stone" });
            var hydrogen = MyCharacterOxygenComponent.HydrogenId;
            bot.Character.OxygenComponent.UpdateStoredGasLevel(ref hydrogen, _withHydrogen ? 0.5f : 0f);
            var health0 = bot.Character.StatComp?.Health?.Value ?? 0;
            double Home() => Vector3D.Distance(bot.Character.PositionComp.GetPosition(), basePos) - reach;
            Note($"{name}: put {Home():0} m from the blocks of {home.DisplayName}, a crater {CraterRadiusM * 2:0} m across and {CraterRadiusM + CraterSunkM:0} m deep between, health {health0:0}");

            var homeAt = -1;
            var fastest = 0.0;
            var lowest = 0.0;       // how far under the crater's rim it got
            var deaths = 0;
            for (var tick = 0; tick < WatchSeconds * 60 && homeAt < 0; tick++)
            {
                var body = bot.Character;
                if (body == null || body.IsDead || body.EntityId != body0) { deaths++; Note($"{name}: a new body after {tick / 60} s"); break; }
                // (not the first second: the move into place shows as a speed)
                if (tick > 60) fastest = Math.Max(fastest, body.Physics?.LinearVelocity.Length() ?? 0);
                lowest = Math.Min(lowest, Vector3D.Dot(body.PositionComp.GetPosition() - craterTop, up));
                if (tick % 60 == 0)
                {
                    if (Home() < 20) homeAt = tick / 60;
                    if (tick % (15 * 60) == 0 || tick < 30 * 60)
                    {
                        var p = body.PositionComp.GetPosition();
                        var flat = p - _crater - up * Vector3D.Dot(p - _crater, up);
                        Note($"{tick / 60} s: {Home():0} m from the base, {flat.Length():0.0} m from the crater's middle, {Vector3D.Dot(p - craterTop, up):0.0} m over its rim, speed {body.Physics?.LinearVelocity.Length():0.0}, health {body.StatComp?.Health?.Value:0}");
                    }
                }
                yield return null;
            }
            var health = bot.Character?.StatComp?.Health?.Value ?? 0;
            Note($"{name}: {(homeAt >= 0 ? $"at the base after {homeAt} s" : $"{Home():0} m from the base after {WatchSeconds} s")}, fastest {fastest:0.0} m/s, health {health0:0} -> {health:0}");
            Check(deaths == 0, $"{name} died or respawned");
            Check(fastest < 12, $"{name} fell: {fastest:0.0} m/s");
            // (down the crater's side at under 12 m/s is still down it: a real cliff is higher)
            if (!_withHydrogen) Check(lowest > -5, $"{name} went down into the crater, {-lowest:0.0} m under its rim");
            Check(health >= health0 - 1, $"{name} got hurt: health {health0:0} -> {health:0}");
            Check(homeAt >= 0, $"{name} did not get home round the crater in {WatchSeconds} s");
        }

        public override void Cleanup()
        {
            base.Cleanup();
            if (_planet != null) WorldApi.RevertTerrain(_planet, _crater, CraterRadiusM + 2);
        }
    }
}
