"""Combine schema QA TRX results into the public coverage matrix."""
import argparse
from collections import defaultdict
import json
from pathlib import Path
import re

import defusedxml.ElementTree as ET


OPERATIONS = ("inspect", "generate")
REJECTIONS_ARTIFACT = "schema-qa-rejections"
EXPECTATIONS = {
    "Supported": "supported",
    "RendererRejects": "unsupported",
    "DatabaseRejects": "unsupported",
    "NoDiscovery": "unsupported",
    "NullDefaultNotPreserved": "unsupported",
    "NoEngineComments": "unsupported",
    "NotEnforced": "limited",
    "Informational": "limited",
    "Inapplicable": "unverified",
}
FAILURES = {"Failed", "Error", "Timeout", "Aborted", "Disconnected", "PassedButRunAborted"}
FIXTURE_DDL_REJECTIONS = {"RendererRejects", "DatabaseRejects"}
EXPECTATION_LINE = re.compile(r"^Expectation: (\w+)\. (.+)$", re.MULTILINE)


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def read_results(directory):
    reports = list(directory.rglob("*.trx"))
    if len(reports) > 1:
        raise ValueError(f"Expected one TRX report in {directory}, found {len(reports)}")
    if not reports:
        return {}

    report = ET.parse(reports[0])
    names = {}
    for test in report.findall(".//{*}UnitTest"):
        method = test.find("{*}TestMethod")
        if method is not None and method.get("className"):
            fixture = method.get("className").rsplit(".", 1)[-1]
            names[test.get("id")] = fixture
    results = defaultdict(list)
    for result in report.findall(".//{*}UnitTestResult"):
        name = result.attrib["testName"].split("(", 1)[0].rsplit(".", 1)[-1]
        results[name].append(result)
        if result.get("testId") in names:
            results[names[result.get("testId")] + "." + name].append(result)
    return results


def observation(rule, results, operation):
    test = rule["test"]
    matches = results.get(test, ())
    expected = rule.get("expected")
    note = rule.get("note")
    if "expected" not in rule:
        if len(matches) > 1:
            raise ValueError(f"Repeated shared scenario {test}")
        if matches:
            stdout = matches[0].findtext("{*}Output/{*}StdOut", default="")
            match = EXPECTATION_LINE.search(stdout)
            if match:
                expectation = match[1]
                if operation == "inspect" and expectation in FIXTURE_DDL_REJECTIONS and matches[0].get("outcome") == "Passed":
                    # The test stopped at fixture setup without reading the catalog.
                    return None
                expected = EXPECTATIONS[expectation]
                if expected != "supported":
                    note = match[2].strip()

    outcomes = sorted({result.attrib["outcome"] for result in matches})
    if FAILURES.intersection(outcomes):
        status = "failed"
    elif outcomes == ["Passed"] and expected is not None:
        status = expected
    else:
        status = "unverified"
    item = {"test": test, "status": status, "outcomes": outcomes}
    if note:
        item["note"] = note
    if "artifact" in rule:
        item["artifact"] = rule["artifact"]
    return item


def summarize(evidence):
    statuses = {item["status"] for item in evidence}
    if "failed" in statuses:
        return "failed"
    if not statuses or "unverified" in statuses:
        return "unverified"
    if len(statuses) > 1:
        return "limited"
    return statuses.pop()


def index_focused(config, providers, scenarios):
    targets = {target["id"]: target for target in config["targets"]}
    features = {feature["id"]: feature for feature in config["features"]}
    if len(targets) != len(config["targets"]) or len(features) != len(config["features"]):
        raise ValueError("Coverage targets and feature IDs must be unique")
    if any(target["provider"] not in providers for target in config["targets"]):
        raise ValueError("A coverage target is missing from the provider inventory")
    if any(scenario not in scenarios for feature in config["features"]
           for operation in OPERATIONS for scenario in feature.get(operation, ())):
        raise ValueError("A mapped shared scenario is missing from shared-scenarios.json")

    focused = defaultdict(list)
    for rule in config["focused"]:
        if rule["operation"] not in (*OPERATIONS, "both") or rule["expected"] not in EXPECTATIONS.values():
            raise ValueError(f"Invalid coverage rule for {rule['test']}")
        if not set(rule["targets"]) <= targets.keys() or not set(rule["features"]) <= features.keys():
            raise ValueError(f"Unknown coverage target or feature for {rule['test']}")
        if rule.get("artifact", REJECTIONS_ARTIFACT) != REJECTIONS_ARTIFACT:
            raise ValueError(f"Unknown coverage artifact for {rule['test']}")
        operations = OPERATIONS if rule["operation"] == "both" else (rule["operation"],)
        for target in rule["targets"]:
            for feature in rule["features"]:
                for operation in operations:
                    focused[target, feature, operation].append(rule)
    rejections = {}
    for section, operation, test_prefix in (
        ("rendererRejections", "generate", "RendererRejects"),
        ("inspectionRejections", "inspect", "InspectionRejects"),
    ):
        for rejection in config[section]:
            if not set(rejection["targets"]) <= targets.keys() or not set(rejection["features"]) <= features.keys():
                raise ValueError(f"Unknown coverage target or feature for {section}")
            for target in rejection["targets"]:
                for feature in rejection["features"]:
                    key = (target, feature, operation)
                    if key in focused or key in rejections:
                        raise ValueError(f"Duplicate coverage rule for {target}/{feature}/{operation}")
                    if operation == "generate" and features[feature].get(operation) and targets[target]["shared"]:
                        raise ValueError(f"Renderer rejection already has a shared generation rule for {target}/{feature}")
                    rejections[key] = {
                        "test": f"SchemaCoverageRejectionTests.{test_prefix}_{target}_{feature}",
                        "expected": "unsupported",
                        "note": rejection["note"],
                        "artifact": REJECTIONS_ARTIFACT,
                    }
    return focused, rejections


def build_coverage(config, inventory, scenarios, artifacts, revision, run_url, core_version, schema_version):
    providers = {row["id"]: row["label"] for row in inventory["databases"]}
    focused, rejections = index_focused(config, providers, scenarios)
    rejection_results = read_results(artifacts / REJECTIONS_ARTIFACT)
    report = {
        "schemaVersion": 1,
        "providerCommit": revision,
        "runUrl": run_url,
        "dubUrlVersion": core_version,
        "dubUrlSchemaVersion": schema_version,
        "features": [{key: row[key] for key in ("id", "label", "group")} for row in config["features"]],
        "providers": [],
    }

    for provider, label in providers.items():
        targets = [target for target in config["targets"] if target["provider"] == provider]
        column = {"id": provider, "label": label, "targets": []}
        report["providers"].append(column)
        for target in targets or [{"id": provider, "variant": None, "shared": False}]:
            artifact = "schema-qa-" + target["id"] if targets else None
            results = read_results(artifacts / artifact) if artifact else {}
            coverage = {}
            for feature in config["features"]:
                feature_id = feature["id"]
                coverage[feature_id] = {}
                for operation in OPERATIONS:
                    key = (target["id"], feature_id, operation)
                    if key in rejections:
                        # A shared inspection case can stop at rejected fixture DDL.
                        evidence = [observation(rejections[key], rejection_results, operation)]
                    else:
                        rules = [{"test": "Shared_" + name} for name in feature.get(operation, ())] if target["shared"] else []
                        rules.extend(focused.get(key, ()))
                        evidence = [
                            item for rule in rules
                            if (item := observation(rule, rejection_results if "artifact" in rule else results, operation))
                            is not None
                        ]
                    coverage[feature_id][operation] = {"status": summarize(evidence), "evidence": evidence}
            column["targets"].append({
                "id": target["id"],
                "variant": target["variant"],
                "artifact": artifact if results else None,
                "coverage": coverage,
            })

    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--run-url", required=True)
    args = parser.parse_args()
    directory = Path(__file__).resolve().parent
    root = directory.parent.parent
    packages = ET.parse(root / "Directory.Build.props")
    report = build_coverage(read_json(directory / "coverage-cases.json"),
                            read_json(root / "connection-urls/catalog-source.json"),
                            read_json(directory / "shared-scenarios.json"), args.artifacts, args.commit,
                            args.run_url, packages.findtext("./PropertyGroup/DubUrlVersion"),
                            packages.findtext("./PropertyGroup/SchemaPackageVersion"))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote coverage for {len(report['providers'])} providers and {len(report['features'])} features")


if __name__ == "__main__":
    main()
