using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.SessionComponents;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// The game's global encounters (<c>MyGlobalEncountersGenerator</c>): one called up as the game's timer does it
    /// (<c>SpawnSelectedGlobalEncounter</c>) comes: its grids, of an NPC's, stand in the world, registered as the
    /// encounter, with the encounter's GPS when the spawn group gives one. Its removal (as when its timer runs out:
    /// <c>RemoveGlobalEncounter</c>) closes all its grids and takes its GPS away.
    /// </summary>
    public sealed class GlobalEncounterCheck : RegressionCheck
    {
        public override string Name => Prefix + "global_encounter";
        public override int TimeoutSeconds => 150;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private MyGlobalEncountersGenerator _generator;
        private long _encounter;
        private int? _cap;

        private ConcurrentDictionary<long, T> Field<T>(string name) => (ConcurrentDictionary<long, T>)typeof(MyGlobalEncountersGenerator).GetField(name, Any).GetValue(_generator);

        protected override IEnumerator Check()
        {
            _generator = MySession.Static.GetComponent<MyGlobalEncountersGenerator>();
            SkipUnless(_generator != null, "no global encounters generator in this session");
            SkipUnless(MySession.Static.NPCBlockLimits.HasRemainingPCU(true), "the global encounters' PCU is spent");
            var groups = MyDefinitionManager.Static.GetSpawnGroupDefinitions().Where(g => g.IsGlobalEncounter && g.Enabled && g.Prefabs.Count > 0).ToList();
            SkipUnless(groups.Count > 0, "no global encounter spawn groups in this game");
            yield return Arrive(Site.At(0, 0, -3));
            var components = Field<HashSet<SpaceEngineers.Game.EntityComponents.GameLogic.MyGlobalEncounterComponent>>("m_encounterComponents");
            var gpss = Field<long>("m_encountersGps");
            var before = new HashSet<long>(components.Keys);
            // the cap of the world: one more than is out now, given back after
            var active = (int)typeof(MyGlobalEncountersGenerator).GetField("m_activeEncounters", Any).GetValue(_generator);
            _cap = MySession.Static.Settings.GlobalEncounterCap;
            MySession.Static.Settings.GlobalEncounterCap = System.Math.Max(_cap.Value, active + 1);

            // the smallest of the groups; the place in space is picked at random and may be taken, so a few tries
            var group = groups.OrderBy(g => g.Prefabs.Count).ThenBy(g => g.HostileSubEncounters.Count + g.FactionSubEncounters.Count).First();
            var spawn = typeof(MyGlobalEncountersGenerator).GetMethod("SpawnSelectedGlobalEncounter", Any);
            for (var attempt = 0; attempt < 3 && _encounter == 0; attempt++)
            {
                spawn.Invoke(_generator, new object[] { group.Id.SubtypeName });
                var until = MySandboxGameTime() + 20;
                while (_encounter == 0 && MySandboxGameTime() < until)
                {
                    yield return null;
                    _encounter = components.Keys.FirstOrDefault(k => !before.Contains(k));
                }
            }
            Check(_encounter != 0, "the global encounter " + group.Id.SubtypeName + " did not come in 3 tries");
            // its sub-encounters come from the generator's queue, one a second: all of them, before it is looked at
            var queue = (IEnumerable)typeof(MyGlobalEncountersGenerator).GetField("m_spawnQueue", Any).GetValue(_generator);
            var encounter = _encounter;
            int Queued() => queue.Cast<object>().Count(i => (long)i.GetType().GetField("EncounterId", Any).GetValue(i) == encounter);
            var subs = Queued();
            yield return Until(() => Queued(), v => v == 0, "the encounter's sub-encounters out of the queue", 60);
            yield return Seconds(3);
            var grids = components[_encounter].Select(c => c.Entity).OfType<MyCubeGrid>().ToList();
            Check(grids.Count > 0, "the encounter " + group.Id.SubtypeName + " has no grids");
            var owner = grids.SelectMany(g => g.BigOwners).FirstOrDefault();
            Check(owner != 0 && MySession.Static.Players.IdentityIsNpc(owner), "the encounter's grids are not of an NPC's (owner " + owner + ")");
            var gpsWanted = group.Prefabs.Any(p => p.GpsGrid);
            Check(!gpsWanted || gpss.ContainsKey(_encounter), "the encounter has no GPS though its spawn group gives one");
            var where = grids[0].PositionComp.GetPosition();
            var names = string.Join(", ", grids.Select(g => g.DisplayName + " (" + g.BlocksCount + ")"));

            typeof(MyGlobalEncountersGenerator).GetMethod("RemoveGlobalEncounter", Any).Invoke(_generator, new object[] { _encounter });
            var id = _encounter;
            _encounter = 0;
            yield return Until(() => grids.Count(g => !g.Closed), v => v == 0, "the encounter's grids closed on its removal", 10);
            Check(!components.ContainsKey(id) && !gpss.ContainsKey(id), "the encounter is still registered after its removal");
            Result(group.Id.SubtypeName + " came " + F(where.Length() / 1000) + " km from the centre: " + names + (gpsWanted ? ", with its GPS" : "") +
                   (subs > 0 ? ", " + subs + " sub-encounters" : "") +
                   "; its removal closed them all");
        }

        private static double MySandboxGameTime() => Sandbox.MySandboxGame.TotalGamePlayTimeInMilliseconds / 1000.0;

        public override void Cleanup()
        {
            try
            {
                if (_encounter != 0) typeof(MyGlobalEncountersGenerator).GetMethod("RemoveGlobalEncounter", Any).Invoke(_generator, new object[] { _encounter });
            }
            catch { }
            finally
            {
                if (_cap.HasValue) MySession.Static.Settings.GlobalEncounterCap = _cap.Value;
                base.Cleanup();
            }
        }
    }

    /// <summary>
    /// A script's message to one address (IGC <c>SendUnicastMessage</c>): three stations in reach of each other's
    /// antennas; the message sent to the second's programmable block comes to its <c>UnicastListener</c>, with the
    /// sender's address as its source, and not to the third's.
    /// </summary>
    public sealed class AntennaUnicastCheck : RegressionCheck
    {
        public override string Name => Prefix + "antenna_unicast";

        private const string Sender = "public void Main(string argument) { IGC.SendUnicastMessage(long.Parse(argument.Split(' ')[0]), \"regression\", argument); }";

        private const string Receiver = @"
public Program() { IGC.UnicastListener.SetMessageCallback(""got""); }
public void Main(string argument, UpdateType update)
{
    while (IGC.UnicastListener.HasPendingMessage) { var m = IGC.UnicastListener.AcceptMessage(); Me.CustomData = m.Tag + "" "" + m.Data + "" from "" + m.Source; }
}";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.EnableIngameScripts, "in-game scripts are off in this world");
            yield return Arrive(Site.At(0, 10, -10));
            MyCubeGrid Station(string name, Vector3D at, string script) => Build(name, at, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 1).With(b => Motion.Charged(b)),
                new Part("RadioAntenna/LargeBlockRadioAntenna", 1, 0, 0),
                new Part("MyProgrammableBlock/LargeProgrammableBlock", -1, 0, 0).With(b => ((MyObjectBuilder_MyProgrammableBlock)b).Program = script));
            var grids = new[] { Station("unicast-sender", Site.Origin, Sender), Station("unicast-to", Site.At(150, 0, 0), Receiver), Station("unicast-other", Site.At(-150, 0, 0), Receiver) };
            var send = Block<MyProgrammableBlock>(grids[0]);
            var to = Block<MyProgrammableBlock>(grids[1]);
            var other = Block<MyProgrammableBlock>(grids[2]);
            var antennas = grids.Select(g => Block<MyRadioAntenna>(g)).ToList();
            foreach (var antenna in antennas)
            {
                ((Sandbox.ModAPI.IMyTerminalBlock)antenna).SetValueFloat("Radius", 500f);
                ((Sandbox.ModAPI.IMyTerminalBlock)antenna).SetValueBool("EnableBroadCast", true);
            }
            yield return Until(() => Scripts.Ready(send) && Scripts.Ready(to) && Scripts.Ready(other) && antennas.All(a => a.IsWorking) ? 1 : 0, v => v > 0,
                "the scripts compiled, the antennas on", 30, () => State(send) + "; " + State(to) + "; " + State(other));
            // the antennas find who is in their reach every 100 frames or so: sent again every 2 s until it comes
            var text = to.EntityId + " hello";
            var sent = 0;
            var last = DateTime.MinValue;
            yield return Until(() =>
                {
                    if (to.CustomData.Length > 0) return 1;
                    if ((DateTime.UtcNow - last).TotalSeconds >= 2)
                    {
                        last = DateTime.UtcNow;
                        sent++;
                        Request(send, "RunProgramRequest", Scripts.Argument(text), Sandbox.ModAPI.Ingame.UpdateType.Terminal);
                    }
                    return 0;
                }, v => v > 0, "the message at its address", 20,
                () => "sent " + sent + " times; " + send.DetailedInfo.ToString().Trim() + " | " + to.DetailedInfo.ToString().Trim());
            var expected = "regression " + text + " from " + send.EntityId;
            Check(to.CustomData == expected, "the addressee got '" + to.CustomData + "', not '" + expected + "'");
            yield return Seconds(2);
            Check(other.CustomData.Length == 0, "a station the message was not for got it: '" + other.CustomData + "'");
            Result("the message to one programmable block came to it alone, from the sender's address (sent " + sent + " times)");
        }
    }

    /// <summary>
    /// The procedural encounters of space (<c>MyEncounterGenerator</c>, placed by the asteroid cells as a player comes
    /// near): one placed in a cell comes with its grids, of an NPC's, and when the cell goes (<c>RemoveEncounter</c>)
    /// an encounter nobody touched goes with it; one a player has changed (a block taken off) stays in the world, kept
    /// for the save.
    /// </summary>
    public sealed class ProceduralEncounterCheck : RegressionCheck
    {
        public override string Name => Prefix + "procedural_encounter";
        public override int TimeoutSeconds => 120;

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly System.Type Generator = typeof(MySession).Assembly.GetType("Sandbox.Game.World.Generator.MyEncounterGenerator");
        private static object Static => Generator?.GetProperty("Static", Any)?.GetValue(null);
        private readonly List<object> _ids = new List<object>();
        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private List<BoundingBoxD> _cells;
        private List<int> _seeds;

        private static BoundingBoxD BoundingBoxOf(object encounterId) => (BoundingBoxD)encounterId.GetType().GetField("BoundingBox").GetValue(encounterId);

        private IDictionary Entities => (IDictionary)Generator.GetField("m_encounterEntities", Any).GetValue(Static);
        private static VRage.Game.MyEncounterId IdOf(BoundingBoxD box, int seed) => new VRage.Game.MyEncounterId(box, seed, 0);
        private static ICollection<VRage.Game.MyEncounterId> PersistentSet => (ICollection<VRage.Game.MyEncounterId>)Generator.GetField("m_persistentEncounters", Any).GetValue(Static);
        private static ICollection<VRage.Game.MyEncounterId> SpawningSet => (ICollection<VRage.Game.MyEncounterId>)Generator.GetField("m_encounterSpawnInProgress", Any).GetValue(Static);

        protected override IEnumerator Check()
        {
            var generator = Static;
            SkipUnless(MySession.Static.Settings.EnableEncounters && generator != null, "encounters are off in this world");
            SkipUnless(MySession.Static.NPCBlockLimits.HasRemainingPCU(false), "the NPCs' PCU is spent");
            yield return Arrive(Site.At(0, 0, -3));
            // seeds whose encounter (picked from the seed, as the game does) is grids only
            var groups = (MySpawnGroupDefinition[])Generator.GetField("m_encounterSpawnGroups", Any).GetValue(generator);
            SkipUnless(groups != null && groups.Length > 0, "no encounter spawn groups in this game");
            var pick = Generator.GetMethod("PickRandomEncounter", Any);
            // ships with a speed or an AI behaviour fly off (the game unpersists an encounter once a grid is 1 km from
            // the box), and despawn far from a player; armed NPC ships of an encounter get engaged by the combat checks
            // of the sites nearby in the suite: take a group of grids alone that stands still, and the same group for
            // both cells - inert wrecks live to the removal
            var seeds = Enumerable.Range(1, 200).Where(seed =>
            {
                var random = new VRage.Library.Utils.MyRandom();
                random.SetSeed(seed);
                var group = (MySpawnGroupDefinition)pick.Invoke(generator, new object[] { random, groups });
                return group.Prefabs.Count > 0 && group.Voxels.Count == 0 && group.Prefabs.All(p => p.Speed <= 0f && string.IsNullOrWhiteSpace(p.Behaviour));
            }).Take(1).ToList();
            SkipUnless(seeds.Count == 1, "no encounter of grids standing still among 200 seeds");
            seeds.Add(seeds[0]);
            var cells = new[] { Site.At(0, 3000, 0), Site.At(0, 4500, 0) }.Select(c => new BoundingBoxD(c - 200, c + 200)).ToList();
            var acceptable = (bool)Generator.GetMethod("IsAcceptableEncounter", Any).Invoke(null, new object[] { cells[0] });
            SkipUnless(acceptable, "the site is within 15 km of a player start: no encounters there");

            var untouched = new List<MyCubeGrid>();
            var touched = new List<MyCubeGrid>();
            var remove = Generator.GetMethod("RemoveEncounter", new[] { typeof(BoundingBoxD), typeof(int) });
            _cells = cells; _seeds = seeds;
            for (var i = 0; i < 2; i++)
            {
                // the game despawns NPC small grids some 4 km off a player, and the changed cell sits past that:
                // stand by it while its grids come up and get changed
                if (i == 1) StandAt(cells[i].Center + new Vector3D(0, -120, 60), cells[i].Center);
                // a cell left registered or kept-for-save by an unfinished earlier run takes nothing new, and the game's
                // own remove does nothing about the kept ones: clear both first
                PersistentSet.Remove(IdOf(cells[i], seeds[i]));
                remove.Invoke(generator, new object[] { cells[i], seeds[i] });
                var before = new HashSet<object>(Entities.Keys.Cast<object>());
                Generator.GetMethod("PlaceEncounterToWorld", Any).Invoke(generator, new object[] { cells[i], seeds[i], null });
                object id = null;
                var grids = i == 0 ? untouched : touched;
                var center = cells[i].Center;
                yield return Until(() =>
                {
                    // the game's own encounters of the players beside come to the same table: ours is the one of our box
                    id = Entities.Keys.Cast<object>().FirstOrDefault(k => !before.Contains(k) && Vector3D.Distance(BoundingBoxOf(k).Center, center) < 1);
                    grids.Clear();
                    if (id != null && Entities.Contains(id)) grids.AddRange(((IEnumerable)Entities[id]).OfType<MyCubeGrid>());
                    return grids.Count;
                }, v => v > 0, "the grids of encounter " + (i + 1) + " (seed " + seeds[i] + ") of our box", 30,
                    () => "id " + (Entities.Contains(IdOf(cells[i], seeds[i])) ? "" : "not ") + "registered, " +
                          (PersistentSet.Contains(IdOf(cells[i], seeds[i])) ? "kept for the save, " : "") +
                          (SpawningSet.Contains(IdOf(cells[i], seeds[i])) ? "spawning now, " : "not spawning, ") +
                          (MySession.Static.NPCBlockLimits.HasRemainingPCU(false) ? "PCU left" : "no NPC PCU left"));
                // the grids come up asynchronously: under the suite's load the blocks can still be a few ticks off
                yield return Until(() => grids.Sum(g => g.BlocksCount), v => v > 0, "the blocks of encounter " + (i + 1), 30);
                _ids.Add(id);
                _grids.AddRange(grids);
            }
            yield return Seconds(2);
            var owner = untouched.Concat(touched).SelectMany(g => g.BigOwners).FirstOrDefault();
            Check(owner != 0 && MySession.Static.Players.IdentityIsNpc(owner), "the encounters' grids are not of an NPC's (owner " + owner + ")");
            var names = string.Join(", ", untouched.Select(g => g.DisplayName)) + "; " + string.Join(", ", touched.Select(g => g.DisplayName));

            // a player's change to the second: a block of it taken off
            Check(untouched.Count > 0 && touched.Count > 0, "the encounters left " + untouched.Count + " and " + touched.Count + " grids by the end");
            var changed = touched.Where(g => !g.Closed && !g.MarkedForClose && g.GetBlocks().Any())
                .OrderByDescending(g => g.BlocksCount).FirstOrDefault();
            var block = changed?.GetBlocks().FirstOrDefault();
            Check(block != null, "the changed encounter has no live grid with a block to take one off (" +
                string.Join(", ", touched.Select(g => g.DisplayName + (g.Closed ? " closed" : " of " + g.BlocksCount + " blocks"))) + ")");
            changed.RazeBlock(block.Min);
            yield return Seconds(1);
            for (var i = 0; i < 2; i++) remove.Invoke(generator, new object[] { cells[i], seeds[i] });
            yield return Until(() => untouched.Count(g => !g.Closed), v => v == 0, "the untouched encounter's grids gone with the cell", 10);
            yield return Seconds(1);
            var gone = touched.Count(g => g.Closed || g.MarkedForClose);
            Check(gone == 0, gone + " of the changed encounter's " + touched.Count + " grids went with the cell");
            Check(touched.All(g => g.Save), "the changed encounter's grids are not kept for the save");
            Result("two encounters came (" + names + "); the cell's going took the untouched one away and left the changed one, kept for the save");
        }

        public override void Cleanup()
        {
            // each step on its own: one failing must not leave the generator's records of the cells behind (a record
            // kept for the save blocks the cell's next placing), and a failure is told, not swallowed
            void Step(string what, Action step)
            {
                try { step(); }
                catch (Exception e) { Log.Warn("cleanup of {0}: {1}: {2}", Name, what, (e.InnerException ?? e).Message); }
            }
            try
            {
                if (_cells != null)
                {
                    var remove = Generator.GetMethod("RemoveEncounter", new[] { typeof(BoundingBoxD), typeof(int) });
                    for (var i = 0; i < _cells.Count; i++)
                    {
                        var cell = i;
                        Step("the record kept for the save of cell " + (cell + 1), () => PersistentSet.Remove(IdOf(_cells[cell], _seeds[cell])));
                        Step("the removal of cell " + (cell + 1), () => remove.Invoke(Static, new object[] { _cells[cell], _seeds[cell] }));
                    }
                }
                foreach (var grid in _grids.Where(g => !g.Closed)) Step("closing " + grid.DisplayName, grid.Close);
                foreach (var id in _ids.Where(i => i != null)) Step("the record of " + id, () => PersistentSet.Remove((VRage.Game.MyEncounterId)id));
            }
            finally { base.Cleanup(); }
        }
    }
}
