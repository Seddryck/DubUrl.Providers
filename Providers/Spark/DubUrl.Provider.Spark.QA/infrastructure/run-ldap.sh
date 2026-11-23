#!/usr/bin/env bash
set -euo pipefail
umask 077

infrastructure_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$(dirname "$infrastructure_dir")"
results_dir="$(realpath "$1")"
export DUBURL_SPARK_LDAP_STATE="$(mktemp -d)"
state="$DUBURL_SPARK_LDAP_STATE"
compose=(docker compose --project-name "duburl-spark-ldap-$$-$RANDOM" --file "$infrastructure_dir/ldap.compose.yaml")

cleanup() {
    local status=0
    "${compose[@]}" down --volumes --rmi local || status=$?
    rm -rf -- "$state"
    return "$status"
}
trap cleanup EXIT

mkdir "$state/spark"
export DUBURL_SPARK_LDAP_PASSWORD="$(openssl rand -hex 24)"
printf '%s' "$DUBURL_SPARK_LDAP_PASSWORD" > "$state/ldap-password"
openssl rand -hex 24 > "$state/keystore-password"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=DubUrlSparkFixtureCA \
    -addext basicConstraints=critical,CA:TRUE -keyout "$state/ca.key" -out "$state/ca.crt" > /dev/null 2>&1
openssl req -new -newkey rsa:2048 -nodes -subj /CN=127.0.0.1 \
    -keyout "$state/server.key" -out "$state/server.csr" > /dev/null 2>&1
printf 'subjectAltName=IP:127.0.0.1\nextendedKeyUsage=serverAuth\n' > "$state/extensions"
openssl x509 -req -in "$state/server.csr" -CA "$state/ca.crt" -CAkey "$state/ca.key" \
    -CAcreateserial -days 1 -extfile "$state/extensions" -out "$state/server.crt"
openssl pkcs12 -export -in "$state/server.crt" -inkey "$state/server.key" -certfile "$state/ca.crt" \
    -passout "file:$state/keystore-password" -out "$state/spark/server.p12"

cat > "$state/spark/hive-site.xml" <<EOF
<configuration>
  <property><name>hive.server2.thrift.bind.host</name><value>0.0.0.0</value></property>
  <property><name>hive.server2.thrift.port</name><value>10000</value></property>
  <property><name>hive.server2.transport.mode</name><value>binary</value></property>
  <property><name>hive.server2.enable.doAs</name><value>false</value></property>
  <property><name>hive.server2.allow.user.substitution</name><value>false</value></property>
  <property><name>hive.server2.authentication</name><value>LDAP</value></property>
  <property><name>hive.server2.authentication.ldap.url</name><value>ldap://ldap:1389</value></property>
  <property><name>hive.server2.authentication.ldap.userDNPattern</name><value>uid=%s,ou=people,dc=fixture,dc=test</value></property>
  <property><name>hive.server2.use.SSL</name><value>true</value></property>
  <property><name>hive.server2.keystore.path</name><value>/fixture/server.p12</value></property>
  <property><name>hive.server2.keystore.password</name><value>$(cat "$state/keystore-password")</value></property>
</configuration>
EOF
# The Spark image runs as uid 185. The host's private parent directory protects these mounted files.
chmod 755 "$state/spark"
chmod 644 "$state/spark/"*

"${compose[@]}" up --build --detach --wait --wait-timeout 210
address="$("${compose[@]}" port spark 10000)"
export DUBURL_SPARK_LDAP_PORT="${address##*:}"
export DUBURL_SPARK_LDAP_CA="$state/ca.crt"
dotnet test "$project_dir" -c Release -f net10.0 --no-build \
    --filter 'TestCategory=PasswordAuthentication&FullyQualifiedName~SparkOdbcTests' \
    --logger 'trx;LogFileName=spark-ldap.trx' --results-directory "$results_dir"
if ! grep -Eq '<Counters total="13" executed="10" passed="10" failed="0"' "$results_dir/spark-ldap.trx"; then
    echo 'Expected 10 passing Spark password cases and 3 documented skips. See spark-ldap.trx.' >&2
    exit 1
fi
