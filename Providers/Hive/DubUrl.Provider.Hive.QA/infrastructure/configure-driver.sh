#!/usr/bin/env bash
# Source with the driver RPM and an empty, private extraction directory.
printf '%s  %s\n' 'be1d451fa2b1d565ceb6f1c621aea58c4848ca896555507884ddbbe978fdca99' "$1" | sha256sum --check
rpm2cpio "$1" | (cd "$2" && cpio -id --quiet)
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
