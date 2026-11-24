using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Common.ObjectBuilders;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A bot flies its combat shuttle by itself (24.11.2026): a whole shuttle made from the bot's own assembly plan (every part
    /// where the plan puts it, welded) by the base of a bot on stage 7 (Олег by default, argument <c>bot=</c>), as its
    /// "[BOT] combat shuttle N" (the bot's own half-built hull left as it is: the bot is given the test one's id) - battery full, hydrogen tanks a third
    /// full. Watched: the bot fuels it (ice into its generator, when the base has some), walks to its bridge, flies it 400 m
    /// out and back, and leaves the seat. Passes when the flight ends "flown out and back". The test shuttle is taken away
    /// after.
    /// </summary>
    public sealed class BotShuttleScenario : TestScenario
    {
        public const string ScenarioName = "bot_shuttle";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        private MyCubeGrid _own;
        private object _bot;

        public override void Cleanup()
        {
            try
            {
                if (_bot != null) Field("_combatGridId").SetValue(_bot, 0L);
            }
            finally { base.Cleanup(); }
        }

        private FieldInfo Field(string name) => _bot.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Static);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var botName = System.Text.RegularExpressions.Regex.Match(TestRunner.Focus ?? "", @"bot=(\w+)") is var arg && arg.Success ? arg.Groups[1].Value : "Олег";
            var plugin = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisAi.SentisAiPlugin", false)).FirstOrDefault(t => t != null);
            Check(plugin != null, "SentisAi not loaded");
            var instance = plugin.GetProperty("Instance").GetValue(null);
            var manager = plugin.GetProperty("Manager").GetValue(instance);
            var bots = (IEnumerable)manager.GetType().GetProperty("Bots").GetValue(manager);
            foreach (var b in bots) if (((string)b.GetType().GetField("Name").GetValue(b)).Contains(botName)) _bot = b;
            Check(_bot != null, "no bot " + botName);
            var player = _bot.GetType().GetProperty("Player").GetValue(_bot) as Sandbox.Game.World.MyPlayer;
            var identity = player?.Identity?.IdentityId ?? 0;
            var character = player?.Character;
            Check(character != null, botName + " has no body");
            var plan = Field("_combatAssembly").GetValue(_bot) ?? Field("_sharedCombatAssembly").GetValue(null);
            // (not read yet - the bot looks at its hull only between other work: asked for it, read on a worker)
            var read = _bot.GetType().GetMethod("CombatPlan", BindingFlags.Instance | BindingFlags.NonPublic);
            for (var i = 0; i < 300 && plan == null && read != null; i++)
            {
                plan = read.Invoke(_bot, null);
                if (plan == null) yield return null;
            }
            SkipUnless(plan != null, botName + " could not read the shuttle's plan");
            var slot = (int)_bot.GetType().GetField("Slot").GetValue(_bot);
            var gridName = "[BOT] combat shuttle " + slot;

            // its own hull keeps its name: renamed aside, it was debris to the bot, and Олег began grinding it down
            // (bot_shuttle 24.11.2026); the bot finds its shuttle by the id it is given below first
            _own = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => g.DisplayName == gridName);

            // where: 70 m from the base's middle, on open ground
            var home = MyEntities.GetEntities().OfType<MyCubeGrid>().Where(g => g.IsStatic && g.BigOwners.Contains(identity))
                .OrderByDescending(g => g.BlocksCount).FirstOrDefault();
            Check(home != null, "no base of " + botName);
            var planet = MyGamePruningStructure.GetClosestPlanet(home.PositionComp.GetPosition());
            var middle = home.PositionComp.WorldAABB.Center;
            var up = Vector3D.Normalize(middle - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.Reject(character.PositionComp.GetPosition() - middle, up) + Vector3D.CalculatePerpendicularVector(up) * 0.01);
            var probe = middle + side * 70;
            var ground = planet.GetClosestSurfacePointGlobal(ref probe);

            // the grid from the plan: every part where it goes
            var parts = (IEnumerable)plan.GetType().GetProperty("Parts").GetValue(plan);
            var ob = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_CubeGrid>();
            ob.GridSizeEnum = MyCubeSize.Large;
            ob.IsStatic = false;
            ob.CreatePhysics = true;
            ob.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.Enabled;
            ob.DisplayName = gridName;
            MyObjectBuilder_Cockpit bridge = null;
            foreach (var part in parts)
            {
                var pt = part.GetType();
                var id = MyDefinitionId.Parse((string)pt.GetProperty("Definition").GetValue(part));
                var cells = ((System.Collections.Generic.IEnumerable<Vector3I>)pt.GetProperty("Cells").GetValue(part)).ToList();
                var block = (MyObjectBuilder_CubeBlock)MyObjectBuilderSerializer.CreateNewObject(id);
                block.Min = cells.Aggregate(Vector3I.Min);
                block.BlockOrientation = (MyBlockOrientation)pt.GetProperty("Orientation").GetValue(part);
                block.ColorMaskHSV = (Vector3)pt.GetProperty("Color").GetValue(part);
                block.Owner = identity;
                block.BuiltBy = identity;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
                if (block is Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock cell) cell.CurrentStoredPower = 1e3f;
                if (block is MyObjectBuilder_GasTank tank) tank.FilledRatio = 0.3f;
                if (block is MyObjectBuilder_Cockpit c && id.SubtypeName == "LargeBlockModularBridgeCockpit") bridge = c;
                ob.CubeBlocks.Add(block);
            }
            Check(bridge != null, "no bridge in the plan");
            var cockpitUp = Base6Directions.GetVector(bridge.BlockOrientation.Up);
            var grid0 = MatrixD.CreateWorld(Vector3D.Zero, Vector3D.Normalize(Vector3D.Cross(up, side)), up);
            var upNow = Vector3D.TransformNormal((Vector3D)cockpitUp, grid0);
            var world = grid0 * MatrixD.CreateFromQuaternion(Quaternion.CreateFromTwoVectors((Vector3)upNow, (Vector3)up));
            world.Translation = ground + up * 40;
            ob.PositionAndOrientation = new MyPositionAndOrientation(world);
            // (fresh builders: their ids are given when made - a remap trips on a timer block's empty toolbar)
            var ship = WorldApi.SpawnGrid(ob);
            Check(ship != null, "the shuttle was not made");
            Track(ship);
            // down onto the ground: its lowest corner a metre over it
            var box = ship.PositionComp.WorldAABB;
            var lowest = box.GetCorners().Min(c => Vector3D.Dot(c - ground, up));
            var m = ship.WorldMatrix;
            m.Translation -= up * (lowest - 1);
            ship.Teleport(m);
            var made = Wait(() => ship.Physics != null, "the shuttle's physics", 10);
            while (made.MoveNext()) yield return made.Current;
            WorldApi.ChargeBatteries(ship);
            foreach (var f in ship.GetFatBlocks().OfType<Sandbox.ModAPI.IMyFunctionalBlock>()) if (!f.Enabled) f.Enabled = true;
            Note($"BOT SHUTTLE | {botName}: a whole shuttle of {ship.BlocksCount} blocks, {ship.Physics.Mass / 1000:0} t, " +
                 $"{Vector3D.Distance(ship.PositionComp.GetPosition(), character.PositionComp.GetPosition()):0} m from the bot; its own hull {( _own == null ? "none" : _own.BlocksCount + " blocks")} left as it is");

            // the bot's: this one is its shuttle, looked at anew, not flown lately
            Field("_combatGridId").SetValue(_bot, ship.EntityId);
            Field("_shuttleFlownAt").SetValue(_bot, long.MinValue / 2);
            Field("_shuttleCheckedAt").SetValue(_bot, long.MinValue / 2);
            var began = DateTime.UtcNow;
            var lastNote = DateTime.MinValue;
            var flownAt = (long)Field("_shuttleFlownAt").GetValue(_bot);
            var flew = false;
            var start = ship.PositionComp.GetPosition();
            var farthest = 0.0;
            while (DateTime.UtcNow - began < TimeSpan.FromSeconds(TimeoutSeconds - 30))
            {
                var note = _bot.GetType().GetProperty("Note")?.GetValue(_bot) as string ?? "";
                var pilot = Field("_shuttlePilot").GetValue(_bot);
                if (pilot != null) flew = true;
                if (!ship.MarkedForClose) farthest = Math.Max(farthest, Vector3D.Reject(ship.PositionComp.GetPosition() - start, up).Length());
                if (DateTime.UtcNow - lastNote > TimeSpan.FromSeconds(10))
                {
                    lastNote = DateTime.UtcNow;
                    Note($"BOT SHUTTLE | {(DateTime.UtcNow - began).TotalSeconds:0} s: {note}; out {farthest:0} m");
                }
                if (flew && pilot == null && (long)Field("_shuttleFlownAt").GetValue(_bot) != flownAt) break;
                yield return null;
            }
            var back = Vector3D.Reject(ship.PositionComp.GetPosition() - start, up).Length();
            Note($"BOT SHUTTLE | end: flew {flew}, went out {farthest:0} m, now {back:0} m from where it stood");
            Check(flew, "the bot did not fly the shuttle");
            Check(farthest > 300, $"went out {farthest:0} m only");
            Check(back < 30, $"came back to {back:0} m from where it stood");
        }
    }
}
