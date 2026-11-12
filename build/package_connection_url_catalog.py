"""Validate and package the generated connection URL catalog."""
import argparse
import hashlib
import json
from pathlib import Path
import re
from zipfile import ZipFile, ZipInfo

from jsonschema import Draft202012Validator, FormatChecker


def validate_references(catalog):
    for database in catalog["databases"]:
        for route in database["routes"]:
            locations = set(route["locations"])
            if not locations <= catalog["locations"].keys():
                raise ValueError(f"Unknown location in profile {route['id']}")
            if not route.get("schemeByLocation", {}).keys() <= locations:
                raise ValueError(f"Unknown endpoint scheme location in profile {route['id']}")
            for method in route["auth"]:
                auth = {"id": method} if isinstance(method, str) else method
                settings = {**catalog["authentication"][auth["id"]], **auth}
                policy = settings.get("tlsOverride", route.get("tls", "driverDefault"))
                if policy not in catalog["tlsPolicies"]:
                    raise ValueError(f"Unknown TLS policy {policy} in profile {route['id']}")
                auth_locations = set(settings.get("locations", ())) | settings.get("queryByLocation", {}).keys()
                if not auth_locations <= locations:
                    raise ValueError(f"Unknown authentication location in profile {route['id']}")


def package(catalog_path, schema_path, output, version, commit, core_version):
    if not re.fullmatch(r"0\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?", version, re.ASCII):
        raise ValueError("Expected a provider package version such as 0.68.0")
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Expected a full Providers source commit")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?", core_version, re.ASCII):
        raise ValueError("Expected a Core package version")

    catalog_bytes, schema_bytes = catalog_path.read_bytes(), schema_path.read_bytes()
    catalog, schema = json.loads(catalog_bytes), json.loads(schema_bytes)
    Draft202012Validator.check_schema(schema)
    Draft202012Validator(schema, format_checker=FormatChecker()).validate(catalog)
    validate_references(catalog)

    manifest = {
        "bundleFormatVersion": 1,
        "catalogVersion": version,
        "schemaVersion": catalog["schemaVersion"],
        "sourceCommit": commit,
        "corePackageVersion": core_version,
    }
    output.mkdir(parents=True, exist_ok=True)
    bundle = output / "connection-url-catalog.zip"
    with ZipFile(bundle, "w") as archive:
        archive.writestr(ZipInfo("catalog.json"), catalog_bytes)
        archive.writestr(ZipInfo("schema.json"), schema_bytes)
        archive.writestr(ZipInfo("manifest.json"), json.dumps(manifest, indent=2) + "\n")
    checksum = hashlib.sha256(bundle.read_bytes()).hexdigest()
    bundle.with_suffix(".zip.sha256").write_bytes(f"{checksum}  {bundle.name}\n".encode("ascii"))
    print(f"Packaged connection URL catalog {version} from {commit} (SHA-256 {checksum})")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--schema", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--core-version", required=True)
    args = parser.parse_args()
    package(args.catalog, args.schema, args.output, args.version, args.commit, args.core_version)
