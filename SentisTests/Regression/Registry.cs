using System;
using System.Collections.Generic;
using System.Linq;
using SentisTests.Core;
using SentisTests.Regression.Checks;

namespace SentisTests.Regression
{
    /// <summary>The regression checks, in the order the suite starts them. Each is a scenario of its own too (<c>reg_…</c>).</summary>
    public static class Registry
    {
        public sealed class Entry
        {
            public string Name;
            public Func<RegressionCheck> Make;
        }

        public static readonly List<Entry> All = new List<Entry>();

        private static void Add(Func<RegressionCheck> make) => All.Add(new Entry { Name = make().Name, Make = make });

        /// <summary>Lists the checks and registers them, and the suite, with the scenarios.</summary>
        public static void RegisterAll()
        {
            All.Clear();
            // building
            Add(() => new PlaceBlockCheck());
            Add(() => new PlaceGridCheck());
            Add(() => new HandWeldCheck());
            Add(() => new WeldWithoutComponentsCheck());
            Add(() => new HandGrindCheck());
            Add(() => new ShipWeldCheck());
            Add(() => new ShipGrindCheck());
            Add(() => new ShipWeldCheck(VRage.Game.MyCubeSize.Small));
            Add(() => new WelderConveyorCheck());
            Add(() => new ShipGrindCheck(VRage.Game.MyCubeSize.Small));
            Add(() => new HandToolTiersCheck());
            Add(() => new ProjectorCheck());
            Add(() => new SubgridProjectionCheck());
            Add(() => new PaintCheck());
            Add(() => new HandDrillCheck());
            Add(() => new ShipDrillCheck());
            Add(() => new ShipDrillCheck(VRage.Game.MyCubeSize.Small));
            // terminal, inventories
            Add(() => new TerminalCheck());
            Add(() => new InventoryTransferCheck());
            Add(() => new CargoCapacityCheck());
            Add(() => new OwnershipCheck());
            Add(() => new FactionMembershipCheck());
            Add(() => new SaveRoundTripCheck());
            Add(() => new ProgrammableBlockCheck());
            Add(() => new ProgrammableSelfRunCheck());
            Add(() => new ButtonPanelCheck());
            Add(() => new ConveyorCheck());
            Add(() => new SorterCheck());
            Add(() => new ConnectorCheck());
            Add(() => new MergeCheck());
            Add(() => new DropPickupCheck());
            Add(() => new GridSplitCheck());
            Add(() => new CollectorCheck());
            // power, production
            Add(() => new ReactorCheck());
            Add(() => new BatteryCheck());
            Add(() => new SolarCheck());
            Add(() => new WindCheck());
            Add(() => new HydrogenCheck());
            Add(() => new HydrogenThrustCheck());
            Add(() => new TankStockpileCheck());
            Add(() => new ThrusterFlameCheck());
            Add(() => new BottleCheck());
            Add(() => new RefineryCheck());
            Add(() => new AssemblerCheck());
            Add(() => new SurvivalKitCheck());
            Add(() => new RefineryCheck("Blast Furnace"));
            Add(() => new UpgradeModulesCheck());
            Add(() => new AssemblerRepeatCheck());
            Add(() => new CooperativeAssemblyCheck());
            Add(() => new SurvivalKitStoneCheck());
            Add(() => new GeneratorPullsIceCheck());
            Add(() => new TankRefillCheck());
            Add(() => new OxygenFarmCheck());
            Add(() => new GasTanksFillCheck());
            // movement
            Add(() => new JetpackCheck());
            Add(() => new LifeSupportCheck());
            Add(() => new WalkCheck());
            Add(() => new LadderCheck());
            Add(() => new ParachuteCheck());
            Add(() => new ShipFlightCheck());
            Add(() => new OverridesCheck());
            Add(() => new HoverCheck());
            Add(() => new RotorCheck());
            Add(() => new PistonCheck());
            Add(() => new HingeCheck());
            Add(() => new LandingGearCheck());
            Add(() => new GravityGeneratorCheck());
            Add(() => new WheelsCheck());
            Add(() => new JumpDriveCheck());
            Add(() => new JumpDriveChargeCheck());
            Add(() => new LandingGearShipCheck());
            Add(() => new ConnectorThrowOutCheck());
            Add(() => new SmallConnectorThrowOutCheck());
            Add(() => new ConvertGridCheck());
            // weapons, damage
            Add(() => new TurretCheck());
            Add(() => new RifleCheck());
            Add(() => new WarheadCheck());
            Add(() => new CollisionCheck());
            Add(() => new CollisionWallCheck());
            Add(() => new CollisionShipsCheck());
            Add(() => new CollisionCubesCheck());
            Add(() => new CollisionCharacterCheck());
            Add(() => new CollisionGroundCheck());
            Add(() => new PlanetTunnelCheck());
            Add(() => new CollisionDockedCheck());
            Add(() => new TurretRelationsCheck());
            Add(() => new TurretControllerCheck());
            Add(() => new FunctionalThresholdCheck());
            Add(() => new HackCheck());
            Add(() => new FallDamageCheck());
            Add(() => new VacuumCheck());
            Add(() => new DeathLootCheck());
            Add(() => new CraterCheck());
            Add(() => new WeaponBlocksCheck());
            Add(() => new HandWeaponsCheck());
            Add(() => new SafeZoneCheck());
            Add(() => new SafeZoneForbidsCheck());
            Add(() => new SafeZoneAllowsCheck());
            Add(() => new SafeZoneAccessCheck());
            // every functional block
            Add(() => new AllBlocksCheck(VRage.Game.MyCubeSize.Large));
            Add(() => new AllBlocksCheck(VRage.Game.MyCubeSize.Small));
            // the rest of a base
            Add(() => new DoorCheck());
            Add(() => new AirVentCheck());
            Add(() => new TimerCheck());
            Add(() => new SensorCheck());
            Add(() => new MedicalRoomCheck());
            Add(() => new MedicalRechargeCheck());
            Add(() => new SurvivalKitHealCheck());
            Add(() => new AirtightDoorCheck());
            Add(() => new RespawnCheck());
            Add(() => new RespawnCheck("SurvivalKit/SurvivalKitLarge"));
            Add(() => new RespawnCheck("RespawnSpace"));
            Add(() => new RespawnCheck("RespawnRover"));
            Add(() => new CryoChamberCheck());
            Add(() => new SeatRechargeCheck());
            Add(() => new CameraRaycastCheck());
            Add(() => new AntennaMessageCheck());
            Add(() => new RemoteAutopilotCheck());
            Add(() => new LaserAntennaCheck());
            Add(() => new EventControllerCheck());
            Add(() => new AiFlightCheck());
            Add(() => new StoreCheck());
            Add(() => new AtmCheck());
            Add(() => new ContractCheck());
            Add(() => new WeatherCheck());
            Add(() => new MeteorCheck());

            foreach (var entry in All)
                ScenarioRegistry.Register(entry.Name, () => entry.Make());
            // "run all" runs them in the suite, at once, not one by one as well
            ScenarioRegistry.OutOfAll(All.Select(e => e.Name).ToArray());
            ScenarioRegistry.Register(RegressionSuite.ScenarioName, () => new RegressionSuite());
            // 100 ships into the planet at once: the load is its own, run alone
            ScenarioRegistry.Register(PlanetTunnelCheck.MassName, () => new PlanetTunnelCheck(100));
        }
    }
}
