using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Weapons;
using SentisTests.Game;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisOptimisations' TurretShootIdle: a turret not shooting, set to stop (Shoot = false, as fire control scripts do
    /// every tick), is not told so over the network (the stop counted as left out); set to shoot it shoots, and its stop
    /// then goes through and ends the shooting; with the setting off every stop goes through.
    /// </summary>
    public sealed class TurretShootIdleCheck : RegressionCheck
    {
        public override string Name => Prefix + "turret_shoot_idle";

        private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

        protected override IEnumerator Check()
        {
            var patch = Find("SentisOptimisationsPlugin.Perfomance.TurretShootIdle");
            SkipUnless(patch != null, "SentisOptimisations has no TurretShootIdle");
            var plugin = Find("SentisOptimisationsPlugin.SentisOptimisationsPlugin");
            var config = plugin?.GetProperty("Config", BindingFlags.Static | BindingFlags.Public)?.GetValue(null)
                         ?? plugin?.GetField("Config", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            var setting = config?.GetType().GetProperty("TurretShootIdle");
            SkipUnless(setting != null, "SentisOptimisations' config has no TurretShootIdle");
            long Skipped() => (long)patch.GetField("Skipped").GetValue(null);
            var playerShooting = typeof(MyLargeTurretBase).GetField("m_isPlayerShooting", BindingFlags.Instance | BindingFlags.NonPublic);

            yield return Arrive(Site.At(0, 4, -6));
            var grid = Build("turret-shoot-idle", Site.Origin, true,
                new Part("LargeGatlingTurret/", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var turret = Block<MyLargeTurretBase>(grid);
            var gun = (Sandbox.ModAPI.IMyLargeTurretBase)turret;
            yield return Until(() => turret.IsWorking ? 1 : 0, v => v > 0, "the turret working", 10, () => State(turret));
            var was = (bool)setting.GetValue(config);
            try
            {
                setting.SetValue(config, true);

                // not shooting: ten stops, none sent
                var before = Skipped();
                for (var i = 0; i < 10; i++) gun.Shoot = false;
                Check(Skipped() - before == 10, "of ten stops of an idle turret " + (Skipped() - before) + " left out");

                // set to shoot: it shoots; the stop goes through and ends it
                gun.Shoot = true;
                yield return Seconds(0.5);
                var shooting = (bool)playerShooting.GetValue(turret);
                Note("set to shoot: Shoot " + gun.Shoot + ", shooting for the player " + shooting);
                Check(gun.Shoot && shooting, "a turret set to shoot does not shoot (Shoot " + gun.Shoot + ", shooting " + shooting + ")");
                // counted at once, no frame between: the count is the server's, other turrets stop too
                before = Skipped();
                gun.Shoot = false;
                var left = Skipped() - before;
                Check(left == 0, "the stop of a shooting turret was left out");
                yield return Seconds(0.2);
                Check(!gun.Shoot && !(bool)playerShooting.GetValue(turret) && !gun.IsShooting,
                    "the turret still shoots after its stop (Shoot " + gun.Shoot + ", for the player " + playerShooting.GetValue(turret) + ", shooting " + gun.IsShooting + ")");

                // the setting off: every stop goes through
                setting.SetValue(config, false);
                before = Skipped();
                for (var i = 0; i < 5; i++) gun.Shoot = false;
                Check(Skipped() == before, "with the setting off " + (Skipped() - before) + " stops were left out");
            }
            finally
            {
                setting.SetValue(config, was);
            }
            Result("an idle turret's ten stops left out; set to shoot it shot, its stop went through and ended it; with the setting off every stop went through");
        }
    }
}
