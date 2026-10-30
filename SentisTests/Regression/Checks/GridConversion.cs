using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using SentisTests.Core;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A static grid turned into a ship: by the player's request ("Convert to ship", <c>OnConvertedToShipRequest</c>
    /// with ConvertToShip), and by the server's own test after a split (<c>TestDynamic = GridSplit</c>, the grid in the
    /// air): both become ships, and SentisOptimisations' <c>GridConversionLog</c> writes a line for each (its count of
    /// lines goes up by two; the lines - asker, reason, the stacks - are in the log).
    /// </summary>
    public sealed class GridConversionCheck : RegressionCheck
    {
        public override string Name => Prefix + "grid_conversion";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 3, -6));
            var asked = Build("conversion-asked", Site.At(0, 40, 0), true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            var split = Build("conversion-split", Site.At(0, 40, 20), true, new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0), new Part("CubeBlock/LargeBlockArmorBlock", 1, 0, 0));
            yield return WaitForTicks(5);
            Check(asked.IsStatic && split.IsStatic, "the grids were not built static");

            var log = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisationsPlugin.CrashFix.GridConversionLog", false)).FirstOrDefault(t => t != null);
            long Written() => log == null ? 0 : (long)log.GetField("Written").GetValue(null);
            var before = Written();

            var reason = typeof(MyCubeGrid).GetNestedType("MyTestDynamicReason", BindingFlags.NonPublic | BindingFlags.Public)
                         ?? throw new ScenarioFailedException("MyCubeGrid.MyTestDynamicReason is not in this game");
            Request(asked, "OnConvertedToShipRequest", Enum.Parse(reason, "ConvertToShip"));
            typeof(MyCubeGrid).GetProperty("TestDynamic", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .SetValue(split, Enum.Parse(reason, "GridSplit"));
            yield return WaitForTicks(15);

            Check(!asked.IsStatic, "the grid the player asked to turn into a ship is still static");
            Check(!split.IsStatic, "the grid tested after a split, in the air, is still static");
            if (log != null) Check(Written() - before == 2, $"SentisOptimisations wrote {Written() - before} conversions, 2 expected");
            Result("both grids turned into ships (asked by the player, tested after a split)" + (log == null ? " - SentisOptimisations not here" : "; both written to the log"));
        }
    }
}
