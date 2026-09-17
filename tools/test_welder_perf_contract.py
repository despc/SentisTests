from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCENARIO = ROOT / "SentisTests" / "Scenarios" / "WelderPerfScenario.cs"
CSPROJ = ROOT / "SentisTests" / "SentisTests.csproj"
PLUGIN = ROOT / "SentisTests" / "SentisTestsPlugin.cs"


def test_perf_resources_exist_and_are_embedded():
    assert (ROOT / "SentisTests" / "Resources" / "PerfProjection.xml").exists()
    assert (ROOT / "SentisTests" / "Resources" / "PerfWelderShip.xml").exists()
    project = CSPROJ.read_text(encoding="utf-8")
    assert 'EmbeddedResource Include="Resources\\PerfProjection.xml"' in project
    assert 'EmbeddedResource Include="Resources\\PerfWelderShip.xml"' in project
    ship = (ROOT / "SentisTests" / "Resources" / "PerfWelderShip.xml").read_text(encoding="utf-8-sig")
    assert '<Volume>10000</Volume>' in ship


def test_perf_scenario_preserves_projector_fixture_and_uses_large_live_radius():
    source = SCENARIO.read_text(encoding="utf-8")
    assert 'LoadAuthoredGrid(ProjectionResource' in source
    assert '.ProjectionOffset =' not in source
    assert '.ProjectionRotation =' not in source
    assert '.ProjectedGrids =' not in source
    assert 'RadiusMultiplier = 100f' in source
    assert 'WelderCount = 3' in source
    assert 'SetWelderRadiusMultiplier(RadiusMultiplier)' in source
    assert 'authoredPose.Position.X + 12000.0' in source


def test_perf_scenario_stocks_container_and_runs_real_welder():
    source = SCENARIO.read_text(encoding="utf-8")
    assert 'RuntimeComponentsNeeded(preview.CubeBlocks, 2)' in source
    assert 'StockComponents(container.GetInventory(), needs)' in source
    assert 'missingAfterStock.Count == 0' in source
    assert 'ToolStartShooting(welder)' in source
    assert 'foreach (var welder in welders)' in source
    assert 'welders.Sum(WorldApi.ProbeProjectedBlocks)' in source
    assert 'projector.ProjectedGrid == null' in source
    assert 'ExpectedRuntimeBlocks = 1000' in source
    assert 'PhysicalFixtureGrids' in source
    assert 'finalFinished == built' in source


def test_perf_scenario_is_registered_and_restores_runtime_settings():
    source = SCENARIO.read_text(encoding="utf-8")
    plugin = PLUGIN.read_text(encoding="utf-8")
    assert 'ScenarioRegistry.Register(WelderPerfScenario.ScenarioName' in plugin
    assert 'SetWelderRadiusMultiplier(_initialWelderMultiplier)' in source
    assert 'SetFreezerEnabled(_initialFreezerEnabled)' in source
    assert 'override void CleanupLeftovers()' in source


if __name__ == "__main__":
    tests = [value for name, value in sorted(globals().items()) if name.startswith("test_")]
    for test in tests:
        test()
    print(f"OK: {len(tests)} welder perf contract tests")
