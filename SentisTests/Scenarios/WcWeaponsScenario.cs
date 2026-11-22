using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
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
    /// Every WeaponCore weapon of the world (the blocks WeaponCore runs: <c>Session.PartPlatforms</c>) with every ammunition
    /// it can be set to, fired at a wall of two layers of large heavy armor (21 x 21 blocks of the pirates').
    ///
    /// Each weapon and ammunition has a spot of its own (spots 4 km apart along X, out of gravity, a fake player by each):
    /// the weapon on a static grid of its size with batteries enough for it (WeaponCore's <c>GetMaxPower</c>), its
    /// magazines kept stocked, the wall <c>distance</c> m in front (less for a short-ranged ammunition). The ammunition set
    /// (<c>SetActiveAmmoBase</c>), the wall given as the target (<c>SetWeaponTargetBase</c>), fire on
    /// (<c>ToggleWeaponFireBase</c>) for <c>window</c> s of game time.
    ///
    /// The wall takes no damage: what each hit would do to its block (the game's amount after every multiplier, the
    /// last before-damage handler) is counted and zeroed, so every shot meets a whole wall. Measured: the fire events (the
    /// weapons' <c>FireCounter</c>) and their ticks, the damage and its first tick. Written with the weapon's and the
    /// ammunition's definitions to <c>Instance/SentisTests/wc_weapons.json</c> and a line each to the log.
    ///
    /// Arguments: <c>only=a,b</c> (block subtypes containing one of them), <c>window=60</c>, <c>batch=12</c> spots at once,
    /// <c>distance=500</c>. A measure: it fails only when nothing fired.
    /// </summary>
    internal sealed class WcWeaponsScenario : TestScenario
    {
        public const string ScenarioName = "wc_weapons";
        private const string Prefix = "wc-weapons-";
        private const double SpotsApart = 4000;
        private const int WallHalf = 10;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly ConfigOverride _gameplay = new ConfigOverride(ConfigOverride.Gameplay);
        private readonly List<MyCubeGrid> _grids = new List<MyCubeGrid>();
        private readonly List<ulong> _protected = new List<ulong>();
        private uint? _autoSave;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 4 * 3600;

        private sealed class Pair
        {
            public MyCubeBlockDefinition Block;
            public string Ammo;
            public List<object> Systems = new List<object>();
            public object AmmoType;
            public MyDefinitionId? Magazine;
            public double Distance;
            public float Power;
            // the spot
            public Vector3D Centre;
            public MyCubeGrid Station, Wall;
            public MyCubeBlock Weapon;
            public object Comp;
            // measured
            public string Status = "";
            public string Fired = "";
            public long Start;
            public long FirstFire = -1, FirstHit = -1, LastFire = -1;
            public int LastCounter;
            public List<long> FireTicks = new List<long>();
            public double Damage, Deformation, RawDamage;
            public int Kills;
            public int Hits;
            public int Retoggles;
            public string EndState = "";
            public string Aim = "";
            public int Switches;
        }

        /// <summary>Why a weapon does not fire: the block, WeaponCore's trigger, each weapon's ammunition and reload.</summary>
        private static string State(Pair pair)
        {
            if (pair.Comp == null) return "no comp";
            var text = "working " + pair.Weapon?.IsWorking + ", functional " + pair.Weapon?.IsFunctional + ", comp status " + P(pair.Comp, "Status") +
                       ", trigger " + P(pair.Comp, "Data.Repo.Values.State.Trigger") + "/" + P(pair.Comp, "ShootManager.Signal") + ", mode " + P(pair.Comp, "Data.Repo.Values.Set.Overrides.ShootMode") + ", toggles " + P(pair.Comp, "Data.Repo.Values.State.ToggleCount") + "/" + pair.Retoggles +
                       ", powered " + P(pair.Comp, "Ai.HasPower") + ", mags in box " + (pair.Magazine == null ? "-" : pair.Weapon?.GetInventory(0)?.GetItemAmount(pair.Magazine.Value).ToString());
            if (P(pair.Comp, "Platform.Weapons") is IList weapons)
                foreach (var w in weapons)
                    text += "; ammo " + P(w, "ProtoWeaponAmmo.CurrentAmmo") + ", loading " + P(w, "Loading") + ", no mags " + P(w, "NoMagsToLoad") + ", out " + P(w, "OutOfAmmo") +
                            ", shot ready " + P(w, "ShotReady") + ", charging " + P(w, "Charging") + ", overheated " + P(w, "PartState.Overheated") + ", target " + P(w, "Target.HasTarget");
            return text;
        }

        private static readonly Dictionary<MyCubeGrid, Pair> Walls = new Dictionary<MyCubeGrid, Pair>();
        private static bool _handler;

        private static int Arg(string name, int otherwise)
        {
            var m = Regex.Match(TestRunner.Focus ?? "", @"\b" + name + @"=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : otherwise;
        }

        private static string Text(string name)
        {
            var m = Regex.Match(TestRunner.Focus ?? "", @"\b" + name + @"=([\w,/.\-]+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static object P(object of, string path) => Mods.Peek(of, path);

        private static double D(object of, string path)
        {
            var v = P(of, path);
            try { return v == null ? double.NaN : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return double.NaN; }
        }

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            var sessionType = Mods.Script("CoreSystems.Session");
            SkipUnless(sessionType != null, "no WeaponCore in this world");
            var session = sessionType.GetField("I", Any).GetValue(null);
            var api = (IDictionary<string, Delegate>)P(session, "Api.ModApiMethods");
            Check(api != null, "WeaponCore's API not found");
            var window = Arg("window", 60);
            var batch = Arg("batch", 12);
            var distance = Arg("distance", 500);
            var only = Text("only")?.Split(',');
            var skip = Text("skip")?.Split(',');
            var pirates = MyPirateAntennas.GetPiratesId();
            Check(pirates != 0, "no pirates' identity in this world");

            // what to fire: every block WeaponCore runs, with every ammunition a player can set it to
            var pairs = new List<Pair>();
            var skipped = new List<string>();
            foreach (DictionaryEntry entry in (IDictionary)P(session, "PartPlatforms"))
            {
                var id = (MyDefinitionId)entry.Key;
                if (!(MyDefinitionManager.Static.GetCubeBlockDefinition(id) is MyCubeBlockDefinition block)) continue;
                if (only != null && !only.Any(o => id.SubtypeName.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (skip != null && skip.Any(o => id.SubtypeName.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (!block.Public || !block.Enabled) { skipped.Add(id.SubtypeName + " (not public)"); continue; }
                var systems = new List<object>();
                foreach (var system in ((IDictionary)P(entry.Value, "PartSystems")).Values)
                    if (system.GetType().Name == "WeaponSystem") systems.Add(system);
                if (systems.Count == 0) { skipped.Add(id.SubtypeName + " (no weapon)"); continue; }
                var byAmmo = new Dictionary<string, Pair>();
                foreach (var system in systems)
                foreach (var ammo in (Array)P(system, "AmmoTypes"))
                {
                    if (P(ammo, "IsShrapnel") is true) continue;
                    if (!(P(ammo, "AmmoDef.Const.IsTurretSelectable") is true)) continue;
                    if (P(ammo, "AmmoDef.HardPointUsable") is false) continue;
                    var name = (string)P(ammo, "AmmoDef.AmmoRound");
                    if (!byAmmo.TryGetValue(name, out var pair))
                    {
                        var magazine = (MyDefinitionId)P(ammo, "AmmoDefinitionId");
                        pair = new Pair
                        {
                            Block = block, Ammo = name, AmmoType = ammo,
                            Magazine = !magazine.TypeId.IsNull && MyDefinitionManager.Static.TryGetPhysicalItemDefinition(magazine, out _) ? magazine : (MyDefinitionId?)null,
                        };
                        byAmmo[name] = pair;
                        pairs.Add(pair);
                    }
                    pair.Systems.Add(system);
                }
                if (byAmmo.Count == 0) skipped.Add(id.SubtypeName + " (no selectable ammunition)");
            }
            Note("WC WEAPONS | " + pairs.Count + " weapon/ammunition pairs of " + pairs.Select(p => p.Block.Id).Distinct().Count() + " blocks; skipped: " + string.Join(", ", skipped));
            Check(pairs.Count > 0, "no WeaponCore weapon to fire");

            foreach (var pair in pairs)
            {
                var system = pair.Systems[0];
                var reach = new[] { D(pair.AmmoType, "AmmoDef.Const.MaxTrajectory") * 0.8, D(system, "WConst.MaxTargetDistance") * 0.9, distance }
                    .Where(v => !double.IsNaN(v) && v > 0).Min();
                var min = D(system, "WConst.MinTargetDistance");
                pair.Distance = Math.Max(Math.Max(reach, double.IsNaN(min) ? 0 : min + 20), 40);
                try { pair.Power = (float)api["GetMaxPower"].DynamicInvoke(pair.Block.Id); }
                catch { pair.Power = 0; }
            }

            _gameplay.Set("DisableAnyDamageAfterStartTime", 0);
            _autoSave = MySession.Static.Settings.AutoSaveInMinutes;
            MySession.Static.Settings.AutoSaveInMinutes = 0;
            if (!_handler)
            {
                MyDamageSystem.Static.RegisterBeforeDamageHandler(int.MaxValue, OnDamage);
                _handler = true;
            }

            var origin = Origin(batch);
            var records = new JArray();
            var file = Path.Combine(Torch.TorchBase.Instance.Config.InstancePath, "SentisTests", "wc_weapons.json");
            for (var first = 0; first < pairs.Count; first += batch)
            {
                var group = pairs.Skip(first).Take(batch).ToList();
                var run = RunBatch(group, origin, pirates, api, window, first / batch + 1, (pairs.Count + batch - 1) / batch);
                while (run.MoveNext()) yield return run.Current;
                foreach (var pair in group)
                {
                    var record = Record(pair, window);
                    records.Add(record);
                    Note("WCW " + record.ToString(Newtonsoft.Json.Formatting.None));
                }
                File.WriteAllText(file, records.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            Note("WC WEAPONS RESULT | " + records.Count + " pairs written to " + file + "; fired " + pairs.Count(p => p.FirstFire >= 0) + ", hit " + pairs.Count(p => p.FirstHit >= 0));
            Check(pairs.Any(p => p.FirstFire >= 0), "no weapon fired");
        }

        private static Vector3D Origin(int spots)
        {
            var anchor = WorldApi.PlanetAnchor(WorldApi.EntityPrefix + "regression-anchor").Translation;
            var planet = MyGamePruningStructure.GetClosestPlanet(anchor);
            var up = planet == null ? Vector3D.Up : Vector3D.Normalize(anchor - planet.PositionComp.GetPosition());
            var origin = anchor;
            for (var d = 300000.0; d <= 3000000; d += 50000)
            {
                origin = anchor + up * d + Vector3D.Backward * 250000;
                var clear = true;
                for (var p = 0; p < spots && clear; p++)
                    clear = MyGravityProviderSystem.CalculateNaturalGravityInPoint(origin + Vector3D.Right * (p * SpotsApart)).Length() < 0.001f &&
                            MyGravityProviderSystem.CalculateNaturalGravityInPoint(origin + Vector3D.Right * (p * SpotsApart) + Vector3D.Forward * 3000).Length() < 0.001f;
                if (clear) break;
            }
            return origin;
        }

        private IEnumerator RunBatch(List<Pair> group, Vector3D origin, long pirates, IDictionary<string, Delegate> api, int window, int number, int of)
        {
            for (var i = 0; i < group.Count; i++) group[i].Centre = origin + Vector3D.Right * (i * SpotsApart);
            FakeClients.RemoveAll();
            FakeClients.Add(group.Count, new FakeClients.NetworkProfile { RttMs = 50 }, i => (group[i].Centre + Vector3D.Backward * 50 + Vector3D.Up * 15, 0, 0), withCharacters: true);
            for (var i = 0; i < FakeClients.Count; i++)
            {
                var steamId = FakeClients.PlayerOf(i).Id.SteamId;
                if (!_protected.Contains(steamId)) _protected.Add(steamId);
                MySession.Static.RemoteAdminSettings[steamId] = AdminSettingsEnum.Invulnerable | AdminSettingsEnum.Untargetable;
            }
            var arrive = WaitForSeconds(3, "batch " + number + "/" + of + ": the players arrive");
            while (arrive.MoveNext()) yield return arrive.Current;

            for (var i = 0; i < group.Count; i++)
            {
                var pair = group[i];
                try { Build(pair, FakeClients.PlayerOf(i).Identity.IdentityId, pirates); }
                catch (Exception e) { pair.Status = "not built: " + e.Message; }
                yield return null;
            }
            yield return WaitForTicks(60);
            foreach (var pair in group.Where(p => p.Station != null))
            {
                WorldApi.EnsureDistributor(pair.Station);
                WorldApi.ChargeBatteries(pair.Station);
            }

            // WeaponCore takes the block over, the ammunition set, its magazines in
            var ready = DateTime.UtcNow;
            while ((DateTime.UtcNow - ready).TotalSeconds < 20 && group.Any(p => p.Weapon != null && !Ready(p, session: null)))
                yield return null;
            foreach (var pair in group.Where(p => p.Weapon != null))
            {
                if (!Ready(pair, null)) { pair.Status = "WeaponCore did not take the block (" + P(pair.Comp, "Platform.State") + ")"; continue; }
                var weapons = (IList)P(pair.Comp, "Platform.Weapons");
                for (var w = 0; w < weapons.Count; w++)
                    try { api["SetActiveAmmoBase"].DynamicInvoke(pair.Weapon, w, pair.Ammo); }
                    catch (Exception e) { pair.Status += "ammo not set on weapon " + w + ": " + e.InnerException?.Message + "; "; }
                Stock(pair);
            }
            var loaded = DateTime.UtcNow;
            var lastSwitch = DateTime.UtcNow;
            while ((DateTime.UtcNow - loaded).TotalSeconds < 20 && group.Any(p => p.Comp != null && Active(p) != p.Ammo))
            {
                foreach (var pair in group.Where(p => p.Comp != null)) Stock(pair);
                // a weapon with the default ammunition loaded (or waiting for its magazines) keeps it: the magazine switched
                // as the terminal's ammunition list does, with a reload
                if ((DateTime.UtcNow - lastSwitch).TotalSeconds >= 2)
                {
                    lastSwitch = DateTime.UtcNow;
                    foreach (var pair in group.Where(p => p.Comp != null && p.Magazine != null && Active(p) != p.Ammo))
                    {
                        var weapons = (IList)P(pair.Comp, "Platform.Weapons");
                        for (var w = 0; w < weapons.Count; w++)
                            try { api["SetMagazine"].DynamicInvoke(pair.Weapon, w, pair.Magazine.Value, true); pair.Switches++; }
                            catch (Exception) { }
                    }
                }
                yield return WaitForTicks(10);
            }
            foreach (var pair in group.Where(p => p.Comp != null))
            {
                try { BuildWall(pair, pirates, api); }
                catch (Exception e) { pair.Status += "no wall: " + e.Message + "; "; }
            }
            yield return WaitForTicks(30);
            Note("batch " + number + "/" + of + ": " + string.Join("; ", group.Select(p => p.Block.Id.SubtypeName + "/" + p.Ammo + " active " + Active(p) + ", " + p.Aim + (p.Status == "" ? "" : " " + p.Status))));

            // fire
            var tick = MySession.Static.GameplayFrameCounter;
            foreach (var pair in group.Where(p => p.Comp != null && p.Wall != null && Ready(p, null)))
            {
                pair.Start = tick;
                pair.LastCounter = Counter(pair);
                var weapons = (IList)P(pair.Comp, "Platform.Weapons");
                for (var w = 0; w < weapons.Count; w++)
                    try { api["SetWeaponTargetBase"].DynamicInvoke(pair.Weapon, pair.Wall, w); }
                    catch (Exception) { }
                try { Fire(pair, true); }
                catch (Exception e) { pair.Status += "fire not toggled: " + (e.InnerException ?? e).Message + "; "; }
                lock (Walls) Walls[pair.Wall] = pair;
            }
            var end = tick + window * 60;
            var lastNote = tick;
            while (MySession.Static.GameplayFrameCounter < end)
            {
                var now = MySession.Static.GameplayFrameCounter;
                foreach (var pair in group.Where(p => p.Start > 0))
                {
                    var counter = Counter(pair);
                    if (counter != pair.LastCounter)
                    {
                        for (var k = pair.LastCounter; k < counter; k++) pair.FireTicks.Add(now);
                        if (pair.FirstFire < 0) pair.FirstFire = now;
                        pair.LastFire = now;
                        pair.LastCounter = counter;
                    }
                    if ((now - pair.Start) % 30 == 0)
                    {
                        Stock(pair);
                        // the request is turned down while the block is not working or nothing is loaded yet: asked again
                        if (P(pair.Comp, "Data.Repo.Values.State.Trigger")?.ToString() != "On" || P(pair.Comp, "ShootManager.Signal")?.ToString() != "Manual")
                        {
                            pair.Retoggles++;
                            try { Fire(pair, true); }
                            catch (Exception) { }
                        }
                    }
                }
                if (now - lastNote >= 600)
                {
                    lastNote = now;
                    Note("batch " + number + "/" + of + ": " + (now - tick) / 60 + "/" + window + " s: " +
                         string.Join("; ", group.Select(p => p.Block.Id.SubtypeName + "/" + p.Ammo + " fired " + p.FireTicks.Count + " dmg " + p.Damage.ToString("F0") +
                                                             (p.FireTicks.Count == 0 ? " [" + State(p) + "]" : ""))));
                }
                yield return null;
            }
            foreach (var pair in group.Where(p => p.Start > 0))
            {
                pair.Fired = Active(pair);
                pair.EndState = State(pair);
                try { Fire(pair, false); }
                catch (Exception) { }
            }
            yield return WaitForTicks(120);
            lock (Walls) Walls.Clear();
            foreach (var pair in group)
            {
                foreach (var grid in new[] { pair.Station, pair.Wall })
                    if (grid != null && !grid.MarkedForClose) grid.Close();
                pair.Comp = null;
            }
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);
        }

        private void Build(Pair pair, long owner, long pirates)
        {
            var size = pair.Block.CubeSize;
            var batteryName = size == MyCubeSize.Large ? "LargeBlockBatteryBlock" : "SmallBlockBatteryBlock";
            var battery = MyDefinitionManager.Static.GetCubeBlockDefinition(new MyDefinitionId(typeof(Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock), batteryName)) as MyBatteryBlockDefinition;
            var output = battery?.MaxPowerOutput ?? 12;
            var batteries = Math.Max(2, Math.Min(60, (int)Math.Ceiling(pair.Power * 1.25 / output) + 1));
            var blocks = new List<BlockSpec> { new BlockSpec(pair.Block.Id.SubtypeName, Vector3I.Zero) };
            var bs = battery?.Size ?? Vector3I.One;
            for (var k = 0; k < batteries; k++)
                blocks.Add(new BlockSpec(batteryName, new Vector3I(k % 10 * bs.X, k / 10 * bs.Y, pair.Block.Size.Z)));
            var ob = WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + pair.Block.Id.SubtypeName, size, true, pair.Centre, blocks);
            // the block by its full id: a subtype may be another type's too
            if (VRage.ObjectBuilders.Private.MyObjectBuilderSerializerKeen.CreateNewObject(pair.Block.Id) is MyObjectBuilder_CubeBlock exact)
            {
                exact.Min = ob.CubeBlocks[0].Min;
                exact.BuiltBy = ob.CubeBlocks[0].BuiltBy;
                ob.CubeBlocks[0] = exact;
            }
            pair.Station = Spawn(ob, owner);
            pair.Weapon = pair.Station.GetFatBlocks().FirstOrDefault(b => b.BlockDefinition.Id == pair.Block.Id);
            if (pair.Weapon == null) throw new InvalidOperationException("the weapon block not built");

        }

        /// <summary>
        /// The wall across where the weapon's muzzles point now (<c>GetMuzzleInfoBase</c>: a fixed gun's barrels need not
        /// point the block's forward, a turret rests where its model has it), <c>Distance</c> m from them.
        /// </summary>
        private void BuildWall(Pair pair, long pirates, IDictionary<string, Delegate> api)
        {
            var muzzles = new List<MyTuple<Vector3D, Vector3D, Vector3D, Vector3D, MatrixD, MatrixD>>();
            try { api["GetMuzzleInfoBase"].DynamicInvoke(pair.Weapon, 0, muzzles); }
            catch (Exception) { }
            var from = muzzles.Count > 0 ? muzzles.Aggregate(Vector3D.Zero, (a, m) => a + m.Item1) / muzzles.Count : pair.Weapon.PositionComp.WorldAABB.Center;
            var dir = muzzles.Count > 0 ? muzzles.Aggregate(Vector3D.Zero, (a, m) => a + m.Item3) : Vector3D.Zero;
            dir = dir.LengthSquared() > 1e-6 ? Vector3D.Normalize(dir) : (Vector3D)pair.Weapon.WorldMatrix.Forward;
            pair.Aim = muzzles.Count + " muzzles, along " + Vector3D.Dot(dir, pair.Weapon.WorldMatrix.Forward).ToString("F2") + " of the block's forward";
            var at = from + dir * pair.Distance;
            var up = Vector3D.CalculatePerpendicularVector(dir);
            var wall = new List<BlockSpec>();
            for (var x = -WallHalf; x <= WallHalf; x++)
            for (var y = -WallHalf; y <= WallHalf; y++)
            for (var z = 0; z <= 1; z++)
                wall.Add(new BlockSpec("LargeHeavyBlockArmorBlock", new Vector3I(x, y, z)));
            // the grid's forward along the shots: its layers (z) one behind the other across them
            pair.Wall = Spawn(WorldApi.GridOb(WorldApi.EntityPrefix + Prefix + "wall-" + pair.Block.Id.SubtypeName, MyCubeSize.Large, true, at, wall, (Vector3)dir, (Vector3)up), pirates);
        }

        private MyCubeGrid Spawn(MyObjectBuilder_CubeGrid ob, long owner)
        {
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            _grids.Add(grid);
            grid.ChangeGridOwnership(owner, MyOwnershipShareModeEnum.Faction);
            return grid;
        }

        private static object CompOf(MyCubeBlock block)
        {
            var session = Mods.Script("CoreSystems.Session").GetField("I", Any).GetValue(null);
            return P(session, "IdToCompMap") is IDictionary map && map.Contains(block.EntityId) ? map[block.EntityId] : null;
        }

        private static bool Ready(Pair pair, object session)
        {
            if (pair.Weapon == null) return false;
            pair.Comp = pair.Comp ?? CompOf(pair.Weapon);
            return pair.Comp != null && P(pair.Comp, "Platform.State")?.ToString() == "Ready";
        }

        private static string Active(Pair pair)
        {
            if (pair.Comp == null) return "?";
            var weapons = P(pair.Comp, "Platform.Weapons") as IList;
            if (weapons == null) return "?";
            return string.Join("+", weapons.Cast<object>().Select(w => P(w, "ActiveAmmoDef.AmmoDef.AmmoRound") as string).Distinct());
        }

        /// <summary>
        /// Fire on or off as a player's terminal "Shoot" does (<c>ShootManager.RequestShootSync</c> with the signal
        /// <c>Manual</c>): the API's <c>ToggleWeaponFire</c> signals <c>On</c>, which a weapon in its AI's shoot mode takes as
        /// confirmed only with a target of its own.
        /// </summary>
        private static void Fire(Pair pair, bool on)
        {
            var manager = P(pair.Comp, "ShootManager");
            var request = manager.GetType().GetMethod("RequestShootSync", Any);
            var parameters = request.GetParameters();
            request.Invoke(manager, new object[] { 0L, Enum.Parse(parameters[1].ParameterType, on ? "On" : "Off"), Enum.Parse(parameters[2].ParameterType, on ? "Manual" : "None") });
            // a request turned down (the block not working yet, nothing loaded) still sets the trigger on, not the signal:
            // every next request is then taken as a toggle already done. The signal set as the request would have.
            var signal = manager.GetType().GetField("Signal", Any);
            if (on && signal != null && signal.GetValue(manager).ToString() != "Manual")
                signal.SetValue(manager, Enum.Parse(signal.FieldType, "Manual"));
        }

        private static int Counter(Pair pair)
        {
            var sum = 0;
            if (P(pair.Comp, "Platform.Weapons") is IList weapons)
                foreach (var w in weapons)
                    sum += P(w, "FireCounter") is int c ? c : 0;
            return sum;
        }

        /// <summary>The weapon's inventory filled with the ammunition's magazines (none for an energy one).</summary>
        private static void Stock(Pair pair)
        {
            if (pair.Magazine == null || pair.Weapon == null || pair.Weapon.MarkedForClose) return;
            if (!(pair.Weapon.GetInventory(0) is Sandbox.Game.MyInventory box)) return;
            var fits = Math.Min(50, Math.Floor((double)box.ComputeAmountThatFits(pair.Magazine.Value)));
            if (fits >= 1) box.AddItems((MyFixedPoint)fits, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(pair.Magazine.Value));
        }

        /// <summary>What a hit does to a block of a wall (the amount after every multiplier), counted and taken back.</summary>
        private static void OnDamage(object target, ref MyDamageInformation info)
        {
            if (!(target is IMySlimBlock slim)) return;
            Pair pair;
            lock (Walls)
                if (!Walls.TryGetValue((MyCubeGrid)slim.CubeGrid, out pair)) return;
            // a real wall's block loses at most what it has: the rest of a hit is overkill (WeaponCore's damage scaling
            // comes after its own cap of the block's hit points)
            var taken = Math.Min(info.Amount, slim.Integrity);
            if (info.IsDeformation) pair.Deformation += taken;
            else
            {
                pair.Damage += taken;
                pair.RawDamage += info.Amount;
                if (taken >= slim.Integrity) pair.Kills++;
            }
            pair.Hits++;
            if (pair.FirstHit < 0 && info.Amount > 0) pair.FirstHit = MySession.Static.GameplayFrameCounter;
            info.Amount = 0;
        }

        private static JObject Record(Pair pair, int window)
        {
            var system = pair.Systems[0];
            var ammo = pair.AmmoType;
            var r = new JObject
            {
                ["block"] = pair.Block.Id.SubtypeName,
                ["type"] = pair.Block.Id.TypeId.ToString().Replace("MyObjectBuilder_", ""),
                ["name"] = pair.Block.DisplayNameText,
                ["grid"] = pair.Block.CubeSize.ToString(),
                ["mod"] = pair.Block.Context?.ModId ?? "game",
                ["pcu"] = pair.Block.PCU,
                ["weapons"] = pair.Systems.Count,
                ["ammo"] = pair.Ammo,
                ["magazine"] = pair.Magazine?.SubtypeName ?? "energy",
                ["status"] = pair.Status,
                ["retoggles"] = pair.Retoggles,
                ["state_end"] = pair.EndState,
                ["fired_ammo"] = pair.Fired,
                ["distance"] = pair.Distance,
                ["aim"] = pair.Aim,
                ["magazine_switches"] = pair.Switches,
                ["power_mw"] = pair.Power,
                ["window_s"] = window,
                // measured
                ["fire_events"] = pair.FireTicks.Count,
                ["first_fire_tick"] = pair.FirstFire < 0 ? -1 : pair.FirstFire - pair.Start,
                ["first_hit_tick"] = pair.FirstHit < 0 ? -1 : pair.FirstHit - pair.Start,
                ["last_fire_tick"] = pair.LastFire < 0 ? -1 : pair.LastFire - pair.Start,
                ["damage"] = pair.Damage,
                ["deformation"] = pair.Deformation,
                ["raw_damage"] = pair.RawDamage,
                ["blocks_killed"] = pair.Kills,
                ["hits"] = pair.Hits,
                ["gaps"] = new JArray(Gaps(pair.FireTicks)),
            };
            // the definitions
            foreach (var f in new[]
                     {
                         "Values.HardPoint.Loading.RateOfFire", "Values.HardPoint.Loading.BarrelsPerShot", "Values.HardPoint.Loading.TrajectilesPerBarrel",
                         "Values.HardPoint.Loading.ReloadTime", "Values.HardPoint.Loading.MagsToLoad", "Values.HardPoint.Loading.ShotsInBurst",
                         "Values.HardPoint.Loading.DelayAfterBurst", "Values.HardPoint.Loading.DelayUntilFire", "Values.HardPoint.Loading.HeatPerShot",
                         "Values.HardPoint.Loading.MaxHeat", "Values.HardPoint.Loading.Cooldown", "Values.HardPoint.Loading.HeatSinkRate",
                         "Values.HardPoint.Loading.DegradeRof", "Values.HardPoint.Loading.FireFull", "Values.Targeting.MaxTargetDistance",
                         "Values.Targeting.MinTargetDistance", "WConst.RateOfFire", "WConst.ReloadTime",
                     })
                r["w." + f.Split('.').Last() + (f.StartsWith("WConst") ? "_const" : "")] = Json(P(system, f));
            foreach (var f in new[]
                     {
                         "AmmoDef.BaseDamage", "AmmoDef.EnergyMagazineSize", "AmmoDef.AmmoMagazine", "AmmoDef.HybridRound", "AmmoDef.EnergyCost",
                         "AmmoDef.Trajectory.DesiredSpeed", "AmmoDef.Trajectory.AccelPerSec", "AmmoDef.Trajectory.MaxTrajectory", "AmmoDef.Trajectory.Guidance",
                         "AmmoDef.AreaOfDamage.ByBlockHit.Enable", "AmmoDef.AreaOfDamage.ByBlockHit.Radius", "AmmoDef.AreaOfDamage.ByBlockHit.Damage",
                         "AmmoDef.AreaOfDamage.EndOfLife.Enable", "AmmoDef.AreaOfDamage.EndOfLife.Radius", "AmmoDef.AreaOfDamage.EndOfLife.Damage",
                         "AmmoDef.Fragment.AmmoRound", "AmmoDef.Fragment.Fragments", "AmmoDef.Beams.Enable",
                         "AmmoDef.DamageScales.Armor.Armor", "AmmoDef.DamageScales.Armor.Heavy", "AmmoDef.DamageScales.Grids.Large", "AmmoDef.DamageScales.Grids.Small",
                         "AmmoDef.DamageScales.MaxIntegrity", "AmmoDef.Ewar.Enable", "AmmoDef.ObjectsHit.MaxObjectsHit",
                         "AmmoDef.Const.BaseDamage", "AmmoDef.Const.MagazineSize", "AmmoDef.Const.MagsToLoad", "AmmoDef.Const.EnergyAmmo", "AmmoDef.Const.IsBeamWeapon",
                         "AmmoDef.Const.DesiredProjectileSpeed", "AmmoDef.Const.MaxTrajectory", "AmmoDef.Const.ByBlockHitDamage", "AmmoDef.Const.ByBlockHitRadius",
                         "AmmoDef.Const.EndOfLifeDamage", "AmmoDef.Const.EndOfLifeRadius", "AmmoDef.Const.HasFragment", "AmmoDef.Const.MustCharge",
                     })
                r["a." + f.Substring("AmmoDef.".Length)] = Json(P(ammo, f));
            if (pair.Magazine != null && MyDefinitionManager.Static.TryGetPhysicalItemDefinition(pair.Magazine.Value, out var item) && item is MyAmmoMagazineDefinition mag)
                r["magazine_capacity"] = mag.Capacity;
            return r;
        }

        private static JToken Json(object v)
        {
            if (v == null) return JValue.CreateNull();
            switch (v)
            {
                case bool b: return b;
                case int i: return i;
                case float f: return f;
                case double d: return d;
                case long l: return l;
                case string s: return s;
                default: return v.ToString();
            }
        }

        /// <summary>The ticks between fire events, grouped: "interval x count" in order.</summary>
        private static IEnumerable<string> Gaps(List<long> ticks)
        {
            var result = new List<string>();
            long last = -1, run = 0, count = 0;
            for (var i = 1; i < ticks.Count; i++)
            {
                var gap = ticks[i] - ticks[i - 1];
                if (gap == last) count++;
                else
                {
                    if (count > 0) result.Add(last + "x" + count);
                    last = gap;
                    count = 1;
                }
                run++;
            }
            if (count > 0) result.Add(last + "x" + count);
            return result.Take(200);
        }

        public override void Cleanup()
        {
            try
            {
                lock (Walls) Walls.Clear();
                FakeClients.RemoveAll();
                foreach (var steamId in _protected) MySession.Static?.RemoteAdminSettings.Remove(steamId);
                _protected.Clear();
                foreach (var grid in _grids)
                    if (!grid.MarkedForClose) grid.Close();
                _grids.Clear();
                if (_autoSave.HasValue) MySession.Static.Settings.AutoSaveInMinutes = _autoSave.Value;
                _gameplay.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
