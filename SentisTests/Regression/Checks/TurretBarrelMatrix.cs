using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Weapons;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A turret without its barrel asked for its matrix (<c>MyLargeTurretBase.GetWorldMatrix</c>, which is
    /// <c>Barrel.Entity.WorldMatrix</c>): a character's target lock kept such a turret and its check of the target took
    /// the live server down with a NullReferenceException (08.11.2026 17:18). SentisOptimisations (TurretBarrelMatrix)
    /// gives the block's own matrix then. A gatling turret built whole gives its barrel's matrix as ever; ground below
    /// its built state (the game makes the barrel only while the block is built) and then closed, it gives the block's
    /// own and nothing throws.
    /// </summary>
    public sealed class TurretBarrelMatrixCheck : RegressionCheck
    {
        public override string Name => Prefix + "turret_barrel_matrix";

        private static long Given() => (long)(AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("SentisOptimisationsPlugin.CrashFix.TurretBarrelMatrix", false)).FirstOrDefault(t => t != null)
            ?.GetField("Given", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? -1L);

        protected override IEnumerator Check()
        {
            SkipUnless(Given() >= 0, "SentisOptimisations' TurretBarrelMatrix is not loaded");
            yield return Arrive(Site.At(0, 4, 8));
            var grid = Build("turret-barrel", Site.Origin, true,
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 0).With(b => Motion.Charged(b)),
                new Part("LargeGatlingTurret/", 0, 1, 0));
            var turret = Block<MyLargeTurretBase>(grid);
            yield return Seconds(1);

            // built whole: the barrel's matrix, as the game gives it
            Check(turret.Barrel?.Entity != null, "the turret built whole has no barrel");
            var given0 = Given();
            var whole = turret.GetWorldMatrix();
            var barrel = turret.Barrel.Entity.WorldMatrix;
            Check(whole.EqualsFast(ref barrel, 1e-6), "the turret built whole did not give its barrel's matrix");
            Check(Given() == given0, "the turret built whole was counted as one without its barrel");

            // ground below its built state: the barrel goes, the turret stays
            var slim = turret.SlimBlock;
            for (var i = 0; i < 200 && turret.IsBuilt && slim.Integrity > slim.MaxIntegrity * 0.1f; i++)
                slim.DecreaseMountLevel(slim.MaxIntegrity * 0.02f, null, true);
            yield return Seconds(0.5);
            Check(!turret.IsBuilt && !turret.Closed, $"the turret is not ground below its built state: built {turret.IsBuilt}, closed {turret.Closed}, integrity {slim.Integrity / slim.MaxIntegrity:P0}");
            Check(turret.Barrel?.Entity == null, "the turret ground below its built state still has its barrel (nothing to check)");
            MatrixD ground = default;
            string threw = null;
            try { ground = turret.GetWorldMatrix(); }
            catch (Exception e) { threw = e.GetType().Name; }
            Check(threw == null, "the turret without its barrel threw " + threw + " asked for its matrix");
            var own = turret.WorldMatrix;
            Check(ground.EqualsFast(ref own, 1e-6), "the turret without its barrel did not give its block's matrix");

            // closed: the same
            grid.Close();
            yield return Seconds(0.5);
            Check(turret.Closed, "the turret's grid did not close");
            threw = null;
            try { turret.GetWorldMatrix(); }
            catch (Exception e) { threw = e.GetType().Name; }
            Check(threw == null, "the closed turret threw " + threw + " asked for its matrix");
            Check(Given() >= given0 + 2, $"the turrets without their barrels were not answered by TurretBarrelMatrix: {Given() - given0}");
            Result($"built whole: its barrel's matrix; ground below its built state ({slim.Integrity / slim.MaxIntegrity:P0}) and closed: the block's own, nothing thrown");
        }
    }
}
