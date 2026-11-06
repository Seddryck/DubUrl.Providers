"""Guard against database jobs passing without the shared baseline."""
from pathlib import Path
import tempfile
import unittest
import json
import xml.etree.ElementTree as ET
from assert_results import validate


class SchemaBaselineResultsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)

    def report(self, names, outcomes=None):
        outcomes = outcomes or ["Passed"] * len(names)
        root = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
        counters = ET.SubElement(ET.SubElement(root, "ResultSummary"), "Counters")
        counters.attrib.update(total=str(len(names)), executed=str(len(names)),
                               passed=str(len(names)), failed="0")
        results = ET.SubElement(root, "Results")
        for name, outcome in zip(names, outcomes):
            ET.SubElement(results, "UnitTestResult", testName=name, outcome=outcome)
        ET.ElementTree(root).write(Path(self.directory.name) / "results.trx")

    def test_accepts_baseline_and_additional_features(self):
        self.report(["Provider.Baseline.DeclaresCreateTableCapability",
                     "Provider.Baseline.RendersAndDeploysSchema", "FeatureTest"])
        validate(self.directory.name, require_schema_baseline=True)

    def test_rejects_features_without_baseline(self):
        self.report(["FeatureTest"])
        with self.assertRaisesRegex(ValueError, "shared schema baseline"):
            validate(self.directory.name, require_schema_baseline=True)

    def test_rejects_missing_deployment_test(self):
        self.report(["DeclaresCreateTableCapability"])
        with self.assertRaisesRegex(ValueError, "RendersAndDeploysSchema"):
            validate(self.directory.name, require_schema_baseline=True)

    def test_rejects_duplicate_baseline(self):
        self.report(["DeclaresCreateTableCapability", "RendersAndDeploysSchema",
                     "RendersAndDeploysSchema"])
        with self.assertRaisesRegex(ValueError, "exactly once"):
            validate(self.directory.name, require_schema_baseline=True)

    def test_rejects_nonpassing_baseline_even_with_inconsistent_counters(self):
        self.report(["DeclaresCreateTableCapability", "RendersAndDeploysSchema"],
                    ["Passed", "NotExecuted"])
        with self.assertRaisesRegex(ValueError, "shared schema baseline"):
            validate(self.directory.name, require_schema_baseline=True)

    def shared_names(self):
        return ["Shared_" + name for name in json.loads((Path(__file__).resolve().parent / "shared-scenarios.json").read_text(encoding="utf-8"))]

    def test_accepts_all_mandatory_shared_scenarios(self):
        self.report(self.shared_names())
        validate(self.directory.name, require_shared_scenarios=True)

    def test_rejects_one_missing_shared_scenario(self):
        self.report(self.shared_names()[:-1])
        with self.assertRaisesRegex(ValueError, "Required shared provider scenario"):
            validate(self.directory.name, require_shared_scenarios=True)

    def test_rejects_duplicate_shared_scenario(self):
        names = self.shared_names()
        self.report(names + names[:1])
        with self.assertRaisesRegex(ValueError, "exactly once"):
            validate(self.directory.name, require_shared_scenarios=True)

    def test_rejects_failed_shared_scenario_even_with_inconsistent_counters(self):
        names = self.shared_names()
        self.report(names, ["Passed"] * (len(names) - 1) + ["Failed"])
        with self.assertRaisesRegex(ValueError, "Required shared provider scenario"):
            validate(self.directory.name, require_shared_scenarios=True)


if __name__ == "__main__":
    unittest.main()
