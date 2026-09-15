"""Offline contract checks for the live production/freezer stress scenario."""
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SCENARIO = ROOT / "SentisTests" / "Scenarios" / "ProductionFreezerStressScenario.cs"
PLUGIN = ROOT / "SentisTests" / "SentisTestsPlugin.cs"
FIXTURE = ROOT / "SentisTests" / "Resources" / "ProductionStressGrid.xml"


class ProductionFreezerStressContractTests(unittest.TestCase):
    def test_fixture_is_scaled_five_times(self):
        root = ET.parse(FIXTURE).getroot()
        amounts = {}
        for item in root.findall(".//MyObjectBuilder_InventoryItem"):
            content = item.find("PhysicalContent")
            if content is not None:
                amounts[content.findtext("SubtypeName")] = int(item.findtext("Amount"))
        self.assertEqual(5000, amounts.get("Iron"))
        self.assertEqual(1250, amounts.get("Nickel"))
        self.assertEqual(500, amounts.get("Silicon"))
        self.assertEqual(10, amounts.get("Uranium"))

        queue = {}
        for item in root.findall(".//Queue/Item"):
            queue[item.findtext("Id/SubtypeId")] = int(item.findtext("Amount"))
        self.assertEqual({"SteelPlate": 25, "MotorComponent": 25, "ComputerComponent": 25}, queue)

    def test_stress_scenario_has_64_independent_grids_and_exact_results(self):
        source = SCENARIO.read_text(encoding="utf-8")
        self.assertRegex(source, r"GridCount\s*=\s*64\b")
        self.assertIn("RefinerySeenFrozen", source)
        self.assertIn("AssemblerSeenFrozen", source)
        self.assertIn("SeenUnfrozenAfterFreeze", source)
        self.assertIn("ChangedWhileFrozen", source)
        self.assertIn("RefineryPendingSeen", source)
        self.assertIn("AssemblerPendingSeen", source)
        self.assertIn("RefineryTakenFrames", source)
        self.assertIn("AssemblerTakenFrames", source)
        self.assertIn("RefineryAppliedFrames", source)
        self.assertIn("AssemblerAppliedFrames", source)
        self.assertIn("PeekTakenFrames", source)
        self.assertIn("PeekAppliedFrames", source)
        self.assertIn("AssemblerProgress", source)
        self.assertIn("UnexpectedQueueEntries", source)
        self.assertRegex(source, r"steel\s*==\s*WantedPerComponent")
        self.assertRegex(source, r"motors\s*==\s*WantedPerComponent")
        self.assertRegex(source, r"computers\s*==\s*WantedPerComponent")
        self.assertIn("final.QueueEntries == 0", source)

    def test_stress_scenario_is_registered(self):
        source = PLUGIN.read_text(encoding="utf-8")
        self.assertIn("ProductionFreezerStressScenario.ScenarioName", source)
        self.assertIn("new ProductionFreezerStressScenario()", source)


if __name__ == "__main__":
    unittest.main()
