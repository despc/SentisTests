using System;
using System.Collections;
using System.IO;
using System.Linq;
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
    /// SentisAi's ShuttlePilot flies the bots' combat shuttle (the 504-block hull they weld, SentisAi.Blueprints/
    /// CombatShuttle.sbc of the instance) whole: made 30 m over the ground by the stand's test place, its battery charged
    /// and its hydrogen tanks full, it lifts to 60 m over the ground, flies 300 m across and sets itself down on its
    /// landing gear. Passes when it stands within 25 m of the place with a gear locked. Every few seconds where it is,
    /// what the pilot says, the battery and the hydrogen.
    /// </summary>
    public sealed class ShuttleFlightScenario : TestScenario
    {
        public const string ScenarioName = "shuttle_flight";
        /// <summary>
        /// Up out of the planet's gravity and back (ShuttlePilot.Orbit, the first step to space): it lifts, climbs nose to
        /// the sky on its hydrogen till the gravity is under 0.05 g, comes back down tail first and sets itself down where it
        /// rose from. Passes when it was out of the gravity and stands within 60 m of the start.
        /// </summary>
        public const string OrbitName = "shuttle_orbit";
        private const string Prefix = "ShuttleFlight_";
        private readonly bool _orbit;
        public ShuttleFlightScenario(string name = ScenarioName) { _orbit = name == OrbitName; }
        public override string Name => _orbit ? OrbitName : ScenarioName;
        public override int TimeoutSeconds => _orbit ? 1800 : 420;

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            finally { base.Cleanup(); }
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var pilotType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisAi.Bots.ShuttlePilot", false)).FirstOrDefault(t => t != null);
            Check(pilotType != null, "no SentisAi.Bots.ShuttlePilot (SentisAi not loaded?)");
            var hullType = pilotType.Assembly.GetType("SentisAi.Bots.BlueprintHull");
            var plugin = pilotType.Assembly.GetType("SentisAi.SentisAiPlugin");
            var instance = plugin?.GetProperty("Instance")?.GetValue(null) ?? plugin?.GetField("Instance")?.GetValue(null);
            var storage = instance?.GetType().GetProperty("StoragePath")?.GetValue(instance) as string ?? "";
            var file = Path.Combine(storage, "SentisAi.Blueprints", "CombatShuttle.sbc");
            SkipUnless(File.Exists(file), "no SentisAi.Blueprints/CombatShuttle.sbc in the instance");
            var hull = hullType.GetMethod("Parse").Invoke(null, new object[] { File.ReadAllText(file).TrimStart('﻿') });
            var xml = (string)hullType.GetProperty("NativeGridXml").GetValue(hull);
            var plan = Sandbox.ModAPI.MyAPIGateway.Utilities.SerializeFromXML<MyObjectBuilder_CubeGrid>(xml);
            Check(plan != null && plan.CubeBlocks.Count > 400, "the shuttle's blueprint did not read");

            var anchor = WorldApi.PlanetAnchor("shuttle_flight");
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor.Translation);
            Check(planet != null, "no planet");
            var centre = planet.PositionComp.GetPosition();
            var probe = anchor.Translation + Vector3D.Normalize(anchor.Translation - centre) * 50 - Vector3D.CalculatePerpendicularVector(anchor.Translation - centre) * 700;
            var ground = planet.GetClosestSurfacePointGlobal(ref probe);
            var up = Vector3D.Normalize(ground - centre);
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var probe2 = _orbit ? ground : ground + side * 300;
            var target = planet.GetClosestSurfacePointGlobal(ref probe2);
            WorldApi.RevertTerrain(planet, ground, 60);
            WorldApi.RevertTerrain(planet, target, 60);
            // (a player by it: the freezer stands a grid still with no one near)
            FakeClients.RemoveAll();
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (ground + up * 3 + side * 150, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(4, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;

            MyEntities.RemapObjectBuilder(plan);
            plan.Name = plan.DisplayName = WorldApi.EntityPrefix + Prefix + "shuttle";
            plan.IsStatic = false;
            plan.CreatePhysics = true;
            plan.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.Enabled;
            var cockpitOb = plan.CubeBlocks.OfType<MyObjectBuilder_Cockpit>().FirstOrDefault(b => b.SubtypeName == "LargeBlockModularBridgeCockpit")
                            ?? plan.CubeBlocks.OfType<MyObjectBuilder_Cockpit>().First();
            foreach (var b in plan.CubeBlocks)
            {
                if (b is MyObjectBuilder_BatteryBlock cell) cell.CurrentStoredPower = 1e3f;
                if (b is MyObjectBuilder_GasTank tank) tank.FilledRatio = 1;
                b.Owner = WorldApi.TestIdentityId();
                b.BuiltBy = WorldApi.TestIdentityId();
            }
            // turned so its cockpit's up is the sky's
            var cockpitUp = Base6Directions.GetVector(cockpitOb.BlockOrientation.Up);
            var grid0 = MatrixD.CreateWorld(Vector3D.Zero, side, up);
            var upNow = Vector3D.TransformNormal((Vector3D)cockpitUp, grid0);
            var world = grid0 * MatrixD.CreateFromQuaternion(Quaternion.CreateFromTwoVectors((Vector3)upNow, (Vector3)up));
            world.Translation = ground + up * 30;
            plan.PositionAndOrientation = new MyPositionAndOrientation(world);
            plan.LinearVelocity = Vector3.Zero;
            plan.AngularVelocity = Vector3.Zero;
            var ship = WorldApi.SpawnGrid(plan);
            Track(ship);
            Check(ship != null, "the shuttle was not made");
            var made = Wait(() => ship.Physics != null, "the shuttle's physics", 10);
            while (made.MoveNext()) yield return made.Current;
            WorldApi.ChargeBatteries(ship);
            foreach (var f in ship.GetFatBlocks().OfType<Sandbox.ModAPI.IMyFunctionalBlock>()) if (!f.Enabled) f.Enabled = true;
            var cockpit = ship.GetFatBlocks().OfType<MyCockpit>().FirstOrDefault(c => c.BlockDefinition.Id.SubtypeName == "LargeBlockModularBridgeCockpit")
                          ?? ship.GetFatBlocks().OfType<MyCockpit>().First();
            var thrusters = ship.GetFatBlocks().OfType<Sandbox.ModAPI.IMyThrust>().ToList();
            var cells = ship.GetFatBlocks().OfType<Sandbox.ModAPI.IMyBatteryBlock>().ToList();
            var tanks = ship.GetFatBlocks().OfType<Sandbox.ModAPI.IMyGasTank>().ToList();
            string Power() => $"battery {cells.Sum(c => c.CurrentStoredPower):0.00} MWh, hydrogen {tanks.Where(t => t.BlockDefinition.SubtypeId.Contains("Hydrogen")).Sum(t => t.FilledRatio * t.Capacity) / 1000:0} kl";
            Note($"SHUTTLE | {ship.BlocksCount} blocks, {ship.Physics.Mass / 1000:0} t, thrusters working {thrusters.Count(t => t.IsWorking)} of {thrusters.Count}, " +
                 $"gyroscopes {ship.GetFatBlocks().OfType<Sandbox.ModAPI.IMyGyro>().Count()}, gears {ship.GetFatBlocks().OfType<SpaceEngineers.Game.ModAPI.IMyLandingGear>().Count()}; {Power()}");

            // (the player in its bridge, as a bot flies it: alone in the air 10 km over the player on the ground, the freezer
            // stood it still - shuttle_orbit hung at 10.5 km, 24.11.2026)
            // (let go, so the client's place follows its body in the seat: held, it stayed on the ground and the shuttle was
            // stood still all the same at 40.6 km)
            FakeClients.SetFree(0, true);
            var body = FakeClients.Character(0);
            if (body != null) cockpit.AttachPilot(body, -1);
            Note($"SHUTTLE | the player in its bridge: {cockpit.Pilot != null}");
            var pilot = Activator.CreateInstance(pilotType, ship, cockpit, target);
            if (_orbit) pilotType.GetField("Orbit").SetValue(pilot, true);
            var update = pilotType.GetMethod("Update");
            var state = pilotType.GetProperty("State");
            var done = pilotType.GetProperty("Done");
            var failed = pilotType.GetProperty("Failed");
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
                    var who = cockpit.Pilot;
                    var stats = who?.StatComp;
                    var life = who == null ? "no pilot" :
                        $"pilot: suit O2 {who.OxygenComponent?.SuitOxygenLevel * 100:0}%, helmet {(who.OxygenComponent?.HelmetEnabled == true ? "on" : "off")}, " +
                        $"health {stats?.Health?.Value:0}, radiation {(stats != null && stats.TryGetStat(Sandbox.Game.Components.MyCharacterStatComponent.RADIATION_ID, out var rad) ? rad.Value : 0):0}";
                    Note($"SHUTTLE | {(DateTime.UtcNow - began).TotalSeconds:0} s: {now}; speed {ship.Physics.LinearVelocity.Length():0.0} m/s, " +
                         $"tilt {MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(cockpit.WorldMatrix.Up, up), -1, 1))):0} deg; {Power()}; {life}");
                }
                if ((bool)failed.GetValue(pilot) || (bool)done.GetValue(pilot)) break;
                if (DateTime.UtcNow - began > TimeSpan.FromSeconds(TimeoutSeconds - 30)) { Note("SHUTTLE | out of time"); break; }
                yield return null;
            }
            if (cockpit.Pilot != null) cockpit.RemovePilot();
            var at = ship.Physics.CenterOfMassWorld;
            var off = Vector3D.Reject(at - target, up).Length();
            var locked = ship.GetFatBlocks().OfType<SpaceEngineers.Game.ModAPI.IMyLandingGear>().Count(g => g.IsLocked);
            Note($"SHUTTLE | end: {state.GetValue(pilot)}; {off:0} m from the place, gears locked {locked}; {Power()}");
            Check((bool)done.GetValue(pilot), $"not landed: {state.GetValue(pilot)}");
            if (_orbit)
            {
                Note($"SHUTTLE | highest {(double)pilotType.GetField("Highest").GetValue(pilot) / 1000:0.0} km, least gravity {(double)pilotType.GetField("LeastGravity").GetValue(pilot):0.000} g, " +
                     $"out of it {pilotType.GetField("ReachedSpace").GetValue(pilot)}");
                Check((bool)pilotType.GetField("ReachedSpace").GetValue(pilot), "did not get out of the gravity: " + pilotType.GetProperty("Why").GetValue(pilot));
            }
            Check(off < (_orbit ? 60 : 25), $"landed {off:0} m from the place");
            var gears = ship.GetFatBlocks().OfType<SpaceEngineers.Game.ModAPI.IMyLandingGear>().Count();
            // (the hull has no landing gear of its own: it stands on its hull)
            Check(gears == 0 || locked > 0, "no landing gear locked");
            Check(ship.Physics.LinearVelocity.Length() < 0.3, $"not standing: {ship.Physics.LinearVelocity.Length():0.0} m/s");
        }
    }
}
