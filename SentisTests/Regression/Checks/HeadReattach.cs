using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.ModAPI.Interfaces;
using SentisTests.Game;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A rotor's head torn off by the game (its joint pulled further apart than the rotor's safety detach) is put back on by
    /// SentisOptimisations (HeadDetachWatch) - its pivot back on the stator's; with a block standing where the head
    /// belongs it is not, until the block is gone; a head a script took off stays off.
    /// </summary>
    public sealed class HeadReattachCheck : RegressionCheck
    {
        public override string Name => Prefix + "head_reattach";
        public override int TimeoutSeconds => 120;

        private static Type Watch => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("SentisOptimisationsPlugin.CrashFix.HeadDetachWatch", false)).FirstOrDefault(t => t != null);

        private static long Count(string field) => (long)(Watch?.GetField(field, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? -1L);

        protected override IEnumerator Check()
        {
            SkipUnless(Watch != null, "SentisOptimisations' HeadDetachWatch is not loaded");
            yield return Arrive(Site.At(8, 4, 0));
            var grid = Build("head-reattach", Site.Origin, true,
                new Part("MotorStator/LargeStator", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var stator = Block<MyMotorStator>(grid);
            yield return AddHead(stator);
            var rotor = (Sandbox.ModAPI.Ingame.IMyMotorStator)stator;
            var head = stator.TopBlock;
            var headGrid = head.CubeGrid;
            // past MechanicalTopCheck's watch of a head just made (12 checks 15 frames apart): it puts heads right too
            yield return Seconds(8);
            var home = headGrid.WorldMatrix;
            double Off() => Vector3D.Distance(Vector3D.Transform(stator.DummyPosition, grid.WorldMatrix),
                                              Vector3D.Transform(head.Position * headGrid.GridSize, headGrid.WorldMatrix));

            // 1. torn off by the game: the head pulled 2 m sideways with the safety detach at its least
            var tear = Tear(stator, headGrid, home);
            while (tear.MoveNext()) yield return tear.Current;
            var putBack = Count("PutBack");
            yield return Until(() => rotor.IsAttached ? 1 : 0, v => v > 0, "the torn-off head put back on", 15,
                () => "the head " + F(Off()) + " m off, put back " + (Count("PutBack") - putBack));
            yield return Seconds(1);
            Check(rotor.IsAttached && Off() < 0.2, "the head put back is " + F(Off()) + " m off its pivot");
            var first = "put back, " + F(Off()) + " m off its pivot";

            // 2. a block where the head belongs: it waits; the block gone, it goes back
            var tear2 = Tear(stator, headGrid, home);
            while (tear2.MoveNext()) yield return tear2.Current;
            var block = Build("head-reattach-block", home.Translation, true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0));
            yield return Seconds(3);
            Check(!rotor.IsAttached, "the head was put back into a block standing where it belongs");
            var stuck = F(Vector3D.Distance(headGrid.PositionComp.GetPosition(), home.Translation));
            block.Close();
            yield return Until(() => rotor.IsAttached ? 1 : 0, v => v > 0, "the head put back once the block was gone", 15, () => "the head " + F(Off()) + " m off");
            yield return Seconds(1);
            Check(Off() < 0.2, "the head put back after the block is " + F(Off()) + " m off its pivot");

            // 3. taken off by a script: it stays off
            rotor.Detach();
            yield return Until(() => rotor.IsAttached ? 0 : 1, v => v > 0, "the head taken off by a script", 5);
            yield return Seconds(3);
            Check(!rotor.IsAttached, "a head a script took off was put back on");
            Result("torn off by the game: " + first + "; with a block where it belongs: not put back (" + stuck + " m away), put back once the block was gone; " +
                   "taken off by a script: stays off");
        }

        /// <summary>The head moved 2 m sideways with the rotor's safety detach at its least: the game tears it off.</summary>
        private IEnumerator Tear(MyMotorStator stator, MyCubeGrid headGrid, MatrixD home)
        {
            var rotor = (Sandbox.ModAPI.Ingame.IMyMotorStator)stator;
            var safetyDetached = Count("SafetyDetached");
            ((Sandbox.ModAPI.IMyTerminalBlock)stator).SetValueFloat("SafetyDetach", 0f);
            for (var i = 0; i < 30 && rotor.IsAttached; i++)
            {
                var pulled = home;
                pulled.Translation += home.Right * 2;
                ((VRage.ModAPI.IMyEntity)headGrid).SetWorldMatrix(pulled);
                yield return null;
            }
            Check(!rotor.IsAttached, "the head pulled 2 m off did not come off");
            Check(Count("SafetyDetached") > safetyDetached, "the head came off but not as torn off by the game (safety detach)");
            ((Sandbox.ModAPI.IMyTerminalBlock)stator).SetValueFloat("SafetyDetach", 5f);
        }
    }
}
