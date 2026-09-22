using System;
using System.IO;
using NLog;
using SentisTests.Core;
using SentisTests.Scenarios;
using Torch;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Session;
using Torch.Managers.PatchManager;
using Torch.Session;

namespace SentisTests
{
    /// <summary>
    /// Live integration-test harness: runs scenarios (game-thread coroutines) against the running
    /// server, driving real game systems (spawning, projectors, welders, physics) and asserting
    /// outcomes. Control: /test list|run|stop|status|results  or AutoRun in SentisTests.cfg.
    /// </summary>
    public class SentisTestsPlugin : TorchPluginBase
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static Persistent<MainConfig> _config;
        public static MainConfig Config => _config?.Data;
        public static ITorchBase TorchInstance { get; private set; }

        private TorchSessionManager _sessionManager;
        private DateTime _autoRunEarliest = DateTime.MaxValue;

        public override void Init(ITorchBase torch)
        {
            TorchInstance = torch;
            try
            {
                _config = Persistent<MainConfig>.Load(Path.Combine(StoragePath, "SentisTests.cfg"));
                Scenarios.ConfigOverride.RestoreFile = Path.Combine(StoragePath, "SentisTests.config-restore.txt");
                ResolveReportDirectory();

                ScenarioRegistry.Register(SmokeScenario.ScenarioName, () => new SmokeScenario());
                ScenarioRegistry.Register(ProjectorWeldScenario.ScenarioName, () => new ProjectorWeldScenario());
                ScenarioRegistry.Register(FrozenRadiusWeldScenario.ScenarioName, () => new FrozenRadiusWeldScenario());
                ScenarioRegistry.Register(WelderPerfScenario.ScenarioName, () => new WelderPerfScenario());
                ScenarioRegistry.Register(RefineryPerfScenario.ScenarioName, () => new RefineryPerfScenario());
                ScenarioRegistry.Register(DrillPerfScenario.ScenarioName, () => new DrillPerfScenario());
                ScenarioRegistry.Register(WheelPerfScenario.ScenarioName, () => new WheelPerfScenario());
                ScenarioRegistry.Register(WheelPerfScenario.ScenarioName64, () => new WheelPerfScenario(64));
                ScenarioRegistry.Register(WheelPerfScenario.FallScenarioName, () => new WheelPerfScenario(32, fast: true));
                ScenarioRegistry.Register(WheelPerfScenario.FallLagScenarioName, () => new WheelPerfScenario(32, lag: true));
                ScenarioRegistry.Register(WheelPerfScenario.SmallScenarioName, () => new WheelPerfScenario(32, rover: "SmallSuspension1x1"));
                ScenarioRegistry.Register(WheelPerfScenario.SmallLagScenarioName, () => new WheelPerfScenario(32, lag: true, rover: "SmallSuspension1x1"));
                ScenarioRegistry.Register(WheelPerfScenario.Small3ScenarioName, () => new WheelPerfScenario(32, rover: "SmallSuspension3x3"));
                ScenarioRegistry.Register(WheelPerfScenario.Small3LagScenarioName, () => new WheelPerfScenario(32, lag: true, rover: "SmallSuspension3x3"));
                ScenarioRegistry.Register(WheelPerfScenario.RestoreScenarioName, () => new WheelPerfScenario(16, restore: true));
                ScenarioRegistry.Register(WheelPerfScenario.ParkedScenarioName, () => new WheelPerfScenario(64, parked: true));
                ScenarioRegistry.Register(GearProbeScenario.ScenarioName, () => new GearProbeScenario());
                ScenarioRegistry.Register(FreezerPhysicsScenario.ScenarioName, () => new FreezerPhysicsScenario());
                ScenarioRegistry.Register(FreezerStressScenario.ScenarioName, () => new FreezerStressScenario());
                ScenarioRegistry.Register(FreezerStressScenario.ProfileScenarioName, () => new FreezerStressScenario(profile: true));
                ScenarioRegistry.Register(GrinderPerfScenario.ScenarioName, () => new GrinderPerfScenario());
                ScenarioRegistry.Register(ThrustPerfScenario.IonScenarioName, () => new ThrustPerfScenario(atmospheric: false));
                ScenarioRegistry.Register(ThrustPerfScenario.AtmoScenarioName, () => new ThrustPerfScenario(atmospheric: true));
                ScenarioRegistry.Register(GasPerfScenario.ScenarioName, () => new GasPerfScenario());
                ScenarioRegistry.Register(PbPerfScenario.ScenarioName, () => new PbPerfScenario());
                ScenarioRegistry.Register(VoxelStreamScenario.ScenarioName, () => new VoxelStreamScenario());
                ScenarioRegistry.Register(GridStreamScenario.ScenarioName, () => new GridStreamScenario());
                ScenarioRegistry.Register(CharacterPerfScenario.ScenarioName, () => new CharacterPerfScenario());
                ScenarioRegistry.Register(RemoteControlAnchorScenario.ScenarioName, () => new RemoteControlAnchorScenario());
                ScenarioRegistry.Register(LaserAntennaControlScenario.ScenarioName, () => new LaserAntennaControlScenario());
                ScenarioRegistry.Register(LaserLinkAwakeScenario.ScenarioName, () => new LaserLinkAwakeScenario());
                ScenarioRegistry.Register(PistonPerfScenario.ScenarioName, () => new PistonPerfScenario());
                ScenarioRegistry.Register(PistonExtendScenario.ScenarioName, () => new PistonExtendScenario());
                ScenarioRegistry.Register(PistonStackScenario.ScenarioName, () => new PistonStackScenario());
                ScenarioRegistry.Register(ExplosionScenario.ScenarioName, () => new ExplosionScenario());
                ScenarioRegistry.Register(ExplosionFriendlyFireScenario.ScenarioName, () => new ExplosionFriendlyFireScenario());
                ScenarioRegistry.Register(WarheadChainScenario.ScenarioName, () => new WarheadChainScenario());
                ScenarioRegistry.Register(CockpitEjectScenario.ScenarioName, () => new CockpitEjectScenario());
                ScenarioRegistry.Register(LootJumpDriveScenario.ScenarioName, () => new LootJumpDriveScenario());
                ScenarioRegistry.Register(GravityDriveScenario.ScenarioName, () => new GravityDriveScenario());
                ScenarioRegistry.Register(GarageRoundtripScenario.ScenarioName, () => new GarageRoundtripScenario());
                ScenarioRegistry.Register(WarheadMassScenario.ChainScenarioName, () => new WarheadMassScenario(all: false));
                ScenarioRegistry.Register(WarheadMassScenario.AllScenarioName, () => new WarheadMassScenario(all: true));
                ScenarioRegistry.Register(FreezeProductionScenario.ScenarioName, () => new FreezeProductionScenario());
                ScenarioRegistry.Register(FreezePowerScenario.ScenarioName, () => new FreezePowerScenario());
                ScenarioRegistry.Register(ProjectionStreamScenario.ScenarioName, () => new ProjectionStreamScenario());
                ScenarioRegistry.Register(WheelPerfScenario.RestoreCostScenarioName, () => new WheelPerfScenario(100, restore: true, sink: false));
                ScenarioRegistry.Register(WheelPerfScenario.PhysicsAbScenarioName, () => new WheelPerfScenario(64, ab: true));
                ScenarioRegistry.Register(WheelPerfScenario.RestoreSmallScenarioName, () => new WheelPerfScenario(16, rover: "SmallSuspension3x3", restore: true));
                ScenarioRegistry.Register(SavePerfScenario.ScenarioName, () => new SavePerfScenario());
                ScenarioRegistry.Register(FrozenSavePerfScenario.ScenarioName, () => new FrozenSavePerfScenario());
                ScenarioRegistry.Register(ReplicationPerfScenario.ScenarioName, () => new ReplicationPerfScenario());
                ScenarioRegistry.Register(ReplicationPerfScenario.AllocScenarioName, () => new ReplicationPerfScenario(allocProbe: true));
                ScenarioRegistry.Register(PatchAuditScenario.ScenarioName, () => new PatchAuditScenario());
                ScenarioRegistry.Register(MixedWeldScenario.ScenarioName, () => new MixedWeldScenario());
                ScenarioRegistry.Register(HandWeldScenario.ScenarioName, () => new HandWeldScenario());
                ScenarioRegistry.Register(ProductionScenario.ScenarioName, () => new ProductionScenario());
                ScenarioRegistry.Register(ProductionFreezerStressScenario.ScenarioName,
                    () => new ProductionFreezerStressScenario());

                try
                {
                    FrameProbe.Install(torch.Managers.GetManager<PatchManager>());
                    AllocProbe.Init(torch.Managers.GetManager<PatchManager>());
                }
                catch (Exception e)
                {
                    Log.Error(e, "FrameProbe install failed; sim-work metrics disabled");
                }

                try
                {
                    Game.FakeClients.Install(torch.Managers.GetManager<PatchManager>());
                }
                catch (Exception e)
                {
                    Log.Error(e, "FakeClients install failed; replication scenarios disabled");
                }

                _sessionManager = torch.Managers.GetManager<TorchSessionManager>();
                if (_sessionManager != null)
                    _sessionManager.SessionStateChanged += OnSessionStateChanged;

                if (Config != null && Config.EnableDebugBridge)
                    Debug.DebugBridge.Start(Config.DebugBridgeToken);
                Log.Info("SentisTests ready; scenarios: {0}", string.Join(", ", ScenarioRegistry.Names));
            }
            catch (Exception e)
            {
                Log.Error(e, "SentisTests Init failed");
            }
        }

        private void ResolveReportDirectory()
        {
            var dir = Config?.ReportDirectory;
            if (string.IsNullOrEmpty(dir))
                dir = "SentisTests";
            TestRunner.ReportDirectory = Path.IsPathRooted(dir)
                ? dir
                : Path.Combine(StoragePath, dir);
        }

        private void OnSessionStateChanged(ITorchSession session, TorchSessionState state)
        {
            try
            {
                if (state == TorchSessionState.Unloading)
                {
                    TestRunner.StopActive("world unloading");
                    Game.FakeClients.RemoveAll();
                    Game.GridFlight.Clear();
                    _autoRunEarliest = DateTime.MaxValue;
                }
                else if (state == TorchSessionState.Loaded)
                {
                    try { Scenarios.ConfigOverride.RestoreLeftovers(); }
                    catch (Exception e) { Log.Warn(e, "putting back config left by an interrupted test failed"); }
                }

                if (state == TorchSessionState.Loaded && Config != null && Config.AutoRun)
                {
                    _autoRunEarliest = DateTime.UtcNow.AddSeconds(Math.Max(5, Config.AutoRunDelaySeconds));
                    Log.Info("SentisTests AutoRun scheduled in {0}s: {1}", Config.AutoRunDelaySeconds,
                        Config.AutoScenarios == null || Config.AutoScenarios.Count == 0
                            ? "all"
                            : string.Join(", ", Config.AutoScenarios));
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "session state handling failed");
            }
        }

        /// <summary>Called by Torch once per game-loop tick (game thread on a dedicated server).</summary>
        public override void Update()
        {
            TickMetrics.FrameBegin();
            FrameProbe.HarnessBegin();
            try
            {
                FrameProbe.BridgeBegin();
                try { Debug.DebugBridge.Tick(); }
                finally { FrameProbe.BridgeEnd(); }
                MaybeStartAutoRun();
                TestRunner.Tick();
                FrameProbe.FakeClientsBegin();
                try
                {
                    Game.GridFlight.Tick();
                    Game.FakeClients.Tick();
                }
                finally { FrameProbe.FakeClientsEnd(); }
            }
            catch (Exception e)
            {
                Log.Error(e, "SentisTests update failed");
            }
            finally
            {
                FrameProbe.HarnessEnd();
                TickMetrics.FrameEnd();
            }
        }

        private void MaybeStartAutoRun()
        {
            if (_autoRunEarliest == DateTime.MaxValue)
                return;
            if (DateTime.UtcNow < _autoRunEarliest)
                return;

            _autoRunEarliest = DateTime.MaxValue;
            var names = Config?.AutoScenarios;
            if (names == null || names.Count == 0)
                TestRunner.EnqueueAll();
            else
                TestRunner.Enqueue(names);
        }

        public override void Dispose()
        {
            try
            {
                TestRunner.StopActive("plugin unloading");
                Debug.DebugBridge.Stop();
                if (_sessionManager != null)
                    _sessionManager.SessionStateChanged -= OnSessionStateChanged;
                _config?.Save(Path.Combine(StoragePath, "SentisTests.cfg"));
            }
            catch (Exception e)
            {
                Log.Error(e, "dispose failed");
            }

            base.Dispose();
        }
    }
}
