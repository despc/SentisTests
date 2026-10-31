"""Offline acceptance-harness contracts; not a substitute for a live verdict."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'SentisTests/Scenarios/MultigridProjectorTrustScenario.cs'

class TrustHarnessContracts(unittest.TestCase):
    def test_calls_real_handler_in_nonlocal_context_not_anticheat_prefix(self):
        self.assertTrue(SOURCE.exists(), 'missing live trust acceptance scenario')
        source = SOURCE.read_text(encoding='utf-8')
        for token in ('FakeClients.AsClient(0', 'MyEventContext.Current.IsLocallyInvoked', '_handler.Invoke(_projector', 'context replay; NOT network transport'):
            self.assertIn(token, source)
        self.assertNotIn('ProjectionPrefix', source)

    def test_rejections_check_world_stock_and_counter(self):
        self.assertTrue(SOURCE.exists(), 'missing live trust acceptance scenario')
        source = SOURCE.read_text(encoding='utf-8')
        for token in ('negative-index', 'out-of-range-index', 'nonexistent-cell', 'foreign-owner', 'foreign-builder', 'remote-distance', 'Refused() > before', 'Snapshot() == state', 'GetConstructionStockpileItemAmount', 'PhysicalCensus() == census'):
            self.assertIn(token, source)

    def test_both_real_subgrids_and_actual_target_distance_are_asserted(self):
        self.assertTrue(SOURCE.exists(), 'missing live trust acceptance scenario')
        source = SOURCE.read_text(encoding='utf-8')
        for token in ('index <= 2', 'GetPreviewGrid', 'GetBuiltGrid', 'actual.BlockDefinition.Id == target.BlockDefinition.Id', 'Vector3D.Distance', 'PrepareTestIdentity', 'GetControllingPlayer', 'StockComponents', 'override void CleanupLeftovers()'):
            self.assertIn(token, source)
        plugin = (ROOT / 'SentisTests/SentisTestsPlugin.cs').read_text(encoding='utf-8')
        self.assertIn('ScenarioRegistry.Register(MultigridProjectorTrustScenario.ScenarioName', plugin)

if __name__ == '__main__':
    unittest.main()
