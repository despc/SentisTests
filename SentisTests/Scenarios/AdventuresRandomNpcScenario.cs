using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// Every random NPC of the live SentisAdventures config (<c>ConfigScriptedNpcs</c>, the enabled ones) with every
    /// blueprint it has, spawned the way the plugin's 30-second pass spawns it (<c>NpcProcessor.CheckAndSpawnNpc</c>)
    /// - from a copy of the NPC's config with that one blueprint (a corovan: all of them, it spawns them all), so
    /// the config itself and its file are not touched. The plugin's own spawns are held off for the run (their
    /// cooldowns set an hour ahead, put back after).
    ///
    /// An NPC that needs players around gets a fake player's character out of gravity, far from the other NPCs; one
    /// that spawns without players goes to a random point of its area. Checked for each spawn:
    ///  - the blueprint came: grids remembered by the plugin as this NPC, with its lifetime (a corovan: as one);
    ///  - owned by the NPC member of <c>OwnerFactionTag</c>;
    ///  - in space out of gravity (unless <c>AllowSpawnInGravitation</c>), <c>DistanceToPlayer</c> from the player;
    ///    a base (<c>SpawnOnVoxels</c>) static, on a planet;
    ///  - a corovan: every blueprint, the escort within <c>DistanceBetweenGridsInParty</c> of the main grid;
    ///  - <c>ShowGps</c>: the player within <c>NotificationsInRadius</c> got the GPS <c>NpcSpawnGpsName</c>;
    ///  - a corovan's <c>CorovanPlayerDetectedMessage</c>: a player 5 km from it is counted as told.
    /// The spawned grids are closed after each check. Arguments: <c>only=</c> (NPC ids containing one of them).
    /// </summary>
    public sealed class AdventuresRandomNpcScenario : TestScenario
    {
        public const string ScenarioName = "adv_random_npc";
        public override string Name => ScenarioName;
        public override string[] Requires => new[] { "SentisAdventures" };
        public override int TimeoutSeconds => 1500;

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private readonly List<Action> _restore = new List<Action>();
        private readonly List<MyCubeGrid> _spawned = new List<MyCubeGrid>();

        private Type _processor;
        private object _repo;

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var pluginType = Integrations.TypeOf("SentisAdventures", "SentisAdventures.SentisAdventuresPlugin");
            _processor = Integrations.TypeOf("SentisAdventures", "SentisAdventures.Adventures.WorldRandomNPC.NpcProcessor");
            var spawner = Integrations.TypeOf("SentisAdventures", "SentisAdventures.NPCSpawner");
            SkipUnless(pluginType != null && _processor != null && spawner != null, "SentisAdventures is not loaded");
            var config = pluginType.GetProperty("Config", Any).GetValue(null);
            _repo = _processor.GetField("NpcRepository", Any).GetValue(null);
            SkipUnless(config != null && _repo != null, "SentisAdventures has no config or database yet");
            var blueprintPath = (string)config.GetType().GetProperty("RandomNpcBlueprintPath").GetValue(config);
            var all = ((IEnumerable)config.GetType().GetProperty("ConfigScriptedNpcs").GetValue(config)).Cast<object>().ToList();
            var only = Regex.Match(TestRunner.Focus ?? "", @"only=([\w,\-]+)") is var m && m.Success ? m.Groups[1].Value.Split(',') : null;
            var npcs = all.Where(n => (bool)P(n, "Enabled") && (only == null || only.Any(o => ((string)P(n, "Id")).Contains(o)))).ToList();
            Note("config: " + all.Count + " NPCs, " + all.Count(n => (bool)P(n, "Enabled")) + " enabled, checking " + npcs.Count + "; disabled: " +
                 string.Join(", ", all.Where(n => !(bool)P(n, "Enabled")).Select(n => P(n, "Id"))));
            Check(npcs.Count > 0, "no enabled NPC to check");

            // the plugin's own spawns wait an hour
            var saveCooldown = _repo.GetType().GetMethod("SaveCooldown");
            var getCooldown = _repo.GetType().GetMethod("GetCooldown");
            foreach (var npc in all)
            {
                var id = (string)P(npc, "Id");
                var was = getCooldown.Invoke(_repo, new object[] { id });
                var at = was == null ? (DateTime?)null : (DateTime)P(was, "LastSpawnTime");
                saveCooldown.Invoke(_repo, new object[] { id, DateTime.Now.AddHours(1) });
                // no cooldown before: one long past is the same
                _restore.Add(() => saveCooldown.Invoke(_repo, new object[] { id, at ?? DateTime.Now.AddDays(-1) }));
            }
            _gameplay.Set("DisableAnyDamageAfterStartTime", 0);

            var spot = FarSpot();
            FakeClients.RemoveAll();
            FakeClients.Add(1, Network, i => (spot, 0, 0), withCharacters: true);
            yield return Wait(() => FakeClients.HasLiveCharacter(0), "the fake player's character", 60);
            var player = FakeClients.PlayerOf(0);
            var me = player.Identity.IdentityId;
            Note("the player out of gravity at " + Fmt(spot) + ", " + (NearestNpc(spot) / 1000).ToString("F0") + " km from the nearest NPC");

            var problems = new List<string>();
            var done = 0;
            foreach (var npc in npcs)
            {
                var id = (string)P(npc, "Id");
                var blueprints = ((IEnumerable)P(npc, "NpcBlueprints")).Cast<object>().ToList();
                var corovan = P(npc, "NpcsSpawnType").ToString() == "COROVAN";
                var runs = corovan ? new List<List<object>> { blueprints } : blueprints.Select(b => new List<object> { b }).ToList();
                var existing = Records().Where(r => (string)P(r, "NpcName") == id).Select(r => MyEntities.GetEntityById((long)P(r, "EntityId")) as MyCubeGrid).Where(g => g != null).ToList();
                Note(id + ": " + existing.Count + " grids of it in the world now (max " + P(npc, "MaxNpcInArea") + " NPCs)" +
                     (corovan && existing.Count > 0 ? ", the nearest " + (existing.Min(g => Vector3D.Distance(g.PositionComp.GetPosition(), spot)) / 1000).ToString("F0") + " km off" : ""));
                foreach (var run in runs)
                {
                    var what = id + " / " + (corovan ? "corovan of " + run.Count : (string)P(run[0], "Blueprint"));
                    var missing = run.Select(b => (string)P(b, "Blueprint")).Where(b => !File.Exists(Path.Combine(blueprintPath, b))).ToList();
                    if (missing.Count > 0)
                    {
                        problems.Add(what + ": no file " + string.Join(", ", missing));
                        Note("FAIL " + what + ": no file " + string.Join(", ", missing));
                        continue;
                    }
                    var check = SpawnOne(npc, run, corovan, me, spot, what, problems);
                    while (check.MoveNext()) yield return check.Current;
                    done++;
                }
            }

            Note("ADV RANDOM NPC RESULT | " + done + " spawns of " + npcs.Count + " NPCs; " +
                 (problems.Count == 0 ? "all fine" : problems.Count + " problems: " + string.Join(" | ", problems)));
            Check(problems.Count == 0, problems.Count + " problems: " + string.Join(" | ", problems));
        }

        private IEnumerator SpawnOne(object npc, List<object> blueprints, bool corovan, long me, Vector3D spot, string what, List<string> problems)
        {
            var id = (string)P(npc, "Id");
            var copy = Copy(npc, blueprints);
            var needsPlayer = (int)P(npc, "SpawnOnlyIfEnemyCountAround") > 0;
            var character = FakeClients.Character(0);
            // the player stays put; an NPC that needs none goes to a random point of its area
            FakeClients.MoveTo(0, spot);
            yield return WaitForTicks(30);
            if (character != null && character.IsClientOnline != true) character.IsClientOnline = true;
            var playerAt = FakeClients.Character(0)?.PositionComp.GetPosition() ?? spot;

            var before = new HashSet<long>(Records().Select(r => (long)P(r, "EntityId")));
            var gpsBefore = Gps(me).Count;
            // a pass makes one try; one without players gets the tries of several passes (the game: one a cooldown)
            var started = false;
            var tries = 0;
            var maxTries = needsPlayer ? 1 : 20;
            while (!started && tries < maxTries)
            {
                tries++;
                try { started = (bool)_processor.GetMethod("CheckAndSpawnNpc", Any).Invoke(null, new[] { copy }); }
                catch (TargetInvocationException e) { problems.Add(what + ": " + e.InnerException?.Message); break; }
            }
            if (!started)
            {
                problems.Add(what + ": no place found in " + tries + " passes");
                Note("FAIL " + what + ": no place in " + tries + " passes");
                yield break;
            }

            // blueprints load in the background: wait until the records stop coming
            List<object> mine = null;
            var quiet = 0;
            var lastCount = 0;
            for (var t = 0; t < 60 && quiet < 4; t++)
            {
                yield return WaitForSeconds(1, what + " spawning");
                mine = Records().Where(r => !before.Contains((long)P(r, "EntityId")) && (string)P(r, "NpcName") == id).ToList();
                quiet = mine.Count > 0 && mine.Count == lastCount ? quiet + 1 : 0;
                lastCount = mine.Count;
            }
            var grids = (mine ?? new List<object>()).Select(r => MyEntities.GetEntityById((long)P(r, "EntityId")) as MyCubeGrid).Where(g => g != null).ToList();
            _spawned.AddRange(grids);
            foreach (var g in grids) Track(g);
            if (grids.Count == 0)
            {
                problems.Add(what + ": nothing spawned in 60 s");
                Note("FAIL " + what + ": nothing spawned");
                yield break;
            }

            var errors = new List<string>();
            var owner = (long)_processor.GetMethod("GetOwner").Invoke(null, new object[] { P(npc, "OwnerFactionTag") });
            var main = grids.OrderByDescending(g => g.BlocksCount).First();
            var at = main.PositionComp.WorldAABB.Center;
            var gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(at).Length();
            var lifetime = TimeSpan.FromMinutes((int)P(npc, "LifeTime"));
            var foreign = grids.SelectMany(g => g.BigOwners).Where(o => o != owner).Distinct().ToList();
            if (owner == 0) errors.Add("no NPC in faction " + P(npc, "OwnerFactionTag"));
            if (foreign.Count > 0 || main.BigOwners.Count == 0) errors.Add("owners " + string.Join(",", grids.SelectMany(g => g.BigOwners).Distinct()) + ", not " + owner);
            if (mine.Any(r => (TimeSpan)P(r, "MaxLifetime") != lifetime)) errors.Add("lifetime not " + lifetime.TotalMinutes + " min");
            if (mine.Any(r => (bool)P(r, "IsCorovan") != corovan)) errors.Add("corovan flag wrong");

            var onVoxels = (bool)P(blueprints[0], "SpawnOnVoxels");
            var place = "";
            if (onVoxels)
            {
                var planet = MyGamePruningStructure.GetClosestPlanet(at);
                if (!main.IsStatic) errors.Add("the base is not static");
                if (gravity < 0.01f) errors.Add("the base is out of gravity");
                place = "base on " + (planet?.StorageName ?? "?") + ", " + gravity.ToString("F2") + " g";
            }
            else
            {
                if (gravity > 0.001f && !(bool)P(npc, "AllowSpawnInGravitation")) errors.Add("in gravity " + gravity.ToString("F2") + " g");
                place = "space, " + gravity.ToString("F2") + " g";
            }
            if (needsPlayer)
            {
                var distance = Vector3D.Distance(at, playerAt);
                var wanted = (int)P(npc, "DistanceToPlayer");
                place += ", " + distance.ToString("F0") + " m from the player (" + wanted + ")";
                if (Math.Abs(distance - wanted) > 300 + main.PositionComp.WorldVolume.Radius) errors.Add(distance.ToString("F0") + " m from the player, not " + wanted);
            }
            if (corovan)
            {
                var party = (int)P(npc, "DistanceBetweenGridsInParty");
                var far = grids.Max(g => Vector3D.Distance(g.PositionComp.WorldAABB.Center, at));
                place += ", " + grids.Count + " grids within " + far.ToString("F0") + " m";
                if (grids.Count < blueprints.Count) errors.Add(grids.Count + " grids for " + blueprints.Count + " blueprints");
                if (far > party + 1500) errors.Add("escort " + far.ToString("F0") + " m from the main grid");
            }

            yield return WaitForSeconds(1, "the GPS");
            if ((bool)P(npc, "ShowGps"))
            {
                var radius = (double)P(npc, "NotificationsInRadius");
                var name = (string)P(npc, "NpcSpawnGpsName");
                var shouldGet = Vector3D.Distance(at, playerAt) <= radius;
                var got = Gps(me).Skip(gpsBefore).Any(g => g.Name == name);
                if (shouldGet && !got) errors.Add("no GPS '" + name + "' for the player " + (Vector3D.Distance(at, playerAt) / 1000).ToString("F0") + " km off");
                place += ", GPS " + (got ? "given" : shouldGet ? "MISSING" : "not for this player (too far)");
            }

            if (corovan && !string.IsNullOrEmpty((string)P(npc, "CorovanPlayerDetectedMessage")))
            {
                // the plugin looks at the first corovan of the id it has (one at a time in a game: MaxNpcInArea)
                var first = Records().Where(r => (bool)P(r, "IsCorovan") && (string)P(r, "NpcName") == id)
                    .Select(r => MyEntities.GetEntityById((long)P(r, "EntityId")) as MyCubeGrid).FirstOrDefault(g => g != null) ?? main;
                if (first != main && !grids.Contains(first)) place += ", told around the older corovan " + (Vector3D.Distance(first.PositionComp.GetPosition(), at) / 1000).ToString("F0") + " km off";
                var near = first.PositionComp.GetPosition();
                FakeClients.MoveTo(0, near + Vector3D.Normalize(near - (MyGamePruningStructure.GetClosestPlanet(near)?.PositionComp.GetPosition() ?? Vector3D.Zero)) * 5000);
                yield return WaitForSeconds(3, "the player goes up to the corovan");
                _processor.GetMethod("NotifyPlayersAroundCorovans", Any).Invoke(null, null);
                var zone = (IDictionary)_processor.GetField("PlayersInCorovanZone", Any).GetValue(null);
                var told = zone.Contains(id) && ((IEnumerable)zone[id]).Cast<object>().Any(p => ReferenceEquals(p, FakeClients.PlayerOf(0)));
                if (!told) errors.Add("a player 5 km from the corovan was not told");
                place += ", player near it " + (told ? "told" : "NOT told");
                zone.Remove(id);
                FakeClients.MoveTo(0, spot);
            }

            Note((errors.Count == 0 ? "ok   " : "FAIL ") + what + ": " + grids.Count + " grids, main «" + main.DisplayName + "» " + main.BlocksCount +
                 " blocks, owner " + P(npc, "OwnerFactionTag") + ", " + place + (tries > 1 ? ", placed on pass " + tries : "") + (errors.Count == 0 ? "" : " | " + string.Join("; ", errors)));
            if (errors.Count > 0) problems.Add(what + ": " + string.Join("; ", errors));

            foreach (var g in grids)
                if (!g.MarkedForClose) g.Close();
            foreach (var gps in Gps(me).Skip(gpsBefore).ToList())
                Sandbox.ModAPI.MyAPIGateway.Session.GPS.RemoveGps(me, gps);
            yield return WaitForSeconds(2, "the grids go");
        }

        /// <summary>A copy of the NPC's config with these blueprints and room for one more of it.</summary>
        private static object Copy(object npc, List<object> blueprints)
        {
            var type = npc.GetType();
            var copy = Activator.CreateInstance(type);
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                if (p.Name == "NpcBlueprints" || p.Name == "DensityRelatedNpcs") continue;
                p.SetValue(copy, p.GetValue(npc));
            }
            var list = (IList)P(copy, "NpcBlueprints");
            foreach (var b in blueprints) list.Add(b);
            var related = (IList)P(copy, "DensityRelatedNpcs");
            foreach (var r in (IEnumerable)P(npc, "DensityRelatedNpcs")) related.Add(r);
            type.GetProperty("MaxNpcInArea").SetValue(copy, int.MaxValue);
            type.GetProperty("SpawnChance").SetValue(copy, 1.0);
            return copy;
        }

        private List<object> Records()
        {
            var collection = _repo.GetType().GetMethod("GetRandomNpc").Invoke(_repo, null);
            return ((IEnumerable)collection.GetType().GetMethod("FindAll").Invoke(collection, null)).Cast<object>().ToList();
        }

        private double NearestNpc(Vector3D at)
        {
            var nearest = double.MaxValue;
            foreach (var r in Records())
                if (MyEntities.GetEntityById((long)P(r, "EntityId")) is MyCubeGrid g)
                    nearest = Math.Min(nearest, Vector3D.Distance(g.PositionComp.GetPosition(), at));
            return nearest;
        }

        /// <summary>Out of gravity and at least 150 km from every NPC of the plugin.</summary>
        private Vector3D FarSpot()
        {
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + "regression-anchor").Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            var up = planet == null ? Vector3D.Up : Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            var side = Vector3D.CalculatePerpendicularVector(up);
            for (var d = 400000.0; d <= 4000000; d += 100000)
            for (var k = 0; k < 8; k++)
            {
                var a = Math.PI * 2 * k / 8;
                var at = anchor + up * d + (side * Math.Cos(a) + Vector3D.Cross(up, side) * Math.Sin(a)) * 300000;
                if (MyGravityProviderSystem.CalculateNaturalGravityInPoint(at).Length() < 0.001f && NearestNpc(at) > 150000) return at;
            }
            return anchor + up * 4000000;
        }

        private static List<VRage.Game.ModAPI.IMyGps> Gps(long identity) =>
            Sandbox.ModAPI.MyAPIGateway.Session.GPS.GetGpsList(identity) ?? new List<VRage.Game.ModAPI.IMyGps>();

        private static object P(object o, string name) => o.GetType().GetProperty(name, Any).GetValue(o);
        private static string Fmt(Vector3D v) => v.X.ToString("F0") + ":" + v.Y.ToString("F0") + ":" + v.Z.ToString("F0");

        public override void Cleanup()
        {
            try
            {
                foreach (var g in _spawned)
                    if (!g.MarkedForClose) g.Close();
                _spawned.Clear();
                foreach (var restore in _restore)
                    try { restore(); }
                    catch (Exception e) { Log.Error(e, ScenarioName + ": putting a cooldown back"); }
                _restore.Clear();
                FakeClients.RemoveAll();
                _gameplay.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
