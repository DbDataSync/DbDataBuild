#!/usr/bin/env bash
# The ODBC driver of PostgreSQL (psqlodbc) for the ODBC origin's conformance tests, without root: downloads the Debian/Ubuntu packages and unpacks them into a directory of your own.
#   scripts/fetch-psqlodbc.sh        unpack into ${DDB_PSQLODBC_DIR:-$HOME/.cache/dbdatabuild/psqlodbc}
# Then `eval "$(scripts/test-engines.sh env)"` exports DBDATABUILD_TEST_ODBC_PG_DRIVER (the driver's file) and LD_LIBRARY_PATH (its libraries), which the conformance tests read.
# The driver manager itself (libodbc.so.2) must be on the machine: `apt install unixodbc` (or libodbc2) gives it. On a machine where you can use sudo, `apt install unixodbc odbc-postgresql` does all of this and
# needs no script: the tests then find the driver `PostgreSQL Unicode` in odbcinst.ini. The tool ships neither a driver manager nor a driver.
set -euo pipefail
dir="${DDB_PSQLODBC_DIR:-$HOME/.cache/dbdatabuild/psqlodbc}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$dir"
cd "$work"
apt-get download odbc-postgresql libpq5 libodbcinst2 >/dev/null
for deb in *.deb; do dpkg -x "$deb" "$dir"; done
driver="$(find "$dir" -name psqlodbcw.so | head -1)"
[ -n "$driver" ] || { echo "psqlodbcw.so was not found in the packages" >&2; exit 1; }
echo "psqlodbc unpacked: $driver"
