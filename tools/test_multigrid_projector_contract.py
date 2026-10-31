"""Offline fixture/guard contracts; actual welding is proved by the live scenario."""
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / "SentisTests/Resources/MultigridProjection.xml"
SCENARIO = ROOT / "SentisTests/Scenarios/MultigridProjectorWeldScenario.cs"


def test_fixture_has_two_chained_subgrids_and_a_real_payload():
    assert FIXTURE.exists(), "missing self-contained multigrid projection fixture"
    grids = ET.parse(FIXTURE).getroot().findall(".//ShipBlueprint/CubeGrids/CubeGrid")
    assert len(grids) == 3
    blocks = [g.findall("CubeBlocks/*") for g in grids]
    ids = [b.findtext("EntityId") for group in blocks for b in group if b.findtext("EntityId")]
    assert len(ids) == len(set(ids)), "fixture block entity IDs must be unique"
    assert len([b for b in blocks[0] if b.findtext("SubtypeName") == "LargeProjector"]) == 1
    for index in (0, 1):
        base = [b for b in blocks[index] if b.findtext("SubtypeName") == "LargePistonBase"]
        top = [b for b in blocks[index + 1] if b.findtext("SubtypeName") == "LargePistonTop"]
        assert len(base) == len(top) == 1
        assert base[0].findtext("TopBlockId") == top[0].findtext("EntityId")
        assert top[0].findtext("ParentEntityId") == base[0].findtext("EntityId")
        assert float(base[0].findtext("Velocity")) == 0
    assert sum(b.findtext("SubtypeName") == "LargeBlockArmorBlock" for b in blocks[2]) == 3
    assert not any(b.findtext("SubtypeName") == "LargePistonBase" for b in blocks[2])
    assert [len(group) for group in blocks] == [9, 2, 4]


def test_scenario_requires_plugin_and_welds_without_direct_construction():
    assert SCENARIO.exists(), "missing live multigrid welding scenario"
    source = SCENARIO.read_text(encoding="utf-8")
    assert 'Integrations.MultigridProjector' in source
    assert 'ToolStartShooting(welder)' in source
    assert 'CanBuild(' in source
    assert '.Build(' not in source and 'IncreaseMountLevel(' not in source
    assert 'GetBuiltGrid' in source and 'IsFullIntegrity' in source
    assert 'CountComponent' in source and 'steelUsed >= armorSteel' in source
    assert 'TopGrid' in source and 'CurrentPosition' in source
    assert 'override void CleanupLeftovers()' in source
    plugin = (ROOT / 'SentisTests/SentisTestsPlugin.cs').read_text(encoding='utf-8')
    assert 'ScenarioRegistry.Register(MultigridProjectorWeldScenario.ScenarioName' in plugin
    project = (ROOT / 'SentisTests/SentisTests.csproj').read_text(encoding='utf-8')
    assert 'EmbeddedResource Include="Resources\\MultigridProjection.xml"' in project


def test_materials_are_checked_per_component_and_cleanup_is_scoped():
    source = SCENARIO.read_text(encoding="utf-8")
    assert 'new MyComponentStack(head.BlockDefinition, 0.01f, 0.01f)' in source, 'head scaffold allowance must use real game component rounding'
    assert 'foreach (var component in expectedCosts)' in source, 'every material must be verified'
    assert 'used == component.Value' in source, 'payment must be exact, not merely positive'
    assert 'grid.BlocksCount <= 4' not in source, 'proximity alone does not make a grid test-owned'
    assert 'DiscoverOwnedGrids' in source


def test_survival_tools_have_an_explicit_prepared_owner():
    source = SCENARIO.read_text(encoding="utf-8")
    assert 'WithOwner(seed, owner)' in source, 'projector seed must have explicit ownership'
    assert 'block.Owner = owner;' in source and 'block.BuiltBy = owner;' in source
    assert 'welder.OwnerId == owner' in source, 'ownerless tools silently fail Survival research checks'
    assert 'MySessionComponentResearch.Static.CanUse(owner,' in source


if __name__ == "__main__":
    tests = [value for name, value in sorted(globals().items()) if name.startswith("test_")]
    for test in tests:
        test()
    print(f"OK: {len(tests)} multigrid projector contract tests")
