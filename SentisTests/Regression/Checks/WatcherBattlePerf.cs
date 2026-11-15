using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// What SentisWatcher's battle records cost the server, and that they count right, with SentisClusters and without.
    /// A static small-grid station nobody sits in (<see cref="Guns"/> fixed gatling guns and <see cref="Lights"/> lights:
    /// a grid of many functional blocks, the costly case for finding who fired) shoots an enemy's heavy armour wall
    /// 150 m ahead for <see cref="FireSeconds"/> s; then a warhead on a large grid of lights goes off against another wall.
    /// Phases: the clusters' loops off (SentisClusters' Parallel off in memory) and on.
    /// Each phase's frames (idle and firing, the explosion's worst) and its time span go to the result: the rows are read
    /// from Watcher's day base after (shots, rounds, round hits, block losses of the station and the walls). And how long
    /// Watcher's "who fired" takes for the gun (Identities.DamageIdentity), against the walk over all the grid's seats it
    /// replaced. Not part of the regression: it switches the clusters' settings.
    /// </summary>
    public sealed class WatcherBattlePerfCheck : RegressionCheck
    {
        public const string ScenarioName = "watcher_battle_perf";
        public override string Name => ScenarioName;
        public override int TimeoutSeconds => 900;
        protected override bool NeedsDamage => true;
        private const int Guns = 8;
        private const int Lights = 1000;
        private const int Launchers = 2;
        private const int FireSeconds = 20;

        private delegate long Ident2(MyEntity entity, out string attribution);
        private delegate long Ident3(MyEntity entity, out string attribution, long freshMs);

        private object _clustersConfig;
        private bool? _parallelWas;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 10, -15));
            var definition = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyWeaponBlockDefinition>()
                .Where(d => d.Public && d.CubeSize == MyCubeSize.Small && d.Id.TypeId.ToString().EndsWith("SmallGatlingGun")).OrderBy(d => d.Id.SubtypeName).FirstOrDefault();
            SkipUnless(definition != null, "no small gatling gun in this game");
            var light = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyCubeBlockDefinition>()
                .Where(d => d.Public && d.CubeSize == MyCubeSize.Small && d.Size == Vector3I.One && d.Id.TypeId.ToString().EndsWith("InteriorLight"))
                .OrderBy(d => d.Id.SubtypeName).FirstOrDefault();
            SkipUnless(light != null, "no small 1x1x1 light in this game");
            var weapon = MyDefinitionManager.Static.GetWeaponDefinition(definition.WeaponDefinitionId);
            var magazine = weapon.AmmoMagazinesId.First();
            var step = definition.Size.X + 1;
            var parts = new List<Part>();
            for (var i = 0; i < Guns; i++) parts.Add(new Part(definition.Id.TypeId + "/" + definition.Id.SubtypeName, i * step, 0, 0));
            parts.Add(new Part("BatteryBlock/SmallBlockBatteryBlock", -3, 0, 0).With(b => ((Sandbox.Common.ObjectBuilders.MyObjectBuilder_BatteryBlock)b).CurrentStoredPower = 1f));
            // rocket launchers too: their missiles' explosions are the damage of many blocks at once
            var launcher = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyWeaponBlockDefinition>()
                .Where(d => d.Public && d.CubeSize == MyCubeSize.Small && d.Id.TypeId.ToString().EndsWith("SmallMissileLauncher")).OrderBy(d => d.Id.SubtypeName).FirstOrDefault();
            SkipUnless(launcher != null, "no small rocket launcher in this game");
            var rockets = MyDefinitionManager.Static.GetWeaponDefinition(launcher.WeaponDefinitionId).AmmoMagazinesId.First();
            for (var i = 0; i < Launchers; i++) parts.Add(new Part(launcher.Id.TypeId + "/" + launcher.Id.SubtypeName, i * (launcher.Size.X + 2), 4, 0));
            var lightId = light.Id.TypeId + "/" + light.Id.SubtypeName;
            for (var n = 0; n < Lights; n++) parts.Add(new Part(lightId, n % 20, -2 - n / 20 % 10, n / 200));
            var station = Build("battle-station", Site.Origin, true, parts, MyCubeSize.Small);
            var guns = station.GetFatBlocks().Where(b => b.BlockDefinition.Id == definition.Id).ToList();
            Check(guns.Count == Guns, guns.Count + " of " + Guns + " guns built");
            var launchers = station.GetFatBlocks().Where(b => b.BlockDefinition.Id == launcher.Id).ToList();
            Check(launchers.Count == Launchers, launchers.Count + " of " + Launchers + " rocket launchers built");
            // the enemy: another player (no faction: enemies), standing by the walls so that the freezer keeps them
            var enemyIndex = -1;
            yield return Join(Site.Origin + Site.Forward * 140 + Site.Right * 30, i => enemyIndex = i);
            var enemy = FakeClients.PlayerOf(enemyIndex).Identity.IdentityId;
            var report = new List<string> { "station " + station.EntityId + ", " + station.GetFatBlocks().Count + " functional blocks" };
            yield return Seconds(1);

            report.Add(WhoFired(guns[0], station));

            _clustersConfig = ClustersConfig();
            var clusters = _clustersConfig != null;
            if (clusters) _parallelWas = Parallel;
            var phases = new List<(string Name, bool? Parallel, bool Detach)> { ("plain", clusters ? false : (bool?)null, false) };
            // a detached cluster is not used on the servers yet (15.11.2026): only the clusters' loops off and on
            if (clusters) phases.Add(("clusters", true, false));
            var index = 0;
            foreach (var phase in phases)
            {
                Command("!clusters attach all");
                if (phase.Parallel.HasValue) Parallel = phase.Parallel.Value;
                yield return Seconds(3);
                if (phase.Detach)
                {
                    var at = station.PositionComp.GetPosition();
                    var answer = string.Join(" ", Command($"!clusters detach {at.X:F0} {at.Y:F0} {at.Z:F0}"));
                    Check(answer.Contains("detached"), phase.Name + ": " + answer);
                }
                var wall = BuildFor(enemy, "battle-wall-" + index, Site.Origin + Site.Forward * 150 + Site.Up * -1,
                    // armour alone has no owner (the hits' victim would be nobody's): a battery of the enemy's behind it
                    Bodies.Wall("CubeBlock/LargeHeavyBlockArmorBlock", 3, 3).Concat(new[] { new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, -3) }).ToArray());
                foreach (var (gun, ammo) in guns.Select(g => (g, magazine)).Concat(launchers.Select(l => (l, rockets))))
                {
                    var box = (Sandbox.Game.MyInventory)gun.GetInventory(0);
                    var fits = Math.Min(10, Math.Floor((double)box.ComputeAmountThatFits(ammo)));
                    if (fits > 0) box.AddItems((VRage.MyFixedPoint)fits, (MyObjectBuilder_PhysicalObject)VRage.ObjectBuilders.MyObjectBuilderSerializer.CreateNewObject(ammo));
                }
                yield return Seconds(2);
                var idle = new List<double>();
                yield return Frames(5, idle);
                var from = Now();
                foreach (var gun in guns.Concat(launchers)) ((Sandbox.ModAPI.IMyTerminalBlock)gun).GetActionWithName("Shoot_On")?.Apply(gun);
                var firing = new List<double>();
                yield return Frames(FireSeconds, firing);
                foreach (var gun in guns.Concat(launchers)) ((Sandbox.ModAPI.IMyTerminalBlock)gun).GetActionWithName("Shoot_Off")?.Apply(gun);
                var to = Now();
                yield return Seconds(3);

                // a warhead on a large grid of lights against a wall of the enemy's
                var slabAt = Site.Origin + Site.Forward * 120 + Site.Right * 60 + Site.Up * (20 + 40 * index);
                var slab = BuildFor(enemy, "battle-slab-" + index, slabAt, Bodies.Wall("CubeBlock/LargeBlockArmorBlock", 4, 2).ToArray());
                var bombParts = new List<Part> { new Part("Warhead/LargeWarhead", 0, 0, 0) };
                for (var n = 0; n < 300; n++) bombParts.Add(new Part("InteriorLight/SmallLight", 1 + n % 10, n / 10 % 10, 2 + n / 100));
                var bomb = Build("battle-bomb-" + index, slabAt + Site.Forward * 2.5, true, bombParts);
                var warhead = bomb.GetFatBlocks().OfType<Sandbox.Game.Entities.Cube.MyWarhead>().FirstOrDefault();
                Check(warhead != null, "the warhead was not built");
                yield return Seconds(2);
                var slabWhole = Integrity(slab);
                ((Sandbox.ModAPI.IMyTerminalBlock)warhead).SetValueBool("Safety", true);
                HoldHighQuality();
                var blastFrom = Now();
                Request(warhead, "DetonateRequest");
                var blast = new List<double>();
                yield return Frames(3, blast);
                var blastTo = Now();
                ReleaseHighQuality();
                Check(warhead.MarkedForClose || warhead.Closed, phase.Name + ": the warhead did not go off");
                var slabLost = slabWhole - Integrity(slab);

                var line = phase.Name + ": idle " + Stats(idle) + "; firing " + Stats(firing) + " t=" + from + ".." + to + " wall " + wall.EntityId +
                           "; blast worst " + (blast.Count > 0 ? blast.Max().ToString("F1") : "?") + " ms, the slab lost " + slabLost.ToString("F0") + " t=" + blastFrom + ".." + blastTo +
                           " slab " + slab.EntityId + " bomb " + bomb.EntityId + "; " + DetachedText();
                report.Add(line);
                Note(line);
                Command("!clusters attach all");
                foreach (var grid in new[] { wall, slab, bomb })
                    if (!grid.MarkedForClose) grid.Close();
                index++;
                yield return Seconds(3);
            }
            Result(string.Join(" | ", report));
        }

        private static double Integrity(MyCubeGrid grid) => grid.MarkedForClose ? 0 : grid.GetBlocks().Sum(b => (double)b.Integrity);

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>The frames of so many seconds, their lengths (ms) into the list.</summary>
        private static IEnumerator Frames(double seconds, List<double> frames)
        {
            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed.TotalMilliseconds;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                yield return null;
                var now = clock.Elapsed.TotalMilliseconds;
                frames.Add(now - last);
                last = now;
            }
        }

        private static string Stats(List<double> frames)
        {
            if (frames.Count == 0) return "no frames";
            var sorted = frames.OrderBy(f => f).ToList();
            return $"{frames.Count} frames, mean {frames.Average():F2} ms, p99 {sorted[(int)(sorted.Count * 0.99)]:F1}, max {sorted.Last():F1}";
        }

        /// <summary>Watcher's "who fired" for the gun, and the walk over the grid's seats it was before.</summary>
        private static string WhoFired(MyCubeBlock gun, MyCubeGrid grid)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisWatcher.Recording.Identities", false)).FirstOrDefault(t => t != null);
            var method = type?.GetMethod("DamageIdentity", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var text = "who fired: ";
            if (method == null) text += "no Identities.DamageIdentity in SentisWatcher";
            else
            {
                const int calls = 20000;
                var clock = Stopwatch.StartNew();
                long who = 0;
                string attribution = null;
                if (method.GetParameters().Length == 3)
                {
                    var call = (Ident3)Delegate.CreateDelegate(typeof(Ident3), method);
                    for (var i = 0; i < calls; i++) who = call(gun, out attribution, 1000);
                    var kept = clock.Elapsed.TotalMilliseconds * 1000 / calls;
                    clock.Restart();
                    for (var i = 0; i < calls; i++) who = call(gun, out attribution, -1);
                    text += $"{kept:F2} us a call (kept a second), {clock.Elapsed.TotalMilliseconds * 1000 / calls:F2} us asked each time";
                }
                else
                {
                    var call = (Ident2)Delegate.CreateDelegate(typeof(Ident2), method);
                    for (var i = 0; i < calls; i++) who = call(gun, out attribution);
                    text += $"{clock.Elapsed.TotalMilliseconds * 1000 / calls:F2} us a call";
                }
                text += $" ({who}, {attribution})";
            }
            const int walks = 2000;
            var walk = Stopwatch.StartNew();
            MyCockpit seat = null;
            for (var i = 0; i < walks; i++)
                seat = grid.GetFatBlocks<MyCockpit>().Where(c => c.Pilot != null).OrderByDescending(c => c.IsMainCockpit).ThenBy(c => c.EntityId).FirstOrDefault();
            return text + $"; the walk over the grid's seats {walk.Elapsed.TotalMilliseconds * 1000 / walks:F2} us{(seat != null ? "" : "")}";
        }

        private static object ClustersConfig()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisClusters.SentisClustersPlugin", false)).FirstOrDefault(t => t != null);
            return type?.GetProperty("Config", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        }

        // SentisClusters' Parallel, in memory: off, the game's loops are its own again (ClusterRunner.Takes)
        private bool Parallel
        {
            get => (bool)_clustersConfig.GetType().GetProperty("Parallel").GetValue(_clustersConfig);
            set => _clustersConfig.GetType().GetProperty("Parallel").SetValue(_clustersConfig, value);
        }

        private static string DetachedText()
        {
            var m = Regex.Match(string.Join("\n", Command("!clusters status")), @"detached clusters: [^\n]*");
            return m.Success ? m.Value : "no detached cluster";
        }

        private static List<string> Command(string text)
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
                Command("!clusters attach all");
                if (_clustersConfig != null)
                {
                    if (_parallelWas.HasValue) Parallel = _parallelWas.Value;
                }
            }
            catch (Exception) { }
            base.Cleanup();
        }
    }
}
