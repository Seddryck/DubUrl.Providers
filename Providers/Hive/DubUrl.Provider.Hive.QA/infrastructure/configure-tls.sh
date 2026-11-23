#!/usr/bin/env bash
set -euo pipefail

tls_dir="$1"
mkdir -p "$tls_dir/server"
chmod 755 "$tls_dir/server"
umask 077
export HIVE_QA_KEYSTORE_PASSWORD="$(openssl rand -hex 16)"
openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 2 \
    -subj '/CN=localhost' -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' \
    -keyout "$tls_dir/server-key.pem" -out "$tls_dir/server/server-cert.pem" 2>/dev/null
openssl pkcs12 -export -name hive-server2 -inkey "$tls_dir/server-key.pem" \
    -in "$tls_dir/server/server-cert.pem" -out "$tls_dir/server/keystore.p12" \
    -passout env:HIVE_QA_KEYSTORE_PASSWORD

cat > "$tls_dir/server/hive-site.xml" <<EOF
<configuration>
  <property><name>hive.server2.use.SSL</name><value>true</value></property>
  <property><name>hive.server2.keystore.path</name><value>/hive-tls/keystore.p12</value></property>
  <property><name>hive.server2.keystore.password</name><value>$HIVE_QA_KEYSTORE_PASSWORD</value></property>
</configuration>
EOF

# The bind mount exposes only this directory to the disposable Hive user.
chmod 644 "$tls_dir/server/"*
rm -- "$tls_dir/server-key.pem"
