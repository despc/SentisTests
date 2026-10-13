using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A bot flies its flying drill to its base's connector by itself (12.10.2026): a welded drill (the plan the bots weld it
    /// by) put by "База Cedar" as built by [BOT] Cedar - 30 m off the base's middle, on its feet in the air over the ground -
    /// and watched: the bot walks to it, sits in its cockpit, flies it and docks it on the base's connector. Live, on the
    /// stand's Cedar; the drill taken away after (Cedar then goes on welding its own). Needs the base's connector up.
    /// </summary>
    public sealed class BotDrillFlyScenario : TestScenario
    {
        public const string ScenarioName = "bot_drill_fly";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        public override IEnumerator Run()
        {
            var home = MyEntities.GetEntities().OfType<MyCubeGrid>().FirstOrDefault(g => g.DisplayName == "База Cedar");
            Check(home != null, "no 'База Cedar'");
            var dock = home.GetFatBlocks().OfType<MyShipConnector>().FirstOrDefault(c => c.BlockDefinition.Id.SubtypeName == "Connector" && c.IsFunctional);
            SkipUnless(dock != null, "База Cedar has no connector up yet");
            var identity = Sandbox.Game.World.MySession.Static.Players.GetAllIdentities().FirstOrDefault(i => i.DisplayName == "[BOT] Cedar");
            Check(identity != null, "no [BOT] Cedar");
            var planet = MyGamePruningStructure.GetClosestPlanet(home.PositionComp.GetPosition());
            var middle = home.PositionComp.WorldAABB.Center;
            // (round the bot itself: the way to a drill across the base's dug ground took it nine minutes)
            var bot = Sandbox.Game.World.MySession.Static.Players.GetOnlinePlayers().FirstOrDefault(p => p.Identity?.IdentityId == identity.IdentityId)?.Character;
            var round = bot?.PositionComp.GetPosition() ?? middle;
            var up = Vector3D.Normalize(middle - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));

            var pilotType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisAi.Bots.GridPilot", false)).FirstOrDefault(t => t != null);
            Check(pilotType != null, "SentisAi not loaded");
            MyObjectBuilder_CubeGrid plan;
            using (var stream = pilotType.Assembly.GetManifestResourceStream("Blueprints/DrillChassis.xml"))
            using (var reader = new System.IO.StreamReader(stream))
                plan = (MyObjectBuilder_CubeGrid)Sandbox.ModAPI.MyAPIGateway.Utilities.SerializeFromXML<MyObjectBuilder_CubeGrid>(reader.ReadToEnd())
                    .CubeBlocks.OfType<MyObjectBuilder_ProjectorBase>().First().ProjectedGrids.First().Clone();
            MyEntities.RemapObjectBuilder(plan);
            plan.Name = plan.DisplayName = WorldApi.EntityPrefix + "BotDrillFly_drill";
            plan.IsStatic = false;
            plan.CreatePhysics = true;
            plan.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.Enabled;
            foreach (var b in plan.CubeBlocks)
            {
                b.Owner = identity.IdentityId;
                b.BuiltBy = identity.IdentityId;
                if (b is MyObjectBuilder_BatteryBlock cell) cell.CurrentStoredPower = 1e3f;
            }
            var down = Base6Directions.GetVector(plan.CubeBlocks.OfType<MyObjectBuilder_ShipConnector>().First().BlockOrientation.Forward);
            // a place 30 m off the base's middle, the way with the most air over the ground
            Vector3D spot = Vector3D.Zero;
            for (var r = 12; r <= 70 && spot == Vector3D.Zero; r += 6)
            for (var k = 0; k < 16 && spot == Vector3D.Zero; k++)
            {
                var dir = Vector3D.Transform(side, QuaternionD.CreateFromAxisAngle(up, Math.PI / 8 * k));
                var probe = round + dir * r;
                var ground = planet.GetClosestSurfacePointGlobal(ref probe);
                var at = ground + up * 4;
                var sphere = new BoundingSphereD(at, 9);
                // (blocks in it, not boxes: the base's, the rig's and the chassis's boxes take in all round it)
                var inside = new System.Collections.Generic.HashSet<Sandbox.Game.Entities.Cube.MySlimBlock>();
                if (MyEntities.GetEntitiesInSphere(ref sphere).OfType<MyCubeGrid>().Any(g => { inside.Clear(); g.GetBlocksInsideSphere(ref sphere, inside); return inside.Count > 0; })) continue;
                spot = at;
            }
            Check(spot != Vector3D.Zero, "no clear place by the base");
            var grid0 = MatrixD.CreateWorld(Vector3D.Zero, side, up);
            var turn = MatrixD.CreateFromQuaternion(Quaternion.CreateFromTwoVectors((Vector3)Vector3D.TransformNormal((Vector3D)down, grid0), (Vector3)(-up)));
            var world = grid0 * turn;
            world.Translation = spot;
            plan.PositionAndOrientation = new MyPositionAndOrientation(world);
            var drill = WorldApi.SpawnGrid(plan);
            Track(drill);
            var made = Wait(() => drill.Physics != null, "the drill's physics", 10);
            while (made.MoveNext()) yield return made.Current;
            WorldApi.ChargeBatteries(drill);
            foreach (var f in drill.GetFatBlocks().OfType<Sandbox.ModAPI.IMyFunctionalBlock>()) if (!f.Enabled && !(f is Sandbox.ModAPI.IMyShipDrill)) f.Enabled = true;
            var own = drill.GetFatBlocks().OfType<MyShipConnector>().First();
            Note($"BOT DRILL FLY | the drill put {Vector3D.Distance(drill.PositionComp.GetPosition(), dock.PositionComp.GetPosition()):0} m from the base's connector; waiting for Cedar to fly it");

            var began = DateTime.UtcNow;
            var noted = DateTime.MinValue;
            while (DateTime.UtcNow - began < TimeSpan.FromSeconds(TimeoutSeconds - 20))
            {
                var status = ((Sandbox.ModAPI.Ingame.IMyShipConnector)own).Status;
                if (status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connected) break;
                if (DateTime.UtcNow - noted > TimeSpan.FromSeconds(15))
                {
                    noted = DateTime.UtcNow;
                    var pilot = drill.GetFatBlocks().OfType<MyCockpit>().FirstOrDefault()?.Pilot?.DisplayName ?? "nobody";
                    Note($"BOT DRILL FLY | {(DateTime.UtcNow - began).TotalSeconds:0} s: in the cockpit {pilot}; {Vector3D.Distance(own.PositionComp.GetPosition(), dock.PositionComp.GetPosition()):0.0} m from the connector, {drill.Physics.LinearVelocity.Length():0.0} m/s, {status}");
                }
                yield return null;
            }
            var end = ((Sandbox.ModAPI.Ingame.IMyShipConnector)own).Status;
            Note($"BOT DRILL FLY | end: {end}, {(DateTime.UtcNow - began).TotalSeconds:0} s");
            Check(end == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connected, "the bot did not dock the drill");
            // (a little longer: the bot out of the seat)
            var settle = WaitForSeconds(5, "the bot gets out");
            while (settle.MoveNext()) yield return settle.Current;
        }
    }
}
