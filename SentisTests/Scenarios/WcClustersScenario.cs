using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// WeaponCore's turrets (the mod, on a world that has it) in SentisClusters' clusters, attached and detached.
    ///
    /// <c>pairs</c> spots 30 km apart along the world's X axis (a cluster each), each a player's station - a floor of
    /// armor, batteries and the turrets of <c>turrets</c> (type/subtype, comma between), stocked with their magazines -
    /// with a fake player by it, and the pirates' targets 600 m in front: a static wall of heavy armor and a ship of heavy
    /// armor put back in front every phase, each with a turret of its own and no ammunition (WeaponCore fires at threats
    /// only). WeaponCore runs on the game thread, in its session; a detached cluster's
    /// tick may run beside it: its turrets turned, its rays and projectiles, its damage to the pirates' blocks all touch
    /// a cluster whose thread may be stepping it.
    ///
    /// <c>heap=N</c> boxes of armor of nobody's 300 m to the side of each spot, drawn together every 2 s: the physics'
    /// load that makes a detached cluster's tick long enough to run into the next frame, beside WeaponCore.
    ///
    /// Phases (<c>modes=none,odd,all,none</c>, <c>phase</c> s each): which spots are detached (none, every second one,
    /// all; <c>!clusters detach x y z</c> / <c>!clusters attach all</c>). Measured in each, by spot: the magazines spent,
    /// the damage to the wall and the ship; for the phase: the server's frames and SentisClusters' counters (exceptions in
    /// the clusters' threads, the game thread's waits for a tick, changes from other threads). A measure: it fails when
    /// no turret fired with all attached, or when the clusters' threads threw.
    /// </summary>
    internal sealed class WcClustersScenario : TestScenario
    {
        public const string ScenarioName = "wc_clusters";
        private const string Prefix = "wc-clusters-";
        private const double SpotsApart = 30000;
        private const double TargetAway = 600;
        private const int TurretStep = 8;

        private readonly ConfigOverride _optimisations = new ConfigOverride(ConfigOverride.Optimisations);
        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private readonly List<ulong> _protected = new List<ulong>();
        private uint? _autoSave;
        private string _threat;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 1800;

        private sealed class Spot
        {
            public int Index;
            public Vector3D Centre;
            public MyCubeGrid Station, Wall, Ship;
            public List<MyCubeBlock> Turrets = new List<MyCubeBlock>();
            public double WallWhole, ShipWhole;
            public double Ammo;
            public List<MyCubeGrid> Heap = new List<MyCubeGrid>();
            public Vector3D HeapCentre;
        }

        private static int Arg(string name, int otherwise)
        {
            var m = Regex.Match(TestRunner.Focus ?? "", @"\b" + name + @"=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : otherwise;
        }

        private static string Text(string name, string otherwise)
        {
            var m = Regex.Match(TestRunner.Focus ?? "", @"\b" + name + @"=([\w,/]+)");
            return m.Success ? m.Groups[1].Value : otherwise;
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            Check(AppDomain.CurrentDomain.GetAssemblies().Any(a => { try { return a.GetType("CoreSystems.Session", false) != null; } catch { return false; } }), "no WeaponCore in this game");
            var pairs = Arg("pairs", 3);
            var phaseSeconds = Arg("phase", 40);
            var modes = Text("modes", "none,odd,all,none").Split(',');
            var turretIds = Text("turrets", "LargeMissileTurret/Sentis_40mm_Turret_LG,LargeMissileTurret/Sentis_150mm_Turret_LG,LargeGatlingTurret/Sentis_Tempest_Turret_LG").Split(',');
            var heap = Arg("heap", 0);
            var turrets = turretIds.Select(t => MyDefinitionManager.Static.GetCubeBlockDefinition(MyDefinitionId.Parse("MyObjectBuilder_" + t)) as MyWeaponBlockDefinition).ToList();
            Check(turrets.All(t => t != null), "no weapon block among " + string.Join(", ", turretIds.Where((t, i) => turrets[i] == null)));
            _threat = turrets[0].Id.SubtypeName;
            var pirates = MyPirateAntennas.GetPiratesId();
            Check(pirates != 0, "no pirates' identity in this world");
            // SentisGameplayImprovements blocks all damage for a while after the start
            _gameplay.Set("DisableAnyDamageAfterStartTime", 0);
            _autoSave = MySession.Static.Settings.AutoSaveInMinutes;
            MySession.Static.Settings.AutoSaveInMinutes = 0;

            // out of every planet's gravity, the spots along X
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + "regression-anchor").Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            var up = planet == null ? Vector3D.Up : Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            var origin = anchor;
            for (var d = 300000.0; d <= 3000000; d += 50000)
            {
                origin = anchor + up * d + Vector3D.Forward * 250000;
                var clear = true;
                for (var p = 0; p < pairs && clear; p++)
                    clear = MyGravityProviderSystem.CalculateNaturalGravityInPoint(origin + Vector3D.Right * (p * SpotsApart)).Length() < 0.001f;
                if (clear) break;
            }
            var spots = Enumerable.Range(0, pairs).Select(p => new Spot { Index = p, Centre = origin + Vector3D.Right * (p * SpotsApart) }).ToList();

            // a fake player by every spot, invulnerable and out of the turrets' way: the freezer leaves the grids by them alone
            FakeClients.RemoveAll();
            FakeClients.Add(pairs, new FakeClients.NetworkProfile { RttMs = 50 }, i => (spots[i].Centre + Vector3D.Backward * 60 + Vector3D.Up * 20, 0, 0), withCharacters: true);
            for (var i = 0; i < FakeClients.Count; i++)
            {
                var steamId = FakeClients.PlayerOf(i).Id.SteamId;
                _protected.Add(steamId);
                MySession.Static.RemoteAdminSettings[steamId] = AdminSettingsEnum.Invulnerable | AdminSettingsEnum.Untargetable;
            }
            var arrive = WaitForSeconds(5, "the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;

            foreach (var spot in spots)
            {
                var owner = FakeClients.PlayerOf(spot.Index).Identity.IdentityId;
                var span = turrets.Count * TurretStep;
                var blocks = new List<BlockSpec>();
                for (var x = -2; x <= span; x++)
                for (var z = -3; z <= 3; z++)
                    blocks.Add(new BlockSpec(z == -3 && x % 3 == 0 ? "LargeBlockBatteryBlock" : "LargeBlockArmorBlock", new Vector3I(x, -1, z)));
                for (var t = 0; t < turrets.Count; t++)
                    blocks.Add(new BlockSpec(turrets[t].Id.SubtypeName, new Vector3I(t * TurretStep, 0, 0)));
                spot.Station = Spawn(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "station-" + spot.Index, MyCubeSize.Large, true, spot.Centre, blocks), owner);
                spot.Turrets = spot.Station.GetFatBlocks().Where(b => turrets.Any(t => t.Id == b.BlockDefinition.Id)).ToList();
                Check(spot.Turrets.Count == turrets.Count, "spot " + spot.Index + ": " + spot.Turrets.Count + " of " + turrets.Count + " turrets built");
                var wallBlocks = new List<BlockSpec>();
                for (var x = -10; x <= 10; x++)
                for (var y = -6; y <= 10; y++)
                    wallBlocks.Add(new BlockSpec("LargeHeavyBlockArmorBlock", new Vector3I(x, y, 0)));
                wallBlocks.Add(new BlockSpec("LargeBlockBatteryBlock", new Vector3I(0, 0, 1)));
                // a turret of its own, no ammunition: WeaponCore fires only at a threat (TargetInfo.OffenseRating > 0 -
                // a grid with weapons of its own, armed, or coming on fast); a grid of armor it leaves alone
                wallBlocks.Add(new BlockSpec(turrets[0].Id.SubtypeName, new Vector3I(0, 11, 0)));
                spot.Wall = Spawn(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "wall-" + spot.Index, MyCubeSize.Large, true, spot.Centre + Vector3D.Forward * TargetAway + Vector3D.Right * (span * 1.25), wallBlocks), pirates);
                yield return null;
            }
            // the physics' load: armor boxes of nobody's (no turret takes them) 300 m to the side, drawn together every 2 s
            foreach (var spot in spots)
            {
                spot.HeapCentre = spot.Centre + Vector3D.Left * 300 + Vector3D.Up * 50;
                var side = (int)Math.Ceiling(Math.Pow(Math.Max(1, heap), 1.0 / 3));
                for (var k = 0; k < heap; k++)
                {
                    var box = new List<BlockSpec>();
                    for (var x = 0; x < 2; x++)
                    for (var y = 0; y < 2; y++)
                    for (var z = 0; z < 2; z++)
                        box.Add(new BlockSpec("LargeHeavyBlockArmorBlock", new Vector3I(x, y, z)));
                    var at = spot.HeapCentre + new Vector3D(k % side, k / side % side, k / side / side) * 8 - new Vector3D(side * 4);
                    var grid = WorldApi.SpawnGrid(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "heap-" + spot.Index + "-" + k, MyCubeSize.Large, false, at, box));
                    Track(grid);
                    _grids.Add(grid);
                    spot.Heap.Add(grid);
                    if (k % 10 == 9) yield return null;
                }
            }
            yield return WaitForTicks(60);
            foreach (var spot in spots)
            {
                WorldApi.EnsureDistributor(spot.Station);
                WorldApi.ChargeBatteries(spot.Station);
                WorldApi.ChargeBatteries(spot.Wall);
            }
            Note("WC CLUSTERS | " + pairs + " spots " + SpotsApart / 1000 + " km apart along X from " + origin.ToString("F0") + ", turrets " + string.Join(", ", turretIds) +
                 "; the pirates' wall and ship " + TargetAway + " m in front; phases " + string.Join(", ", modes) + ", " + phaseSeconds + " s each");

            var results = new List<string>();
            var firedAttached = false;
            var thrownBefore = Counter("Run.ClusterRunner", "Exceptions");
            foreach (var mode in modes)
            {
                ClusterCommand("!clusters attach all");
                yield return WaitForTicks(30);
                foreach (var spot in spots)
                {
                    PlaceShip(spot, pirates);
                    Stock(spot);
                    WorldApi.ChargeBatteries(spot.Station);
                    spot.WallWhole = Integrity(spot.Wall);
                }
                yield return WaitForTicks(30);
                foreach (var spot in spots)
                {
                    spot.ShipWhole = Integrity(spot.Ship);
                    spot.Ammo = Ammo(spot);
                    var detach = mode == "all" || mode == "odd" && spot.Index % 2 == 1;
                    if (detach) Note(mode + ": spot " + spot.Index + ": " + string.Join(" ", ClusterCommand($"!clusters detach {spot.Centre.X:F0} {spot.Centre.Y:F0} {spot.Centre.Z:F0}")));
                }
                var waits0 = Counter("Run.Detacher", "EntityWaits");
                var foreign0 = Counter("Run.Detacher", "ForeignChanges");
                TickMetrics.Take();
                var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
                var lastPull = -2.0;
                var wait = WaitForSeconds(phaseSeconds, mode);
                while (wait.MoveNext())
                {
                    if (heap > 0 && phaseWatch.Elapsed.TotalSeconds - lastPull >= 2)
                    {
                        lastPull = phaseWatch.Elapsed.TotalSeconds;
                        foreach (var spot in spots)
                        foreach (var box in spot.Heap)
                        {
                            if (box.MarkedForClose || box.Physics == null) continue;
                            var towards = spot.HeapCentre - box.PositionComp.GetPosition();
                            if (towards.LengthSquared() > 1) box.Physics.LinearVelocity = (Vector3)(Vector3D.Normalize(towards) * Math.Min(15, towards.Length() / 3));
                        }
                    }
                    yield return wait.Current;
                }
                var metrics = TickMetrics.Take();
                var line = mode + ": " + string.Join("; ", spots.Select(s => "spot " + s.Index + (Detached(s) ? " detached" : "") + " fired " + (s.Ammo - Ammo(s)).ToString("F0") +
                                                                               ", wall -" + (s.WallWhole - Integrity(s.Wall)).ToString("F0") + ", ship -" + (s.ShipWhole - Integrity(s.Ship)).ToString("F0") +
                                                                               (s.Ship == null || s.Ship.MarkedForClose ? " (ship gone)" : ""))) +
                           " | ticks " + Ticks() + " | frame avg " + metrics.AvgFrameMs.ToString("F1") + " ms, p99 " + metrics.P99FrameMs.ToString("F1") + " | game thread waits for a tick " + (Counter("Run.Detacher", "EntityWaits") - waits0) +
                           ", changes from other threads " + (Counter("Run.Detacher", "ForeignChanges") - foreign0);
                Note(mode + ": WeaponCore on spot 0: " + WcState(spots[0].Station));
                if (mode == "none" && spots.Any(s => s.Ammo - Ammo(s) > 0 || s.WallWhole - Integrity(s.Wall) > 0)) firedAttached = true;
                Note(line);
                results.Add(line);
            }
            ClusterCommand("!clusters attach all");
            var thrown = Counter("Run.ClusterRunner", "Exceptions") - thrownBefore;
            Note("WC CLUSTERS RESULT | " + string.Join(" || ", results) + " | exceptions in the clusters' threads " + thrown);
            Check(firedAttached, "no WeaponCore turret fired with every spot attached: nothing to compare");
        }

        /// <summary>What WeaponCore holds for a grid: its AI (power, targets it sees) and each weapon (active, ammunition, target).</summary>
        private static string WcState(MyCubeGrid grid)
        {
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var session = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType("CoreSystems.Session", false); } catch { return null; } }).First(x => x != null);
                var ais = (IDictionary)session.GetField("EntityAIs", any).GetValue(session.GetField("I", any).GetValue(null));
                if (!ais.Contains(grid)) return "no AI for the grid";
                var ai = ais[grid];
                var text = "AI power " + Peek(ai, "HasPower") + ", init " + Peek(ai, "AiInit") + ", targets " + Peek(ai, "Targets.Count") + ", sorted " + Peek(ai, "SortedTargets.Count") +
                           ", enemy entities " + Peek(ai, "EnemyEntities") + ", enemies near " + Peek(ai, "EnemiesNear") + ", priority in range " + Peek(ai, "DetectionInfo.PriorityInRange") +
                           ", other in range " + Peek(ai, "DetectionInfo.OtherInRange") + ", owner " + Peek(ai, "AiOwner");
                foreach (DictionaryEntry target in (IDictionary)Peek(ai, "Targets", raw: true))
                    text += "; sees " + ((VRage.Game.Entity.MyEntity)target.Key).DisplayName + " as " + Peek(target.Value, "EntInfo.Relationship") + " " + Peek(target.Value, "EntInfo.Type") + " at " + Peek(target.Value, "Velocity");
                foreach (var comp in (IList)ai.GetType().GetField("WeaponComps", any).GetValue(ai))
                {
                    text += " | " + Peek(comp, "Cube.BlockDefinition.Id.SubtypeName") + " working " + Peek(comp, "IsWorking") + ", status " + Peek(comp, "Status") + ", platform " + Peek(comp, "Platform.State") +
                            ", asleep " + Peek(comp, "IsAsleep") + ", other signals " + Peek(comp, "DetectOtherSignals") + ", control " + Peek(comp, "Data.Repo.Values.State.Control") +
                            ", overrides grids " + Peek(comp, "Data.Repo.Values.Set.Overrides.Grids") + " neutrals " + Peek(comp, "Data.Repo.Values.Set.Overrides.Neutrals") +
                            " unowned " + Peek(comp, "Data.Repo.Values.Set.Overrides.Unowned") + " move " + Peek(comp, "Data.Repo.Values.Set.Overrides.MoveMode") + " focus " + Peek(comp, "Data.Repo.Values.Set.Overrides.FocusTargets");
                    foreach (var w in (IList)Peek(comp, "Platform.Weapons", raw: true))
                        text += "; weapon active " + Peek(w, "TurretActive") + ", out of ammo " + Peek(w, "OutOfAmmo") + ", no mags " + Peek(w, "NoMagsToLoad") + ", ammo " +
                                Peek(w, "ProtoWeaponAmmo.CurrentAmmo") + ", sleeping " + Peek(w, "Acquire.IsSleeping") + ", acquire tick " + Peek(w, "TargetAcquireTick") + ", max range " + Peek(w, "MaxTargetDistance") + ", target " + Peek(w, "Target.HasTarget") + "/" + Peek(w, "Target.TargetState") + ", ammo def " + Peek(w, "ActiveAmmoDef.AmmoDef.AmmoRound");
                }
                return text;
            }
            catch (Exception e) { return "unreadable: " + e.Message; }
        }

        private static object Peek(object of, string path, bool raw = false)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var name in path.Split('.'))
            {
                if (of == null) return raw ? null : "null";
                // the most derived class's member first: WeaponCore's classes hide their bases' (an ambiguous match otherwise)
                object next = null;
                var found = false;
                for (var type = of.GetType(); type != null && !found; type = type.BaseType)
                {
                    var f = type.GetField(name, any | BindingFlags.DeclaredOnly);
                    if (f != null) { next = f.GetValue(of); found = true; continue; }
                    var p = type.GetProperties(any | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.Name == name && x.GetIndexParameters().Length == 0);
                    if (p != null) { next = p.GetValue(of); found = true; }
                }
                if (!found) return raw ? null : "?" + name;
                of = next;
            }
            return raw ? of : of ?? "null";
        }

        private MyCubeGrid Spawn(MyObjectBuilder_CubeGrid ob, long owner)
        {
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            _grids.Add(grid);
            grid.ChangeGridOwnership(owner, MyOwnershipShareModeEnum.Faction);
            return grid;
        }

        /// <summary>A fresh ship of the pirates' in front of the spot's turrets, still: the last one taken away.</summary>
        private void PlaceShip(Spot spot, long pirates)
        {
            if (spot.Ship != null && !spot.Ship.MarkedForClose) spot.Ship.Close();
            var blocks = new List<BlockSpec>();
            for (var x = -3; x <= 3; x++)
            for (var y = -3; y <= 3; y++)
            for (var z = -1; z <= 1; z++)
                blocks.Add(new BlockSpec(x == 0 && y == 0 && z == 0 ? "LargeBlockBatteryBlock" : "LargeHeavyBlockArmorBlock", new Vector3I(x, y, z)));
            blocks.Add(new BlockSpec(_threat, new Vector3I(0, 4, 0)));
            var span = (spot.Turrets.Count - 1) * TurretStep * 2.5 / 2;
            spot.Ship = Spawn(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "ship-" + spot.Index, MyCubeSize.Large, false,
                spot.Centre + Vector3D.Forward * (TargetAway * 0.6) + Vector3D.Right * span + Vector3D.Up * 60, blocks), pirates);
            WorldApi.ChargeBatteries(spot.Ship);
        }

        /// <summary>
        /// Every turret's magazines, as many as fit, up to 20: the ones of the ammunition WeaponCore has active for its
        /// weapons (<c>ActiveAmmoDef.AmmoDefinitionId</c>), not the game's definition's - those the mod's turrets take
        /// no shot from ("out of ammo, no mags" with ten of them in the inventory, V5 29.10.2026).
        /// </summary>
        private static void Stock(Spot spot)
        {
            foreach (var turret in spot.Turrets)
            foreach (var magazine in Magazines(spot.Station, turret))
            {
                if (!(turret.GetInventory(0) is Sandbox.Game.MyInventory box)) continue;
                var fits = Math.Min(20, Math.Floor((double)box.ComputeAmountThatFits(magazine)));
                if (fits > 0) box.AddItems((MyFixedPoint)fits, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(magazine));
            }
        }

        /// <summary>The magazines of the ammunition WeaponCore has active for the turret's weapons (physical items only).</summary>
        private static List<MyDefinitionId> Magazines(MyCubeGrid station, MyCubeBlock turret)
        {
            var result = new List<MyDefinitionId>();
            try
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var session = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType("CoreSystems.Session", false); } catch { return null; } }).First(x => x != null);
                var ais = (IDictionary)session.GetField("EntityAIs", any).GetValue(session.GetField("I", any).GetValue(null));
                if (!ais.Contains(station)) return result;
                var ai = ais[station];
                foreach (var comp in (IList)ai.GetType().GetField("WeaponComps", any).GetValue(ai))
                {
                    if (Peek(comp, "Cube", raw: true) != turret) continue;
                    foreach (var w in (IList)Peek(comp, "Platform.Weapons", raw: true))
                        if (Peek(w, "ActiveAmmoDef.AmmoDefinitionId", raw: true) is MyDefinitionId id && !id.TypeId.IsNull &&
                            MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out _) && !result.Contains(id))
                            result.Add(id);
                }
            }
            catch (Exception) { }
            return result;
        }

        private static double Ammo(Spot spot) => spot.Turrets.Sum(t => t.MarkedForClose ? 0 : Magazines(spot.Station, t).Sum(m => (double)t.GetInventory(0).GetItemAmount(m)));

        private static double Integrity(MyCubeGrid grid) => grid == null || grid.MarkedForClose ? 0 : grid.GetBlocks().Sum(b => (double)b.Integrity);

        private static bool Detached(Spot spot) => string.Join(" ", ClusterCommand($"!clusters here {spot.Centre.X:F0} {spot.Centre.Y:F0} {spot.Centre.Z:F0}")).Contains("detached");

        private static long Counter(string type, string field)
        {
            var t = Integrations.TypeOf("SentisClusters", "SentisClusters." + type);
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var f = t?.GetField(field, any);
            var p = f == null ? t?.GetProperty(field, any) : null;
            try { return f != null ? Convert.ToInt64(f.GetValue(null)) : p != null ? Convert.ToInt64(p.GetValue(null)) : -1; }
            catch { return -1; }
        }

        /// <summary>SentisClusters' detached clusters now: each one's speed and tick, as its web view has them.</summary>
        private static string Ticks()
        {
            try
            {
                var stats = Integrations.TypeOf("SentisClusters", "SentisClusters.Stats.ClusterStats");
                var snapshot = stats?.GetField("Current")?.GetValue(null);
                if (snapshot == null) return "?";
                var rows = ((Array)snapshot.GetType().GetField("Clusters").GetValue(snapshot)).Cast<object>()
                    .Where(r => (bool)r.GetType().GetField("Detached").GetValue(r)).ToList();
                return rows.Count == 0 ? "none detached" : string.Join(", ", rows.Select(r => "speed " + Convert.ToDouble(r.GetType().GetField("SimSpeed").GetValue(r)).ToString("F2") +
                                                                                        " tick " + Convert.ToDouble(r.GetType().GetField("TickMs").GetValue(r)).ToString("F1") + " ms"));
            }
            catch (Exception e) { return "? " + e.Message; }
        }

        private static List<string> ClusterCommand(string text)
        {
            var answers = new List<string>();
            var commands = (Torch.Commands.CommandManager)Torch.TorchBase.Instance.CurrentSession.Managers.GetManager(typeof(Torch.Commands.CommandManager));
            commands?.HandleCommandFromServer(text, message => answers.Add(message.Message));
            return answers;
        }

        public override void Cleanup()
        {
            try
            {
                ClusterCommand("!clusters attach all");
                FakeClients.RemoveAll();
                foreach (var steamId in _protected) MySession.Static?.RemoteAdminSettings.Remove(steamId);
                _protected.Clear();
                foreach (var grid in _grids)
                    if (!grid.MarkedForClose) grid.Close();
                _grids.Clear();
                if (_autoSave.HasValue) MySession.Static.Settings.AutoSaveInMinutes = _autoSave.Value;
                _optimisations.Restore();
                _gameplay.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
