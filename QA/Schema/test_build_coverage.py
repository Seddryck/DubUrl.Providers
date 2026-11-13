"""Validate coverage semantics without provisioning a database."""
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

from build_coverage import build_coverage, read_json


SCHEMA_QA = Path(__file__).resolve().parent
CONFIG = read_json(SCHEMA_QA / "coverage-cases.json")
INVENTORY = read_json(SCHEMA_QA.parent.parent / "connection-urls/catalog-source.json")
SCENARIOS = read_json(SCHEMA_QA / "shared-scenarios.json")


class CoverageTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.artifacts = Path(directory.name)

    def trx(self, target, cases):
        root = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
        definitions = ET.SubElement(root, "TestDefinitions")
        results = ET.SubElement(root, "Results")
        for index, (fixture, name, outcome, output) in enumerate(cases):
            test = ET.SubElement(definitions, "UnitTest", id=str(index))
            ET.SubElement(test, "TestMethod", className="Test.Namespace." + fixture)
            case = ET.SubElement(results, "UnitTestResult", testId=str(index), testName=name, outcome=outcome)
            if output is not None:
                ET.SubElement(ET.SubElement(case, "Output"), "StdOut").text = output
        directory = self.artifacts / ("schema-qa-" + target)
        directory.mkdir()
        ET.ElementTree(root).write(directory / "test.trx", encoding="utf-8")

    def build(self):
        return build_coverage(CONFIG, INVENTORY, SCENARIOS, self.artifacts, "revision",
                              "https://github.com/example/repo/actions/runs/100", "0.61.0", "0.105.0")

    def targets(self):
        return {target["id"]: target for row in self.build()["providers"] for target in row["targets"]}

    def test_inventory_and_both_operations_are_present_without_test_reports(self):
        report = self.build()
        self.assertEqual(report["schemaVersion"], 1)
        self.assertEqual(report["providerCommit"], "revision")
        self.assertEqual([row["id"] for row in report["providers"]],
                         [row["id"] for row in INVENTORY["databases"]])
        for row in report["providers"]:
            for target in row["targets"]:
                self.assertIsNone(target["artifact"])
                for feature in target["coverage"].values():
                    self.assertEqual(set(feature), {"inspect", "generate"})
                    self.assertTrue(all(cell["status"] == "unverified" for cell in feature.values()))

    def test_shared_expectations_distinguish_support_rejection_and_limits(self):
        self.trx("DuckDb", [
            ("DuckDbBaselineTests", "Shared_ColumnTypes", "Passed", "Expectation: Supported. Positive."),
            ("DuckDbBaselineTests", "Shared_ColumnFacets", "Passed", "Expectation: NotEnforced. String width is ignored."),
            ("DuckDbBaselineTests", "Shared_GeneratedStored", "Passed", "Expectation: RendererRejects. Virtual only."),
            ("DuckDbBaselineTests", "Shared_GeneratedVirtual", "Passed", "Expectation: Supported. Positive."),
            ("DuckDbBaselineTests", "Shared_DiscoveryTablesColumns", "Passed", None),
        ])
        target = self.targets()["DuckDb"]
        coverage = target["coverage"]
        self.assertEqual(target["artifact"], "schema-qa-DuckDb")
        self.assertEqual(coverage["tables"]["generate"]["status"], "supported")
        self.assertEqual(coverage["tables"]["inspect"]["status"], "unverified")
        self.assertEqual(coverage["column-facets"]["generate"]["status"], "limited")
        self.assertEqual(coverage["column-facets"]["generate"]["evidence"][0]["note"], "String width is ignored.")
        self.assertEqual(coverage["stored-computed"]["generate"]["status"], "unsupported")
        self.assertEqual(coverage["computed"]["generate"]["status"], "limited")

    def test_skipped_cases_and_missing_evidence_are_unverified(self):
        self.trx("Spark", [
            ("SparkSchemaContractTests", "Shared_LiteralDefaults", "Passed", "Expectation: RendererRejects. Bounded text."),
            ("SparkOdbcTests", "EvaluateColumnDefaultsOnInsert", "NotExecuted", None),
        ])
        cells = self.targets()["Spark"]["coverage"]["literal-default"]
        self.assertEqual(cells["generate"]["status"], "unverified")
        self.assertEqual(cells["inspect"]["status"], "unverified")

    def test_failed_parameterized_case_takes_precedence(self):
        self.trx("Oracle", [
            ("StructuredTypeTests", "FixedDenseVectorsRoundTrip(SByte)", "Passed", None),
            ("StructuredTypeTests", "FixedDenseVectorsRoundTrip(Single)", "Failed", None),
        ])
        vector = self.targets()["Oracle"]["coverage"]["vector"]
        for cell in vector.values():
            self.assertEqual(cell["status"], "failed")
            self.assertEqual(cell["evidence"][0]["outcomes"], ["Failed", "Passed"])

    def test_sql_server_variants_keep_different_meanings_for_the_same_passing_test(self):
        test = [("StructuredTypeTests", "NativeJsonIsQualifiedByVersion", "Passed", None)]
        self.trx("SqlServer", test)
        self.trx("SqlServer-2025", test)
        targets = self.targets()
        self.assertEqual(targets["SqlServer"]["coverage"]["native-json"]["generate"]["status"], "unsupported")
        self.assertEqual(targets["SqlServer"]["coverage"]["native-json"]["inspect"]["status"], "unverified")
        self.assertEqual(targets["SqlServer-2025"]["coverage"]["native-json"]["generate"]["status"], "supported")
        self.assertEqual(targets["SqlServer-2025"]["coverage"]["native-json"]["inspect"]["status"], "supported")

    def test_sql_server_array_rejection_verifies_only_ddl_generation(self):
        test = [("StructuredTypeTests", "NativeArraysAreRejectedBeforeDdl(16)", "Passed", None),
                ("StructuredTypeTests", "NativeArraysAreRejectedBeforeDdl(17)", "Passed", None)]
        self.trx("SqlServer", test)
        self.trx("SqlServer-2025", test)
        targets = self.targets()
        for target in ("SqlServer", "SqlServer-2025"):
            with self.subTest(target=target):
                coverage = targets[target]["coverage"]["native-array"]
                self.assertEqual(coverage["inspect"]["status"], "unverified")
                self.assertEqual(coverage["generate"]["status"], "unsupported")
                self.assertIn("rejects native array columns", coverage["generate"]["evidence"][0]["note"])

    def test_catalog_evidence_verifies_inspection_for_the_tested_variants(self):
        sql_server = [("StructuredTypeTests", "CatalogDistinguishesJsonArraysFromNativeArrayColumns", "Passed", None)]
        self.trx("SqlServer", sql_server)
        self.trx("SqlServer-2025", sql_server)
        self.trx("PostgreSql", [
            ("GeneratedColumnDiscoveryTests", "DiscoveryPreservesGeneratedColumnStorage", "Passed", None),
        ])
        self.trx("PostgreSql-18", [
            ("GeneratedColumnDiscoveryTests", "DiscoveryPreservesGeneratedColumnStorage", "Passed", None),
            ("PostgreSqlSchemaTests", "Shared_GeneratedVirtual", "Passed", "Expectation: RendererRejects. Stored only."),
        ])
        targets = self.targets()
        for target in ("SqlServer", "SqlServer-2025"):
            array = targets[target]["coverage"]["native-array"]
            self.assertEqual(array["inspect"]["status"], "unsupported")
            self.assertEqual(array["generate"]["status"], "unverified")
        postgres = targets["PostgreSql"]["coverage"]
        self.assertEqual(postgres["virtual-computed"]["inspect"]["status"], "unsupported")
        self.assertIn("PostgreSQL 17", postgres["virtual-computed"]["inspect"]["evidence"][0]["note"])
        self.assertEqual(postgres["virtual-computed"]["generate"]["status"], "unverified")
        self.assertEqual(postgres["stored-computed"]["inspect"]["status"], "supported")
        postgres18 = targets["PostgreSql-18"]["coverage"]
        self.assertEqual(postgres18["virtual-computed"]["inspect"]["status"], "supported")
        self.assertEqual(postgres18["virtual-computed"]["generate"]["status"], "unsupported")
        self.assertEqual(postgres18["stored-computed"]["inspect"]["status"], "supported")

    def test_unit_rejections_use_separate_evidence_for_each_operation(self):
        self.trx("rejections", [
            ("SchemaCoverageRejectionTests", "RendererRejects_SqlServer_native-map", "Passed", None),
            ("SchemaCoverageRejectionTests", "RendererRejects_SqlServer-2025_native-map", "NotExecuted", None),
            ("SchemaCoverageRejectionTests", "InspectionRejects_Spark_foreign-keys", "Passed", None),
            ("SchemaCoverageRejectionTests", "InspectionRejects_Spark_unique", "Failed", None),
        ])
        targets = self.targets()
        sql_server = targets["SqlServer"]["coverage"]["native-map"]
        self.assertEqual(sql_server["inspect"]["status"], "unverified")
        self.assertEqual(sql_server["generate"]["status"], "unsupported")
        self.assertEqual(sql_server["generate"]["evidence"][0]["artifact"], "schema-qa-rejections")
        self.assertIn("SQL Server renderer rejects", sql_server["generate"]["evidence"][0]["note"])
        self.assertEqual(targets["SqlServer-2025"]["coverage"]["native-map"]["generate"]["status"], "unverified")
        spark = targets["Spark"]["coverage"]
        self.assertEqual(spark["foreign-keys"]["inspect"]["status"], "unsupported")
        self.assertEqual(spark["foreign-keys"]["generate"]["status"], "unverified")
        self.assertEqual(spark["unique"]["inspect"]["status"], "failed")

    def test_fixture_ddl_rejection_is_not_evidence_of_inspection(self):
        self.trx("PostgreSql", [
            ("PostgreSqlSchemaTests", "Shared_GeneratedVirtual", "Passed", "Expectation: RendererRejects. Stored only."),
        ])
        self.trx("MySql", [
            ("MySqlBaselineTests", "Shared_DiscoveryIdentity", "Passed", "Expectation: RendererRejects. Custom seed rejected."),
        ])
        targets = self.targets()
        virtual = targets["PostgreSql"]["coverage"]["virtual-computed"]
        self.assertEqual(virtual["generate"]["status"], "unsupported")
        self.assertEqual(virtual["inspect"]["status"], "unverified")
        self.assertEqual(targets["MySql"]["coverage"]["identity"]["inspect"]["status"], "unverified")

    def test_hive_schema_cases_populate_distinct_operations(self):
        self.trx("Hive", [
            ("HiveSchemaTests", "InspectionReportsUnavailableDiscovery", "Passed", None),
            ("HiveSchemaTests", "GeneratedScalarTableCanBeCreatedAndQueried", "Passed", None),
            ("HiveOdbcTests", "OpenHiveServer2", "Passed", None),
        ])
        coverage = self.targets()["Hive"]["coverage"]
        self.assertEqual(coverage["tables"]["inspect"]["status"], "unsupported")
        self.assertEqual(coverage["native-array"]["inspect"]["status"], "unsupported")
        self.assertEqual(coverage["table-replacement"]["inspect"]["status"], "unverified")
        self.assertEqual(coverage["tables"]["generate"]["status"], "supported")
        self.assertEqual(coverage["comments"]["generate"]["status"], "unverified")

    def test_databricks_live_structured_evidence_covers_both_operations(self):
        self.trx("Databricks", [("DatabricksOdbcTests", "StructuredRecursiveRoundTrip", "Passed", None)])
        coverage = self.targets()["Databricks"]["coverage"]
        self.assertEqual(coverage["native-array"]["inspect"]["status"], "supported")
        self.assertEqual(coverage["native-array"]["generate"]["status"], "supported")
        self.assertEqual(coverage["native-json"]["generate"]["status"], "unverified")


if __name__ == "__main__":
    unittest.main()
