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
                ResolveReportDirectory();

                ScenarioRegistry.Register(SmokeScenario.ScenarioName, () => new SmokeScenario());
                ScenarioRegistry.Register(ProjectorWeldScenario.ScenarioName, () => new ProjectorWeldScenario());
                ScenarioRegistry.Register(FrozenRadiusWeldScenario.ScenarioName, () => new FrozenRadiusWeldScenario());
                ScenarioRegistry.Register(WelderPerfScenario.ScenarioName, () => new WelderPerfScenario());
                ScenarioRegistry.Register(RefineryPerfScenario.ScenarioName, () => new RefineryPerfScenario());
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
                else if (state == TorchSessionState.Loaded && Config != null && Config.AutoRun)
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
