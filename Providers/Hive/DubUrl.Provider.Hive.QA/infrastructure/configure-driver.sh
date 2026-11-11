#!/usr/bin/env bash
# Source with the driver package, an empty private directory, and the target server.
case "$3" in
    hive)
        printf '%s  %s\n' 'd861e1a2b81bb7b98f688f0fabe8faa51ef48d5fe504c5fc8ab9328df9c14580' "$1" | sha256sum --check || return 1
        dpkg-deb -x "$1" "$2" || return 1
        printf 'Cloudera Hive ODBC %s\n' "$(dpkg-deb -f "$1" Version)"
        ;;
    spark)
        printf '%s  %s\n' 'be1d451fa2b1d565ceb6f1c621aea58c4848ca896555507884ddbbe978fdca99' "$1" | sha256sum --check || return 1
        rpm2cpio "$1" | (cd "$2" && cpio -id --quiet) || return 1
        printf '%s\n' 'Cloudera Hive ODBC 2.5.0.1001-1.el6.x86_64'
        ;;
    *)
        printf 'Unknown ODBC QA target: %s\n' "$3" >&2
        return 1
        ;;
esac
driver_root="$2/opt/cloudera/hiveodbc"
driver_dir="$driver_root/lib/64"

export ODBCSYSINI="$2"
export CLOUDERAHIVEINI="$2/.cloudera.hiveodbc.ini"
export LD_LIBRARY_PATH="$driver_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
cat > "$ODBCSYSINI/odbcinst.ini" <<EOF || return 1
[Cloudera ODBC Driver for Apache Hive]
Driver=$driver_dir/libclouderahiveodbc64.so
EOF
cat > "$CLOUDERAHIVEINI" <<EOF || return 1
[Driver]
DriverManagerEncoding=UTF-16
ODBCInstLib=libodbcinst.so.2
ErrorMessagesPath=$driver_root/ErrorMessages
LogLevel=0
EOF
odbcinst -q -d -n 'Cloudera ODBC Driver for Apache Hive' >/dev/null
