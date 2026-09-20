using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using SentisTests.Core;
using SentisTests.Game;
using SpaceEngineers.Game.Entities.Blocks;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace SentisTests.Scenarios
{
    /// <summary>
    /// What a frozen grid produces, measured against an identical one that keeps running.
    ///
    /// The freezer compensates assemblers and refineries: they keep the frames they missed and work
    /// them off after the thaw. Nothing else is compensated, and the three blocks that quietly make
    /// things over time are not production blocks at all - an O2/H2 generator turning ice into gas,
    /// an oxygen farm turning sunlight into oxygen, and a farm plot growing a plant. This measures
    /// what each of them does while frozen.
    ///
    /// Two identical sites, <see cref="ApartM"/> apart: a player stands at the near one, nobody is
    /// anywhere near the far one. Each site has a gas grid (generator, ice, hydrogen tank) and a farm
    /// grid (oxygen farm, oxygen tank, farm plot with a planted seed and a full water tank), so the
    /// generator's own oxygen cannot be mistaken for the farm's.
    /// </summary>
    public sealed class FreezeProductionScenario : TestScenario
    {
        public const string ScenarioName = "freeze_production";
        private const string Prefix = "frzprod-";
        private const double AltitudeM = 8000;
        // Close enough that both sites see the same weather and the same sun - a plot 30 km away
        // stood in a colder biome and froze to death - and far enough with FreezeDistanceM below.
        private const double ApartM = 1000;
        private const double SiteSpacingM = 120;
        private const int FreezeDistanceM = 300;
        private const double SettleSeconds = 15;
        private const double PlantSettleSeconds = 30;
        private const double FreezeWaitSeconds = 180;
        private const double RunSeconds = 180;
        private const double ThawSeconds = 30;
        /// <summary>How far the two sites may end up apart before the catch-up is called wrong.</summary>
        private const double GrowthTolerancePercent = 3;
        private const double LogEverySeconds = 30;
        private const int IcePerContainer = 100000;
        private const string Seed = "MyObjectBuilder_SeedItem/Mushrooms";

        private static readonly FakeClients.NetworkProfile Network = new FakeClients.NetworkProfile { RttMs = 50 };

        private readonly ConfigOverride _config = new ConfigOverride();
        private Site _near;
        private Site _far;

        public override string Name => ScenarioName;
        public override int TimeoutSeconds => (int)(RunSeconds + 420);

        public override IEnumerator Run()
        {
            WorldApi.EnsureUnpaused(Name);
            FakeClients.RemoveAll();
            yield return WaitForTicks(30);

            _config.Set("FreezerEnabled", true);
            // Nothing freezes while the plots are being planted and watered: a plot that dries out
            // in that window dies, and a dead plant measures the setup rather than the freezer.
            _config.Set("DelayBeforeFreezeSec", 600);
            _config.Set("FreezeDistanceStatic", FreezeDistanceM);
            _config.Set("FreezeDistanceDynamic", FreezeDistanceM);

            var anchorM = WorldApi.LoadAuthoredGroup(WheelPerfScenario.ResourceName, WorldApi.EntityPrefix + Prefix + "anchor")[0]
                .PositionAndOrientation.Value.GetMatrix();
            var planet = MyGamePruningStructure.GetClosestPlanet(anchorM.Translation);
            Check(planet != null, "no planet");
            var up = Vector3D.Normalize(anchorM.Translation - planet.PositionComp.GetPosition());
            var side = Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(up));

            // Same orientation at both sites: an oxygen farm's output depends on how it faces the sun.
            var nearAt = anchorM.Translation + up * AltitudeM;
            _near = BuildSite("near", nearAt, up, side);
            _far = BuildSite("far", nearAt + side * ApartM, up, side);

            var settle = WaitForSeconds(SettleSeconds, "the sites settle");
            while (settle.MoveNext()) yield return settle.Current;

            foreach (var site in new[] { _near, _far })
            {
                Check(site.Generator != null, site.Label + ": no gas generator");
                Check(site.HydrogenTank != null, site.Label + ": no hydrogen tank");
                Check(site.Farm != null, site.Label + ": no oxygen farm");
                Check(site.OxygenTank != null, site.Label + ": no oxygen tank");
                Check(site.Plot != null, site.Label + ": no farm plot");
                site.Plant(this);
            }

            // Everything is compared from here: the far site freezes somewhere in the next minute,
            // and what it is owed starts at that moment, not when the measuring window opens.
            var planted = new[] { _near.Read(), _far.Read() };
            FakeClients.Add(1, Network, p => (nearAt + up * 30, 0, 0), withCharacters: true);
            var growing = Tend(PlantSettleSeconds, "the plants take root");
            while (growing.MoveNext()) yield return growing.Current;

            Note("near " + _near.Describe() + " | far " + _far.Describe());
            _config.Set("DelayBeforeFreezeSec", 5);
            var freezing = TendUntil(() => _far.Frozen, FreezeWaitSeconds, "the far site freezes");
            while (freezing.MoveNext()) yield return freezing.Current;

            Check(!_near.Frozen, "the near site froze although a player is standing at it");
            Check(_far.Frozen, "the far site did not freeze, so there is nothing to compare against");

            var before = new[] { _near.Read(), _far.Read() };
            var measuring = Tend(RunSeconds, "measuring");
            while (measuring.MoveNext()) yield return measuring.Current;

            var after = new[] { _near.Read(), _far.Read() };
            var nearRan = after[0].Minus(before[0]);
            var farRan = after[1].Minus(before[1]);
            var stillFrozen = _far.Frozen;
            Note("near " + _near.Describe() + " | far " + _far.Describe());

            // ------------------------------------------------------------- and now let it thaw
            // The freezer off thaws everything, which is where the catch-up is handed out.
            _config.Set("FreezerEnabled", false);
            var thawing = Tend(ThawSeconds, "the far site thaws and catches up");
            while (thawing.MoveNext()) yield return thawing.Current;

            var afterThaw = new[] { _near.Read(), _far.Read() };
            var nearTotal = afterThaw[0].Minus(planted[0]);
            var farTotal = afterThaw[1].Minus(planted[1]);

            Note("FREEZE PRODUCTION RESULT | over the frozen window (" + RunSeconds.ToString("F0") + " s) running: " +
                 nearRan + " | frozen: " + farRan +
                 " | over everything since planting, running: " + nearTotal + " | frozen then thawed: " + farTotal +
                 " | far site frozen the whole window: " + stillFrozen);

            Check(nearRan.Hydrogen > 0.001 || nearRan.Ice > 1,
                "the running site produced no hydrogen either, so the bench says nothing: " + nearRan);
            Check(stillFrozen, "the far site thawed early, so the frozen window measured nothing");

            // While frozen the grid does nothing at all - including its plants, which used to keep
            // growing because their growth lives in a component the freezer did not reach.
            Check(farRan.Hydrogen <= 0.0001 && farRan.Ice <= 1,
                "the frozen site produced hydrogen while frozen: " + farRan);
            Check(farRan.Growth < GrowthTolerancePercent,
                "the plant on the frozen site kept growing: " + farRan.Growth.ToString("F2") + "% vs " +
                nearRan.Growth.ToString("F2") + "% on the running one");

            // And on the thaw it is handed what it missed.
            Check(farTotal.Growth >= nearTotal.Growth - GrowthTolerancePercent,
                "the plant was not caught up after the thaw: " + farTotal.Growth.ToString("F2") + "% against " +
                nearTotal.Growth.ToString("F2") + "% on the running site");
            Check(farTotal.Growth <= nearTotal.Growth + GrowthTolerancePercent,
                "the plant was caught up too far: " + farTotal.Growth.ToString("F2") + "% against " +
                nearTotal.Growth.ToString("F2") + "% on the running site");
            Check(farTotal.Ice > 1, "the catch-up burned no ice: " + farTotal);
            Check(farTotal.Hydrogen >= nearTotal.Hydrogen * 0.5,
                "the catch-up produced far less hydrogen than the running site: " +
                (farTotal.Hydrogen * 100).ToString("F2") + "% against " + (nearTotal.Hydrogen * 100).ToString("F2") + "%");
            Check(farTotal.Hydrogen <= nearTotal.Hydrogen * 1.5,
                "the catch-up produced more hydrogen than the running site: " +
                (farTotal.Hydrogen * 100).ToString("F2") + "% against " + (nearTotal.Hydrogen * 100).ToString("F2") + "%");
        }

        /// <summary>
        /// Waits, topping both plots' water up as it goes: the plants drink while they grow, and a
        /// plot that runs dry stops growing and starts dying, which is not what is being measured.
        /// </summary>
        private IEnumerator Tend(double seconds, string what) => TendUntil(null, seconds, what);

        /// <summary>The same, but it stops as soon as <paramref name="until"/> holds.</summary>
        private IEnumerator TendUntil(Func<bool> until, double seconds, string what)
        {
            var started = DateTime.UtcNow;
            double lastFull = -LogEverySeconds;
            double lastShort = -5;
            while ((DateTime.UtcNow - started).TotalSeconds < seconds)
            {
                if (until != null && until()) yield break;
                _near.Water();
                _far.Water();
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                if (elapsed - lastFull >= LogEverySeconds)
                {
                    lastFull = elapsed;
                    Note(what + " " + elapsed.ToString("F0") + "/" + seconds.ToString("F0") + "s | near " +
                         _near.Read() + " | far " + _far.Read());
                }
                else if (elapsed - lastShort >= 5)
                {
                    lastShort = elapsed;
                    Note(what + " (" + elapsed.ToString("F0") + "/" + seconds.ToString("F0") + "s)");
                }

                for (var i = 0; i < 30; i++) yield return null;
            }
        }

        // ------------------------------------------------------------------ the two sites

        private sealed class Reading
        {
            public double Hydrogen;      // tank fill, 0..1
            public double Oxygen;        // tank fill, 0..1
            public double Ice;           // kg left in the gas grid
            public double Growth;        // 0..100 %
            public double Health;        // 0..100 %

            public Reading Minus(Reading start) => new Reading
            {
                Hydrogen = Hydrogen - start.Hydrogen,
                Oxygen = Oxygen - start.Oxygen,
                Ice = start.Ice - Ice,
                Growth = Growth - start.Growth,
                Health = Health - start.Health,
            };

            public override string ToString() =>
                "hydrogen " + (Hydrogen * 100).ToString("F2") + "%, oxygen " + (Oxygen * 100).ToString("F2") +
                "%, ice " + Ice.ToString("F0") + " kg, growth " + Growth.ToString("F2") + "%, health " + Health.ToString("F0") + "%";
        }

        private sealed class Site
        {
            public string Label;
            public MyCubeGrid GasGrid;
            public MyCubeGrid FarmGrid;
            public MyGasGenerator Generator;
            public MyGasTank HydrogenTank;
            public MyOxygenFarm Farm;
            public MyGasTank OxygenTank;
            public MyCubeBlock Plot;

            public bool Frozen =>
                RuntimePluginControls.IsGridFrozen(GasGrid.EntityId) && RuntimePluginControls.IsGridFrozen(FarmGrid.EntityId);

            public void Plant(FreezeProductionScenario scenario)
            {
                Water();
                var logic = FarmLogic(Plot);
                if (logic == null) return;
                var planted = PlantSeed(logic, MyDefinitionId.Parse(Seed));
                if (!planted) scenario.NoteFrom("could not plant a seed in the " + Label + " farm plot");
            }

            /// <summary>Tops the plot's water up: it is the growing that is being measured, not a drought.</summary>
            public void Water()
            {
                var storage = WaterStorage(Plot);
                storage?.ChangeFilledRatio(1.0, true);
            }

            /// <summary>Why a farm or a generator is producing what it is producing.</summary>
            public string Describe() =>
                "generator " + (Generator != null && Generator.IsWorking ? "working" : "idle") +
                ", farm " + (Farm != null && Farm.IsWorking ? "working" : "idle") +
                " sun " + SunFactor().ToString("F2") +
                ", farm reaches the oxygen tank: " + Reaches(Farm, OxygenTank) +
                ", generator reaches the hydrogen tank: " + Reaches(Generator, HydrogenTank) +
                ", plant " + (Planted ? "planted" : "not planted") +
                ", frozen: " + (RuntimePluginControls.IsGridFrozen(GasGrid.EntityId) ? "gas " : "") +
                (RuntimePluginControls.IsGridFrozen(FarmGrid.EntityId) ? "farm" : "");

            public bool Planted
            {
                get
                {
                    var logic = FarmLogic(Plot);
                    return logic is Sandbox.ModAPI.Ingame.IMyFarmPlotLogic plot && plot.IsPlantPlanted;
                }
            }

            /// <summary>0..1 of the farm's output the sun is currently giving it.</summary>
            public double SunFactor()
            {
                var solar = Farm?.GameLogic;
                var max = solar?.GetType().GetProperty("MaxOutput")?.GetValue(solar);
                return max is float f ? f : 0;
            }

            public Reading Read()
            {
                var logic = FarmLogic(Plot);
                return new Reading
                {
                    Hydrogen = HydrogenTank?.FilledRatio ?? 0,
                    Oxygen = OxygenTank?.FilledRatio ?? 0,
                    Ice = IceOn(GasGrid),
                    Growth = logic == null ? 0 : SyncValue<float>(logic, "m_growthProgressSync"),
                    Health = logic == null ? 0 : SyncValue<float>(logic, "m_plantHealthSync"),
                };
            }
        }

        // ------------------------------------------------------------------ the game's own bits

        /// <summary>Gas moves inside a conveyor group: a source not on the tank's network feeds nothing.</summary>
        private static bool Reaches(MyCubeBlock from, MyCubeBlock to) =>
            from is Sandbox.Game.GameSystems.Conveyors.IMyConveyorEndpointBlock a &&
            to is Sandbox.Game.GameSystems.Conveyors.IMyConveyorEndpointBlock b &&
            Sandbox.Game.GameSystems.MyGridConveyorSystem.Reachable(a.ConveyorEndpoint, b.ConveyorEndpoint);

        private static double IceOn(MyCubeGrid grid) =>
            grid.GetFatBlocks().Where(b => b.HasInventory)
                .SelectMany(b => Enumerable.Range(0, b.InventoryCount).Select(i => b.GetInventory(i) as Sandbox.Game.MyInventory))
                .Where(inv => inv != null)
                .Sum(inv => (double)inv.GetItemAmount(new MyDefinitionId(typeof(MyObjectBuilder_Ore), "Ice")));

        /// <summary>The growth lives in a game logic component; nothing public gives the progress away.</summary>
        private static object FarmLogic(MyCubeBlock plot)
        {
            if (plot == null) return null;
            foreach (var component in ((MyEntity)plot).Components)
                if (component != null && component.GetType().Name == "MyFarmPlotLogic")
                    return component;

            var logic = plot.GameLogic;
            return logic != null && logic.GetType().Name == "MyFarmPlotLogic" ? logic : null;
        }

        private static Sandbox.ModAPI.IMyResourceStorageComponent WaterStorage(MyCubeBlock plot)
        {
            if (plot == null) return null;
            ((MyEntity)plot).Components.TryGet<Sandbox.ModAPI.IMyResourceStorageComponent>(out var storage);
            return storage;
        }

        private static bool PlantSeed(object logic, MyDefinitionId seed)
        {
            var plant = logic.GetType().GetMethod("PlantSeed", BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(MyDefinitionId) }, null);
            return plant != null && (bool)plant.Invoke(logic, new object[] { seed });
        }

        private static T SyncValue<T>(object owner, string field)
        {
            var value = owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner);
            var read = value?.GetType().GetProperty("Value")?.GetValue(value);
            return read is T typed ? typed : default(T);
        }

        // ------------------------------------------------------------------ building

        private Site BuildSite(string label, Vector3D at, Vector3D up, Vector3D side)
        {
            var gas = BuildGrid(label + "-gas", at, up, side, GasBlocks);
            // An oxygen farm makes oxygen out of sunlight, so the farm grid is built facing the sun:
            // a panel pointing at the ground would measure the sky rather than the freezer.
            var sun = Vector3D.Normalize(Sandbox.Game.World.MySector.DirectionToSunNormalized);
            var farm = BuildGrid(label + "-farm", at + side * SiteSpacingM, sun,
                Vector3D.Normalize(Vector3D.CalculatePerpendicularVector(sun)), FarmBlocks);
            StockIce(gas);

            var site = new Site
            {
                Label = label,
                GasGrid = gas,
                FarmGrid = farm,
                Generator = WorldApi.FindFunctional<MyGasGenerator>(gas),
                HydrogenTank = WorldApi.FindFunctional<MyGasTank>(gas),
                Farm = WorldApi.FindFunctional<MyOxygenFarm>(farm),
                OxygenTank = WorldApi.FindFunctional<MyGasTank>(farm),
                Plot = farm.GetFatBlocks().FirstOrDefault(b =>
                    string.Equals(b.BlockDefinition.Id.SubtypeName, "LargeBlockFarmPlot", StringComparison.OrdinalIgnoreCase)),
            };
            return site;
        }

        private static void GasBlocks(Action<MyObjectBuilder_CubeBlock, Vector3I> add)
        {
            Plate(add, containers: true);
            add(new Sandbox.Common.ObjectBuilders.MyObjectBuilder_OxygenGenerator { SubtypeName = "" }, new Vector3I(0, 1, 0));
            // The tank's port is in the middle of its bottom face, so it has to stand on the plate.
            add(WorldApi.MakeBlockOb("LargeHydrogenTank"), new Vector3I(3, 1, 0));
        }

        private static void FarmBlocks(Action<MyObjectBuilder_CubeBlock, Vector3I> add)
        {
            Plate(add, containers: false);
            add(WorldApi.MakeBlockOb("LargeBlockOxygenFarm"), new Vector3I(0, 1, 0));
            // The farm is 3x1x1 and its conveyor port is not where a tank would meet it by luck, so
            // conveyors run along its side and up to the tank.
            for (var x = 0; x < 4; x++) add(WorldApi.MakeBlockOb("LargeBlockConveyor"), new Vector3I(x, 1, 1));
            add(new Sandbox.Common.ObjectBuilders.MyObjectBuilder_OxygenTank { SubtypeName = "" }, new Vector3I(4, 1, 1));
            add(WorldApi.MakeBlockOb("LargeBlockFarmPlot"), new Vector3I(0, 1, 2));
        }

        private static void Plate(Action<MyObjectBuilder_CubeBlock, Vector3I> add, bool containers)
        {
            for (var x = 0; x < 6; x++)
            for (var z = 0; z < 3; z++)
            {
                if (containers && x == 1 && z == 1)
                {
                    add(WorldApi.MakeBlockOb("LargeBlockSmallContainer"), new Vector3I(x, 0, z));
                    continue;
                }

                add(WorldApi.MakeBlockOb("LargeBlockConveyor"), new Vector3I(x, 0, z));
            }

            for (var x = 0; x < 3; x++)
                add(WorldApi.MakeBlockOb("LargeBlockBatteryBlock"), new Vector3I(x, -1, 0));
        }

        private MyCubeGrid BuildGrid(string name, Vector3D at, Vector3D up, Vector3D side,
            Action<Action<MyObjectBuilder_CubeBlock, Vector3I>> layout)
        {
            var blocks = new List<MyObjectBuilder_CubeBlock>();
            var owner = WorldApi.PlayerIdentityId();
            layout((block, min) =>
            {
                block.Min = new SerializableVector3I(min.X, min.Y, min.Z);
                block.Owner = owner;
                block.BuiltBy = owner;
                block.ShareMode = MyOwnershipShareModeEnum.Faction;
                blocks.Add(block);
            });

            var ob = new MyObjectBuilder_CubeGrid
            {
                Name = WorldApi.EntityPrefix + Prefix + name,
                DisplayName = WorldApi.EntityPrefix + Prefix + name,
                GridSizeEnum = MyCubeSize.Large,
                IsStatic = true,
                PositionAndOrientation = new MyPositionAndOrientation(MatrixD.CreateWorld(at, side, up)),
                PersistentFlags = VRage.ObjectBuilders.MyPersistentEntityFlags2.InScene,
                CubeBlocks = blocks,
            };
            var grid = WorldApi.SpawnGrid(ob);
            Track(grid);
            WorldApi.ChargeBatteries(grid);
            return grid;
        }

        private static void StockIce(MyCubeGrid grid)
        {
            var ice = new MyObjectBuilder_Ore { SubtypeName = "Ice" };
            foreach (var block in grid.GetFatBlocks())
            {
                if (!(block is MyCargoContainer) && !(block is MyGasGenerator)) continue;
                var inventory = block.GetInventory(0) as Sandbox.Game.MyInventory;
                if (inventory == null) continue;
                var fits = MyFixedPoint.Min((MyFixedPoint)IcePerContainer, inventory.ComputeAmountThatFits(ice.GetId()));
                if (fits > 0) inventory.AddItems(fits, ice);
            }
        }

        internal void NoteFrom(string message) => Note(message);

        public override void Cleanup()
        {
            try
            {
                FakeClients.RemoveAll();
                _config.Restore();
            }
            finally { base.Cleanup(); }
        }
    }
}
