using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The plugins' passes over the world, moved off one frame or off the whole world. Each one is driven here
    /// on this scenario's grids only - nothing that acts on the whole stand is switched on, except the grinder
    /// radius, which is a live setting put back after:
    /// <list type="bullet">
    /// <item>SentisAdventures' contraband beacons: a pass goes from the beacons. A grid of a player in a beacon's
    /// range is taken in, one out of range is not, one that has left is forgotten; its cost against the old pass
    /// (every grid asking every player) with <see cref="FarGrids"/> grids in the world;</item>
    /// <item>SentisOptimisations' freezer: what it learns of a grid's blocks is kept; a group's look with and
    /// without it;</item>
    /// <item>SentisGameplayImprovements: a block nobody owns is switched off, a grid with the game's name gets
    /// its owner's; the grinder radius reaches every grinder within a few frames.</item>
    /// </list>
    /// </summary>
    internal sealed class WorldSweepsScenario : TestScenario
    {
        public const string ScenarioName = "world_sweeps";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 240;

        private const string Prefix = "sweep-";
        private const int FarGrids = 2000;
        private const int Spitfires = 10;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private readonly List<string> _failures = new List<string>();
        private readonly List<string> _results = new List<string>();
        private Vector3D _up, _side, _forward;
        private object _contraband;
        private MyBeacon _beacon;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            _up = Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            _side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(_up));
            _forward = Vector3D.Cross(_side, _up);
            Vector3D centre = default;
            for (var d = 100000.0; d <= 1000000; d += 50000)
            {
                centre = anchor + _up * d - _forward * 20000;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(centre).Length() < 0.001f) break;
            }

            FakeClients.RemoveAll();
            FakeClients.Add(1, Network, p => (centre + _up * 100, 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the player arrives");
            while (arrive.MoveNext()) yield return arrive.Current;
            var player = FakeClients.Character(0)?.GetPlayerIdentityId() ?? 0;
            Check(player != 0, "the fake player has no identity");

            var contraband = Contraband(centre, player);
            while (contraband.MoveNext()) yield return contraband.Current;
            var freezer = Freezer(centre + _side * 30000);
            while (freezer.MoveNext()) yield return freezer.Current;
            var sweep = Sweep(centre + _forward * 2000);
            while (sweep.MoveNext()) yield return sweep.Current;

            Note("WORLD SWEEPS RESULT: " + string.Join(" || ", _results));
            Check(_failures.Count == 0, string.Join("; ", _failures));
        }

        // ------------------------------------------------------------------ contraband

        private IEnumerator Contraband(Vector3D centre, long player)
        {
            var behaviorType = PluginType("SentisAdventures.Adventures.Contraband.ContrabandGridPositionBehavior");
            Check(behaviorType != null, "SentisAdventures is not loaded");
            _contraband = behaviorType.GetField("Instance", Any).GetValue(null);
            var beacons = (IEnumerable)behaviorType.GetProperty("ContrabandBeacons").GetValue(_contraband);
            var process = behaviorType.GetMethod("ProcessBeacons", Any);
            var inRadius = (IDictionary)behaviorType.GetField("_inRadius", Any).GetValue(_contraband);

            var beaconGrid = Grid("beacon", centre, player, "LargeBlockBeacon");
            var near = Grid("near", centre + _side * 200, player, "LargeBlockBatteryBlock");
            var far = Grid("far", centre + _side * 5000, player, "LargeBlockBatteryBlock");
            for (var i = 0; i < FarGrids; i++)
            {
                // spread far and wide, none in the beacon's range
                var at = centre + _side * (20000 + 60 * (i % 50)) + _forward * (60 * (i / 50));
                Grid("far-" + i, at, player, null);
                if (i % 100 == 99) yield return null;
            }
            var settle = WaitForSeconds(2, "the grids settle");
            while (settle.MoveNext()) yield return settle.Current;

            _beacon = WorldApi.FindFunctional<MyBeacon>(beaconGrid);
            ((Sandbox.ModAPI.Ingame.IMyBeacon)_beacon).Radius = 1000;
            _beacon.CustomData = "{\"MinBlockCount\": 1, \"MinutesBeforeScan\": 15, \"Blueprints\": [], \"ContrabandItems\": []}";
            beacons.GetType().GetMethod("Add").Invoke(beacons, new object[] { _beacon });

            process.Invoke(_contraband, new object[] { DateTime.Now });
            var known = (IDictionary)inRadius[_beacon];
            Expect("contraband: the player's grid in the beacon's range is taken in", known != null && known.Contains(near.EntityId));
            Expect("contraband: the grid out of range is not", known == null || !known.Contains(far.EntityId));

            var away = near.WorldMatrix;
            away.Translation = centre + _side * 3000;
            near.Teleport(away);
            yield return null;
            process.Invoke(_contraband, new object[] { DateTime.Now });
            Expect("contraband: the grid that has left the range is forgotten", !((IDictionary)inRadius[_beacon]).Contains(near.EntityId));

            // the cost: the pass now, and the pass as it was - every grid asking every player, then every beacon
            var nowMs = new List<double>();
            var oldMs = new List<double>();
            for (var round = 0; round < 5; round++)
            {
                var at = Stopwatch.GetTimestamp();
                process.Invoke(_contraband, new object[] { DateTime.Now });
                nowMs.Add(Ms(at));
                at = Stopwatch.GetTimestamp();
                OldPass(new[] { _beacon });
                oldMs.Add(Ms(at));
                yield return null;
            }
            var grids = MyEntities.GetEntities().OfType<MyCubeGrid>().Count();
            var players = MyAPIGateway.Players.Count;
            Report("contraband pass with " + grids + " grids and " + players + " players: now avg " + nowMs.Average().ToString("F3") + " ms, as it was " +
                   oldMs.Average().ToString("F3") + " ms (max " + oldMs.Max().ToString("F3") + ")");
        }

        /// <summary>The old contraband pass, minus the contraband logic: every grid, the players within 15 km of it, then the beacons.</summary>
        private static int OldPass(MyBeacon[] beacons)
        {
            var hits = 0;
            foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
            {
                var position = grid.PositionComp.GetPosition();
                var players = new List<IMyPlayer>();
                MyAPIGateway.Players.GetPlayers(players);
                var near = new List<IMyPlayer>();
                foreach (var p in players)
                    if (Vector3D.Distance(p.GetPosition(), position) < 15000) near.Add(p);
                if (near.Count == 0) continue;
                foreach (var beacon in beacons.ToList())
                    if (Vector3D.Distance(position, beacon.PositionComp.GetPosition()) <= beacon.RadioBroadcaster.BroadcastRadius) hits++;
            }
            return hits;
        }

        // ------------------------------------------------------------------ freezer

        private IEnumerator Freezer(Vector3D at)
        {
            var freezeLogic = PluginType("SentisOptimisationsPlugin.Freezer.FreezeLogic");
            Check(freezeLogic != null, "SentisOptimisations is not loaded");
            var scan = freezeLogic.GetMethod("Scan", Any);
            var scans = (IDictionary)freezeLogic.GetField("Scans", Any).GetValue(null);
            Check(scan != null && scans != null, "the freezer has no Scan or Scans");

            var ships = new List<MyCubeGrid>();
            for (var i = 0; i < Spitfires; i++)
            {
                var ob = WorldApi.LoadGridTemplate("SentisTests.Resources.Spitfire.xml", WorldApi.EntityPrefix + Prefix + "spitfire-" + i,
                    at + _side * (300 * i), _forward, _up);
                MyAPIGateway.Entities.RemapObjectBuilder(ob);
                var ship = WorldApi.SpawnGrid(ob);
                Track(ship);
                ships.Add(ship);
                yield return null;
            }
            var settle = WaitForSeconds(2, "the ships settle");
            while (settle.MoveNext()) yield return settle.Current;

            const string setting = "LargeBlockSmallContainer_admin2:LargeBlockSmallContainer_admin";
            var subtypes = setting.Split(':');
            foreach (var ship in ships) scans.Remove(ship.EntityId);
            var at0 = Stopwatch.GetTimestamp();
            foreach (var ship in ships) scan.Invoke(null, new object[] { ship, setting, subtypes });
            var fresh = Ms(at0) / ships.Count;
            at0 = Stopwatch.GetTimestamp();
            for (var round = 0; round < 10; round++)
                foreach (var ship in ships) scan.Invoke(null, new object[] { ship, setting, subtypes });
            var kept = Ms(at0) / (ships.Count * 10);
            Expect("freezer: a grid's look is kept", ships.All(s => scans.Contains(s.EntityId)));
            Report("freezer, a Spitfire's blocks looked at: " + fresh.ToString("F3") + " ms; kept: " + kept.ToString("F4") + " ms (twice a second for every group away from the players)");
        }

        // ------------------------------------------------------------------ SentisGameplayImprovements sweeps

        private IEnumerator Sweep(Vector3D at)
        {
            var sweepType = PluginType("SentisGameplayImprovements.BackgroundActions.GridSweep");
            var renamerType = PluginType("SentisGameplayImprovements.BackgroundActions.GridAutoRenamer");
            Check(sweepType != null && renamerType != null, "SentisGameplayImprovements is not loaded");

            // nobody owns it, and it has the game's name
            var unowned = Grid("unowned", at, 0, "LargeBlockBatteryBlock");
            unowned.DisplayName = "Large Grid 4242";
            var grinderGrid = Grid("grinder", at + _side * 100, 0, "LargeShipGrinder");
            yield return null;
            var battery = WorldApi.FindFunctional<MyBatteryBlock>(unowned);
            Expect("sweep: the unowned battery starts on", battery.Enabled);
            sweepType.GetMethod("SwitchOffUnowned", Any).Invoke(null, new object[] { unowned });
            Expect("sweep: the unowned battery is off", !battery.Enabled);

            var renamer = Activator.CreateInstance(renamerType);
            renamerType.GetMethod("CheckAndRename", Any).Invoke(renamer, new object[] { unowned });
            Expect("sweep: the grid with the game's name is renamed (" + unowned.DisplayName + ")", !unowned.DisplayName.Contains("Large Grid"));

            // the grinder radius: a live setting, spread over frames
            var grinder = WorldApi.FindFunctional<MyShipGrinder>(grinderGrid);
            var sensor = ((Sandbox.Definitions.MyShipToolDefinition)grinder.BlockDefinition).SensorRadius;
            _gameplay.Set("GrinderRadiusMultiplier", 2f);
            var frames = 0;
            while (Math.Abs(grinder.DetectorSphere.Radius - sensor * 2) > 0.01 && frames < 120)
            {
                frames++;
                yield return null;
            }
            Expect("sweep: the grinder radius x2 reached the grinder, in " + frames + " frames", Math.Abs(grinder.DetectorSphere.Radius - sensor * 2) <= 0.01);
            _gameplay.Restore();
            frames = 0;
            while (Math.Abs(grinder.DetectorSphere.Radius - sensor) > 0.01 && frames < 120)
            {
                frames++;
                yield return null;
            }
            Expect("sweep: the grinder radius is back to x1", Math.Abs(grinder.DetectorSphere.Radius - sensor) <= 0.01);
        }

        // ------------------------------------------------------------------ helpers

        private MyCubeGrid Grid(string name, Vector3D at, long owner, string extra)
        {
            MyObjectBuilder_CubeBlock Block(string subtype, int x, int y, int z)
            {
                var block = WorldApi.MakeBlockOb(subtype);
                block.Min = new SerializableVector3I(x, y, z);
                block.Owner = owner;
                block.BuiltBy = owner;
                if (block is MyObjectBuilder_BatteryBlock battery) battery.CurrentStoredPower = 3f;
                return block;
            }

            var blocks = new List<MyObjectBuilder_CubeBlock> { Block("LargeBlockArmorBlock", 0, 0, 0) };
            if (extra != null) blocks.Add(Block(extra, 0, 1, 0));
            var grid = WorldApi.SpawnGrid(new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, _forward, _up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            });
            Track(grid);
            return grid;
        }

        private static Type PluginType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

        private static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

        private void Expect(string what, bool ok)
        {
            Note((ok ? "" : "FAIL ") + what);
            if (!ok) _failures.Add(what);
        }

        private void Report(string line)
        {
            Note(line);
            _results.Add(line);
        }

        public override void Cleanup()
        {
            try
            {
                if (_contraband != null && _beacon != null)
                {
                    var beacons = _contraband.GetType().GetProperty("ContrabandBeacons").GetValue(_contraband);
                    beacons.GetType().GetMethod("Remove").Invoke(beacons, new object[] { _beacon });
                }
                _gameplay.Restore();
                FakeClients.RemoveAll();
            }
            catch (Exception e)
            {
                Log.Warn("cleaning up after the test failed: " + e.Message);
            }
            finally { base.Cleanup(); }
        }
    }
}
