using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The one-off events that made the longest frames on the stand, each done on purpose and measured: what the call
    /// itself took and the longest frame around it.
    /// <list type="bullet">
    /// <item><b>place</b> - a player places blocks (the request a client sends) on a small base and on a big factory;</item>
    /// <item><b>respawn screen</b> - a player in the respawn screen asks for the list of respawn points (every 10 s);</item>
    /// <item><b>respawn</b> - a dead player respawns at a survival kit and in a drop pod on the planet;</item>
    /// <item><b>economy</b> - the economy tick (stations, contracts, stores);</item>
    /// <item><b>encounter leaves</b> - a group of big grids closed in one frame;</item>
    /// <item><b>save</b> - the world saved.</item>
    /// </list>
    /// The call trees of slow events go to the log from <see cref="EventTimer"/>. The result line lists every event:
    /// "event: call N ms, worst frame M ms".
    /// </summary>
    public sealed class PeakEventsScenario : TestScenario
    {
        public const string ScenarioName = "peak_events";
        private const string Prefix = "peak-";
        private const int RespawnPoints = 10;
        private const int BlocksPerBase = 8;

        private readonly List<string> _results = new List<string>();
        private long _identity;
        private double _baselineWorst;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var center = planet.PositionComp.GetPosition();
            var up = Vector3D.Normalize(anchorM.Translation - center);
            var east = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));
            var origin = planet.GetClosestSurfacePointGlobal(center + Vector3D.Normalize(up + east * (3000.0 / planet.AverageRadius)) * planet.AverageRadius);
            var localUp = Vector3D.Normalize(origin - center);
            var forward = Vector3D.Normalize(Vector3D.Reject(east, localUp));
            origin += localUp * 30;

            // a player: a client with a character
            FakeClients.Add(1, new FakeClients.NetworkProfile { RttMs = 30 }, i => (origin, 0, 0), withCharacters: true);
            var player = FakeClients.PlayerOf(0);
            Check(player?.Identity != null, "the fake player has no identity");
            _identity = player.Identity.IdentityId;
            yield return WaitForTicks(30);

            // ------------------------------------------------------------ the quiet frames, to compare with
            TickMetrics.Take();
            var quiet = WaitForSeconds(5, "quiet frames");
            while (quiet.MoveNext()) yield return quiet.Current;
            var baseline = TickMetrics.Take();
            _baselineWorst = baseline.MaxFrameMs;
            Note("baseline: " + baseline.Format());

            var buildRequest = typeof(MyCubeGrid).GetMethod("BuildBlocksRequest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Note("patches of MyCubeGrid.BuildBlocksRequest: " + string.Join(", ", LeaveTargets.PatchesOf(buildRequest)));

            // the first write of what the placement event sends to the clients: its serializers built here
            {
                var probe = new VRage.Library.Collections.BitStream();
                probe.ResetWrite();
                var watch = Stopwatch.StartNew();
                var visuals = new MyCubeGrid.MyBlockVisuals(0, MyStringHash.NullOrEmpty);
                VRage.Serialization.MySerializer.Write(probe, ref visuals);
                var visualsMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                var set = new HashSet<MyCubeGrid.MyBlockLocation> { new MyCubeGrid.MyBlockLocation(new MyDefinitionId(typeof(MyObjectBuilder_CubeBlock), "LargeBlockArmorBlock"), Vector3I.Zero, Vector3I.Zero, Vector3I.Zero, Quaternion.Identity, 1, 1) };
                VRage.Serialization.MySerializer.Write(probe, ref set);
                Note("first serializer writes: MyBlockVisuals " + visualsMs.ToString("F1") + " ms, HashSet<MyBlockLocation> " + watch.Elapsed.TotalMilliseconds.ToString("F1") + " ms");
            }

            // ------------------------------------------------------------ place
            var floor = WorldApi.SpawnGrid(Owned(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "floor", MyCubeSize.Large, true,
                origin + forward * 40, Floor(WorldApi.FindSubtype(MyCubeSize.Large, "armorblock")), forward, localUp)));
            Track(floor);
            var factoryOb = WorldApi.LoadAuthoredGrid(RefineryPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "factory");
            factoryOb.IsStatic = true;
            factoryOb.PositionAndOrientation = new MyPositionAndOrientation(origin + forward * 120 + localUp * 10, forward, localUp);
            var factory = WorldApi.SpawnGrid(Owned(factoryOb));
            Track(factory);
            WorldApi.ChargeBatteries(factory);
            yield return WaitForTicks(120);

            foreach (var grid in new[] { floor, factory })
            {
                var place = Place(grid, player, grid == floor ? "place on a base of " + WorldApi.CountBlocks(floor) + " blocks" : "place on a factory of " + WorldApi.CountBlocks(factory) + " blocks");
                while (place.MoveNext()) yield return place.Current;
            }

            // ------------------------------------------------------------ respawn points of the player
            var kits = new List<MyCubeGrid>();
            var kit = WorldApi.FindSubtype(MyCubeSize.Large, "survivalkit");
            var battery = WorldApi.FindSubtype(MyCubeSize.Large, "battery");
            var kitSize = Definition(kit).Size;
            for (var i = 0; i < RespawnPoints; i++)
            {
                var at = origin - forward * (40 + 25 * i) + localUp * 5;
                var ob = Owned(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "kit-" + i, MyCubeSize.Large, true, at,
                    new[] { new BlockSpec(kit, Vector3I.Zero), new BlockSpec(battery, new Vector3I(kitSize.X, 0, 0)) }, forward, localUp));
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                WorldApi.ChargeBatteries(grid);
                kits.Add(grid);
            }
            yield return WaitForTicks(120);

            // ------------------------------------------------------------ respawn screen
            var refresh = Integrations.TypeAnywhere("SpaceEngineers.Game.GUI.MyGuiScreenMedicals")
                ?.GetMethod("RefreshRespawnPointsRequest", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (refresh == null)
            {
                // A game build without that screen costs this one measurement, not the scenario.
                Note("respawn screen list: RefreshRespawnPointsRequest is not in this game");
            }
            else
            {
                for (var i = 0; i < 4; i++)
                {
                    var m = Measure("respawn screen list #" + (i + 1) + " (" + RespawnPoints + " points)", () => FakeClients.AsClient(0, () => refresh.Invoke(null, null)), 20);
                    while (m.MoveNext()) yield return m.Current;
                    yield return WaitForTicks(i == 0 ? 60 : 600);
                }
            }

            // ------------------------------------------------------------ respawn
            var respawn = Sync.Players.RespawnComponent;
            Check(respawn != null, "no respawn component");
            foreach (var (label, entityId) in new[] { ("respawn at a survival kit", KitId(kits[0])), ("respawn in a drop pod on the planet", planet.EntityId) })
            {
                var character = player.Character;
                if (character != null && !character.IsDead)
                {
                    // the suicide a client asks for (the damage guard after a start may still keep the character alive)
                    var suicide = typeof(MyCharacter).GetMethod("OnSuicideRequest", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    FakeClients.AsClient(0, () => suicide?.Invoke(character, null));
                    yield return WaitForTicks(60);
                    if (!character.IsDead)
                    {
                        character.Kill(true, new VRage.Game.ModAPI.MyDamageInformation(false, 1000f, MyDamageType.Suicide, character.EntityId));
                        yield return WaitForTicks(60);
                    }
                    Note(label + ": the character " + (character.IsDead ? "died" : "is still alive"));
                }
                var id = entityId;
                var m = Measure(label, () => respawn.HandleRespawnRequest(false, false, id, null, player.Id, null, null, null, null, true, null, Color.White), 60);
                while (m.MoveNext()) yield return m.Current;
                Note(label + ": the player's character " + (player.Character != null && !player.Character.IsDead ? "is alive at " + Vector3D.Distance(player.Character.PositionComp.GetPosition(), origin).ToString("F0") + " m from the base" : "is missing"));
                yield return WaitForTicks(300);
            }

            // ------------------------------------------------------------ economy
            var economy = MySession.Static.GetComponent<MySessionComponentEconomy>();
            var stations = typeof(MySessionComponentEconomy).GetMethod("UpdateStations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (economy != null && stations != null)
            {
                var m = Measure("economy tick", () => stations.Invoke(economy, null), 300);
                while (m.MoveNext()) yield return m.Current;
            }
            else Note("no economy to tick");

            // ------------------------------------------------------------ an encounter leaves
            foreach (var name in new[] { "OnEntityRemove", "OnEntityDelete", "OnEntityAdd" })
            {
                var handlers = (typeof(MyEntities).GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as Delegate)?.GetInvocationList();
                Note("MyEntities." + name + " handlers: " + (handlers == null ? "none" : string.Join(", ", handlers.Select(h => h.Method.DeclaringType?.FullName + "." + h.Method.Name))));
            }
            var group = new List<MyCubeGrid>();
            for (var i = 0; i < 5; i++)
            {
                var ob = WorldApi.LoadAuthoredGrid(RefineryPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "encounter-" + i);
                ob.IsStatic = true;
                ob.PositionAndOrientation = new MyPositionAndOrientation(origin + forward * 400 + east * (i * 150) + localUp * 20, forward, localUp);
                var grid = WorldApi.SpawnGrid(ob);
                Track(grid);
                group.Add(grid);
            }
            yield return WaitForTicks(300);
            var blocks = group.Sum(WorldApi.CountBlocks);
            var removeField = typeof(MyEntities).GetField("OnEntityRemove", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var original = removeField?.GetValue(null) as Action<VRage.Game.Entity.MyEntity>;
            Func<string> times = null;
            var timed = original == null ? null : TimeEach(original, out times);
            if (timed != null) removeField.SetValue(null, timed);
            try
            {
                var close = Measure("encounter leaves: " + group.Count + " grids, " + blocks + " blocks closed in one frame", () => { foreach (var grid in group) grid.Close(); }, 60);
                while (close.MoveNext()) yield return close.Current;
            }
            finally
            {
                if (timed != null) removeField.SetValue(null, original);
            }
            if (timed != null) Note("OnEntityRemove handlers while the encounter left: " + times());

            // ------------------------------------------------------------ save
            var torch = SentisTestsPlugin.TorchInstance;
            if (torch != null)
            {
                TickMetrics.Take();
                yield return WaitForTicks(1);
                var watch = Stopwatch.StartNew();
                var task = torch.Save();
                var callMs = watch.Elapsed.TotalMilliseconds;
                while (task != null && !task.IsCompleted && watch.Elapsed.TotalSeconds < 300) yield return null;
                yield return WaitForTicks(60);
                Record("world save (" + watch.Elapsed.TotalSeconds.ToString("F1") + " s in all)", callMs, TickMetrics.Take());
            }

            Note("PEAK EVENTS RESULT | quiet worst frame " + _baselineWorst.ToString("F1") + " ms | " + string.Join(" | ", _results));
        }

        // ------------------------------------------------------------------ measuring

        /// <summary>Does the event at a frame's start and records its own time and the longest frame of the next <paramref name="frames"/>.</summary>
        private IEnumerator Measure(string label, Action act, int frames)
        {
            yield return WaitForTicks(1);
            TickMetrics.Take();
            var watch = Stopwatch.StartNew();
            try
            {
                act();
            }
            catch (Exception e)
            {
                Note(label + " threw: " + (e.InnerException ?? e).Message);
            }
            var callMs = watch.Elapsed.TotalMilliseconds;
            yield return WaitForTicks(frames);
            Record(label, callMs, TickMetrics.Take());
        }

        private void Record(string label, double callMs, TickMetrics.Snapshot frames)
        {
            var line = label + ": call " + callMs.ToString("F1") + " ms, worst frame " + frames.MaxFrameMs.ToString("F1") + " ms";
            _results.Add(line);
            Note("PEAK " + line + " (" + frames.Format() + ")");
        }

        /// <summary>The handlers of an entity event, each with its time summed (test only; put back after).</summary>
        private static Action<VRage.Game.Entity.MyEntity> TimeEach(Action<VRage.Game.Entity.MyEntity> handlers, out Func<string> report)
        {
            var list = handlers.GetInvocationList().Cast<Action<VRage.Game.Entity.MyEntity>>().ToArray();
            var ticks = new long[list.Length];
            var calls = new int[list.Length];
            Action<VRage.Game.Entity.MyEntity> all = null;
            for (var i = 0; i < list.Length; i++)
            {
                var index = i;
                var handler = list[i];
                all += entity =>
                {
                    var started = Stopwatch.GetTimestamp();
                    try { handler(entity); }
                    finally { ticks[index] += Stopwatch.GetTimestamp() - started; calls[index]++; }
                };
            }
            report = () => string.Join(", ", Enumerable.Range(0, list.Length).OrderByDescending(i => ticks[i])
                .Select(i => list[i].Method.DeclaringType?.Name + "." + list[i].Method.Name + " " + (ticks[i] * 1000.0 / Stopwatch.Frequency).ToString("F2") + " ms/" + calls[i]));
            return all;
        }

        // ------------------------------------------------------------------ placing blocks

        private IEnumerator Place(MyCubeGrid grid, MyPlayer player, string label)
        {
            var subtypes = new[]
            {
                WorldApi.FindSubtype(MyCubeSize.Large, "armorblock"), WorldApi.FindSubtype(MyCubeSize.Large, "armorblock"),
                WorldApi.FindSubtype(MyCubeSize.Large, "conveyor"), WorldApi.FindSubtype(MyCubeSize.Large, "conveyor"),
                WorldApi.FindSubtype(MyCubeSize.Large, "battery"), WorldApi.FindSubtype(MyCubeSize.Large, "armorblock"),
                WorldApi.FindSubtype(MyCubeSize.Large, "conveyor"), WorldApi.FindSubtype(MyCubeSize.Large, "armorblock"),
            };
            var request = typeof(MyCubeGrid).GetMethod("BuildBlocksRequest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Check(request != null, "MyCubeGrid.BuildBlocksRequest not found");
            var placed = 0;
            var tried = 0;
            foreach (var subtype in subtypes.Take(BlocksPerBase))
            {
                var definition = Definition(subtype);
                var cell = FreeCellOnTop(grid, definition.Size);
                if (cell == null) { Note(label + ": no room for " + subtype); continue; }
                var character = player.Character;
                if (character == null) { Note(label + ": the player has no character"); yield break; }
                Give(character, definition);
                FakeClients.MoveTo(0, grid.GridIntegerToWorld(cell.Value) + grid.WorldMatrix.Up * 4);
                yield return WaitForTicks(2);
                var min = cell.Value;
                var max = min + definition.Size - Vector3I.One;
                var location = new MyCubeGrid.MyBlockLocation(definition.Id, min, max, min + definition.Center, Quaternion.Identity, 0, _identity);
                var visuals = new MyCubeGrid.MyBlockVisuals(new Vector3(0, -1, 0).PackHSVToUint(), MyStringHash.NullOrEmpty);
                tried++;
                var m = Measure(label + ", " + subtype, () => FakeClients.AsClient(0, () =>
                    request.Invoke(grid, new object[] { visuals, new HashSet<MyCubeGrid.MyBlockLocation> { location }, character.EntityId, false, _identity })), 10);
                while (m.MoveNext()) yield return m.Current;
                if (grid.GetCubeBlock(min) != null) placed++;
            }
            Note(label + ": " + placed + " of " + tried + " blocks placed");
        }

        private static Vector3I? FreeCellOnTop(MyCubeGrid grid, Vector3I size)
        {
            // the grid's up in its own cells
            var upDirection = grid.WorldMatrix.GetClosestDirection(grid.WorldMatrix.Up);
            var up = Base6Directions.GetIntVector(upDirection);
            foreach (var block in grid.GetBlocks().OrderBy(b => Vector3I.Dot(b.Max, up) * -1).ThenBy(b => b.Position.X).ThenBy(b => b.Position.Z))
            {
                var min = block.Max + up;
                var max = min + size - Vector3I.One;
                if (grid.CanAddCubes(min, max)) return min;
            }
            return null;
        }

        /// <summary>What a player carries to place the block: its first component.</summary>
        private static void Give(MyCharacter character, MyCubeBlockDefinition definition)
        {
            var inventory = character.GetInventory();
            if (inventory == null || definition.Components.Length == 0) return;
            var first = definition.Components[0];
            var item = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(first.Definition.Id);
            inventory.AddItems(first.Count + 1, item);
        }

        private static MyCubeBlockDefinition Definition(string subtype) =>
            MyDefinitionManager.Static.GetDefinitionsOfType<MyCubeBlockDefinition>().First(d => d.Id.SubtypeName == subtype);

        // ------------------------------------------------------------------ grids

        private static IEnumerable<BlockSpec> Floor(string armor)
        {
            for (var x = 0; x < 10; x++)
            for (var z = 0; z < 10; z++)
                yield return new BlockSpec(armor, new Vector3I(x, 0, z));
        }

        private MyObjectBuilder_CubeGrid Owned(MyObjectBuilder_CubeGrid ob)
        {
            foreach (var block in ob.CubeBlocks)
            {
                block.Owner = _identity;
                block.BuiltBy = _identity;
                block.ShareMode = MyOwnershipShareModeEnum.None;
            }
            return ob;
        }

        private static long KitId(MyCubeGrid grid) =>
            grid.GetFatBlocks().FirstOrDefault(b => b.BlockDefinition.Id.SubtypeName.IndexOf("survivalkit", StringComparison.OrdinalIgnoreCase) >= 0)?.EntityId ?? 0;

        public override void Cleanup()
        {
            try
            {
                // the drop pod the player respawned in
                foreach (var grid in MyEntities.GetEntities().OfType<MyCubeGrid>().ToList())
                    if (!grid.MarkedForClose && grid.BigOwners.Contains(_identity)) grid.Close();
                FakeClients.RemoveAll();
            }
            finally { base.Cleanup(); }
        }
    }
}
