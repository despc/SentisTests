using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
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
    /// NPC grids spawned by the SentisAdventures plugin from a blueprint - a ship with a rotor head
    /// and a main cockpit turned sideways on it:
    ///  - in space: the group must come whole (the head on its rotor, where the blueprint has it
    ///    against the ship), owned by the NPC, sent to every fake client, and six seconds later still
    ///    the same grids (the plugin used to close and re-create them: "FixShip");
    ///  - as a base on the planet, on a flat spot the plugin finds: stood upright by its cockpit
    ///    (which is on top of it) and static - all but the rotor head - before it is spawned, not
    ///    moved and converted afterwards; and down on the ground: its bottom on the highest ground
    ///    under it, no block in the rock (checked against the voxels), rock right under it. The
    ///    plugin used to put the cockpit itself on the ground and bury what was under it.
    /// And a grid the plugin remembers as its NPC is forgotten when it leaves the world.
    /// </summary>
    internal sealed class NpcSpawnScenario : TestScenario
    {
        public const string ScenarioName = "npc_spawn";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 180;

        private const string Prefix = "npc-";

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };
        private Vector3D _up, _side;
        private string _blueprint;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var spawner = PluginType("SentisAdventures.NPCSpawner");
            var processor = PluginType("SentisAdventures.Adventures.WorldRandomNPC.NpcProcessor");
            Check(spawner != null && processor != null, "the SentisAdventures plugin is not loaded");
            Check(PluginType("SentisAdventures.FixShipLogic") == null, "FixShipLogic is still in the plugin");
            var repository = processor.GetField("NpcRepository", BindingFlags.Static | BindingFlags.Public);
            var ready = Wait(() => repository.GetValue(null) != null, "the plugin has its database", 30);
            while (ready.MoveNext()) yield return ready.Current;

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            var planetCentre = planet.PositionComp.GetPosition();
            _up = Vector3D.Normalize(anchor - planetCentre);
            _side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(_up));
            Vector3D centre = default;
            for (var d = 100000.0; d <= 1000000; d += 50000)
            {
                centre = anchor + _up * d + _side * 3000;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(centre).Length() < 0.001f) break;
            }

            FakeClients.RemoveAll();
            FakeClients.Add(2, Network, p => (centre + _up * 60 + _side * (20 * p), 0, 0), withCharacters: true);
            var arrive = WaitForSeconds(5, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;
            var npc = FakeClients.Character(0)?.GetPlayerIdentityId() ?? 0;
            Check(npc != 0, "the fake player has no identity");

            // ------------------------------------------------------------- the blueprint
            var template = Spawn(centre + _side * 400, "template");
            var settle = WaitForSeconds(2, "the template settles");
            while (settle.MoveNext()) yield return settle.Current;
            var stator = WorldApi.FindFunctional<MyMotorStator>(template);
            var recreate = typeof(MyMechanicalConnectionBlockBase).GetMethod("DoRecreateTop", BindingFlags.Instance | BindingFlags.NonPublic);
            recreate.Invoke(stator, new object[] { npc, Enum.Parse(recreate.GetParameters()[1].ParameterType, "Normal"), true });
            var topped = Wait(() => stator.TopGrid != null, "the rotor head is built", 10);
            while (topped.MoveNext()) yield return topped.Current;
            ((Sandbox.ModAPI.Ingame.IMyMotorStator)stator).RotorLock = true;
            var head = stator.TopGrid;
            Track(head);
            var headOffset = head.PositionComp.GetPosition() - template.PositionComp.GetPosition();
            var headLocal = Vector3D.TransformNormal(headOffset, MatrixD.Transpose(template.WorldMatrix));
            WriteBlueprint(new List<MyCubeGrid> { template, head });
            template.Close();
            head.Close();
            Note("blueprint: " + _blueprint + ", head " + headLocal.Length().ToString("F2") + " m off the ship");

            // ------------------------------------------------------------- in space
            List<MyCubeGrid> ship = null;
            var at = spawner.GetMethod("SpawnAroundTarget").Invoke(null, new object[]
            {
                npc, _blueprint, centre, 300.0, null, 0, null, new Action<List<MyCubeGrid>>(g => ship = g),
            });
            Check(at != null, "the plugin found no place in empty space");
            var spawned = Wait(() => ship != null, "the NPC ship is spawned", 30);
            while (spawned.MoveNext()) yield return spawned.Current;
            foreach (var grid in ship) Track(grid);
            var shipAt = DateTime.UtcNow;
            var sent = Wait(() => FakeClients.ArrivedCount(ship) == 0, "the players get the NPC ship", 30);
            while (sent.MoveNext()) yield return sent.Current;
            var sentSeconds = (DateTime.UtcNow - shipAt).TotalSeconds;
            Note("ship sent to the players in " + sentSeconds.ToString("F1") + " s: " + FakeClients.Arrivals(ship));

            var main = ship.OrderByDescending(g => g.BlocksCount).First();
            var shipStator = WorldApi.FindFunctional<MyMotorStator>(main);
            Check(ship.Count == 2, "the ship came as " + ship.Count + " grids");
            Check(shipStator?.TopGrid != null && ship.Contains(shipStator.TopGrid), "the rotor head is not on its rotor");
            Check(main.BigOwners.Contains(npc) && ship.All(g => g.BigOwners.All(o => o == npc)),
                "the ship is not the NPC's: " + string.Join(",", ship.SelectMany(g => g.BigOwners)));
            var headNow = Vector3D.TransformNormal(shipStator.TopGrid.PositionComp.GetPosition() - main.PositionComp.GetPosition(), MatrixD.Transpose(main.WorldMatrix));
            Check(Vector3D.Distance(headNow, headLocal) < 0.5, "the rotor head moved against the ship: " + headLocal + " -> " + headNow);
            Check(Vector3D.Distance(main.PositionComp.GetPosition(), (Vector3D)at) < 400, "the ship is far from the spot the plugin chose");

            // the plugin remembers its NPC grids and forgets them when they go
            var repo = repository.GetValue(null);
            var saveNpc = repo.GetType().GetMethod("SaveRandomNpc", new[] { typeof(long), typeof(long), typeof(string), typeof(string), typeof(TimeSpan), typeof(bool), typeof(DateTime?) });
            var isNpc = repo.GetType().GetMethod("IsRandomNpc");
            foreach (var grid in ship)
                saveNpc.Invoke(repo, new object[] { grid.EntityId, npc, grid.DisplayName, WorldApi.EntityPrefix + "npc", TimeSpan.FromMinutes(5), false, null });

            var ids = ship.Select(g => g.EntityId).ToList();
            var later = WaitForSeconds(6, "no re-creation after the spawn");
            while (later.MoveNext()) yield return later.Current;
            Check(ship.All(g => !g.Closed && !g.MarkedForClose) && ids.All(id => MyEntities.GetEntityById(id) != null),
                "the ship was closed or re-created after the spawn");

            // ------------------------------------------------------------- on the planet
            // the spot is found the way the plugin finds one for its bases: free and flat
            var findSpot = processor.GetMethod("FindBaseSpot");
            // (hills around the test area: look further until a plain turns up)
            Vector3D? spot = null;
            var tried = 0;
            var forwardAxis = Vector3D.Cross(_side, _up);
            foreach (var distance in new[] { 1500.0, 5000, 10000, 20000, 40000 })
            {
                for (var k = 0; k < 8 && !spot.HasValue; k++)
                {
                    var angle = Math.PI * 2 * k / 8;
                    var around = planet.GetClosestSurfacePointGlobal(anchor + (_side * Math.Cos(angle) + forwardAxis * Math.Sin(angle)) * distance);
                    tried++;
                    spot = (Vector3D?)findSpot.Invoke(null, new object[] { planet, around, 300.0, around, 0.0, new List<Vector3D>(), 0.0 });
                }
                if (spot.HasValue) break;
            }
            Check(spot.HasValue, "the plugin found no flat spot for a base in " + tried + " areas");
            Note("flat spot found in area " + tried + ", " + Vector3D.Distance(spot.Value, anchor).ToString("F0") + " m from the test area");
            var surface = spot.Value;
            var up = Vector3D.Normalize(surface - planetCentre);
            // the players go there: the server sends a grid to the players near it
            for (var p = 0; p < FakeClients.Count; p++) FakeClients.MoveTo(p, surface + up * 150 + _side * (20 * p));
            var moved = WaitForSeconds(5, "the players go down to the planet");
            while (moved.MoveNext()) yield return moved.Current;
            var stand = processor.GetMethod("StandOnSurface");
            List<MyCubeGrid> station = null;
            spawner.GetMethod("SpawnBlueprint").Invoke(null, new object[]
            {
                _blueprint, npc,
                new Func<List<MyObjectBuilder_CubeGrid>, bool>(grids =>
                {
                    stand.Invoke(null, new object[] { grids, surface, planetCentre, new Func<Vector3D, Vector3D>(planet.GetClosestSurfacePointGlobal), true, 0f });
                    return true;
                }),
                new Action<List<MyCubeGrid>>(g => station = g),
            });
            var built = Wait(() => station != null, "the NPC base is spawned", 30);
            while (built.MoveNext()) yield return built.Current;
            foreach (var grid in station) Track(grid);
            var baseSent = Wait(() => FakeClients.ArrivedCount(station) == 0, "the players get the NPC base", 30);
            while (baseSent.MoveNext()) yield return baseSent.Current;

            var baseMain = station.OrderByDescending(g => g.BlocksCount).First();
            var baseHead = WorldApi.FindFunctional<MyMotorStator>(baseMain)?.TopGrid;
            var cockpit = WorldApi.FindFunctional<MyCockpit>(baseMain);
            var upright = Vector3D.Dot(cockpit.WorldMatrix.Up, up);
            var toCockpit = cockpit.PositionComp.GetPosition() - surface;
            var off = (toCockpit - up * Vector3D.Dot(toCockpit, up)).Length();
            Note("base: cockpit up . planet up = " + upright.ToString("F4") + ", cockpit " + off.ToString("F2") + " m off the spot (seen from above), " +
                 "base " + (baseMain.IsStatic ? "static" : "DYNAMIC") + ", head " + (baseHead == null ? "MISSING" : baseHead.IsStatic ? "STATIC" : "dynamic"));
            Check(upright > 0.999, "the base is not upright: " + upright);
            Check(off < 1.5, "the cockpit is " + off.ToString("F2") + " m off the spot it was put on");
            Check(baseMain.IsStatic, "the base is not static");
            Check(baseHead != null && station.Contains(baseHead) && !baseHead.IsStatic, "the base's rotor head is missing or static");

            // ------------------------------------------------------------- down on the ground, not in it
            var blocks = Blocks(station);
            var lowest = blocks.SelectMany(b => b.Corners).Min(c => Vector3D.Dot(c, up));
            var forward = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var right = Vector3D.Cross(forward, up);
            var corners = blocks.SelectMany(b => b.Corners).ToList();
            var ground = new List<double>();
            for (var i = 0; i <= 2; i++)
            for (var j = 0; j <= 2; j++)
            {
                double Lerp(Vector3D axis, int k) => corners.Min(c => Vector3D.Dot(c, axis)) + (corners.Max(c => Vector3D.Dot(c, axis)) - corners.Min(c => Vector3D.Dot(c, axis))) * k / 2;
                var point = forward * Lerp(forward, i) + right * Lerp(right, j) + up * lowest;
                ground.Add(Vector3D.Dot(planet.GetClosestSurfacePointGlobal(point), up));
            }
            var gap = lowest - ground.Max();          // how high the bottom is over the highest ground under it
            var uneven = ground.Max() - ground.Min(); // how uneven the ground under the base is

            // the rock itself: no block may be in it (its box a little shrunk, so touching the ground is fine) ...
            var buried = blocks.Where(b => planet.IsAnyOfPointInside(b.Inner)).Select(b => b.Name).ToList();
            // ... and the base stands on it: a metre under its bottom, there is rock somewhere
            var under = new List<Vector3D>();
            for (var i = 0; i <= 4; i++)
            for (var j = 0; j <= 4; j++)
            {
                double At(Vector3D axis, int k) => corners.Min(c => Vector3D.Dot(c, axis)) + (corners.Max(c => Vector3D.Dot(c, axis)) - corners.Min(c => Vector3D.Dot(c, axis))) * k / 4;
                under.Add(forward * At(forward, i) + right * At(right, j) + up * (lowest - 1));
            }
            var standsOnRock = planet.IsAnyOfPointInside(under.ToArray());
            Note("base on the ground: bottom " + gap.ToString("F2") + " m over the highest ground under it, the ground " + uneven.ToString("F2") +
                 " m uneven, blocks in the rock: " + (buried.Count == 0 ? "none" : string.Join(", ", buried)) + ", rock under it " + standsOnRock);
            Check(Math.Abs(gap) < 0.3, "the bottom of the base is " + gap.ToString("F2") + " m from the highest ground under it");
            Check(buried.Count == 0, buried.Count + " blocks of the base are in the rock: " + string.Join(", ", buried));
            Check(standsOnRock, "there is no rock under the base: it hangs in the air");

            var baseIds = station.Select(g => g.EntityId).ToList();
            var baseAt = baseMain.PositionComp.GetPosition();
            var baseLater = WaitForSeconds(6, "the base is left alone");
            while (baseLater.MoveNext()) yield return baseLater.Current;
            Check(baseIds.All(id => MyEntities.GetEntityById(id) != null) && station.All(g => !g.Closed), "the base was closed or re-created after the spawn");
            Check(Vector3D.Distance(baseMain.PositionComp.GetPosition(), baseAt) < 0.01, "the base moved after the spawn");

            // ------------------------------------------------------------- the ship goes, and is forgotten
            Check(ids.All(id => (bool)isNpc.Invoke(repo, new object[] { id })), "the plugin did not remember the ship");
            foreach (var grid in ship) grid.Close();
            var forgotten = Wait(() => ids.All(id => !(bool)isNpc.Invoke(repo, new object[] { id })), "the closed ship is forgotten", 10);
            while (forgotten.MoveNext()) yield return forgotten.Current;

            Note("NPC SPAWN RESULT: ship " + ship.Count + " grids whole, sent to " + FakeClients.Count + " players in " + sentSeconds.ToString("F1") +
                 " s, not re-created; base upright (" + upright.ToString("F4") + "), " + off.ToString("F2") + " m off, static with a free rotor head, " +
                 "on the ground (" + gap.ToString("F2") + " m, ground " + uneven.ToString("F2") + " m uneven, nothing in the rock); " +
                 "closed NPC grids forgotten");
        }

        /// <summary>Every block of the grids: its eight world corners, and the same box shrunk by half a metre.</summary>
        private static List<(string Name, Vector3D[] Corners, Vector3D[] Inner)> Blocks(List<MyCubeGrid> grids)
        {
            var result = new List<(string, Vector3D[], Vector3D[])>();
            foreach (var grid in grids)
            foreach (var block in grid.GetBlocks())
            {
                var size = grid.GridSize;
                var box = new BoundingBoxD((Vector3D)block.Min * size - size / 2, (Vector3D)block.Max * size + size / 2);
                var inner = new BoundingBoxD(box.Min + 0.5, box.Max - 0.5);
                var world = grid.WorldMatrix;
                result.Add((grid.DisplayName + "/" + block.BlockDefinition.Id.SubtypeName + "@" + block.Min,
                    box.GetCorners().Select(c => Vector3D.Transform(c, world)).ToArray(),
                    inner.GetCorners().Select(c => Vector3D.Transform(c, world)).ToArray()));
            }
            return result;
        }

        private void WriteBlueprint(List<MyCubeGrid> grids)
        {
            var definitions = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Definitions>();
            var blueprint = MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();
            blueprint.Id = new SerializableDefinitionId(new MyObjectBuilderType(typeof(MyObjectBuilder_ShipBlueprintDefinition)), WorldApi.EntityPrefix + Prefix + "bp");
            blueprint.CubeGrids = grids.Select(g => (MyObjectBuilder_CubeGrid)g.GetObjectBuilder(true)).ToArray();
            definitions.ShipBlueprints = new[] { blueprint };
            _blueprint = Path.Combine(Path.GetTempPath(), WorldApi.EntityPrefix + Prefix + "bp.sbc");
            // the game's own serializer: the mod API one refuses paths outside the game's folders
            Check(VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen.SerializeXML(_blueprint, false, definitions), "the blueprint could not be written");
        }

        /// <summary>A small ship: armour, a battery, a rotor, and a main cockpit lying on its back - its up is the grid's forward.</summary>
        private MyCubeGrid Spawn(Vector3D at, string name)
        {
            MyObjectBuilder_CubeBlock Block(string subtype, int x, int y, int z)
            {
                var block = WorldApi.MakeBlockOb(subtype);
                block.Min = new SerializableVector3I(x, y, z);
                return block;
            }

            var battery = (MyObjectBuilder_BatteryBlock)Block("LargeBlockBatteryBlock", -1, 1, 0);
            battery.CurrentStoredPower = 3f;
            var cockpit = (MyObjectBuilder_Cockpit)Block("LargeBlockCockpit", 0, 1, 2);
            cockpit.IsMainCockpit = true;
            cockpit.BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Up, Base6Directions.Direction.Backward);
            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = false,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, _side, _up)),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>
                {
                    Block("LargeBlockArmorBlock", -1, 0, 0), Block("LargeBlockArmorBlock", 0, 0, 0), Block("LargeBlockArmorBlock", 1, 0, 0),
                    Block("LargeBlockArmorBlock", -1, 0, 1), Block("LargeBlockArmorBlock", 0, 0, 1), Block("LargeBlockArmorBlock", 1, 0, 1),
                    Block("LargeBlockArmorBlock", 0, 0, 2),
                    battery, cockpit, Block("LargeStator", 1, 1, 0),
                },
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            return grid;
        }

        private static Type PluginType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                if (_blueprint != null)
                    foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_blueprint), Path.GetFileName(_blueprint) + "*"))
                        File.Delete(file);
            }
            catch (Exception e)
            {
                Log.Warn("cleaning up after the test failed: " + e.Message);
            }
            finally { base.Cleanup(); }
        }
    }
}
