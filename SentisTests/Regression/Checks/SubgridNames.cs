using System;
using System.Collections;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using SentisTests.Game;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// SentisGameplayImprovements' AutoRenameGrids names a rotor's or a piston's head the game called "Subgrid" (in any
    /// language) "{main grid}_Subgrid_{n}": n from 1, in the order of the heads' ids, the numbers taken kept; while the
    /// main grid has the game's default name the heads wait for it.
    /// </summary>
    public sealed class SubgridNamesCheck : RegressionCheck
    {
        public override string Name => Prefix + "subgrid_names";

        private static Type Renamer => Core.Integrations.TypeOf("SentisGameplayImprovements", "SentisGameplayImprovements.BackgroundActions.GridAutoRenamer");

        protected override IEnumerator Check()
        {
            SkipUnless(Renamer != null, "SentisGameplayImprovements' GridAutoRenamer is not loaded");
            var renamer = Activator.CreateInstance(Renamer);
            void Rename(MyCubeGrid g) => Renamer.GetMethod("CheckAndRename").Invoke(renamer, new object[] { g });

            yield return Arrive(Site.At(10, 4, -10));
            var grid = Build("subgrid-names", Site.Origin, true,
                new Part("MotorStator/LargeStator", 0, 0, 0),
                new Part("MotorStator/LargeStator", 3, 0, 0),
                new Part("MotorStator/LargeStator", 6, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 1, 0, 0).With(b => Motion.Charged(b)));
            var stators = grid.GetFatBlocks().OfType<MyMotorStator>().OrderBy(s => s.Position.X).ToList();
            foreach (var stator in stators) yield return AddHead(stator);
            yield return Seconds(1);
            var heads = stators.Select(s => s.TopBlock.CubeGrid).ToList();
            var byId = heads.OrderBy(h => h.EntityId).ToList();

            // the main grid still has the game's default name: the heads wait
            grid.DisplayName = "Large Grid 4321";
            heads[0].DisplayName = "Subgrid";
            heads[1].DisplayName = "Подструктура";
            heads[2].DisplayName = "Subgrid";
            foreach (var h in heads) Rename(h);
            var waited = string.Join(", ", heads.Select(h => h.DisplayName));
            Note("the main grid named by the game: " + waited);
            Check(heads.All(h => h.DisplayName == "Subgrid" || h.DisplayName == "Подструктура"), "heads renamed after a main grid with the game's name: " + waited);

            grid.DisplayName = "Fixture Ship";
            // renamed through the game's request (sent to the players), not a plain DisplayName: OnNameChanged fires
            var told = 0;
            Action<MyCubeGrid> onName = _ => told++;
            foreach (var h in heads) h.OnNameChanged += onName;
            Rename(heads[1]);
            foreach (var h in heads) h.OnNameChanged -= onName;
            Check(told == 3, "the heads' renames told the game " + told + " times of 3");
            var named = string.Join(", ", byId.Select(h => h.DisplayName));
            Note("renamed: " + named);
            for (var i = 0; i < byId.Count; i++)
                Check(byId[i].DisplayName == "Fixture Ship_Subgrid_" + (i + 1), "head " + (i + 1) + " by id named '" + byId[i].DisplayName + "'");

            // a head made again: the next free number, the others kept
            byId[1].DisplayName = "Subgrid";
            foreach (var h in heads) Rename(h);
            Rename(grid);
            var again = string.Join(", ", byId.Select(h => h.DisplayName));
            Note("the second head named Subgrid again: " + again);
            Check(byId[0].DisplayName == "Fixture Ship_Subgrid_1" && byId[1].DisplayName == "Fixture Ship_Subgrid_2" &&
                  byId[2].DisplayName == "Fixture Ship_Subgrid_3" && grid.DisplayName == "Fixture Ship", "renamed again: " + again);
            Result("waited for the main grid's name; then " + named + "; one renamed again: " + again);
        }
    }
}
