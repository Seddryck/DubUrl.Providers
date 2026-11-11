#!/usr/bin/env bash
set -euo pipefail

infrastructure_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(dirname "$infrastructure_dir")"
results_dir="$(pwd)/.test-results/hive"
mkdir -p "$results_dir"

: "${HIVE_ODBC_RPM:?Set HIVE_ODBC_RPM to the path to the Cloudera Hive 2.5.0.1001 RPM.}"
rpm="$(realpath "$HIVE_ODBC_RPM")"
driver_config="$(mktemp -d)"

spark_compose="$infrastructure_dir/compose.yaml"
if [[ ! -f "$spark_compose" ]]; then
    spark_compose="$project_dir/../../Spark/DubUrl.Provider.Spark.QA/infrastructure/compose.yaml"
fi
export SPARK_THRIFT_BINDING='127.0.0.1::10000'
compose=(docker compose --project-name "duburl-hive-odbc-qa-$$-$RANDOM"
    --file "$spark_compose" --file "$infrastructure_dir/hive-compose.yaml")
cleanup() {
    "${compose[@]}" down --volumes
    rm -r -- "$driver_config"
}
trap cleanup EXIT

adapter_dir="$project_dir/adapter"
if [[ -f "$project_dir/DubUrl.Provider.Hive.QA.csproj" ]]; then
    dotnet build "$project_dir" -c Release -f net10.0
    adapter_dir="$project_dir/bin/Release/net10.0"
fi

source "$infrastructure_dir/configure-driver.sh" "$rpm" "$driver_config"
printf '%s\n' 'ClouderaHiveODBC 2.5.0.1001-1.el6.x86_64' > "$results_dir/driver.txt"

if ! "${compose[@]}" up --detach --wait --wait-timeout 180; then
    "${compose[@]}" logs --tail 100
    exit 1
fi

"${compose[@]}" exec -T hive-server2 /opt/hive/bin/beeline \
    -u 'jdbc:hive2://127.0.0.1:10000/default;auth=noSasl' -n hive --force=false \
    -e "$(cat "$infrastructure_dir/initialize.sql")"

export HIVE_ODBC_HIVESERVER2_ENDPOINT="$( "${compose[@]}" port hive-server2 10000 )"
export HIVE_ODBC_SPARK_ENDPOINT="$( "${compose[@]}" port spark-thrift 10000 )"
# The pinned legacy driver reads .cloudera.hiveodbc.ini from the test process's home directory.
HOME="$driver_config" dotnet vstest "$adapter_dir/DubUrl.Provider.Hive.QA.dll" \
    --TestAdapterPath:"$adapter_dir" --logger:'trx;LogFileName=hive.trx' --ResultsDirectory:"$results_dir"
