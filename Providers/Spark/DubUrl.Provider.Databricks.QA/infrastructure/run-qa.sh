#!/usr/bin/env bash
set -euo pipefail

for setting in DATABRICKS_HOST DATABRICKS_WAREHOUSE_ID DATABRICKS_TOKEN; do
  if [[ -z "${!setting:-}" ]]; then
    echo "::error::Configure $setting in the databricks-qa environment."
    exit 1
  fi
done

test_options=()
if [[ "${1:-}" == "--oauth" ]]; then
  if [[ -z "${DATABRICKS_OAUTH_TOKEN:-}" ]]; then
    echo "::error::Configure DATABRICKS_OAUTH_TOKEN with a current Databricks user OAuth access token."
    exit 1
  fi
  test_options+=(--filter 'FullyQualifiedName~DatabricksOAuthLiveTests|FullyQualifiedName~DatabricksOdbcTests.OpenFreeEditionWarehouse')
fi

status=0
dotnet test Providers/Spark/DubUrl.Provider.Databricks.QA -c Release -f net10.0 --no-build \
  --logger 'trx;LogFileName=databricks.trx' --results-directory .test-results/databricks "${test_options[@]}" || status=$?
report=.test-results/databricks/databricks.trx
if [[ -f "$report" ]]; then
  perl -pi -e 'for my $name (qw(DATABRICKS_TOKEN DATABRICKS_RESTRICTED_TOKEN DATABRICKS_OAUTH_TOKEN)) { my $token = $ENV{$name}; s/\Q$token\E/[REDACTED]/g if length($token // q{}); }' "$report"
fi
exit "$status"
