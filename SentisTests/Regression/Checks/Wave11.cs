using System;
using System.Collections;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Components;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRageMath;

namespace SentisTests.Regression.Checks
{
    /// <summary>Food and radiation of the character, read from its stats.</summary>
    internal static class Stats
    {
        public static Sandbox.Game.Entities.MyEntityStat Food(Sandbox.Game.Entities.Character.MyCharacter body) => Get(body, MyCharacterStatComponent.FOOD_ID, "food");
        public static Sandbox.Game.Entities.MyEntityStat Radiation(Sandbox.Game.Entities.Character.MyCharacter body) => Get(body, MyCharacterStatComponent.RADIATION_ID, "radiation");

        private static Sandbox.Game.Entities.MyEntityStat Get(Sandbox.Game.Entities.Character.MyCharacter body, VRage.Utils.MyStringHash id, string what)
        {
            if (body.StatComp == null || !body.StatComp.TryGetStat(id, out var stat))
                throw new Core.ScenarioFailedException("the character has no " + what + " stat");
            return stat;
        }
    }

    /// <summary>
    /// A meal eaten: the player hungry (food at 20 %) eats a pack of chili from the suit (the consume request of the
    /// inventory, as from the toolbar): the pack is gone and the food stat climbs; a second one while the first still acts
    /// is refused, as the game says.
    /// </summary>
    public sealed class EatCheck : RegressionCheck
    {
        public override string Name => Prefix + "eat";

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.FoodConsumptionRate > 0, "food is off in this world");
            yield return Arrive(Site.At(0, 0, -3));
            var food = Stats.Food(Body);
            food.Value = food.MaxValue * 0.2f;
            var suit = Inventory(Body);
            Put(suit, "ConsumableItem/MealPack_Chili", 2);
            var meal = (SerializableDefinitionId)Item("ConsumableItem/MealPack_Chili");
            var before = food.Value;
            Request(suit, "InventoryConsumeItem_Implementation", (MyFixedPoint)1, meal, Body.EntityId);
            Check(Amount(suit, "ConsumableItem/MealPack_Chili") == 1, "the suit has " + Amount(suit, "ConsumableItem/MealPack_Chili") + " packs after eating one of 2");
            yield return Until(() => food.Value - before, v => v >= 5, "the food stat up by 5 after the meal", 15);
            Request(suit, "InventoryConsumeItem_Implementation", (MyFixedPoint)1, meal, Body.EntityId);
            Check(Amount(suit, "ConsumableItem/MealPack_Chili") == 1, "a second pack was eaten while the first still acts");
            Result("ate a pack of chili: food " + F(before) + " -> " + F(food.Value) + " of " + F(food.MaxValue) + ", a second one refused while it acts");
        }
    }

    /// <summary>The player starved (food at zero) loses health to hunger, as the food stat's damage level says.</summary>
    public sealed class HungerCheck : RegressionCheck
    {
        public override string Name => Prefix + "hunger";
        protected override bool NeedsDamage => true;

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.FoodConsumptionRate > 0, "food is off in this world");
            yield return Arrive(Site.At(0, 0, -3));
            var food = Stats.Food(Body);
            Body.StatComp.Health.Value = Body.StatComp.Health.MaxValue;
            food.Value = 0;
            var health = Body.StatComp.Health.Value;
            yield return Until(() => health - Body.StatComp.Health.Value, v => v > 0, "the starving player losing health", 10, () => "food " + F(food.Value));
            var lost = health - Body.StatComp.Health.Value;
            food.Value = food.MaxValue;
            Body.StatComp.Health.Value = Body.StatComp.Health.MaxValue;
            Result("starving (food 0), the player lost " + F(lost) + " health to hunger");
        }
    }

    /// <summary>A food processor on a battery cooks a pack of chili from 3 vegetables and a fruit, by its blueprint.</summary>
    public sealed class FoodProcessorCheck : RegressionCheck
    {
        public override string Name => Prefix + "food_processor";

        protected override IEnumerator Check()
        {
            yield return Arrive(Site.At(0, 8, -8));
            var grid = Build("food-processor", Site.Origin, true,
                new Part("Assembler/FoodProcessor", 0, 0, 0),
                new Part("BatteryBlock/LargeBlockBatteryBlock", 0, -1, 0).With(b => Motion.Charged(b)));
            var processor = Block<MyAssembler>(grid);
            Put(processor.InputInventory, "ConsumableItem/Vegetables", 3);
            Put(processor.InputInventory, "ConsumableItem/Fruit", 1);
            var blueprint = MyDefinitionManager.Static.GetBlueprintDefinition(new MyDefinitionId(typeof(MyObjectBuilder_BlueprintDefinition), "Position0070_MealPack_Chili"));
            Check(blueprint != null, "no blueprint of chili in this game");
            Request(processor, "OnAddQueueItemRequest", -1, (SerializableDefinitionId)blueprint.Id, (MyFixedPoint)1);
            yield return Until(() => Amount(processor.OutputInventory, "ConsumableItem/MealPack_Chili"), v => v >= 1, "a pack of chili cooked", 60, () => State(processor));
            Check(Amount(processor.InputInventory, "ConsumableItem/Vegetables") == 0 && Amount(processor.InputInventory, "ConsumableItem/Fruit") == 0,
                "the cooking left " + Amount(processor.InputInventory, "ConsumableItem/Vegetables") + " vegetables and " + Amount(processor.InputInventory, "ConsumableItem/Fruit") + " fruit of what the blueprint takes");
            Result("the food processor cooked a pack of chili from 3 vegetables and a fruit");
        }
    }

    /// <summary>
    /// A farm: an irrigation system on the conveyor of a farm plot makes water of ice and fills the plot; the player plants
    /// fruit seeds from the suit, the plant grows on the water (the growth hurried to its end: a fruit takes hours), and the
    /// player harvests the fruit into the suit. On a planet: in space the cold kills the plant.
    /// </summary>
    public sealed class FarmPlotCheck : RegressionCheck
    {
        public override string Name => Prefix + "farm_plot";
        public override SiteKind Kind => SiteKind.Planet;

        private static readonly System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;

        protected override IEnumerator Check()
        {
            var at = Site.Origin + Site.Up * 4;
            yield return Arrive(at + Site.Forward * 4);
            var plot = new Part("FunctionalBlock/LargeBlockFarmPlot", 0, 0, 0);
            Part irrigation = null;
            yield return ConveyorNeighbour(plot, "OxygenGenerator/IrrigationSystem", null, p => irrigation = p);
            var used = Cells(irrigation);
            used.Add(Vector3I.Zero);
            var free = Base6Directions.IntDirections.Select(d => (Vector3I)d).First(c => !used.Contains(c));
            var grid = Build("farm", at, true, plot, irrigation,
                new Part("BatteryBlock/LargeBlockBatteryBlock", free.X, free.Y, free.Z).With(b => Motion.Charged(b)));
            var plotBlock = (MyCubeBlock)grid.GetCubeBlock(Vector3I.Zero).FatBlock;
            var farm = plotBlock.Components.Get<SpaceEngineers.Game.EntityComponents.GameLogic.MyFarmPlotLogic>();
            Check(farm != null, "the farm plot has no farm logic");
            var water = (Sandbox.ModAPI.IMyResourceStorageComponent)typeof(SpaceEngineers.Game.EntityComponents.GameLogic.MyFarmPlotLogic).GetField("m_resourceStorageComponent", Any).GetValue(farm);
            var irrigator = (MyCubeBlock)grid.GetCubeBlock(irrigation.Min).FatBlock;
            Put(Inventory(irrigator), "Ore/Ice", 200);
            yield return Until(() => water.FilledRatio, v => v > 0.05, "the farm plot's water filled from the irrigation", 60, () => State(irrigator) + "; " + State(plotBlock));

            var suit = Inventory(Body);
            Put(suit, "SeedItem/Fruit", 3);
            Request(farm, "PlantSeed_Server", (SerializableDefinitionId)Item("SeedItem/Fruit"));
            Check(farm.IsPlantPlanted, "the fruit seeds were not planted");
            Check(Amount(suit, "SeedItem/Fruit") == 3 - farm.AmountOfSeedsRequired, "planting took " + (3 - Amount(suit, "SeedItem/Fruit")) + " seeds, the plot takes " + farm.AmountOfSeedsRequired);
            var progress = typeof(SpaceEngineers.Game.EntityComponents.GameLogic.MyFarmPlotLogic).GetField("m_growthProgressSync", Any).GetValue(farm);
            Func<float> grown = () => (float)progress.GetType().GetProperty("Value").GetValue(progress);
            yield return Until(() => grown(), v => v > 0, "the plant growing", 15, () => "water " + F(water.FilledRatio * 100) + " %, alive " + farm.IsAlive);
            var early = grown();
            // to its end: a fruit grows for hours
            typeof(SpaceEngineers.Game.EntityComponents.GameLogic.MyFarmPlotLogic).GetField("m_plantGrowthTimeRemainingInFrames", Any).SetValue(farm, 1f);
            yield return Until(() => farm.IsPlantFullyGrown ? 1 : 0, v => v > 0, "the plant fully grown", 10, () => "grown " + F(grown()) + " %, alive " + farm.IsAlive);
            var fruit = farm.OutputItemAmount;
            Request(farm, "HarvestIntoPlayersInventory_Server", false);
            Check(Amount(suit, "ConsumableItem/Fruit") == fruit, "the player harvested " + Amount(suit, "ConsumableItem/Fruit") + " fruit, the plant gave " + fruit);
            Check(!farm.IsPlantPlanted, "the plot still has the plant after the harvest");
            Result("irrigation filled the plot with water (" + F(water.FilledRatio * 100) + " %), fruit planted and growing (" + F(early) + " % in a few seconds), harvested " + fruit + " fruit");
        }
    }

    /// <summary>
    /// Radiation of a damaged reactor: a reactor whole does not radiate; hurt below its full integrity it does (the
    /// definition's radiation source only when damaged), and the player standing by it takes radiation in.
    /// </summary>
    public sealed class RadiationCheck : RegressionCheck
    {
        public override string Name => Prefix + "radiation";
        public override int TimeoutSeconds => 150;
        public override SiteKind Kind => SiteKind.Planet;

        /// <summary>
        /// Why the player never gets the blocks' radiation, if the game's turn-taking never pairs them (null if it does or
        /// the game does not take turns that way now).
        /// </summary>
        private string Pairing()
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var hazards = MySession.Static.GetComponent<SpaceEngineers.Game.SessionComponents.MyHazardExposureComponent>();
            var sources = hazards?.GetType().GetField("m_sources", flags)?.GetValue(hazards) as System.Collections.IList;
            if (sources == null || sources.Count == 0) return null;
            var players = new System.Collections.Generic.List<MyPlayer>();
            MySession.Static.Players.GetOnlineHumanPlayers(players);
            if (players.Count <= 100 / sources.Count) return null;
            var me = players.IndexOf(Player);
            var block = -1;
            for (var i = 0; i < sources.Count; i++)
                if (sources[i].GetType().Name == "MyRadiationHazardBlock") block = i;
            if (me < 0 || block < 0) return null;
            int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
            var g = Gcd(players.Count, sources.Count);
            return me % g == block % g ? null
                : "the game never works out the blocks' radiation for this player: " + players.Count + " players online, " + sources.Count
                  + " hazards, the player number " + me + " and the blocks' hazard number " + block + " never meet on one frame (a fault of the game's)";
        }

        protected override IEnumerator Check()
        {
            SkipUnless(MySession.Static.Settings.EnableRadiation, "radiation is off in this world");
            var at = Site.Origin + Site.Up * 4;
            var grid = Build("radiation", at, true,
                new Part("Reactor/LargeBlockLargeGenerator", 0, 0, 0));
            yield return Arrive(grid.PositionComp.WorldAABB.Center + Site.Forward * 8);
            var radiation = Stats.Radiation(Body);
            radiation.Value = 0;
            yield return Seconds(4);
            var whole = radiation.Value;
            Check(whole < 1, "by the whole reactor the player took " + F(whole) + " radiation in 4 s");
            var reactor = grid.GetCubeBlock(Vector3I.Zero);
            reactor.DoDamage(reactor.MaxIntegrity * 0.3f, MyDamageType.Bullet, true, null, 0);
            // the game works out one player and one hazard a frame, in turn: with the suite's players online it comes round
            // to each every ten seconds or more. With over 100 / (hazards) players online it takes the player and the hazard
            // by one frame counter (MyHazardExposureComponent.PlayersAndSourcesStaggered: frame % players, frame % hazards):
            // where the two counts have a common factor, some players never get some hazards - a fault of the game's
            var pairing = Pairing();
            if (pairing != null)
            {
                Note(pairing);
                SkipUnless(false, pairing);
            }
            yield return Until(() => radiation.Value, v => v > whole + 1, "the player taking radiation by the damaged reactor", 90,
                () => "reactor at " + F(reactor.Integrity / reactor.MaxIntegrity * 100) + " %, the player " + F(Vector3D.Distance(Body.PositionComp.GetPosition(), grid.PositionComp.WorldAABB.Center)) + " m off");
            var took = radiation.Value;
            radiation.Value = 0;
            Result("by the whole reactor " + F(whole) + " radiation in 4 s; hurt to " + F(reactor.Integrity / reactor.MaxIntegrity * 100) + " %, it raised the player's radiation to " + F(took));
        }
    }
}
