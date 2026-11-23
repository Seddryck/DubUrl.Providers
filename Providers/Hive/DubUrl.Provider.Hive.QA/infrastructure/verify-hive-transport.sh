#!/usr/bin/env bash
set -euo pipefail

mode="$1"
case "$mode" in
    http|https) url='jdbc:hive2://localhost:10001/default;transportMode=http;httpPath=cliservice' ;;
    tcps) url='jdbc:hive2://localhost:10000/default;auth=noSasl' ;;
    *) exit 2 ;;
esac

if [[ "$mode" != http ]]; then
    trust_dir="$(mktemp -d)"
    trap 'rm -r -- "$trust_dir"' EXIT
    password="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
    "$JAVA_HOME/bin/keytool" -importcert -noprompt -alias hive-qa -storetype JKS -keystore "$trust_dir/trust.jks" \
        -storepass "$password" -file /hive-tls/server-cert.pem >/dev/null
    url+=";ssl=true;sslTrustStore=$trust_dir/trust.jks;trustStorePassword=$password"
fi

if ! result="$(/opt/hive/bin/beeline -u "$url" -n hive --force=false --silent=true \
        --showHeader=false --outputformat=tsv2 -e 'SELECT 1' 2>&1)" || ! grep -qx 1 <<< "$result"; then
    sed -E 's/(trustStorePassword=)[^;[:space:]]+/\1[redacted]/g' <<< "$result" >&2
    exit 1
fi
printf 'HiveServer2 %s Beeline control returned 1.\n' "$mode"
