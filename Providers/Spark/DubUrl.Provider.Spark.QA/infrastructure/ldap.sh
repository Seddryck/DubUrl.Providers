#!/usr/bin/env bash
set -euo pipefail
umask 077

mkdir /tmp/ldap
cat > /tmp/ldap/slapd.conf <<'EOF'
include /etc/ldap/schema/core.schema
include /etc/ldap/schema/cosine.schema
include /etc/ldap/schema/inetorgperson.schema
modulepath /usr/lib/ldap
moduleload back_mdb
database mdb
maxsize 10485760
suffix "dc=fixture,dc=test"
directory /tmp/ldap
access to attrs=userPassword by anonymous auth by * none
access to * by * read
EOF

hash="$(slappasswd -T /fixture-secrets/ldap-password)"
cat > /tmp/ldap/fixture.ldif <<EOF
dn: dc=fixture,dc=test
objectClass: domain
dc: fixture

dn: ou=people,dc=fixture,dc=test
objectClass: organizationalUnit
ou: people

dn: uid=alice,ou=people,dc=fixture,dc=test
objectClass: inetOrgPerson
cn: Alice
sn: Fixture
uid: alice
userPassword: $hash
EOF
slapadd -f /tmp/ldap/slapd.conf -l /tmp/ldap/fixture.ldif
exec slapd -f /tmp/ldap/slapd.conf -h ldap://0.0.0.0:1389/ -d 0
