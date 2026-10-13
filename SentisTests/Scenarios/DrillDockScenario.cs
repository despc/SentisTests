using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using Sandbox.Common.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// SentisAi's GridPilot flies the user's flying drill ("Атмосферный бур", from the plan the bots weld it by: SentisAi's
    /// Blueprints/DrillChassis.xml, the drill in its projector) to a base's connector and docks it there (12.10.2026). A
    /// small static base by the stand's test anchor - a large block, a battery and a large connector facing up on it - and
    /// the drill made 40 m off and 15 m up, its connector down, its batteries full. Passes when it is docked: the
    /// connectors connected. Every few seconds where it is and what the pilot says.
    /// </summary>
    public sealed class DrillDockScenario : TestScenario
    {
        public const string ScenarioName = "drill_dock";
        /// <summary>The same with a dig on the way (the pilot's nose-down dig, 13.10.2026): 30 m off on the other side, 4 m deep.</summary>
        public const string MineName = "drill_mine";
        /// <summary>A dig at a deposit of a real ore under the test place (MineAt): the ore found by its material, brought home.</summary>
        public const string OreName = "drill_mine_ore";
        private const string Prefix = "DrillDock_";
        private readonly string _name;
        public override string Name => _name;
        /// <summary>Where it digs first, how far off the connector on the other side (0: no dig).</summary>
        public double MineAway;
        public bool OreDig;

        public DrillDockScenario(string name = ScenarioName)
        {
            _name = name;
            if (name == MineName) { MineAway = 30; Wall = false; Up = 15; }
            if (name == OreName) { OreDig = true; Wall = false; Up = 15; }
        }
        public override int TimeoutSeconds => OreDig ? 900 : 420;

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

        /// <summary>Over the top of what the bases are built on, the drill's start: how far off and up.</summary>
        public double Away = 40, Up = 4;
        /// <summary>A wall between the drill and the base (the way round it to be found).</summary>
        public bool Wall = true;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var pilotType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisAi.Bots.GridPilot", false)).FirstOrDefault(t => t != null);
            Check(pilotType != null, "no SentisAi.Bots.GridPilot (SentisAi not loaded?)");
            var anchor = WorldApi.PlanetAnchor("drill_dock");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var probe = anchor.Translation + Vector3D.Normalize(anchor.Translation - centre) * 50 + Vector3D.CalculatePerpendicularVector(anchor.Translation - centre) * 300;
            var ground = planet.GetClosestSurfacePointGlobal(ref probe);
            var up = Vector3D.Normalize(ground - centre);
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            WorldApi.RevertTerrain(planet, ground, 80);
            // (a player by it: the freezer stands a grid still with no one near)
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (ground + up * 3 + side * 15, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(4, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            // the base: a block, a battery beside it, a large connector on the block facing up
            var baseOb = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "base", MyCubeSize.Large, true, ground + up * 1.5, Enumerable.Empty<BlockSpec>(), (Vector3)side, (Vector3)up);
            var block = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
            baseOb.CubeBlocks.Add(block);
            var battery = WorldApi.MakeBlockOb("LargeBlockBatteryBlock");
            battery.Min = new SerializableVector3I(1, 0, 0);
            baseOb.CubeBlocks.Add(battery);
            var connector = WorldApi.MakeBlockOb("Connector");
            connector.Min = new SerializableVector3I(0, 1, 0);
            connector.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Up, Base6Directions.Direction.Backward);
            baseOb.CubeBlocks.Add(connector);
            if (Wall)
                // (across the way from the drill: it starts 40 m off along the base's backward, which is the grid's +Z)
                for (var y = 0; y < 6; y++)
                for (var x = -3; x <= 3; x++)
                {
                    var wall = WorldApi.MakeBlockOb("LargeBlockArmorBlock");
                    wall.Min = new SerializableVector3I(x, y, 6);
                    baseOb.CubeBlocks.Add(wall);
                }
            var home = WorldApi.SpawnGrid(baseOb);
            Track(home);
            WorldApi.ChargeBatteries(home);
            var dock = home.GetFatBlocks().OfType<MyShipConnector>().FirstOrDefault();
            Check(dock != null, "no connector on the base");

            // the drill, from the plan the bots weld it by
            var plan = LoadDrillPlan(pilotType.Assembly);
            Check(plan != null, "no drill in SentisAi's Blueprints/DrillChassis.xml");
            MyEntities.RemapObjectBuilder(plan);
            plan.Name = plan.DisplayName = WorldApi.EntityPrefix + Prefix + "drill";
            plan.IsStatic = false;
            plan.CreatePhysics = true;
            plan.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.Enabled;
            var connectorOb = plan.CubeBlocks.OfType<MyObjectBuilder_ShipConnector>().First();
            var down = Base6Directions.GetVector(connectorOb.BlockOrientation.Forward);
            foreach (var b in plan.CubeBlocks)
            {
                if (b is MyObjectBuilder_BatteryBlock cell) cell.CurrentStoredPower = 1e3f;
                b.Owner = WorldApi.TestIdentityId();
                b.BuiltBy = WorldApi.TestIdentityId();
            }
            // turned so its connector faces the ground, 40 m off the base (behind the wall) and 15 m up
            var start = dock.PositionComp.GetPosition() - side * Away + up * Up;
            var grid0 = MatrixD.CreateWorld(Vector3D.Zero, side, up);
            var downNow = Vector3D.TransformNormal((Vector3D)down, grid0);
            var turn = MatrixD.CreateFromQuaternion(Quaternion.CreateFromTwoVectors((Vector3)downNow, (Vector3)(-up)));
            var world = grid0 * turn;
            world.Translation = start;
            plan.PositionAndOrientation = new MyPositionAndOrientation(world);
            plan.LinearVelocity = Vector3.Zero;
            plan.AngularVelocity = Vector3.Zero;
            var drill = WorldApi.SpawnGrid(plan);
            Track(drill);
            Check(drill != null, "the drill was not made");
            var made = Wait(() => drill.Physics != null, "the drill's physics", 10);
            while (made.MoveNext()) yield return made.Current;
            WorldApi.ChargeBatteries(drill);
            foreach (var f in drill.GetFatBlocks().OfType<Sandbox.ModAPI.IMyFunctionalBlock>()) if (!f.Enabled) f.Enabled = true;
            var thrusters = drill.GetFatBlocks().OfType<Sandbox.ModAPI.IMyThrust>().ToList();
            var cells = drill.GetFatBlocks().OfType<Sandbox.ModAPI.IMyBatteryBlock>().ToList();
            Note($"DRILL DOCK | thrusters working {thrusters.Count(t => t.IsWorking)} of {thrusters.Count} (functional {thrusters.Count(t => t.IsFunctional)}), " +
                 $"batteries {cells.Sum(c => c.CurrentStoredPower):0.00} MWh, working {cells.Count(c => c.IsWorking)}");
            var own = drill.GetFatBlocks().OfType<MyShipConnector>().FirstOrDefault();
            Check(own != null, "no connector on the drill");
            Note($"DRILL DOCK | drill {drill.BlocksCount} blocks, {drill.Physics.Mass:0} kg, {drill.GetFatBlocks().OfType<Sandbox.ModAPI.IMyThrust>().Count()} thrusters, " +
                 $"{drill.GetFatBlocks().OfType<Sandbox.ModAPI.IMyGyro>().Count()} gyroscopes; {Vector3D.Distance(drill.Physics.CenterOfMassWorld, dock.PositionComp.GetPosition()):0} m from the base's connector");

            var pilot = Activator.CreateInstance(pilotType, drill, own, dock);
            var update = pilotType.GetMethod("Update");
            var state = pilotType.GetProperty("State");
            var done = pilotType.GetProperty("Done");
            var failed = pilotType.GetProperty("Failed");
            Note($"DRILL DOCK | lift {pilotType.GetMethod("LiftRatio").Invoke(pilot, null):0.00} of its weight");
            string oreName = null;
            if (OreDig)
            {
                // an ore under the ground round the place, by the voxels' material (not stone, not ice), nearest first
                var basis = dock.PositionComp.GetPosition();
                var other = Vector3D.Cross(up, side);
                Vector3D? deposit = null;
                for (var r = 10; r <= 600 && deposit == null; r += 10)
                for (var a = 0; a < 36 && deposit == null; a++)
                {
                    var flat = basis + (side * Math.Cos(a * Math.PI / 18) + other * Math.Sin(a * Math.PI / 18)) * r;
                    var top = planet.GetClosestSurfacePointGlobal(ref flat);
                    for (var depth = 3; depth <= 30 && deposit == null; depth += 3)
                    {
                        var point = top - up * depth;
                        var ore = planet.GetMaterialAt(ref point)?.MinedOre;
                        if (ore != null && ore != "Stone" && ore != "Ice") { deposit = point; oreName = ore; }
                    }
                }
                SkipUnless(deposit != null, "no ore under the ground within 600 m of the test place");
                var dep = deposit.Value;
                var surface = planet.GetClosestSurfacePointGlobal(ref dep);
                pilotType.GetMethod("MineAt").Invoke(pilot, new object[] { dep, surface, oreName, 3.0 });
                Note($"DRILL DOCK | digs {oreName} {Vector3D.Distance(dep, surface):0} m under the ground, {Vector3D.Distance(dep, basis):0} m off the connector");
            }
            if (MineAway > 0)
            {
                var probe2 = dock.PositionComp.GetPosition() + side * MineAway;
                var dig = planet.GetClosestSurfacePointGlobal(ref probe2);
                pilotType.GetMethod("MineFirst").Invoke(pilot, new object[] { dig, 4.0 });
                Note($"DRILL DOCK | digs first {Vector3D.Distance(dig, dock.PositionComp.GetPosition()):0} m off the connector, 4 m deep");
            }
            var said = "";
            var began = DateTime.UtcNow;
            var lastNote = DateTime.MinValue;
            while (true)
            {
                update.Invoke(pilot, null);
                var now = (string)state.GetValue(pilot);
                var stage = now.Split(':', ',')[0];
                if (stage != said || DateTime.UtcNow - lastNote > TimeSpan.FromSeconds(10))
                {
                    said = stage;
                    lastNote = DateTime.UtcNow;
                    Note($"DRILL DOCK | {(DateTime.UtcNow - began).TotalSeconds:0} s: {now}; speed {drill.Physics.LinearVelocity.Length():0.0} m/s, " +
                         $"tilt {MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(-own.WorldMatrix.Forward, up), -1, 1))):0} deg");
                }
                if ((bool)failed.GetValue(pilot)) break;
                if ((bool)done.GetValue(pilot)) break;
                if (DateTime.UtcNow - began > TimeSpan.FromSeconds(TimeoutSeconds - 20)) { Note("DRILL DOCK | out of time"); break; }
                yield return null;
            }
            var status = ((Sandbox.ModAPI.Ingame.IMyShipConnector)own).Status;
            Note($"DRILL DOCK | end: {state.GetValue(pilot)}; connector {status}");
            Check(status == Sandbox.ModAPI.Ingame.MyShipConnectorStatus.Connected, $"not docked: {state.GetValue(pilot)}");
            if (oreName != null)
            {
                var got = (double)pilotType.GetMethod("OreOf").Invoke(pilot, new object[] { oreName });
                var all = (double)pilotType.GetProperty("Ore").GetValue(pilot);
                Note($"DRILL DOCK | {oreName} brought: {got:0} kg of {all:0} kg in all");
                Check(got > 10, $"brought {got:0} kg of {oreName}");
            }
            if (MineAway > 0)
            {
                var ore = (double)pilotType.GetProperty("Ore").GetValue(pilot);
                Note($"DRILL DOCK | ore brought: {ore:0} kg");
                Check(ore > 50, $"brought {ore:0} kg of ore");
            }
        }

        private static MyObjectBuilder_CubeGrid LoadDrillPlan(Assembly sentisAi)
        {
            using (var stream = sentisAi.GetManifestResourceStream("Blueprints/DrillChassis.xml"))
            {
                if (stream == null) return null;
                using (var reader = new System.IO.StreamReader(stream))
                {
                    var chassis = Sandbox.ModAPI.MyAPIGateway.Utilities.SerializeFromXML<MyObjectBuilder_CubeGrid>(reader.ReadToEnd());
                    var projector = chassis.CubeBlocks.OfType<MyObjectBuilder_ProjectorBase>().FirstOrDefault();
                    return projector?.ProjectedGrids?.FirstOrDefault()?.Clone() as MyObjectBuilder_CubeGrid;
                }
            }
        }
    }
}
