#!/usr/bin/env bash
set -euo pipefail
umask 077

fixture_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${DUBURL_CORE_SOURCE:?Set DUBURL_CORE_SOURCE to a DubUrl v0.70.0 checkout for the shared live contract.}"
core_source="$(realpath "$DUBURL_CORE_SOURCE")"
test_project="$fixture_dir/../../../Providers/Spark/DubUrl.Provider.Spark.QA/DubUrl.Provider.Spark.QA.csproj"
mkdir -p "$1"
results_dir="$(realpath "$1")"
state="$(mktemp -d)"
compose=(docker compose --project-name "duburl-spark-kerberos-$$-$RANDOM" --file "$fixture_dir/spark.compose.yaml")

cleanup() {
    local status=0
    "${compose[@]}" down --volumes --rmi local || status=$?
    rm -rf -- "$state"
    return "$status"
}
trap cleanup EXIT

dotnet build "$test_project" -c Release -f net10.0 -p:DubUrlCoreSource="$core_source" --nologo
if ! "${compose[@]}" up --build --detach --wait --wait-timeout 210; then
    "${compose[@]}" logs --tail 80 spark kdc
    exit 1
fi

kdc_address="$("${compose[@]}" port kdc 88)"
spark_address="$("${compose[@]}" port spark 10000)"
export KRB5_CONFIG="$state/krb5.conf"
export KRB5_CLIENT_KTNAME="FILE:$state/no-client.keytab"
sed "s/kdc = kdc.fixture.test/kdc = $kdc_address/" "$fixture_dir/krb5.conf" > "$KRB5_CONFIG"
export DUBURL_SPARK_OS_QA_URL="odbc+spark://$spark_address?KrbHostFQDN=spark.fixture.test&KrbServiceName=spark&KrbRealm=DUBURL.TEST&UseNativeQuery=1&SocketTimeout=10"
export DUBURL_SPARK_OS_QA_ACCOUNT=alice

for scenario in positive missing wrong; do
    export KRB5CCNAME="FILE:$state/$scenario.ccache"
    export DUBURL_KERBEROS_NEGATIVE_SCENARIO="$scenario"
    case "$scenario" in
        positive) principal=alice; test_name=ExistingTicketAuthenticatesConfiguredDatabaseAccount ;;
        missing) principal=; test_name=PluginOnlyAccountRejectsOtherOrMissingTicket ;;
        wrong) principal=bob; test_name=PluginOnlyAccountRejectsOtherOrMissingTicket ;;
    esac
    if [[ -n "$principal" ]]; then
        "${compose[@]}" exec -T kdc cat "/realm/$principal.keytab" > "$state/$principal.keytab"
        kinit -kt "$state/$principal.keytab" "$principal@DUBURL.TEST"
        kvno spark/spark.fixture.test@DUBURL.TEST
    elif klist -s; then
        echo 'The missing-ticket scenario unexpectedly has credentials.' >&2
        exit 1
    fi

    report="spark-kerberos-$scenario.trx"
    dotnet test "$test_project" -c Release -f net10.0 -p:DubUrlCoreSource="$core_source" --no-build \
        --filter "TestCategory=SparkKerberos&FullyQualifiedName~$test_name" \
        --logger "trx;LogFileName=$report" --results-directory "$results_dir"
    if ! grep -Eq '<Counters total="2" executed="2" passed="2" failed="0"' "$results_dir/$report"; then
        echo "Expected both Spark Kerberos opens to pass for $scenario. See $report." >&2
        exit 1
    fi
done

# The wrong-ticket failure must reach the server's principal allowlist.
"${compose[@]}" logs spark > "$state/spark.log"
if ! grep -Fq 'No rules applied to bob@DUBURL.TEST' "$state/spark.log"; then
    echo "Spark did not record a server-side denial of the authenticated principal." >&2
    exit 1
fi
