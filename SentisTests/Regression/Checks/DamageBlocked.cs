using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using SentisTests.Core;
using SentisTests.Game;
using VRage.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisWatcher's DamageBlockedWatch: gatling guns fire at three walls of the tests' identity (an enemy: the watch
    /// counts hits of enemies and neutrals) out of any safe zone - one with its DestructibleBlocks off (the game's rounds
    /// then stop before its blocks), one as it is, and one in SentisGameplayImprovements' PvE zone (put round it for the
    /// check in memory only, the zone's field and the setting's, as they were at the end; no file written), where players do
    /// no damage to grids. The first is told in the log as shot and taking no damage, with the reason; the second takes
    /// damage and is not told; the third takes none and is not told either (the zone's rule).
    /// </summary>
    public sealed class DamageBlockedCheck : RegressionCheck
    {
        public override string Name => Prefix + "damage_blocked";
        public override int TimeoutSeconds => 90;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            SkipUnless(Integrations.TypeOf(Integrations.Watcher, "SentisWatcher.Recording.DamageBlockedWatch") != null, "no SentisWatcher with DamageBlockedWatch");
            var gun = MyDefinitionManager.Static.GetAllDefinitions().OfType<MyWeaponBlockDefinition>()
                .Where(d => d.Public && d.Id.TypeId.ToString().EndsWith("SmallGatlingGun"))
                .OrderByDescending(d => d.CubeSize == MyCubeSize.Large).FirstOrDefault();
            SkipUnless(gun != null, "no gatling gun in this game");
            var log = TorchLog();
            var start = new FileInfo(log).Length;
            yield return Arrive(Site.At(0, -40, 0));

            var pve = Integrations.TypeOf(Integrations.Gameplay, "SentisGameplayImprovements.PveZone.PvECore");
            MyCubeGrid shielded = null, plain = null, zoned = null;
            var lanes = pve != null ? 3 : 2;
            if (pve == null) Note("no SentisGameplayImprovements' PvE zone: its lane left out");
            var guns = new MyCubeBlock[lanes];
            for (var i = 0; i < lanes; i++)
            {
                var at = Site.Origin + Site.Right * (i * 600);
                var large = gun.CubeSize == MyCubeSize.Large;
                var battery = large ? "BatteryBlock/LargeBlockBatteryBlock" : "BatteryBlock/SmallBlockBatteryBlock";
                var station = Build("blocked-gun", at, true, new[]
                {
                    new Part(gun.Id.TypeId.ToString() + "/" + gun.Id.SubtypeName, 0, 0, 0),
                    new Part(battery, -Definition(battery).Size.X, 0, 0).With(b => Motion.Charged(b)),
                }, gun.CubeSize);
                var block = station.GetCubeBlock(Vector3I.Zero).FatBlock;
                var weapon = MyDefinitionManager.Static.GetWeaponDefinition(gun.WeaponDefinitionId);
                var magazine = weapon.AmmoMagazinesId.First();
                Put(Inventory(block), magazine.TypeId.ToString().Replace("MyObjectBuilder_", "") + "/" + magazine.SubtypeName, 5);
                var wall = BuildFor(WorldApi.TestIdentityId(), i == 0 ? "blocked-wall" : i == 1 ? "plain-wall" : "pve-wall",
                    at + Site.Forward * 100, Bodies.Wall("CubeBlock/LargeHeavyBlockArmorBlock", 3, 3)
                        // an owned block: the grid's owner (armour has none), whom the rounds' hits are counted against
                        .Concat(new[] { new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, -3).With(b => Motion.Charged(b)) }).ToArray());
                if (i == 0)
                {
                    wall.DestructibleBlocks = false;
                    shielded = wall;
                }
                else if (i == 1) plain = wall;
                else
                {
                    zoned = wall;
                    // the PvE zone round this wall only (the lanes are 600 m apart)
                    PveZone(pve, new BoundingSphereD(wall.PositionComp.GetPosition(), 60));
                    Check(PveProtects(pve, wall), "the wall in the PvE zone put round it is not protected by it");
                }
                guns[i] = block;
            }
            // the walls' owner (BigOwners) is the game's next update's: the hits are counted against it
            yield return Seconds(2);
            foreach (var block in guns)
            {
                Switch(block, true);
                ((Sandbox.ModAPI.IMyTerminalBlock)block).GetActionWithName("Shoot_On")?.Apply(block);
            }
            Note("walls' owners: " + string.Join(", ", new[] { shielded, plain }.Select(w => w.DisplayName + " " + string.Join("/", w.BigOwners) +
                 " (" + (w.BigOwners.Count > 0 ? MyIDModule.GetRelationPlayerPlayer(w.BigOwners[0], Identity).ToString() : "no owner") + ")")));
            var whole = Integrity(plain);
            var shieldedWhole = Integrity(shielded);
            var zonedWhole = zoned != null ? Integrity(zoned) : 0;
            yield return Seconds(6);
            if (zoned != null) Check(Math.Abs(Integrity(zoned) - zonedWhole) < 0.01, "the wall in the PvE zone was damaged by a player: the zone does not work");
            Check(Integrity(plain) < whole, "the plain wall took no damage from the gatling gun: the check's guns do not hit");
            Check(Math.Abs(Integrity(shielded) - shieldedWhole) < 0.01, "the wall with DestructibleBlocks off was damaged");
            // the window (10 s) ends, then NLog writes in the background
            yield return Seconds(14);
            var text = Tail(log, start);
            bool Told(long id) => text.Split('\n').Any(l => l.Contains("WARN") && l.Contains("takes no damage") && l.Contains(id.ToString()));
            var line = text.Split('\n').FirstOrDefault(l => l.Contains("takes no damage") && l.Contains(shielded.EntityId.ToString()));
            Note("told: " + (line?.Trim() ?? "nothing"));
            Check(Told(shielded.EntityId), "the wall with DestructibleBlocks off, shot, is not told");
            Check(line != null && line.Contains("DestructibleBlocks is off"), "the reason (DestructibleBlocks off) is not told");
            Check(!Told(plain.EntityId), "the plain wall, damaged, is told as taking no damage");
            if (zoned != null) Check(!Told(zoned.EntityId), "the wall in SGI's PvE zone, shot by a player, is told as taking no damage");
            Result("the wall with DestructibleBlocks off shot and told with the reason; the plain one damaged, not told" +
                   (zoned != null ? "; the one in SGI's PvE zone undamaged, not told" : ""));
        }

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private object _oldZone;
        private bool? _oldEnabled;
        private Type _pve;

        /// <summary>SGI's PvE zone set to the sphere in memory: the zone (PvECore._zone) and the setting's field, no setter (it would write the file).</summary>
        private void PveZone(Type pve, BoundingSphereD sphere)
        {
            _pve = pve;
            var zoneField = pve.GetField("_zone", Any);
            var zoneType = pve.GetNestedType("Zone", Any);
            var config = Config(pve);
            var enabled = config.GetType().GetField("_pveZoneEnabled", Any);
            _oldZone = zoneField.GetValue(null);
            _oldEnabled = (bool)enabled.GetValue(config);
            zoneField.SetValue(null, Activator.CreateInstance(zoneType, Any, null, new object[] { sphere }, null));
            enabled.SetValue(config, true);
        }

        private static object Config(Type pve) =>
            pve.Assembly.GetType("SentisGameplayImprovements.SentisGameplayImprovementsPlugin").GetProperty("Config", Any).GetValue(null);

        private static bool PveProtects(Type pve, MyCubeGrid grid) => (bool)pve.GetMethod("IsProtected", Any).Invoke(null, new object[] { grid });

        public override void Cleanup()
        {
            try
            {
                if (_pve != null && _oldEnabled.HasValue)
                {
                    _pve.GetField("_zone", Any).SetValue(null, _oldZone);
                    var config = Config(_pve);
                    config.GetType().GetField("_pveZoneEnabled", Any).SetValue(config, _oldEnabled.Value);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "damage_blocked: SGI's PvE zone not set back");
            }
            base.Cleanup();
        }

        private static double Integrity(MyCubeGrid grid) => grid.MarkedForClose ? 0 : grid.GetBlocks().Sum(b => (double)b.Integrity);

        private static string TorchLog() =>
            Directory.GetFiles(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs"), "Torch-*.log").OrderByDescending(File.GetLastWriteTimeUtc).First();

        private static string Tail(string path, long from)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(Math.Min(from, stream.Length), SeekOrigin.Begin);
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }
    }
}
