#!/usr/bin/env bash
set -euo pipefail

infrastructure_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(dirname "$infrastructure_dir")"
results_dir="$(pwd)/.test-results/hive"
mkdir -p "$results_dir"

: "${HIVE_ODBC_DEB:?Set HIVE_ODBC_DEB to the Cloudera Hive 2.10.1.1001-2 Debian package.}"
hive_package="$(realpath "$HIVE_ODBC_DEB")"
driver_config="$(mktemp -d)"

export HIVE_QA_TLS_DIR="$driver_config/tls"
compose=(docker compose --project-name "duburl-hive-odbc-qa-$$-$RANDOM"
    --file "$infrastructure_dir/hive-compose.yaml")
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
export HIVE_ODBC_HTTP_ENDPOINT="$( "${compose[@]}" port hive-http 10001 )"
export HIVE_ODBC_HTTPS_ENDPOINT="localhost:$( "${compose[@]}" port hive-https 10001 | cut -d: -f2 )"
export HIVE_ODBC_TCPS_ENDPOINT="localhost:$( "${compose[@]}" port hive-tcps 10000 | cut -d: -f2 )"
export HIVE_ODBC_TLS_CERT="$HIVE_QA_TLS_DIR/server/server-cert.pem"
export HIVE_ODBC_HTTP_PASSWORD="$(openssl rand -hex 16)"

source "$infrastructure_dir/configure-driver.sh" "$hive_package" "$driver_config" > "$results_dir/hive-driver.txt"
dotnet vstest "$adapter_dir/DubUrl.Provider.Hive.QA.dll" \
    --TestAdapterPath:"$adapter_dir" --logger:'trx;LogFileName=hive.trx' --ResultsDirectory:"$results_dir"
