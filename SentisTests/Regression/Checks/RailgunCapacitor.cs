using System.Collections;
using Sandbox.Game.Weapons;
using Sandbox.Game.Weapons.Guns;
using SentisTests.Core;
using SentisTests.Game;
using VRage.ModAPI;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A large railgun's capacitor (<c>MyEntityCapacitorComponent</c>) on a station with power to spare, left at a fifth of
    /// its charge: it charges full in the time its draw takes (the charge goes in every 100 frames); switched off it stays
    /// as it is, switched on again it charges. With SentisOptimisations (CapacitorPace) a capacitor not charged updates
    /// every 100th frame only and a switched-off one not at all.
    /// </summary>
    public sealed class RailgunCapacitorCheck : RegressionCheck
    {
        public override string Name => Prefix + "railgun_capacitor";
        public override int TimeoutSeconds => 120;

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 6, -12));
            var grid = Build("railgun-capacitor", Site.Origin, true,
                new Part("SmallMissileLauncherReload/LargeRailgun", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 4, 0, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 4, 1, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 4, 2, 0).With(b => Motion.Charged(b)),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 4, 3, 0).With(b => Motion.Charged(b)));
            var gun = Block<MySmallMissileLauncherReload>(grid);
            yield return Until(() => gun.IsWorking ? 1 : 0, v => v > 0, "the railgun working", 10, () => State(gun));
            var capacitor = gun.Components.Get<MyEntityCapacitorComponent>();
            Check(capacitor != null, "the railgun has no capacitor");
            var paced = Integrations.TypeOf(Integrations.Optimisations, "Optimizer.Optimizations.CapacitorPace") != null;
            var full = capacitor.FullRechargeTimeMs / 1000f;

            // left at a fifth: charges the rest in its time (the draw met)
            capacitor.SetStoredPower(capacitor.Capacity * 0.2f);
            Check(!capacitor.IsCharged, "the capacitor set to a fifth is charged");
            var updates = capacitor.NeedsUpdate;
            if (paced) Check(updates == MyEntityUpdateEnum.EACH_100TH_FRAME, "the capacitor charging updates " + updates + ", not every 100th frame only");
            var rest = full * 0.8f;
            var started = System.DateTime.UtcNow;
            yield return Until(() => capacitor.StoredPowerRatio, v => v >= 0.999f, "the capacitor charged", (int)(rest * 1.5f) + 10,
                () => "stored " + F(capacitor.StoredPowerRatio * 100) + " %, " + State(gun));
            var took = (System.DateTime.UtcNow - started).TotalSeconds;
            Check(capacitor.IsCharged, "the capacitor full is not charged");
            Check(capacitor.NeedsUpdate == MyEntityUpdateEnum.NONE, "the capacitor charged still updates: " + capacitor.NeedsUpdate);

            // switched off, emptied: stays empty, no updates; on again: charges
            Switch(gun, false);
            yield return Seconds(1);
            capacitor.SetStoredPower(capacitor.Capacity * 0.9f);
            var off = capacitor.NeedsUpdate;
            if (paced) Check(off == MyEntityUpdateEnum.NONE, "the switched-off capacitor updates " + off);
            yield return Seconds(4);
            Check(capacitor.StoredPowerRatio < 0.91f, "the switched-off capacitor charged to " + F(capacitor.StoredPowerRatio * 100) + " %");
            Switch(gun, true);
            yield return Until(() => capacitor.StoredPowerRatio, v => v >= 0.999f, "the capacitor charged after the railgun was switched on", (int)(full * 0.15f) + 10,
                () => "stored " + F(capacitor.StoredPowerRatio * 100) + " %, updates " + capacitor.NeedsUpdate + ", " + State(gun));
            Result("a fifth to full in " + F(took) + " s (the draw's time " + F(rest) + " s), updates while charging " + updates +
                   "; switched off " + off + ", stayed; on again, charged" + (paced ? "" : " (no CapacitorPace)"));
        }
    }
}
