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
    /// A SentisAi bot at the bottom of a pit, its jetpack empty, gets out on foot. On the stand bots mined ice or
    /// magnesium 25-30 m down, the hydrogen went on the way, and they stood at the bottom "digging out, 2 m to go"
    /// for ten minutes and more until the suit was flat (23 deaths a day, 04.10.2026). The scenario cuts a shaft
    /// 26 m deep with a chamber at its foot 25 m from a bot on foot, puts the bot on the chamber's floor with no
    /// hydrogen and watches its depth: it must be on the surface within six minutes, in the same body. The ground is
    /// put back as generated afterwards.
    /// </summary>
    /// <remarks>
    /// <c>bot_pit_closed</c>: as the bots on the stand were - rock over the head, the mouth of its pit at the surface
    /// 2 m aside (set on the bot as the pit it dug), its base 350 m off (so it heads for the mouth first).
    /// <c>bot_pit_far</c>: the same pit with no mouth known (as after a restart: the bot's memory of its pit is gone), its
    /// base 350 m off - Brook came back after each restart of the stand into a pit 25 m deep, 843 m from its base, and did
    /// not get out in three minutes.
    /// </remarks>
    public sealed class BotPitScenario : TestScenario
    {
        public const string ScenarioName = "bot_pit", ClosedName = "bot_pit_closed", FarName = "bot_pit_far";
        private readonly bool _closed, _far;

        public BotPitScenario(bool closed = false, bool far = false)
        {
            _closed = closed || far;
            _far = far;
        }
        private const double ShaftDepthM = 26, ChamberRadiusM = 3.5;
        private const int WatchSeconds = 360;

        public override string Name => _far ? FarName : _closed ? ClosedName : ScenarioName;
        public override int TimeoutSeconds => WatchSeconds + 120;

        private MyPlanet _planet;
        private Vector3D _site;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            MyPlayer bot = null;
            var found = Wait(() =>
            {
                bot = MySession.Static.Players.GetOnlinePlayers().FirstOrDefault(p => p.DisplayName.StartsWith("[BOT] ") && p.Character != null && !p.Character.IsDead &&
                                                                                     !(p.Controller?.ControlledEntity is MyShipController) &&
                                                                                     MyGamePruningStructure.GetClosestPlanet(p.Character.PositionComp.GetPosition()) != null &&
                                                                                     // (not on its way to charge or fill up: a walk to its kit that has gone on for minutes ends in a respawn at it)
                                                                                     !BusyCharging(p));
                return bot != null;
            }, "a bot on foot on a planet", 300);
            while (found.MoveNext()) yield return found.Current;
            // a body that has stood for a while: one just respawned is still being put down by the game (moved into
            // the pit, it came up 200 m off at once)
            var settling = bot.Character.EntityId;
            yield return WaitForTicks(30 * 60);
            Check(bot.Character != null && bot.Character.EntityId == settling && !bot.Character.IsDead, $"{bot.DisplayName} respawned while the scenario waited for it to settle");
            var name = bot.DisplayName;
            var at = bot.Character.PositionComp.GetPosition();
            var planet = MyGamePruningStructure.GetClosestPlanet(at);
            _planet = planet;
            var ground = new PlanetGround(planet);
            var up = ground.Up(at);
            // away from its base: the pit is not under its own blocks
            var identity = bot.Identity.IdentityId;
            var home = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.IsStatic && g.BigOwners.Contains(identity))
                .OrderBy(g => Vector3D.Distance(g.PositionComp.GetPosition(), at)).FirstOrDefault();
            var away = home == null ? Vector3D.CalculatePerpendicularVector(up) : at - home.PositionComp.GetPosition();
            away -= up * Vector3D.Dot(away, up);
            if (away.LengthSquared() < 1) away = Vector3D.CalculatePerpendicularVector(up);
            away.Normalize();
            var site = ground.Ground(at + away * (_closed ? 350 : 25));
            _site = site;
            up = ground.Up(site);

            // the shaft, a little wider than the body, and a chamber at its foot as mining leaves it
            for (var d = -2.0; d <= ShaftDepthM && !_closed; d += 1)
                MyVoxelGenerator.CutOutShapeWithProperties(planet, new MyShapeSphere { Center = site - up * d, Radius = 1.6f }, out _, out _, null, updateSync: true);
            var chamber = site - up * (ShaftDepthM + ChamberRadiusM - 1);
            MyVoxelGenerator.CutOutShapeWithProperties(planet, new MyShapeSphere { Center = chamber, Radius = (float)ChamberRadiusM }, out _, out _, null, updateSync: true);
            yield return WaitForTicks(10);
            var floor = chamber;
            for (var h = 0.0; h > -ChamberRadiusM - 3; h -= 0.25)
                if (ground.ContentAt(chamber + up * h) >= 128) { floor = chamber + up * (h + 0.25); break; }

            double Depth()
            {
                var p = bot.Character?.PositionComp.GetPosition() ?? site;
                var surface = planet.GetClosestSurfacePointGlobal(ref p);
                return Vector3D.Dot(surface - p, ground.Up(p));
            }

            var body0 = bot.Character.EntityId;
            var spot = floor + up * 1.0;
            var matrix = MatrixD.CreateWorld(spot, Vector3D.Normalize(Vector3D.Cross(up, Vector3D.CalculatePerpendicularVector(up))), up);
            bot.Character.PositionComp.SetWorldMatrix(ref matrix);
            if (bot.Character.Physics != null) bot.Character.Physics.LinearVelocity = Vector3.Zero;
            if (_closed && !_far)
            {
                // the pit it dug: its mouth 2 m aside of where it stands, up on the surface
                var aside = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
                var beside = spot + aside * 2;
                var mouth = planet.GetClosestSurfacePointGlobal(ref beside);
                var entrance = SentisAiBot(bot)?.GetType().GetField("_entrance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Check(entrance != null, "no SentisAi bot behind " + name);
                entrance.SetValue(SentisAiBot(bot), (Vector3D?)mouth);
            }
            // pockets full of stone, as the bots stuck on the stand were: it heads home (else a bot mining stone just goes on
            // mining at the bottom)
            var pockets = MyEntityExtensions.GetInventory(bot.Character);
            var stone = new VRage.Game.MyDefinitionId(typeof(VRage.Game.MyObjectBuilder_Ore), "Stone");
            var room = pockets.ComputeAmountThatFits(stone);
            if (room > 1) pockets.AddItems(room * 0.95f, new VRage.Game.MyObjectBuilder_Ore { SubtypeName = "Stone" });
            var hydrogen = MyCharacterOxygenComponent.HydrogenId;
            bot.Character.OxygenComponent.UpdateStoredGasLevel(ref hydrogen, 0f);
            yield return WaitForTicks(120);
            Check(Depth() > ShaftDepthM - 2, $"{name} is not in the pit two seconds after being put there ({Depth():0.0} m down)");
            Note($"{name}: put {Depth():0.0} m down at the foot of a {ShaftDepthM:0} m shaft {Vector3D.Distance(site, at):0} m from where it stood, energy {bot.Character.SuitEnergyLevel * 100:0}%, hydrogen 0%");

            var outAt = -1;
            var deaths = 0;
            for (var tick = 0; tick < WatchSeconds * 60 && outAt < 0; tick++)
            {
                if (tick % 60 == 0)
                {
                    var body = bot.Character;
                    if (body == null || body.IsDead || body.EntityId != body0) { deaths++; Note($"{name}: a new body after {tick / 60} s"); break; }
                    var depth = Depth();
                    if (depth < 2) outAt = tick / 60;
                    if (tick % (15 * 60) == 0)
                        Note($"{tick / 60} s: {depth:0.0} m down, {Vector3D.Distance(body.PositionComp.GetPosition() - up * Vector3D.Dot(body.PositionComp.GetPosition() - site, up), site):0.0} m aside of the shaft, energy {body.SuitEnergyLevel * 100:0}%");
                }
                yield return null;
            }
            Note(outAt >= 0 ? $"{name}: out on the surface after {outAt} s" : $"{name}: still {Depth():0.0} m down after {WatchSeconds} s");
            Check(deaths == 0, $"{name} died or respawned in the pit");
            Check(outAt >= 0, $"{name} did not get out of a {ShaftDepthM:0} m pit on foot in {WatchSeconds} s ({Depth():0.0} m down)");
        }

        /// <summary>The SentisAi bot playing the player (its plugin's manager, by reflection: no reference to it here).</summary>
        /// <summary>On its way to charge or fill up, or not yet on its feet: a walk to its kit that has gone on for minutes ends in a respawn at it.</summary>
        internal static bool BusyCharging(MyPlayer player) =>
            new[] { "Recharging", "Spawning", "Joined" }.Contains(SentisAiBot(player)?.GetType().GetProperty("Phase")?.GetValue(SentisAiBot(player))?.ToString());

        internal static object SentisAiBot(MyPlayer player)
        {
            var plugins = Torch.TorchBase.Instance?.Managers?.GetManager(typeof(Torch.API.Managers.IPluginManager)) as Torch.API.Managers.IPluginManager;
            var ai = plugins?.Plugins.Values.FirstOrDefault(p => p.GetType().FullName == "SentisAi.SentisAiPlugin");
            var manager = ai?.GetType().GetProperty("Manager")?.GetValue(ai);
            var bots = manager?.GetType().GetProperty("Bots")?.GetValue(manager) as System.Collections.IEnumerable;
            return bots?.Cast<object>().FirstOrDefault(b => b.GetType().GetProperty("Player")?.GetValue(b) == player);
        }

        public override void Cleanup()
        {
            base.Cleanup();
            if (_planet != null) WorldApi.RevertTerrain(_planet, _site - new PlanetGround(_planet).Up(_site) * (ShaftDepthM / 2), ShaftDepthM / 2 + ChamberRadiusM + 4);
        }
    }
}
