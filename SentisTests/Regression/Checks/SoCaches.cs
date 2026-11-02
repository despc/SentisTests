using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using SentisTests.Game;
using VRage.Game;

namespace SentisTests.Regression.Checks
{
    /// <summary>A SentisOptimisations counter read by reflection (the checks run without a reference to the plugin).</summary>
    internal static class SoCounters
    {
        public static Type Type(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("Optimizer.Optimizations." + name, false)).FirstOrDefault(t => t != null);

        public static long Read(Type type, string field) => (long)(type.GetField(field, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? -1L);
    }

    /// <summary>
    /// The text of an enum value comes from SentisOptimisations' cache (EnumNameCache, a patch of the runtime's own
    /// Enum.InternalFormat): the patch is in place - a value asked again is answered from the cache - and the text is the
    /// runtime's, for a plain value, a flags combination and a number with no name.
    /// </summary>
    public sealed class EnumNameCacheCheck : RegressionCheck
    {
        public override string Name => Prefix + "enum_name_cache";

        [Flags] private enum Bits { None = 0, A = 1, B = 2, C = 4 }

        protected override IEnumerator Check()
        {
            var type = SoCounters.Type("EnumNameCache");
            SkipUnless(type != null, "SentisOptimisations' EnumNameCache is not loaded");
            yield return null;
            var hits = SoCounters.Read(type, "Hits");
            var misses = SoCounters.Read(type, "Misses");
            var values = new Enum[] { MyCubeSize.Large, MyCubeSize.Small, Bits.A | Bits.C, (Bits)64, (MyCubeSize)77 };
            var expected = new[] { "Large", "Small", "A, C", "64", "77" };
            for (var round = 0; round < 3; round++)
                for (var i = 0; i < values.Length; i++)
                    Check(values[i].ToString() == expected[i] && $"{values[i]}" == expected[i] && string.Format("{0}", values[i]) == expected[i],
                        "round " + round + ": " + expected[i] + " came out as " + values[i] + " / " + $"{values[i]}");
            var hit = SoCounters.Read(type, "Hits") - hits;
            var missed = SoCounters.Read(type, "Misses") - misses;
            // 5 values x 3 forms x 3 rounds: each value is worked out by the runtime at most once (another thread may have
            // asked it first), the rest come from the cache
            Check(hit >= 40, "the enum texts did not come from the cache: " + hit + " hits, " + missed + " misses of 45 calls (the patch is not in place?)");
            Result("45 enum texts as the runtime makes them; " + hit + " from the cache, " + missed + " worked out");
        }
    }

    /// <summary>
    /// Four turrets on one grid and an enemy station in their range: the grid's list of the grid groups around it is
    /// made anew at most every 10 frames and taken from SentisOptimisations' cache in between (GridConnectionsLock), and
    /// the turrets still find the enemy and fire.
    /// </summary>
    public sealed class TurretGridConnectionsCheck : RegressionCheck
    {
        public override string Name => Prefix + "turret_grid_connections";
        public override int TimeoutSeconds => 150;
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            var type = SoCounters.Type("GridConnectionsLock");
            SkipUnless(type != null && type.GetField("Kept") != null, "SentisOptimisations' GridConnectionsLock cache is not loaded");
            HoldHighQuality();
            yield return Arrive(Site.At(0, 10, 20));
            var grid = Build("grid-connections", Site.Origin, true,
                new Part("LargeGatlingTurret/", 0, 0, 0), new Part("LargeGatlingTurret/", 3, 0, 0),
                new Part("LargeGatlingTurret/", 6, 0, 0), new Part("LargeGatlingTurret/", 9, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 2).With(b => Motion.Charged(b)));
            var turrets = grid.GetFatBlocks().OfType<SpaceEngineers.Game.Entities.Weapons.MyLargeGatlingTurret>().ToList();
            Check(turrets.Count == 4, "four turrets expected, built " + turrets.Count);
            foreach (var turret in turrets) Put(Inventory(turret), "AmmoMagazine/NATO_25x184mm", 10);
            var kept = SoCounters.Read(type, "Kept");
            var rebuilt = SoCounters.Read(type, "Rebuilt");
            var enemy = BuildFor(WorldApi.TestIdentityId(), "grid-connections-target", Site.At(0, 0, 150),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", -1, 0, 0));
            var health = enemy.GetBlocks().Sum(b => (double)b.Integrity);
            yield return Until(() => turrets.Count(t => ((Sandbox.ModAPI.Ingame.IMyLargeTurretBase)t).HasTarget), n => n == turrets.Count,
                "every turret locking the enemy", 60, () => string.Join("; ", turrets.Select(State)));
            yield return Until(() => health - (enemy.MarkedForClose ? 0 : enemy.GetBlocks().Sum(b => (double)b.Integrity)), v => v > 0,
                "damage to the enemy from the turrets", 60);
            yield return Seconds(3);
            var keptNow = SoCounters.Read(type, "Kept") - kept;
            var rebuiltNow = SoCounters.Read(type, "Rebuilt") - rebuilt;
            Check(rebuiltNow > 0, "the list of grids around was never made (" + rebuiltNow + "): the searches did not reach it");
            Check(keptNow > 0, "the list of grids around was never taken from the cache: " + rebuiltNow + " made, " + keptNow + " kept");
            Result("4 turrets locked the enemy and fired (" + F(health - (enemy.MarkedForClose ? 0 : enemy.GetBlocks().Sum(b => (double)b.Integrity))) +
                   " integrity); the list of grids around made " + rebuiltNow + " times, kept " + keptNow);
        }
    }
}
