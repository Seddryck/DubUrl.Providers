#!/usr/bin/env bash
# Source with the pinned Debian package and a private extraction directory.
printf '%s  %s\n' 'd861e1a2b81bb7b98f688f0fabe8faa51ef48d5fe504c5fc8ab9328df9c14580' "$1" | sha256sum --check
dpkg-deb -x "$1" "$2"
printf 'Cloudera Hive ODBC %s\n' "$(dpkg-deb -f "$1" Version)"
driver_root="$2/opt/cloudera/hiveodbc"
driver_dir="$driver_root/lib/64"

export ODBCSYSINI="$2"
export CLOUDERAHIVEINI="$2/.cloudera.hiveodbc.ini"
export LD_LIBRARY_PATH="$driver_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
cat > "$ODBCSYSINI/odbcinst.ini" <<EOF
[Cloudera ODBC Driver for Apache Hive]
Driver=$driver_dir/libclouderahiveodbc64.so
EOF
cat > "$CLOUDERAHIVEINI" <<EOF
[Driver]
DriverManagerEncoding=UTF-16
ODBCInstLib=libodbcinst.so.2
ErrorMessagesPath=$driver_root/ErrorMessages
LogLevel=0
EOF
odbcinst -q -d -n 'Cloudera ODBC Driver for Apache Hive' >/dev/null
