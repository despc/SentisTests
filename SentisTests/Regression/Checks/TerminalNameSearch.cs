using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Ingame = Sandbox.ModAPI.Ingame;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A script's search of blocks by name (<c>GridTerminalSystem.SearchBlocksOfName</c>, <c>GetBlockWithName</c>) answers
    /// as the game's own does, with SentisOptimisations keeping what it found (<c>TerminalNameSearch</c>): each answer is
    /// checked against the game's rule worked out here from the system's blocks - a name holding the asked one with no
    /// regard to case (in Russian too), the blocks in the system's order, open to scripts, through the script's filter;
    /// GetBlockWithName the very name. Asked again, the same; a block renamed into the name, one renamed out of it, one
    /// taken off the grid - each seen at the next search.
    /// </summary>
    public sealed class TerminalNameSearchCheck : RegressionCheck
    {
        public override string Name => Prefix + "terminal_name_search";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 3, -6));
            var grid = Build("name-search", Site.Origin, true,
                new Part("CubeBlock/LargeBlockArmorBlock", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", -1, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, 1),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, 0, -1));
            var batteries = Blocks<MyBatteryBlock>(grid).OrderBy(b => b.Position.X).ThenBy(b => b.Position.Z).ToList();
            Check(batteries.Count == 4, "the grid has " + batteries.Count + " batteries");
            batteries[0].SetCustomName("Ракета 1");
            batteries[1].SetCustomName("пуск РАКЕТА 2");
            batteries[2].SetCustomName("Battery");
            batteries[3].SetCustomName("ракета 4");
            yield return WaitForTicks(1);

            var system = grid.GridSystems.TerminalSystem;
            var scripts = (Ingame.IMyGridTerminalSystem)system;
            var counters = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SentisOptimisationsPlugin.TerminalNameSearch", false)).FirstOrDefault(t => t != null);
            long Read(string field) => counters == null ? 0 : (long)counters.GetField(field).GetValue(null);

            // the game's rule, from the system's blocks
            List<MyTerminalBlock> Expected(string name, Func<Ingame.IMyTerminalBlock, bool> collect) =>
                system.Blocks.Where(b => System.StringExtensions.Contains(b.CustomName.ToString(), name, StringComparison.OrdinalIgnoreCase) &&
                                         b.IsAccessibleForProgrammableBlock && (collect == null || collect(b))).ToList();
            string Names(IEnumerable<Ingame.IMyTerminalBlock> blocks) => "[" + string.Join(", ", blocks.Select(b => b.CustomName)) + "]";
            void Same(string name, Func<Ingame.IMyTerminalBlock, bool> collect, int count, string when)
            {
                var got = new List<Ingame.IMyTerminalBlock> { batteries[2] };     // the search clears what the list had
                scripts.SearchBlocksOfName(name, got, collect);
                var expected = Expected(name, collect);
                Check(got.SequenceEqual(expected), $"{when}: \"{name}\" found {Names(got)}, the game's rule gives {Names(expected)}");
                Check(got.Count == count, $"{when}: \"{name}\" found {got.Count} blocks, {count} expected");
            }

            var searched = Read("Searched");
            var kept = Read("Kept");
            Same("ракета", null, 3, "the first search");
            Same("ракета", null, 3, "asked again");
            Same("РАКЕТА", b => b.CustomName != "ракета 4", 2, "with the script's filter");
            Same("nothing like it", null, 0, "a name no block has");
            if (counters != null) Check(Read("Kept") > kept && Read("Searched") > searched, $"SentisOptimisations kept nothing: searched {Read("Searched") - searched}, kept {Read("Kept") - kept}");

            Check(scripts.GetBlockWithName("ракета 4") == batteries[3], "GetBlockWithName(\"ракета 4\") is not that battery");
            Check(scripts.GetBlockWithName("Ракета 4") == null, "GetBlockWithName took a name of another case");

            batteries[2].SetCustomName("ракета 3");
            Same("ракета", null, 4, "a block renamed into the name");
            Check(scripts.GetBlockWithName("ракета 3") == batteries[2], "GetBlockWithName(\"ракета 3\") missed the block renamed to it");
            batteries[0].SetCustomName("Battery");
            Same("ракета", null, 3, "a block renamed out of the name");
            Check(scripts.GetBlockWithName("ракета 3") == batteries[2] && scripts.GetBlockWithName("Ракета 1") == null, "GetBlockWithName after the renames");

            grid.RazeBlock(batteries[3].Position);
            yield return WaitForTicks(5);
            Check(!grid.MarkedForClose, "the grid was built anew with a battery taken off");
            Same("ракета", null, 2, "a block taken off");
            Check(scripts.GetBlockWithName("ракета 4") == null, "GetBlockWithName still finds the block taken off");
            Result("the searches by name answer as the game's rule, asked again, after renames into and out of the name and a block taken off" +
                   (counters == null ? " (SentisOptimisations not here)" : $"; SO searched {Read("Searched") - searched}, kept {Read("Kept") - kept}"));
        }
    }
}
