import argparse
import json
from pathlib import Path
import defusedxml.ElementTree as ET

def validate(directory, allow_skipped=False, require_schema_baseline=False, require_shared_scenarios=False):
    reports = list(Path(directory).rglob("*.trx"))
    if len(reports) != 1:
        raise ValueError(f"Expected one TRX report, found {len(reports)}")
    report = ET.parse(reports[0])
    counters = next((node for node in report.iter() if node.tag.split("}")[-1] == "Counters"), None)
    if counters is None:
        raise ValueError("TRX counters missing")
    counts = {key: int(counters.get(key, "0")) for key in ("total", "executed", "passed", "failed")}
    if counts["executed"] == 0 or counts["failed"] or counts["passed"] != counts["executed"]:
        raise ValueError("No tests executed or tests failed")
    if not allow_skipped and counts["executed"] != counts["total"]:
        raise ValueError("Required tests were skipped")
    if require_schema_baseline:
        results = [node for node in report.iter()
                   if node.tag.split("}")[-1] == "UnitTestResult"]
        for name in ("DeclaresCreateTableCapability", "RendersAndDeploysSchema"):
            matches = [node for node in results if node.get("testName", "").split(".")[-1] == name]
            if len(matches) != 1 or matches[0].get("outcome") != "Passed":
                raise ValueError("Required shared schema baseline did not pass exactly once: " + name)
    if require_shared_scenarios:
        names = json.loads((Path(__file__).resolve().parent / "shared-scenarios.json").read_text(encoding="utf-8"))
        results = [node for node in report.iter() if node.tag.split("}")[-1] == "UnitTestResult"]
        for name in names:
            matches = [node for node in results if node.get("testName", "").split(".")[-1] == "Shared_" + name]
            if len(matches) != 1 or matches[0].get("outcome") != "Passed":
                raise ValueError("Required shared provider scenario did not pass exactly once: " + name)
    print(f"Validated {counts['passed']} passing tests")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory")
    parser.add_argument("--allow-skipped", action="store_true")
    parser.add_argument("--require-schema-baseline", action="store_true")
    parser.add_argument("--require-shared-scenarios", action="store_true")
    args = parser.parse_args()
    validate(args.directory, args.allow_skipped, args.require_schema_baseline, args.require_shared_scenarios)
