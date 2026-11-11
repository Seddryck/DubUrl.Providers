#!/usr/bin/env bash
set -euo pipefail

infrastructure_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(dirname "$infrastructure_dir")"
results_dir="$(pwd)/.test-results/hive"
mkdir -p "$results_dir"

: "${HIVE_ODBC_DEB:?Set HIVE_ODBC_DEB to the Cloudera Hive 2.10.1.1001-2 Debian package.}"
: "${SPARK_HIVE_ODBC_RPM:?Set SPARK_HIVE_ODBC_RPM to the Cloudera Hive 2.5.0.1001 RPM.}"
hive_package="$(realpath "$HIVE_ODBC_DEB")"
spark_package="$(realpath "$SPARK_HIVE_ODBC_RPM")"
driver_config="$(mktemp -d)"

spark_compose="$infrastructure_dir/compose.yaml"
if [[ ! -f "$spark_compose" ]]; then
    spark_compose="$project_dir/../../Spark/DubUrl.Provider.Spark.QA/infrastructure/compose.yaml"
fi
export HIVE_QA_TLS_DIR="$driver_config/tls"
export SPARK_THRIFT_BINDING='127.0.0.1::10000'
compose=(docker compose --project-name "duburl-hive-odbc-qa-$$-$RANDOM"
    --file "$spark_compose" --file "$infrastructure_dir/hive-compose.yaml")
cleanup() {
    local status=$?
    "${compose[@]}" down --volumes || status=1
    rm -r -- "$driver_config" || status=1
    exit "$status"
}
trap cleanup EXIT
bash "$infrastructure_dir/configure-tls.sh" "$HIVE_QA_TLS_DIR"

adapter_dir="$project_dir/adapter"
if [[ -f "$project_dir/DubUrl.Provider.Hive.QA.csproj" ]]; then
    dotnet build "$project_dir" -c Release -f net10.0
    adapter_dir="$project_dir/bin/Release/net10.0"
fi

if ! "${compose[@]}" up --detach --wait --wait-timeout 180; then
    "${compose[@]}" logs --tail 100
    exit 1
fi

"${compose[@]}" exec -T hive-server2 /opt/hive/bin/beeline \
    -u 'jdbc:hive2://127.0.0.1:10000/default;auth=noSasl' -n hive --force=false \
    -e "$(cat "$infrastructure_dir/initialize.sql")"

for transport in http https tcps; do
    "${compose[@]}" exec -T "hive-$transport" bash -s -- "$transport" < "$infrastructure_dir/verify-hive-transport.sh"
done

export HIVE_ODBC_HIVESERVER2_ENDPOINT="$( "${compose[@]}" port hive-server2 10000 )"
export HIVE_ODBC_SPARK_ENDPOINT="$( "${compose[@]}" port spark-thrift 10000 )"
export HIVE_ODBC_HTTP_ENDPOINT="$( "${compose[@]}" port hive-http 10001 )"
export HIVE_ODBC_HTTPS_ENDPOINT="localhost:$( "${compose[@]}" port hive-https 10001 | cut -d: -f2 )"
export HIVE_ODBC_TCPS_ENDPOINT="localhost:$( "${compose[@]}" port hive-tcps 10000 | cut -d: -f2 )"
export HIVE_ODBC_TLS_CERT="$HIVE_QA_TLS_DIR/server/server-cert.pem"
export HIVE_ODBC_HTTP_PASSWORD="$(openssl rand -hex 16)"

run_tests() (
    local target="$1" package="$2" filter="$3"
    local config="$driver_config/$target"
    mkdir "$config" || exit 1
    source "$infrastructure_dir/configure-driver.sh" "$package" "$config" "$target" \
        > "$results_dir/$target-driver.txt" || exit 1
    # The legacy driver reads .cloudera.hiveodbc.ini from the test process's home.
    HOME="$config" dotnet vstest "$adapter_dir/DubUrl.Provider.Hive.QA.dll" \
        --TestAdapterPath:"$adapter_dir" --TestCaseFilter:"$filter" \
        --logger:"trx;LogFileName=$target.trx" --ResultsDirectory:"$results_dir"
)

status=0
run_tests hive "$hive_package" 'Name!=OpenSparkThriftServer' || status=1
run_tests spark "$spark_package" 'Name=OpenSparkThriftServer' || status=1
exit "$status"
