using System.Collections;
using System.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Game;
using VRage.Game;
using VRage.ObjectBuilders;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A hydrogen tank welded up next to a full one (as a projector's welders build a torpedo on its rack): it fills
    /// from the full one, and what the full one gives is what it takes - no gas lost (prod 06.11.2026: torpedoes welded
    /// on a ship took 750 L/s each and stayed empty, the ship's tank drained).
    /// </summary>
    public sealed class TankWeldFillCheck : RegressionCheck
    {
        public override string Name => Prefix + "tank_weld_fill";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 4, -8));
            var source = new Part("OxygenTank/LargeHydrogenTankSmall", 0, 0, 0).With(b => ((MyObjectBuilder_GasTank)b).FilledRatio = 1f);
            Part target = null;
            yield return ConveyorNeighbour(source, "OxygenTank/LargeHydrogenTankSmall", null, p => target = p);
            target.Built = 0.05f;
            var taken = Cells(source).Concat(Cells(target)).ToList();
            var free = Enumerable.Range(1, 8).Select(x => new VRageMath.Vector3I(0, -x, 0)).First(c => !taken.Contains(c));
            var grid = Build("tank-weld-fill", Site.Origin, true, source, target,
                new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z).With(b => Motion.Charged(b)));
            var full = (MyGasTank)grid.GetCubeBlock(source.Min).FatBlock;
            var slim = grid.GetCubeBlock(target.Min);
            yield return Seconds(3);
            Note("before welding: the frame " + F(slim.Integrity / slim.MaxIntegrity * 100) + " %, the full tank " + full.FilledRatio.ToString("F4"));

            // welded up from its components, as a welder does
            var inventory = Inventory(Body);
            var definition = slim.BlockDefinition;
            for (var i = 0; i < 100 && !slim.IsFullIntegrity; i++)
            {
                foreach (var component in definition.Components)
                    inventory.AddItems(component.Count, (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(component.Definition.Id));
                slim.MoveItemsToConstructionStockpile(inventory);
                slim.IncreaseMountLevel(slim.MaxIntegrity, Identity, inventory, 1f, false);
            }
            Check(slim.IsFullIntegrity, "the tank not welded whole: " + F(slim.Integrity / slim.MaxIntegrity * 100) + " %");
            var welded = (MyGasTank)slim.FatBlock;
            // standing welded and idle past the tank's 100-frame check (it stops its per-frame updates), then set to
            // stockpile, as a torpedo's script does to fill it from the ship (a tank fills from a tank only so)
            yield return Seconds(4);
            Note("welded, idle: per-frame updates " + ((welded.NeedsUpdate & VRage.ModAPI.MyEntityUpdateEnum.EACH_FRAME_AFTER) != 0));
            ((Sandbox.ModAPI.Ingame.IMyGasTank)welded).Stockpile = true;
            var given0 = full.FilledRatio * full.Capacity;
            var taken0 = welded.FilledRatio * welded.Capacity;
            yield return Seconds(8);
            var given = given0 - full.FilledRatio * full.Capacity;
            var got = welded.FilledRatio * welded.Capacity - taken0;
            var sink = welded.ResourceSink.CurrentInputByType(welded.BlockDefinition.StoredGasId);
            Note($"8 s welded: the full tank gave {F(given)} L, the welded one took {F(got)} L (now {welded.FilledRatio:F4}, input {F(sink)} L/s, working {welded.IsWorking})");
            Check(got > 0, $"the welded tank took nothing in 8 s (input {F(sink)} L/s, the full tank gave {F(given)} L)");
            Check(System.Math.Abs(given - got) <= System.Math.Max(1000, given * 0.02), $"gas lost: the full tank gave {F(given)} L, the welded one took {F(got)} L");
            Result($"the welded tank filled from the full one: {F(got)} L of the {F(given)} L given");
        }
    }
}
