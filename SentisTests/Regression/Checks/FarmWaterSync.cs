using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>
    /// A farm plot's water told to the clients in steps of 1% of the tank, not every frame (SO FarmPlotWaterSync): a plot
    /// filled by an irrigation system of ice, fruit planted. While the water flows (filling, then the plant drinking) the
    /// server's level moves as the game moves it, but most of its changes are kept on the server - few levels are sent -
    /// and the level the clients were told is never more than 1% off the server's; the plot's terminal text is sent no
    /// more than once in 100 frames; a few seconds after, the clients' level is the server's. The plant grows.
    /// </summary>
    public sealed class FarmWaterSyncCheck : RegressionCheck
    {
        public override string Name => Prefix + "farm_water_sync";
        public override SiteKind Kind => SiteKind.Planet;

        private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        protected override IEnumerator Check()
        {
            Type sync = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                sync = sync ?? assembly.GetType("Optimizer.Optimizations.FarmPlotWaterSync", false);
            if (sync == null) throw new Core.ScenarioSkippedException("SentisOptimisations' farm plot water sync is not loaded");
            long Count(string field) => (long)sync.GetField(field, BindingFlags.Public | BindingFlags.Static).GetValue(null);
            var sentLevel = sync.GetMethod("SentLevel", BindingFlags.Public | BindingFlags.Static);
            Check(sentLevel != null, "the farm plot water sync's test API is not what this check knows");

            var at = Site.Origin + Site.Up * 4;
            yield return Arrive(at + Site.Forward * 4);
            var plot = new Part("FunctionalBlock/LargeBlockFarmPlot", 0, 0, 0);
            Part irrigation = null;
            yield return ConveyorNeighbour(plot, "OxygenGenerator/IrrigationSystem", null, p => irrigation = p);
            var used = Cells(irrigation);
            used.Add(Vector3I.Zero);
            var free = Base6Directions.IntDirections.Select(d => (Vector3I)d).First(c => !used.Contains(c));
            var grid = Build("farm-sync", at, true, plot, irrigation,
                new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z).With(b => Motion.Charged(b)));
            var plotBlock = (MyCubeBlock)grid.GetCubeBlock(Vector3I.Zero).FatBlock;
            var farm = plotBlock.Components.Get<SpaceEngineers.Game.EntityComponents.GameLogic.MyFarmPlotLogic>();
            Check(farm != null, "the farm plot has no farm logic");
            var water = (Sandbox.ModAPI.IMyResourceStorageComponent)typeof(SpaceEngineers.Game.EntityComponents.GameLogic.MyFarmPlotLogic).GetField("m_resourceStorageComponent", Any).GetValue(farm);
            var irrigator = (MyCubeBlock)grid.GetCubeBlock(irrigation.Min).FatBlock;

            var sent0 = Count("LevelsSent");
            var kept0 = Count("LevelsKept");
            var info0 = Count("InfoSent");
            var held0 = Count("InfoHeld");
            var frames = 0;
            var worst = 0.0;
            void Watch()
            {
                frames++;
                var told = (double)sentLevel.Invoke(null, new object[] { water });
                if (!double.IsNaN(told)) worst = Math.Max(worst, Math.Abs(told - water.FilledRatio));
            }

            Put(Inventory(irrigator), "Ore/Ice", 200);
            for (var f = Frames(60); f > 0 && water.FilledRatio <= 0.2; f--)
            {
                Watch();
                yield return null;
            }
            Check(water.FilledRatio > 0.2, "the plot's water did not fill from the irrigation: " + water.FilledRatio.ToString("P1") + "; " + State(irrigator));

            Put(Inventory(Body), "SeedItem/Fruit", 3);
            Request(farm, "PlantSeed_Server", (VRage.ObjectBuilders.SerializableDefinitionId)Item("SeedItem/Fruit"));
            Check(farm.IsPlantPlanted, "the fruit seeds were not planted");
            var progress = typeof(SpaceEngineers.Game.EntityComponents.GameLogic.MyFarmPlotLogic).GetField("m_growthProgressSync", Any).GetValue(farm);
            Func<float> grown = () => (float)progress.GetType().GetProperty("Value").GetValue(progress);
            for (var f = Frames(15); f > 0 && grown() <= 0; f--)
            {
                Watch();
                yield return null;
            }
            Check(grown() > 0, "the plant is not growing: water " + water.FilledRatio.ToString("P1") + ", alive " + farm.IsAlive);
            for (var f = Frames(3); f > 0; f--)
            {
                Watch();
                yield return null;
            }

            var sent = Count("LevelsSent") - sent0;
            var kept = Count("LevelsKept") - kept0;
            var info = Count("InfoSent") - info0;
            var held = Count("InfoHeld") - held0;
            var told = (double)sentLevel.Invoke(null, new object[] { water });
            Note($"{frames} frames: levels sent {sent}, kept on the server {kept}; terminal texts sent {info}, held {held}; " +
                 $"the clients' level at most {worst:P2} off; now server {water.FilledRatio:P2}, told {told:P2}");
            Check(kept > 0 && sent * 5 < kept, $"the level was sent {sent} times and kept on the server {kept} in {frames} frames: not in steps");
            Check(worst <= 0.0105, $"the level the clients were told was {worst:P2} off the server's (1% at most)");
            Check(info <= frames / 100 + 3, $"the plot's terminal text was sent {info} times in {frames} frames (once in 100 at most)");
            Check(held > 0, "no terminal text of the plot was held");
            Check(!double.IsNaN(told) && Math.Abs(told - water.FilledRatio) <= 0.0105, $"the clients' level {told:P2}, the server's {water.FilledRatio:P2}");
            Result($"water {water.FilledRatio:P1} and the plant growing; in {frames} frames {sent} levels sent and {kept} kept on the server, " +
                   $"the clients at most {worst:P2} off; terminal texts {info} sent, {held} held");
        }
    }
}
