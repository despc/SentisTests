using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// A load for SentisClusters' detaching: heavy clusters of the physics where turrets aim, and light ones.
    ///
    /// A heavy cluster is copies of Spitfire - the ship welder_perf welds, about 43 turrets on it - dynamic, 250 m apart,
    /// with pirates' targets round them (armor, a battery, a container: something to aim at) 600 m off, the targets'
    /// blocks indestructible. The turrets have no ammunition unless <c>ammo=1</c>: they search and turn, they do not
    /// shoot (the shots are the game thread's, MyProjectiles, and would load the frame, not the cluster). A light cluster
    /// is a fake player alone. The clusters stand 30 km apart along the world's X axis (the game parts clusters only along
    /// the world's axes), a fake player by each heavy one too. The run's focus sets the size: <c>clusters=N ships=M
    /// targets=K ammo=1 hold=S</c>. The load stays S s and goes.
    ///
    /// <c>tools/detach_speed.py</c> runs it and reads SentisClusters' web view with detaching off and on: off, the heavy
    /// clusters slow the server; on, only they should slow down, the light ones and the server keep 1.
    /// </summary>
    internal sealed class TurretDetachScenario : TestScenario
    {
        public const string ScenarioName = "turret_detach";
        private const string Prefix = "turret-detach-";
        private const string Spitfire = "SentisTests.Resources.Spitfire.xml";
        public const int LightClusters = 3;
        private const double ClustersApart = 30000;
        private const double TargetRing = 600;
        private const int InFlight = 8;
        private const int WarmupSeconds = 30;
        private const int ClosePerFrame = 10;

        private readonly ConfigOverride _config = new ConfigOverride();
        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private uint? _autoSave;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 3600;

        private static int Arg(string name, int otherwise)
        {
            var m = Regex.Match(TestRunner.Focus ?? "", @"\b" + name + @"=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : otherwise;
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var clusters = Arg("clusters", 3);
            var ships = Arg("ships", 30);
            var targets = Arg("targets", 20);
            var ammo = Arg("ammo", 0) != 0;
            var holdSeconds = Arg("hold", 400);
            // the physics' load: the ships closer (spacing=40), turning (spin=1, rad/s) and drawn to their clump's middle
            // every 2 s (pull=1): they keep hitting each other, their blocks indestructible
            var spacing = Arg("spacing", 250);
            var spin = Arg("spin", 0);
            var pull = Arg("pull", 0) != 0;
            _autoSave = MySession.Static.Settings.AutoSaveInMinutes;
            MySession.Static.Settings.AutoSaveInMinutes = 0;
            _config.Set("FreezerEnabled", false);

            // far out of every planet's gravity, away from the regression's sites and the load test's place
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + "regression-anchor").Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            var up = planet == null ? Vector3D.Up : Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            var origin = anchor;
            for (var d = 200000.0; d <= 2000000; d += 50000)
            {
                origin = anchor + up * d - Vector3D.Forward * 200000;
                var clear = true;
                for (var c = 0; c < clusters + LightClusters && clear; c++)
                    clear = MyGravityProviderSystem.CalculateNaturalGravityInPoint(Centre(origin, c)).Length() < 0.001f;
                if (clear) break;
            }

            // ------------------------------------------------------------- the ships, built on the game's workers
            var template = WorldApi.LoadGridTemplate(Spitfire, WorldApi.EntityPrefix + Prefix + "template", origin, Vector3.Forward, Vector3.Up);
            if (ammo) Note("ammunition: " + Stock(template));
            var built = new System.Collections.Concurrent.ConcurrentQueue<MyCubeGrid>();
            var inFlight = 0;
            var next = 0;
            var arrived = 0;
            var total = clusters * ships;
            var side = (int)Math.Ceiling(Math.Sqrt(ships));
            var spawn = System.Diagnostics.Stopwatch.StartNew();
            while (arrived < total)
            {
                while (built.TryDequeue(out var ship))
                {
                    arrived++;
                    Interlocked.Decrement(ref inFlight);
                    if (ship == null) continue;
                    MyEntities.Add(ship);
                    Track(ship);
                    _grids.Add(ship);
                }
                while (next < total && Volatile.Read(ref inFlight) < InFlight)
                {
                    var cluster = next / ships;
                    var k = next % ships;
                    // a square along the world's X and Z: one clump
                    var position = Centre(origin, cluster) + Vector3D.Right * ((k % side - (side - 1) / 2.0) * spacing) +
                                   Vector3D.Backward * ((k / side - (side - 1) / 2.0) * spacing);
                    var ob = (MyObjectBuilder_CubeGrid)template.Clone();
                    ob.Name = WorldApi.EntityPrefix + Prefix + next;
                    ob.DisplayName = ob.Name;
                    ob.PositionAndOrientation = new MyPositionAndOrientation(position, Vector3.Forward, Vector3.Up);
                    MyAPIGateway.Entities.RemapObjectBuilder(ob);
                    Interlocked.Increment(ref inFlight);
                    MyAPIGateway.Entities.CreateFromObjectBuilderParallel(ob, false, entity => built.Enqueue(entity as MyCubeGrid));
                    next++;
                }
                if (spawn.Elapsed.TotalSeconds > 600) throw new ScenarioFailedException("the ships did not come in 600 s: " + arrived + " of " + total);
                yield return null;
            }
            var turrets = _grids.Sum(g => g.GetFatBlocks().OfType<MyLargeTurretBase>().Count());
            for (var i = 0; i < _grids.Count; i++)
            {
                _grids[i].DestructibleBlocks = false;
                if (spin > 0 && _grids[i].Physics != null)
                    _grids[i].Physics.AngularVelocity = new Vector3(spin * (i % 3 == 0 ? 1 : -0.6f), spin * (i % 2 == 0 ? 0.7f : -0.8f), spin * 0.5f);
            }

            // ------------------------------------------------------------- the pirates' targets round every clump
            var pirates = MyPirateAntennas.GetPiratesId();
            Check(pirates != 0, "no pirates' identity in this world");
            var rng = new Random(1);
            for (var c = 0; c < clusters; c++)
                for (var t = 0; t < targets; t++)
                {
                    var angle = t * 2 * Math.PI / targets;
                    var at = Centre(origin, c) + Vector3D.Right * (Math.Cos(angle) * TargetRing) + Vector3D.Backward * (Math.Sin(angle) * TargetRing) +
                             Vector3D.Up * ((rng.NextDouble() - 0.5) * 300);
                    var target = WorldApi.SpawnGrid(Target(Prefix + "target-" + c + "-" + t, at, pirates));
                    target.DestructibleBlocks = false;
                    Track(target);
                    _grids.Add(target);
                }
            Note(_grids.Count + " grids in " + clusters + " heavy clusters: " + total + " Spitfires with " + turrets + " turrets (" +
                 (ammo ? "with ammunition" : "no ammunition") + "), " + clusters * targets + " pirates' targets; spawned in " +
                 spawn.Elapsed.TotalSeconds.ToString("F0") + " s");

            // ------------------------------------------------------------- a fake player by every cluster
            FakeClients.RemoveAll();
            FakeClients.Add(clusters + LightClusters, new FakeClients.NetworkProfile { RttMs = 50 },
                p => (Centre(origin, p) + Vector3D.Up * (p < clusters ? 400 : 0), 0, 0), withCharacters: true);
            var warmup = WaitForSeconds(WarmupSeconds, "the players arrive, the turrets find their targets");
            while (warmup.MoveNext()) yield return warmup.Current;
            Note("DETACH LOAD READY | heavy clusters at " + string.Join(", ", Enumerable.Range(0, clusters).Select(c => Km(Centre(origin, c)))) +
                 "; light at " + string.Join(", ", Enumerable.Range(clusters, LightClusters).Select(c => Km(Centre(origin, c)))) +
                 "; holding " + holdSeconds + " s");

            var fleet = _grids.Where(g => !(g.DisplayName ?? "").Contains("target")).ToList();
            var held = System.Diagnostics.Stopwatch.StartNew();
            var lastPull = 0.0;
            while (held.Elapsed.TotalSeconds < holdSeconds)
            {
                if (pull && held.Elapsed.TotalSeconds - lastPull >= 2)
                {
                    lastPull = held.Elapsed.TotalSeconds;
                    for (var i = 0; i < fleet.Count; i++)
                    {
                        var ship = fleet[i];
                        if (ship.MarkedForClose || ship.Physics == null) continue;
                        var at = ship.PositionComp.GetPosition();
                        var middle = Enumerable.Range(0, clusters).Select(c => Centre(origin, c)).OrderBy(c => Vector3D.DistanceSquared(c, at)).First();
                        var towards = middle - ship.PositionComp.GetPosition();
                        if (towards.LengthSquared() > 1) ship.Physics.LinearVelocity = (Vector3)(Vector3D.Normalize(towards) * Math.Min(10, towards.Length() / 5));
                    }
                }
                yield return null;
            }
            Note("DETACH LOAD DONE");
            FakeClients.RemoveAll();
            for (var i = 0; i < _grids.Count; i++)
            {
                if (!_grids[i].MarkedForClose) _grids[i].Close();
                if (i % ClosePerFrame == ClosePerFrame - 1) yield return null;
            }
            _grids.Clear();
        }

        /// <summary>The template's turrets and weapons given their ammunition, as a fleet's own would be.</summary>
        private static string Stock(MyObjectBuilder_CubeGrid template)
        {
            var stocked = 0;
            foreach (var block in template.CubeBlocks)
            {
                var definition = Sandbox.Definitions.MyDefinitionManager.Static.GetCubeBlockDefinition(block.GetId()) as Sandbox.Definitions.MyWeaponBlockDefinition;
                if (definition == null) continue;
                var weapon = Sandbox.Definitions.MyDefinitionManager.Static.GetWeaponDefinition(definition.WeaponDefinitionId);
                var magazine = weapon?.AmmoMagazinesId?.FirstOrDefault();
                if (!magazine.HasValue) continue;
                var inventory = new MyObjectBuilder_Inventory { Items = new List<MyObjectBuilder_InventoryItem>() };
                inventory.Items.Add(new MyObjectBuilder_InventoryItem
                {
                    Amount = 20,
                    PhysicalContent = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(magazine.Value),
                });
                block.ComponentContainer = block.ComponentContainer ?? new MyObjectBuilder_ComponentContainer();
                block.ComponentContainer.Components.RemoveAll(c => c.Component is MyObjectBuilder_Inventory);
                block.ComponentContainer.Components.Add(new MyObjectBuilder_ComponentContainer.ComponentData { TypeId = "MyInventoryBase", Component = inventory });
                stocked++;
            }
            return stocked + " weapons stocked";
        }

        /// <summary>A pirates' block to aim at: heavy armor, a battery that powers it, a small container.</summary>
        private static MyObjectBuilder_CubeGrid Target(string name, Vector3D at, long owner)
        {
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + name,
                DisplayName = WorldApi.EntityPrefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(at, Vector3.Forward, Vector3.Up),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>(),
            };
            void Add(string type, string subtype, int x)
            {
                var block = (MyObjectBuilder_CubeBlock)MyObjectBuilderSerializer.CreateNewObject(MyObjectBuilderType.Parse(type), subtype);
                block.SubtypeName = subtype;
                block.Min = new SerializableVector3I(x, 0, 0);
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.None;
                if (block is MyObjectBuilder_BatteryBlock battery) battery.CurrentStoredPower = 3f;
                ob.CubeBlocks.Add(block);
            }
            Add("MyObjectBuilder_CubeBlock", "LargeHeavyBlockArmorBlock", 0);
            Add("MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", 1);
            Add("MyObjectBuilder_CargoContainer", "LargeBlockSmallContainer", 2);
            return ob;
        }

        private static Vector3D Centre(Vector3D origin, int cluster) => origin + Vector3D.Right * (cluster * ClustersApart);

        private static string Km(Vector3D at) => (at.X / 1000).ToString("F0") + " " + (at.Y / 1000).ToString("F0") + " " + (at.Z / 1000).ToString("F0");

        public override void Cleanup()
        {
            try { FakeClients.RemoveAll(); }
            catch (Exception e) { Log.Warn("turret_detach: removing the players: {0}", e.Message); }
            try { _config.Restore(); }
            catch (Exception e) { Log.Warn("turret_detach: restoring the settings: {0}", e.Message); }
            if (_autoSave.HasValue) MySession.Static.Settings.AutoSaveInMinutes = _autoSave.Value;
            base.Cleanup();
        }
    }
}
