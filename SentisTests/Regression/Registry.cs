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

        private static readonly List<Func<RegressionCheck>> Added = new List<Func<RegressionCheck>>();
        private static bool _registered;

        /// <summary>
        /// A check another plugin brings: it goes into the suite after the ones here and is a scenario of its own.
        /// Taken before <see cref="RegisterAll"/> as well as after it.
        /// </summary>
        public static void AddCheck(Func<RegressionCheck> make)
        {
            Added.Add(make);
            if (!_registered) return;
            Add(make);
            RegisterScenario(All[All.Count - 1]);
        }

        private static void RegisterScenario(Entry entry)
        {
            ScenarioRegistry.Register(entry.Name, () => entry.Make());
            ScenarioRegistry.OutOfAll(entry.Name);
        }

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
            Add(() => new SmallConveyorCheck());
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
            Add(() => new JetpackGravityCheck());
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
            Add(() => new DecoyCheck());
            Add(() => new SearchlightCheck());
            Add(() => new TurretControllerCheck());
            Add(() => new FunctionalThresholdCheck());
            Add(() => new HackCheck());
            Add(() => new FallDamageCheck());
            Add(() => new VacuumCheck());
            Add(() => new BreathingRoomCheck());
            Add(() => new PlayerDamageCheck());
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
            Add(() => new CockpitOxygenCheck());
            Add(() => new CameraRaycastCheck());
            Add(() => new AntennaMessageCheck());
            Add(() => new RemoteAutopilotCheck());
            Add(() => new LaserAntennaCheck());
            Add(() => new EventControllerCheck());
            Add(() => new AiFlightCheck());
            Add(() => new StoreCheck());
            Add(() => new AtmCheck());
            Add(() => new ContractCheck());
            Add(() => new ContractHaulingCheck());
            Add(() => new ContractRepairCheck());
            Add(() => new ContractFindCheck());
            Add(() => new CargoShipCheck());
            Add(() => new NpcStationCheck());
            Add(() => new NpcContractCheck());
            Add(() => new EatCheck());
            Add(() => new HungerCheck());
            Add(() => new FoodProcessorCheck());
            Add(() => new FarmPlotCheck());
            Add(() => new RadiationCheck());
            Add(() => new AirVentDepressurizeCheck());
            Add(() => new HullBreachCheck());
            Add(() => new TurretAmmoConveyorCheck());
            Add(() => new GrindContainerCheck());
            Add(() => new UseConveyorOffCheck());
            Add(() => new BatteryModesCheck());
            Add(() => new HangarDoorCheck());
            Add(() => new ArtificialMassCheck());
            Add(() => new SphericalGravityCheck());
            Add(() => new WarheadCountdownCheck());
            Add(() => new DestroyedCargoSpillsCheck());
            Add(() => new RunOverCheck());
            Add(() => new SunRotationCheck());
            Add(() => new OffensiveCombatCheck());
            Add(() => new BasicFollowPlayerCheck());
            Add(() => new PathRecorderCheck());
            Add(() => new BlockLimitsCheck());
            Add(() => new WelderLimitsCheck());
            Add(() => new ThrustEnvironmentCheck(SiteKind.Planet));
            Add(() => new ThrustEnvironmentCheck(SiteKind.Space));
            Add(() => new FlameHurtsPlayerCheck());
            Add(() => new ExplosionHurtsPlayerCheck());
            Add(() => new PlanetBreathCheck());
            Add(() => new JetpackBottleCheck());
            Add(() => new TurretCharacterCheck());
            Add(() => new UnknownSignalCheck());
            Add(() => new AlgaeFarmCheck());
            Add(() => new LcdTextCheck());
            Add(() => new WarheadChainCheck());
            Add(() => new AmmoExplosionCheck());
            Add(() => new PcuAccountingCheck());
            Add(() => new RotorLockCheck());
            Add(() => new ToolHurtsPlayerCheck("ShipGrinder/LargeShipGrinder"));
            Add(() => new ToolHurtsPlayerCheck("Drill/LargeBlockDrill"));
            Add(() => new HydrogenTankBlastCheck());
            Add(() => new SolarShadowCheck());
            Add(() => new SpeedLimitCheck(VRage.Game.MyCubeSize.Large));
            Add(() => new SpeedLimitCheck(VRage.Game.MyCubeSize.Small));
            Add(() => new ConveyorPushCheck());
            Add(() => new HingeLockCheck());
            Add(() => new PlanetaryEncounterCheck());
            Add(() => new BroadcastControllerCheck());
            Add(() => new ContractAbandonCheck());
            Add(() => new ContractExpireCheck());
            Add(() => new ContractDeleteCheck());
            Add(() => new FactionRelationsCheck());
            Add(() => new FactionBankCheck());
            Add(() => new ReputationDamageCheck());
            Add(() => new FaunaCheck("Wolf", true));
            Add(() => new FaunaCheck("SpaceSpider", false));
            Add(() => new TargetDummyCheck());
            Add(() => new AccessRightsCheck());
            Add(() => new HandGrindFullCheck());
            Add(() => new LightningCheck());
            Add(() => new FactionRanksCheck());
            Add(() => new ScrapCheck());
            Add(() => new RadiationDeletedCharacterCheck());
            Add(() => new ClaimNpcOwnershipCheck());
            Add(() => new RejoinCheck());
            Add(() => new BlockTransferCheck());
            Add(() => new TimerGroupCheck());
            Add(() => new RelativeDampenersCheck());
            Add(() => new DrillOresCheck());
            Add(() => new DefensiveCombatCheck());
            Add(() => new DefensiveFleeDamageCheck());
            Add(() => new CleanupExceptionCheck());
            Add(() => new AutoRenameCheck());
            Add(() => new OnlineRewardCheck());
            Add(() => new FloatingLifetimeCheck());
            Add(() => new RefineryAllOresCheck());
            Add(() => new AssemblerAllComponentsCheck());
            Add(() => new AllBlocksBuildCheck(VRage.Game.MyCubeSize.Large));
            Add(() => new AllBlocksBuildCheck(VRage.Game.MyCubeSize.Small));
            Add(() => new WeatherCheck());
            Add(() => new MeteorCheck());
            Add(() => new GridStorageCheck());
            Add(() => new NpcBuildRefusedCheck());
            Add(() => new LandingGearRefusedCheck());
            Add(() => new BeaconWarningCheck());
            Add(() => new CharacterInventoryCheck());
            Add(() => new VendingMachineCheck());
            Add(() => new ResearchCheck());
            Add(() => new TrashRemovalCheck());
            Add(() => new PirateDronesCheck());
            Add(() => new RespawnFactionMedbayCheck());
            Add(() => new EconomyRestockCheck());
            Add(() => new GlobalEncounterCheck());
            Add(() => new AntennaUnicastCheck());
            Add(() => new ProceduralEncounterCheck());
            Add(() => new ChatRoutingCheck());
            Add(() => new TemperatureDamageCheck());
            Add(() => new PowerPriorityCheck());
            Add(() => new GpsCheck());
            Add(() => new ConnectorTradingCheck());
            Add(() => new TransponderSignalCheck());
            Add(() => new AutoHealingCheck());
            Add(() => new RemoteBlockRemovalCheck());
            Add(() => new GridPowerSwitchCheck());
            Add(() => new SensorRelationsCheck());
            Add(() => new ProductionTiersCheck());
            Add(() => new RefillStationCheck());
            Add(() => new StoreBuyShipCheck());
            Add(() => new ProgrammableLimitsCheck());
            Add(() => new PilotMassCheck());
            Add(() => new ParkingCheck());
            Add(() => new ConveyorMechanismsCheck());
            Add(() => new OverlapFreeCheck());
            Add(() => new OverlapRotorCheck());
            Add(() => new SubgridControlCheck());
            Add(() => new ArmorDamageCheck());
            Add(() => new SubgridDamageCheck());
            Add(() => new MagneticBootsCheck());
            Add(() => new VoxelRevertCheck());
            Add(() => new OldIdentitiesCheck());
            Add(() => new AfkKickCheck());
            Add(() => new BlockGroupsCheck());
            Add(() => new CharacterLightCheck());
            Add(() => new SimpleBlocksCheck());
            Add(() => new TradeWindowCheck());
            Add(() => new GridPasteCheck());
            Add(() => new BlockSkinCheck());
            Add(() => new CockpitToolbarCheck());
            Add(() => new ProceduralAsteroidsCheck());
            Add(() => new ContractHuntCheck());
            Add(() => new ContractPvEBountyCheck());
            Add(() => new ContractSalvageCheck());
            Add(() => new ContractGridHaulingCheck());
            Add(() => new SuitAppearanceCheck());
            Add(() => new MainCockpitCheck());
            Add(() => new LandingGearAutoLockCheck());
            Add(() => new ConnectorPowerCheck());
            Add(() => new StationSplitCheck());
            Add(() => new CharacterBroadcastCheck());
            Add(() => new MedicalRefuelCheck());
            Add(() => new RespawnShipReplacedCheck());
            Add(() => new VentPlanetAirCheck());
            Add(() => new CryoOfflineCheck());
            Add(() => new RespawnRefusedCheck());
            Add(() => new RemoteControlCheck());
            Add(() => new ConsumableKitsCheck());
            Add(() => new DampenersOffCheck());
            Add(() => new RotorDisplacementCheck());
            Add(() => new RepairServiceCheck());
            Add(() => new SalvageServiceCheck());
            Add(() => new ProductionQueueCheck());
            Add(() => new SwitchMagazineCheck());
            Add(() => new ResearchShareCheck());
            Add(() => new FactionEditCheck());
            Add(() => new StoreCancelCheck());
            Add(() => new ParachuteAutoDeployCheck());
            Add(() => new MoveToPositionCheck());
            Add(() => new ForageCheck());
            Add(() => new TargetLockCheck());
            Add(() => new PcuTransferCheck());
            Add(() => new StoreGasCheck());
            Add(() => new TerminalRequestsCheck());
            Add(() => new SunTrackingCheck());
            Add(() => new ProjectorLoadCheck());
            Add(() => new RifleReloadCheck());
            Add(() => new RazeAreaCheck());
            Add(() => new BuildColorCheck());
            Add(() => new SafeZoneSettingsCheck());
            Add(() => new DoorUseCheck());
            Add(() => new SoundBlocksCheck());
            Add(() => new TurretManualCheck());
            Add(() => new PlayerToolbarCheck());
            Add(() => new CompetitiveDropCheck());
            Add(() => new VoxelCacheResendCheck());
            Add(() => new CrashDeformationCheck());
            Add(() => new SpaceBallCheck());
            Add(() => new SafeZoneUpkeepCheck());
            Add(() => new ProgrammableEditorCheck());
            Add(() => new AdminEntityActionsCheck());
            Add(() => new AdminSettingsCheck());
            Add(() => new AdminPlayerToolsCheck());
            Add(() => new SharedAssemblerCheck());
            Add(() => new ProgrammableEditorQuitCheck());
            Add(() => new JumpDriveDistanceCheck());
            Add(() => new JumpAcrossClustersCheck());
            Add(() => new RespawnAcrossClustersCheck());
            Add(() => new FlightClusterBorderCheck());
            Add(() => new AsteroidMaterialCheck());
            Add(() => new EntityStoreCheck());

            foreach (var make in Added)
                Add(make);

            // "run all" runs them in the suite, at once, not one by one as well
            foreach (var entry in All)
                RegisterScenario(entry);
            _registered = true;
            ScenarioRegistry.Register(RegressionSuite.ScenarioName, () => new RegressionSuite());
            ScenarioRegistry.Register(RegressionSuite.HeavyName, () => new RegressionSuite(heavy: true));
            // 100 ships into the planet at once: the load is its own, run alone
            ScenarioRegistry.Register(PlanetTunnelCheck.MassName, () => new PlanetTunnelCheck(100));
            // with SentisClusters: scripts against detached clusters; it switches detaching, so not in the suite
            ScenarioRegistry.Register(PbDetachCheck.ScenarioName, () => new PbDetachCheck());
            ScenarioRegistry.Register(JetpackDetachCheck.ScenarioName, () => new JetpackDetachCheck());
            ScenarioRegistry.Register(RoverDetachCheck.ScenarioName, () => new RoverDetachCheck());
            ScenarioRegistry.Register(JumpDetachCheck.ScenarioName, () => new JumpDetachCheck());
            ScenarioRegistry.Register(RaycastDetachCheck.ScenarioName, () => new RaycastDetachCheck());
            ScenarioRegistry.Register(ProjectileDetachCheck.ScenarioName, () => new ProjectileDetachCheck());
            ScenarioRegistry.Register(ClusterStressCheck.ScenarioName, () => new ClusterStressCheck());
            ScenarioRegistry.Register(ClusterFlightCheck.ScenarioName, () => new ClusterFlightCheck());
            ScenarioRegistry.Register(ClusterCrashCheck.ScenarioName, () => new ClusterCrashCheck());
        }
    }
}
