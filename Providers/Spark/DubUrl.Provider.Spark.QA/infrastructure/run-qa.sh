#!/usr/bin/env bash
set -euo pipefail

infrastructure_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(dirname "$infrastructure_dir")"
results_dir="$(pwd)/.test-results/spark"
mkdir -p "$results_dir"

: "${SPARK_ODBC_DRIVER_VERSION:?Set SPARK_ODBC_DRIVER_VERSION to the pinned Simba Debian package version.}"
driver_settings="$(odbcinst -q -d -n 'Simba Spark ODBC Driver')"
driver_library="$(sed -n 's/^Driver[[:space:]]*=[[:space:]]*//p' <<< "$driver_settings")"
if [[ "$driver_library" != /* || ! -f "$driver_library" ]]; then
    echo 'Simba Spark ODBC Driver must be registered with an absolute path to the installed driver library.' >&2
    exit 1
fi
owner="$(dpkg-query -S "$driver_library")"
package="${owner%%: *}"
version="$(dpkg-query -W -f='${Version}' "$package")"
if [[ "$version" != "$SPARK_ODBC_DRIVER_VERSION" ]]; then
    echo "Expected Simba package version $SPARK_ODBC_DRIVER_VERSION, found $version." >&2
    exit 1
fi
printf '%s %s\n' "$package" "$version" | tee "$results_dir/driver.txt"

compose=(docker compose --project-name "duburl-spark-odbc-qa-$$-$RANDOM" --file "$infrastructure_dir/compose.yaml")
trap '"${compose[@]}" down --volumes' EXIT
if ! "${compose[@]}" up --detach --wait --wait-timeout 180; then
    "${compose[@]}" logs --tail 100
    exit 1
fi

dotnet test "$project_dir" -c Release -f net10.0 \
    --logger 'trx;LogFileName=spark.trx' --results-directory "$results_dir"
