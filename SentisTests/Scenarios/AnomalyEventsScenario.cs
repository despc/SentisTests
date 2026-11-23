using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SentisTests.Core;
using SentisTests.Game;
using VRage;
using VRage.Game;
using VRage.Game.Entity.UseObject;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// The events of SentisAnomalyZone, each made to come at once (<c>AnomalyZoneApi.StartEvent</c>) in a zone of the
    /// scenario's in space, with three players seated in three ships inside it (two of one faction, one of a faction at war
    /// with it; the ships' dampeners off so a pull is seen as it is). Each event checked by what it does:
    ///  - lightning, the fireball: the ships lose integrity (the fireball ends when it has hit);
    ///  - radiation: the players' radiation stat grows;
    ///  - a gravity shift, the storm, the pulses: the ships gain the speed the pull gives (the pulses: away from the center);
    ///  - raiders: pirate grids of the prefab near the ships, remembered by the plugin;
    ///  - EMP: the thrusters and gyros off, then on again;
    ///  - meteors: the game's meteors fly;
    ///  - fog: a sphere of SentisRender for every player inside;
    ///  - the gift: a container open to everyone with the items, remembered;
    ///  - the pilot swap: the two pilots of one faction in each other's ship.
    /// And the scheduler: with events on, one comes by itself after its warning. Arguments: <c>only=Lightning,Fog</c>.
    /// </summary>
    public sealed class AnomalyEventsScenario : TestScenario
    {
        public const string ScenarioName = "anomaly_events";
        private const string ZoneId = "events";
        private const double Radius = 600;
        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly Dictionary<string, object> _settingsBefore = new Dictionary<string, object>();
        private readonly List<long> _factions = new List<long>();
        private readonly List<MyCubeGrid> _ships = new List<MyCubeGrid>();
        private Type _api;
        private Vector3D _site;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 600;
        public override string[] Requires => new[] { Integrations.AnomalyZone };

        private object Call(string method, params object[] args)
        {
            var m = _api.GetMethod(method, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException("AnomalyZoneApi", method);
            return m.Invoke(null, args);
        }

        private void Setting(string name, object value)
        {
            var old = Call("Set", name, value);
            if (!_settingsBefore.ContainsKey(name)) _settingsBefore[name] = old;
        }

        private string Running => (string)Call("RunningEvent", ZoneId);

        public override IEnumerator Run()
        {
            _api = RequireType(Integrations.TypeOf(Integrations.AnomalyZone, "SentisAnomalyZone.AnomalyZoneApi"), "SentisAnomalyZone.AnomalyZoneApi");
            Check((bool)_api.GetProperty("Running").GetValue(null), "SentisAnomalyZone is not running");
            SkipUnless(_api.GetMethod("StartEvent") != null, "this SentisAnomalyZone has no events");
            WorldApi.EnsureUnpaused(Name);
            var only = Regex.Match(TestRunner.Focus ?? "", @"only=([\w,]+)") is var m && m.Success ? m.Groups[1].Value.Split(',') : null;
            bool Wanted(string kind) => only == null || only.Any(o => string.Equals(o, kind, StringComparison.OrdinalIgnoreCase));

            var anchor = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + "ae-anchor")[0]
                .PositionAndOrientation.Value.GetMatrix().Translation;
            _site = anchor + Vector3D.Normalize(new Vector3D(0.5, 0.8, -0.3)) * 450000;

            // events by hand only, short ones; who counts: any seated player
            Setting("EventsEnabled", false);
            Setting("EventWarningSeconds", 2.0);
            Setting("MinLargeGridBlocks", 1);
            Setting("CountUnpoweredGrids", true);
            Setting("JumpInRadius", 0.0);
            Setting("JumpOutRadius", 0.0);
            Setting("SafeZoneBanRadius", 0.0);
            Setting("SpawnRewards", false);
            Call("Forget", ZoneId);
            Call("PutZone", ZoneId, "События", _site.X, _site.Y, _site.Z, Radius, 100000.0);
            Note("zone of " + Radius + " m at " + _site);

            // three players in three ships: A1 and A2 of one faction, B of another at war with it
            FakeClients.RemoveAll();
            FakeClients.Add(3, Network, i => (_site + new Vector3D(i * 150 - 150, 30, 0), 0, 0), withCharacters: true);
            yield return Wait(() => Enumerable.Range(0, 3).All(FakeClients.HasLiveCharacter), "three players with bodies", 30);
            var ids = Enumerable.Range(0, 3).Select(i => FakeClients.PlayerOf(i).Identity.IdentityId).ToArray();
            var suffix = (DateTime.UtcNow.Ticks % 900 + 100).ToString();
            var factions = MySession.Static.Factions;
            // a run before that stopped half-way leaves the fake players in its factions: a player in one makes no other
            foreach (var id in ids)
            {
                var old = factions.GetPlayerFaction(id);
                if (old == null) continue;
                Note("player " + id + " still in [" + old.Tag + "] from before: " + (old.Name.StartsWith("Events ") ? "removed" : "left"));
                if (old.Name.StartsWith("Events ")) MyFactionCollection.RemoveFaction(old.FactionId);
                else MyFactionCollection.KickMember(old.FactionId, id);
            }
            yield return Wait(() => ids.All(id => factions.GetPlayerFaction(id) == null), "the players out of old factions", 15);
            // the test's factions of runs before (a tag is three letters: one left over stops a new one)
            foreach (var f in factions.Factions.Values.Where(f => f.Name.StartsWith("Events A ") || f.Name.StartsWith("Events B ")).ToList())
                MyFactionCollection.RemoveFaction(f.FactionId);
            var random = new Random();
            string Tag()
            {
                string tag;
                do tag = "Q" + (char)('A' + random.Next(26)) + (char)('A' + random.Next(26));
                while (factions.TryGetFactionByTag(tag) != null);
                return tag;
            }
            ((IMyFactionCollection)factions).CreateFaction(ids[0], Tag(), "Events A " + suffix, "", "");
            ((IMyFactionCollection)factions).CreateFaction(ids[2], Tag(), "Events B " + suffix, "", "");
            for (var t = 0; t < 40 && (factions.GetPlayerFaction(ids[0]) == null || factions.GetPlayerFaction(ids[2]) == null); t++)
                yield return WaitForSeconds(1, "the factions made");
            Note("factions: A " + (factions.GetPlayerFaction(ids[0])?.Tag ?? "none") + ", B " + (factions.GetPlayerFaction(ids[2])?.Tag ?? "none") +
                 "; factions in all " + factions.Factions.Count);
            Check(factions.GetPlayerFaction(ids[0]) != null && factions.GetPlayerFaction(ids[2]) != null, "the factions were not made");
            var fa = factions.GetPlayerFaction(ids[0]).FactionId;
            var fb = factions.GetPlayerFaction(ids[2]).FactionId;
            _factions.Add(fa);
            _factions.Add(fb);
            MyFactionCollection.SendJoinRequest(fa, ids[1]);
            MyFactionCollection.AcceptJoin(fa, ids[1]);
            yield return Wait(() => factions.GetPlayerFaction(ids[1])?.FactionId == fa, "A2 in faction A", 15);
            MyFactionCollection.DeclareWar(fa, fb);
            MyFactionCollection.DeclareWar(fb, fa);

            if (!_handler)
            {
                Sandbox.Game.GameSystems.MyDamageSystem.Static.RegisterBeforeDamageHandler(int.MaxValue - 2, OnDamage);
                _handler = true;
            }
            lock (Bodies)
                for (var i = 0; i < 3; i++) Bodies.Add(FakeClients.Character(i).EntityId);
            for (var i = 0; i < 3; i++) _ships.Add(Ship(ShipAt(i), ids[i], "ae-ship-" + i));
            yield return WaitForSeconds(1, "the ships settle");
            for (var i = 0; i < 3; i++) Seat(i);
            yield return Wait(() => Enumerable.Range(0, 3).All(i => Cockpit(_ships[i]).Pilot == FakeClients.Character(i)), "the players seated", 15);
            Note("ships " + string.Join(", ", _ships.Select(s => s.EntityId)) + ", pilots seated; factions A " + fa + " (2 players), B " + fb);

            var problems = new List<string>();
            var ran = 0;
            foreach (var kind in new[] { "Lightning", "Radiation", "GravityShift", "Storm", "Pulses", "Raiders", "Fireball", "Emp", "Meteors", "Fog", "Gift", "PilotSwap", "Swarm" })
            {
                if (!Wanted(kind)) continue;
                ran++;
                var reset = Reset();
                while (reset.MoveNext()) yield return reset.Current;
                var check = Event(kind, problems);
                while (check.MoveNext()) yield return check.Current;
            }

            if (only == null || Wanted("Hydrogen"))
            {
                var reset = Reset();
                while (reset.MoveNext()) yield return reset.Current;
                var check = CaptureHydrogen(problems);
                while (check.MoveNext()) yield return check.Current;
            }

            if (only == null || Wanted("Scheduler"))
            {
                var reset = Reset();
                while (reset.MoveNext()) yield return reset.Current;
                Setting("EventGapMin", 5.0);
                Setting("EventGapMax", 5.0);
                Setting("EventsEnabled", true);
                var started = DateTime.UtcNow;
                yield return Wait(() => Running != null, "an event comes by itself", 60);
                Note($"scheduler: {Running} came by itself {(DateTime.UtcNow - started).TotalSeconds:0} s after the events were switched on");
                Setting("EventsEnabled", false);
                yield return Wait(() => Running == null, "the event ends", 120);
            }

            Note("ANOMALY EVENTS RESULT | " + ran + " events; " + (problems.Count == 0 ? "all fine" : problems.Count + " problems: " + string.Join(" | ", problems)));
            Check(problems.Count == 0, string.Join(" | ", problems));
        }

        private int _round;

        /// <summary>Where a ship of this round stands: every round 40 m on, clear of the round before's.</summary>
        private Vector3D ShipAt(int i) => _site + new Vector3D(i * 150 - 150, 0, _round * 40 - 240);

        /// <summary>The ships back at their places, still, whole, their pilots in them.</summary>
        /// <summary>New ships at the places (the events break them), still, the players seated in their own again.</summary>
        private IEnumerator Reset()
        {
            yield return Wait(() => Running == null, "the event before ends", 60);
            // everyone out of whatever seat (a ship broken by an event leaves its cockpit as a grid of its own), and every
            // grid of the test zone but the plugin's own away: the ships, their pieces, the raiders, the gift
            for (var i = 0; i < 3; i++)
                if (FakeClients.Character(i)?.Parent is MyCockpit seat) seat.RemovePilot();
            var sphere = new BoundingSphereD(_site, Radius + 1500);
            var protectedIds = new HashSet<long>(new[] { (long)Call("CenterGrid", ZoneId) });
            foreach (var g in MyEntities.GetTopMostEntitiesInSphere(ref sphere).OfType<MyCubeGrid>().ToList())
                if (!protectedIds.Contains(g.EntityId) && !g.MarkedForClose) g.Close();
            for (var i = 0; i < 3; i++) _ships[i] = null;
            yield return WaitForTicks(10);
            _round++;
            for (var i = 0; i < 3; i++)
            {
                if (!FakeClients.HasLiveCharacter(i)) Note("player " + i + " has no live body before an event");
                _ships[i] = Ship(ShipAt(i), FakeClients.PlayerOf(i).Identity.IdentityId, "ae-ship-" + i);
            }
            yield return WaitForSeconds(1, "the ships settle");
            for (var i = 0; i < 3; i++) Seat(i);
            yield return WaitForSeconds(2, "the players sit down");
            for (var i = 0; i < 3; i++)
            {
                if (Cockpit(_ships[i])?.Pilot == FakeClients.Character(i)) continue;
                var body = FakeClients.Character(i);
                var seat = Cockpit(_ships[i]);
                var canUse = seat == null || body == null ? "?" : seat.GetType().GetMethod("CanUse", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
                    .Invoke(seat, new object[] { UseActionEnum.Manipulate, body })?.ToString();
                Note($"player {i}: body {(body == null ? "none" : body.IsDead ? "dead" : "alive")}, in {(body?.Parent as MyCubeBlock)?.CubeGrid?.DisplayName ?? "nothing"}, " +
                     $"{(body == null || seat == null ? 0 : Vector3D.Distance(body.PositionComp.GetPosition(), seat.PositionComp.GetPosition())):F1} m from the seat, can use: {canUse}");
                Seat(i);
            }
            yield return Wait(() => Enumerable.Range(0, 3).All(i => Cockpit(_ships[i])?.Pilot == FakeClients.Character(i)), "the players seated again", 15);
            foreach (var ship in _ships)
            {
                ship.Physics.LinearVelocity = Vector3.Zero;
                ((IMyShipController)Cockpit(ship)).DampenersOverride = false;
            }
        }

        /// <summary>The players' bodies take no damage: the explosions of an event would kill them before the next.</summary>
        private static readonly HashSet<long> Bodies = new HashSet<long>();
        private static bool _handler;

        private static void OnDamage(object target, ref MyDamageInformation info)
        {
            if (target is Sandbox.Game.Entities.Character.MyCharacter character)
                lock (Bodies)
                    if (Bodies.Contains(character.EntityId)) info.Amount = 0;
        }

        private IEnumerator Event(string kind, List<string> problems)
        {
            var integrity = _ships.Select(Integrity).ToArray();
            var radiation = Enumerable.Range(0, 3).Select(Radiation).ToArray();
            var meteors = MyEntities.GetEntities().OfType<MyMeteor>().Count();
            var gridsBefore = (string[])Call("EventGrids");
            switch (kind)
            {
                case "Lightning":
                    Setting("LightningSeconds", 5.0);
                    Setting("LightningEvery", 0.6);
                    break;
                case "Radiation":
                    Setting("RadiationSeconds", 4.0);
                    Setting("RadiationPerSecond", 5.0);
                    break;
                case "GravityShift":
                    Setting("GravityShiftSeconds", 3.0);
                    Setting("GravityShiftG", 1.0);
                    break;
                case "Storm":
                    Setting("StormSeconds", 4.0);
                    Setting("StormDistance", 400.0);
                    Setting("StormG", 2.0);
                    break;
                case "Pulses":
                    Setting("PulsesCount", 2);
                    Setting("PulseSeconds", 0.3);
                    Setting("PulsesPause", 1.0);
                    Setting("PulsesG", 10.0);
                    break;
                case "Raiders":
                    Setting("RaidersPrefabs", "DS_Gunboat");
                    Setting("RaidersCount", 1);
                    Setting("RaidersPerPlayers", 0);
                    break;
                case "Fireball":
                    Setting("FireballSeconds", 30.0);
                    Setting("FireballSpeed", 150.0);
                    Setting("FireballDistance", 400.0);
                    Setting("FireballRadius", 8.0);
                    break;
                case "Emp":
                    Setting("EmpOffSeconds", 3.0);
                    Setting("EmpWaveSpeed", 2000.0);
                    break;
                case "Meteors":
                    Setting("MeteorsCount", 6);
                    Setting("MeteorsSeconds", 2.0);
                    Setting("MeteorsDistance", 800.0);
                    break;
                case "Fog":
                    Setting("FogSeconds", 3.0);
                    Setting("FogArea", 10000.0);
                    {
                        // B outside the zone, 3 km off its center: in the fog all the same
                        var out3 = _ships[2].WorldMatrix;
                        out3.Translation = _site + new Vector3D(0, 3000, 0);
                        _ships[2].Teleport(out3);
                    }
                    break;
                case "Gift":
                    Setting("GiftItems", "Ingot/Platinum=7; Ingot/Gold=3");
                    break;
                case "Swarm":
                    _rock = Slab(_ships[0].PositionComp.WorldAABB.Center);
                    Setting("SwarmDistance", 100.0);
                    Setting("SwarmGrinders", 3);
                    Setting("SwarmLaunchers", 3);
                    Setting("SwarmSeconds", 30.0);
                    Setting("SwarmSpeed", 60.0);
                    Setting("SwarmFireRange", 100.0);
                    Setting("SwarmFireEvery", 2.0);
                    break;
            }

            var why = (string)Call("StartEvent", ZoneId, kind, true);
            if (why != null)
            {
                problems.Add(kind + ": did not start: " + why);
                Note("FAIL " + kind + ": did not start: " + why);
                yield break;
            }
            var started = DateTime.UtcNow;
            var line = "";
            var ok = true;
            void Fail(string what)
            {
                ok = false;
                problems.Add(kind + ": " + what);
                line += " | " + what;
            }

            switch (kind)
            {
                case "Lightning":
                case "Fireball":
                {
                    var fireball = kind == "Fireball" ? Call("RunningEventObject", ZoneId) : null;
                    yield return WaitForTicks(5);
                    var meteorId = fireball == null ? 0 : (long)fireball.GetType().GetProperty("MeteorId").GetValue(fireball);
                    var isMeteor = MyEntities.GetEntityById(meteorId) is MyMeteor;
                    yield return Wait(() => Running == null, kind + " goes on", 40);
                    yield return WaitForSeconds(1, "the explosions");
                    var lost = _ships.Select((s, i) => integrity[i] - Integrity(s)).ToArray();
                    line = "integrity lost " + string.Join(", ", lost.Select(l => l.ToString("F0"))) + $" in {(DateTime.UtcNow - started).TotalSeconds:0.0} s";
                    if (lost.Sum() <= 0) Fail("no ship was damaged");
                    if (kind == "Fireball")
                    {
                        var hit = (bool)fireball.GetType().GetField("Hit").GetValue(fireball);
                        var fires = (int)fireball.GetType().GetField("Fires").GetValue(fireball);
                        line += $"; a meteor {isMeteor}, {fires} fire effects, exploded on a ship {hit}";
                        if (fires == 0) Fail("no fire effect was drawn");
                        if (!isMeteor) Fail("the fireball is no meteor of the game");
                        if (!hit) Fail("the fireball did not explode on a ship");
                        if ((DateTime.UtcNow - started).TotalSeconds > 25) Fail("the fireball did not hit before its time was up");
                    }
                    break;
                }
                case "Radiation":
                {
                    yield return Wait(() => Running == null, kind + " goes on", 20);
                    var now = Enumerable.Range(0, 3).Select(Radiation).ToArray();
                    line = "radiation " + string.Join(", ", now.Select((r, i) => radiation[i].ToString("F1") + "->" + r.ToString("F1")));
                    if (Enumerable.Range(0, 3).Any(i => now[i] <= radiation[i])) Fail("a player inside took no radiation");
                    break;
                }
                case "GravityShift":
                case "Storm":
                case "Pulses":
                {
                    yield return WaitForSeconds(kind == "Pulses" ? 1.8 : 2.5, kind + " goes on");
                    var speeds = _ships.Select(s => (double)s.Physics.LinearVelocity.Length()).ToArray();
                    var outward = _ships.Select(s => Vector3D.Dot(s.Physics.LinearVelocity, Vector3D.Normalize(s.PositionComp.WorldAABB.Center - _site))).ToArray();
                    line = "speeds " + string.Join(", ", speeds.Select(v => v.ToString("F1"))) + " m/s, outward " + string.Join(", ", outward.Select(v => v.ToString("F1")));
                    var least = kind == "GravityShift" ? 1.0 * 9.81 * 2.5 * 0.6 : kind == "Storm" ? 2.0 * 9.81 * 2.5 * 0.6 : 2 * 10 * 9.81 * 0.3 * 0.7;
                    if (speeds.Count(v => v >= least) < (kind == "GravityShift" ? 2 : 3)) Fail($"the ships did not gain {least:F0} m/s");
                    if (kind == "Pulses" && outward.Count(v => v > 0) < 3) Fail("a ship was not pushed away from the center");
                    yield return Wait(() => Running == null, kind + " ends", 20);
                    break;
                }
                case "Raiders":
                {
                    yield return Wait(() => ((string[])Call("EventGrids")).Any(g => g.StartsWith("Raiders:") && !gridsBefore.Contains(g)), "the raiders come", 30);
                    var raiders = ((string[])Call("EventGrids")).Where(g => g.StartsWith("Raiders:") && !gridsBefore.Contains(g))
                        .Select(g => MyEntities.GetEntityById(long.Parse(g.Split(':')[1])) as MyCubeGrid).Where(g => g != null).ToList();
                    var pirates = MyPirateAntennas.GetPiratesId();
                    var near = raiders.Count == 0 ? 0 : raiders.Min(r => _ships.Min(s => Vector3D.Distance(r.PositionComp.WorldAABB.Center, s.PositionComp.WorldAABB.Center)));
                    line = raiders.Count + " grids: " + string.Join(", ", raiders.Select(r => "«" + r.DisplayName + "» " + r.BlocksCount + " blocks")) +
                           $", owner pirates {raiders.All(r => r.BigOwners.Contains(pirates))}, {near:F0} m from the nearest ship";
                    if (raiders.Count == 0) Fail("no raider came");
                    else if (!raiders.Any(r => r.BigOwners.Contains(pirates))) Fail("the raiders are not the pirates'");
                    foreach (var r in raiders) r.Close();
                    break;
                }
                case "Emp":
                {
                    yield return WaitForSeconds(1, "the wave passes");
                    var off = _ships.Sum(s => s.GetFatBlocks().OfType<MyFunctionalBlock>().Count(b => (b is MyThrust || b is MyGyro) && !b.Enabled));
                    var all = _ships.Sum(s => s.GetFatBlocks().OfType<MyFunctionalBlock>().Count(b => b is MyThrust || b is MyGyro));
                    yield return Wait(() => Running == null, "the EMP ends", 20);
                    var stillOff = _ships.Sum(s => s.GetFatBlocks().OfType<MyFunctionalBlock>().Count(b => (b is MyThrust || b is MyGyro) && !b.Enabled));
                    line = $"thrusters and gyros off {off} of {all}, after it {stillOff} off";
                    if (off < all) Fail("not every thruster and gyro went off");
                    if (stillOff > 0) Fail(stillOff + " stayed off");
                    break;
                }
                case "Meteors":
                {
                    yield return WaitForSeconds(1.5, "the meteors fly");
                    var now = MyEntities.GetEntities().OfType<MyMeteor>().Count();
                    line = "meteors " + meteors + " -> " + now;
                    if (now <= meteors) Fail("no meteor flew");
                    yield return Wait(() => Running == null, "the meteors end", 20);
                    break;
                }
                case "Fog":
                {
                    yield return WaitForSeconds(1, "the fog comes");
                    var fog = RenderIds("zone:ev:").Where(id => id.Contains(":fog:")).ToList();
                    line = fog.Count + " fog spheres (B 3 km out of the zone)";
                    if (RenderIds(null) == null) line += " (no SentisRender)";
                    else if (fog.Count < 3) Fail(fog.Count + " fog spheres for 3 players");
                    yield return Wait(() => Running == null, "the fog ends", 20);
                    yield return WaitForSeconds(0.5, "the fog goes");
                    var left = RenderIds("zone:ev:").Count(id => id.Contains(":fog:"));
                    if (left > 0) Fail(left + " fog spheres left after it");
                    break;
                }
                case "Gift":
                {
                    yield return Wait(() => ((string[])Call("EventGrids")).Any(g => g.StartsWith("Gift:") && !gridsBefore.Contains(g)), "the gift comes", 15);
                    var id = ((string[])Call("EventGrids")).Where(g => g.StartsWith("Gift:") && !gridsBefore.Contains(g)).Select(g => long.Parse(g.Split(':')[1])).FirstOrDefault();
                    var gift = MyEntities.GetEntityById(id) as MyCubeGrid;
                    var items = gift == null ? "none" : string.Join(", ", gift.GetFatBlocks().Where(b => b.HasInventory).SelectMany(b => b.GetInventory().GetItems())
                        .Select(it => it.Content.SubtypeName + " x" + (double)it.Amount));
                    var open = gift != null && gift.GetFatBlocks().All(b => !(b is MyTerminalBlock t) || t.IDModule == null || t.IDModule.ShareMode == MyOwnershipShareModeEnum.All);
                    var inZone = gift != null && Vector3D.Distance(gift.PositionComp.GetPosition(), _site) <= Radius;
                    line = gift == null ? "no grid" : $"«{gift.DisplayName}»: {items}; open to all {open}, in the zone {inZone}";
                    if (gift == null) Fail("no gift");
                    else
                    {
                        if (!items.Contains("Platinum x7") || !items.Contains("Gold x3")) Fail("the gift holds " + items);
                        if (!open) Fail("the gift is not open to everyone");
                        if (!inZone) Fail("the gift is outside the zone");
                        gift.Close();
                    }
                    break;
                }
                case "Swarm":
                {
                    var swarm = Call("RunningEventObject", ZoneId);
                    int Count(string what) => swarm == null ? 0 : (int)swarm.GetType().GetProperty(what)?.GetValue(swarm) ;
                    int Rockets() => swarm == null ? 0 : (int)swarm.GetType().GetField("Rockets").GetValue(swarm);
                    var bodies = swarm == null ? new List<long>() : (List<long>)swarm.GetType().GetProperty("Bodies").GetValue(swarm);
                    var made = Count("Made");
                    var skipped = swarm == null ? 0 : (int)swarm.GetType().GetField("SkippedInRock").GetValue(swarm);
                    var inRock = bodies.Select(id => MyEntities.GetEntityById(id)).Count(e => e != null && _rock != null &&
                        _rock.IsAnyOfPointInside(new[] { e.PositionComp.GetPosition() }));
                    var tries = swarm == null ? 0 : (int)swarm.GetType().GetField("RockTries").GetValue(swarm);
                    var swarmLine = $"rock: {tries} points in it tried again, {inRock} astronauts in it, {skipped} found no place; ";
                    if (tries == 0) Fail("no spawn point fell in the rock: the check was not tried");
                    if (inRock > 0) Fail(inRock + " astronauts came inside the rock");
                    yield return WaitForSeconds(1, "the antennas");
                    var antennas = swarm == null ? new List<string>() : (List<string>)swarm.GetType().GetProperty("Antennas").GetValue(swarm);
                    var cutting = 0;
                    for (var t = 0; t < 25 && Running != null; t++)
                    {
                        yield return WaitForSeconds(1, "the swarm at work");
                        cutting = Math.Max(cutting, Count("Cutting"));
                        if (cutting > 0 && Rockets() > 0 && t >= 8) break;
                    }
                    var rockets = Rockets();
                    var alive = Count("Alive");
                    var lost = _ships.Select((s, i) => integrity[i] - Integrity(s)).ToArray();
                    var nearest = bodies.Select(id => MyEntities.GetEntityById(id)).Where(e => e != null)
                        .Select(e => _ships.Min(s => Vector3D.Distance(e.PositionComp.GetPosition(), s.PositionComp.WorldAABB.Center))).DefaultIfEmpty(-1).Min();
                    Call("StopEvent", ZoneId);
                    line = swarmLine + $"antennas: {string.Join(", ", antennas.Distinct())} x{antennas.Count}; {made} astronauts, {alive} alive, at most {cutting} cutting at once, {rockets} rockets, the nearest {nearest:F0} m from a ship, integrity lost " +
                           string.Join(", ", lost.Select(l => l.ToString("F0")));
                    if (made + skipped < 6) Fail(made + " astronauts of 6");
                    if (made < 3) Fail("only " + made + " astronauts came");
                    if (antennas.Count < 3 || antennas.Any(a => a != "Призрак Рипера 300")) Fail("the grinders' antennas: " + string.Join(", ", antennas));
                    if (cutting == 0) Fail("no grinder cut");
                    if (rockets == 0) Fail("no rocket fired");
                    if (lost.Sum() <= 0) Fail("no ship lost integrity");
                    yield return Wait(() => Running == null, "the swarm ends", 40);
                    // (stopped, it is taken away on the zone's next second)
                    yield return WaitForSeconds(2, "the swarm taken away");
                    var left = bodies.Count(id => MyEntities.GetEntityById(id) is VRage.Game.Entity.MyEntity e && !e.MarkedForClose);
                    if (left > 0) Fail(left + " astronauts left after it");
                    _rock?.Close();
                    _rock = null;
                    break;
                }
                case "PilotSwap":
                {
                    yield return WaitForSeconds(1.5, "the pilots change");
                    var a1 = FakeClients.Character(0);
                    var a2 = FakeClients.Character(1);
                    var b = FakeClients.Character(2);
                    line = "ship A1: " + (Cockpit(_ships[0]).Pilot?.DisplayName ?? "empty") + ", ship A2: " + (Cockpit(_ships[1]).Pilot?.DisplayName ?? "empty") +
                           ", ship B: " + (Cockpit(_ships[2]).Pilot?.DisplayName ?? "empty");
                    if (Cockpit(_ships[0]).Pilot != a2 || Cockpit(_ships[1]).Pilot != a1) Fail("A1 and A2 did not change ships");
                    if (Cockpit(_ships[2]).Pilot != b) Fail("B, of another faction, was moved");
                    break;
                }
            }
            Note((ok ? "ok   " : "FAIL ") + kind + ": " + line);
        }

        private MyVoxelBase _rock;

        /// <summary>A slab of stone 512 x 100 x 512 m whose top is 20 m under the point.</summary>
        private static MyVoxelBase Slab(Vector3D above)
        {
            var storage = MyAPIGateway.Session.VoxelMaps.CreateStorage(new Vector3I(512));
            var data = new VRage.Voxels.MyStorageData();
            var min = new Vector3I(0, 0, 0);
            var max = new Vector3I(511, 99, 511);
            data.Resize(min, max);
            data.ClearContent(VRage.Voxels.MyVoxelConstants.VOXEL_CONTENT_FULL);
            data.ClearMaterials(Sandbox.Definitions.MyDefinitionManager.Static.GetVoxelMaterialDefinitions().First().Index);
            storage.WriteRange(data, VRage.Voxels.MyStorageDataTypeFlags.ContentAndMaterial, min, max);
            var corner = above + new Vector3D(-256, -120, -256);
            return MyAPIGateway.Session.VoxelMaps.CreateVoxelMap(WorldApi.EntityPrefix + "ae-rock", storage, corner, 0) as MyVoxelBase;
        }

        /// <summary>
        /// A capture by A alone (B's ship taken out of the zone): 80% of each tank wanted, two large tanks inside - the limit of
        /// one large tank gives each half of it; B's ship, outside, nothing.
        /// </summary>
        private IEnumerator CaptureHydrogen(List<string> problems)
        {
            Setting("CaptureHydrogenEnabled", true);
            Setting("CaptureHydrogenPercent", 80.0);
            Setting("CaptureHydrogenLimit", 0.0);
            var away = _ships[2].WorldMatrix;
            away.Translation = _site + new Vector3D(0, 0, Radius + 1500);
            _ships[2].Teleport(away);
            var before = (int)Call("Captures", ZoneId);
            Call("PutZone", ZoneId, "События", _site.X, _site.Y, _site.Z, Radius, 3.0);
            yield return Wait(() => (int)Call("Captures", ZoneId) > before, "A captures the zone", 30);
            yield return WaitForSeconds(1, "the hydrogen");
            Call("PutZone", ZoneId, "События", _site.X, _site.Y, _site.Z, Radius, 100000.0);
            double Fill(MyCubeGrid g) => g.GetFatBlocks().OfType<Sandbox.Game.Entities.Blocks.MyGasTank>().Select(x => x.FilledRatio).DefaultIfEmpty(-1).First();
            var fills = _ships.Select(Fill).ToArray();
            var line = "tanks " + string.Join(", ", fills.Select(f => (f * 100).ToString("F1") + "%"));
            var ok = Math.Abs(fills[0] - 0.5) < 0.01 && Math.Abs(fills[1] - 0.5) < 0.01 && fills[2] < 0.001;
            if (!ok) problems.Add("Hydrogen: " + line + ", not 50%, 50% and 0% (B outside)");
            Note((ok ? "ok   " : "FAIL ") + "Hydrogen for a capture: " + line);
        }

        private static List<string> RenderIds(string prefix)
        {
            var render = Integrations.TypeAnywhere("SentisRender.Render");
            var registry = render?.GetProperty("Registry", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (registry == null) return prefix == null ? null : new List<string>();
            return (List<string>)registry.GetType().GetMethod("Ids").Invoke(registry, new object[] { prefix });
        }

        private static double Integrity(MyCubeGrid grid) =>
            grid == null || grid.MarkedForClose ? 0 : grid.GetBlocks().Sum(b => (double)b.Integrity);

        private static double Radiation(int client)
        {
            var stats = FakeClients.Character(client)?.StatComp as MyCharacterStatComponent;
            return stats?.Radiation?.Value ?? -1;
        }

        private static MyCockpit Cockpit(MyCubeGrid grid) => grid?.GetFatBlocks().OfType<MyCockpit>().FirstOrDefault();

        private void Seat(int i)
        {
            var seat = Cockpit(_ships[i]);
            var body = FakeClients.Character(i);
            FakeClients.SetFree(i, true);
            body.PositionComp.SetPosition(seat.PositionComp.GetPosition() + seat.WorldMatrix.Backward * 1.5);
            seat.RequestUse(UseActionEnum.Manipulate, body);
        }

        /// <summary>A small ship: a seat, a battery, a gyro, a thruster, armour; the owner's, its dampeners off.</summary>
        private MyCubeGrid Ship(Vector3D at, long owner, string name)
        {
            MyObjectBuilder_CubeBlock Block(MyObjectBuilderType type, string subtype, int x, int y, int z)
            {
                var block = (MyObjectBuilder_CubeBlock)MyObjectBuilderSerializer.CreateNewObject(type, subtype);
                block.Min = new SerializableVector3I(x, y, z);
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
                return block;
            }

            var blocks = new List<MyObjectBuilder_CubeBlock>();
            for (var x = 0; x < 6; x++)
            for (var z = 0; z < 2; z++)
                blocks.Add(Block(typeof(MyObjectBuilder_CubeBlock), "LargeBlockArmorBlock", x, 0, z));
            var cockpit = (MyObjectBuilder_Cockpit)Block(typeof(MyObjectBuilder_Cockpit), "LargeBlockCockpitSeat", 0, 1, 0);
            cockpit.IsMainCockpit = true;
            blocks.Add(cockpit);
            var battery = (MyObjectBuilder_BatteryBlock)Block(typeof(MyObjectBuilder_BatteryBlock), "LargeBlockBatteryBlock", 2, 1, 0);
            battery.CurrentStoredPower = 3f;
            battery.MaxStoredPower = 3f;
            battery.Enabled = true;
            battery.ProducerEnabled = true;
            blocks.Add(battery);
            blocks.Add(Block(typeof(MyObjectBuilder_Gyro), "LargeBlockGyro", 4, 1, 0));
            blocks.Add(Block(typeof(MyObjectBuilder_Thrust), "LargeBlockSmallThrust", 5, 1, 1));
            var tank = (MyObjectBuilder_OxygenTank)Block(typeof(MyObjectBuilder_OxygenTank), "LargeHydrogenTank", 0, 0, 2);
            tank.FilledRatio = 0;
            blocks.Add(tank);
            var builder = new MyObjectBuilder_CubeGrid
            {
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = false,
                DisplayName = WorldApi.EntityPrefix + name,
                CreatePhysics = true,
                DampenersEnabled = false,
                PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.Enabled,
                PositionAndOrientation = new MyPositionAndOrientation(at, Vector3.Forward, Vector3.Up),
                CubeBlocks = blocks
            };
            MyAPIGateway.Entities.RemapObjectBuilder(builder);
            var grid = (MyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(builder);
            Track(grid);
            return grid;
        }

        public override void Cleanup()
        {
            try
            {
                if (_api != null)
                {
                    Call("Set", "EventsEnabled", false);
                    _rock?.Close();
                    foreach (var g in (string[])Call("EventGrids"))
                        if (MyEntities.GetEntityById(long.Parse(g.Split(':')[1])) is MyCubeGrid grid && !grid.MarkedForClose) grid.Close();
                    Call("Forget", ZoneId);
                    Call("RemoveZone", ZoneId);
                    foreach (var setting in _settingsBefore) Call("Set", setting.Key, setting.Value);
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "anomaly_events: the zone's settings were not restored");
            }
            foreach (var ship in _ships)
                if (ship != null && !ship.MarkedForClose) ship.Close();
            lock (Bodies) Bodies.Clear();
            foreach (var faction in _factions) MyFactionCollection.RemoveFaction(faction);
            FakeClients.RemoveAll();
            base.Cleanup();
        }
    }
}
