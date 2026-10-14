#!/usr/bin/env bash
# Throwaway local engines for the conformance suite (tests/DbDataBuild.Tests.Conformance). Needs docker.
#   scripts/test-engines.sh up [engine ...]   start the engines, wait until each is ready, print the exports
#                                             engines: mssql pg (the default) mssql2025 oracle spark bigquery flight, or `all`
#   scripts/test-engines.sh env               print the exports for the engines that are running
#   scripts/test-engines.sh down              stop and remove them
# Then: eval "$(scripts/test-engines.sh env)"  and  dotnet test tests/DbDataBuild.Tests.Conformance [--filter "DisplayName~oracle"]
# The suite refuses any host that is not loopback. Passwords here belong to throwaway containers only.
# Oracle, Spark and the BigQuery emulator are only probed for their dialect (docs/research/target-engines.md); each takes a minute or two to start.
# `flight` is two Arrow Flight SQL servers (voltrondata/sqlflite, DuckDB behind it): one plain and one with TLS (a certificate made when it starts, which `env` copies out for the `ca_file` option). They are origins only.
set -euo pipefail
MSSQL_IMAGE="${DDB_MSSQL_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
MSSQL2025_IMAGE="${DDB_MSSQL2025_IMAGE:-mcr.microsoft.com/mssql/server:2025-latest}"
PG_IMAGE="${DDB_PG_IMAGE:-postgres:17-alpine}"
ORACLE_IMAGE="${DDB_ORACLE_IMAGE:-gvenzl/oracle-free:23-slim}"
SPARK_IMAGE="${DDB_SPARK_IMAGE:-apache/spark:4.0.0}"
BIGQUERY_IMAGE="${DDB_BIGQUERY_IMAGE:-ghcr.io/goccy/bigquery-emulator:latest}"
FLIGHT_IMAGE="${DDB_FLIGHT_IMAGE:-voltrondata/sqlflite:latest}"
FLIGHT_PASSWORD='ddbflight'
FLIGHT_DIR="${DDB_FLIGHT_DIR:-$HOME/.cache/dbdatabuild/flight}"
SA_PASSWORD='Ddb!Conf_7xQ2'
PG_PASSWORD='ddbconf'
ORACLE_PASSWORD='ddbconf'
LABEL=(--label dbdatabuild-conformance=1)

port() { docker port "$1" "$2" | head -1 | sed 's/.*://'; }
running() { docker ps --format '{{.Names}}' | grep -qx "$1"; }

wait_for() {   # wait_for <seconds> <description> <command...>
  local seconds=$1 what=$2; shift 2
  for _ in $(seq 1 "$seconds"); do "$@" >/dev/null 2>&1 && return 0; sleep 1; done
  echo "timed out waiting for $what" >&2; return 1
}

start() {
  case "$1" in
    mssql) docker run -d --rm --name ddb-conf-mssql "${LABEL[@]}" -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$SA_PASSWORD" -p 127.0.0.1::1433 "$MSSQL_IMAGE" >/dev/null ;;
    mssql2025) docker run -d --rm --name ddb-conf-mssql2025 "${LABEL[@]}" -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$SA_PASSWORD" -p 127.0.0.1::1433 "$MSSQL2025_IMAGE" >/dev/null ;;
    pg) docker run -d --rm --name ddb-conf-pg "${LABEL[@]}" -e "POSTGRES_PASSWORD=$PG_PASSWORD" -p 127.0.0.1::5432 "$PG_IMAGE" >/dev/null ;;
    oracle) docker run -d --rm --name ddb-conf-oracle "${LABEL[@]}" -e "ORACLE_PASSWORD=$ORACLE_PASSWORD" -p 127.0.0.1::1521 "$ORACLE_IMAGE" >/dev/null ;;
    spark)
      # the Thrift server (HiveServer2) in http mode, which the ADBC Spark driver speaks; Spark 4 in its default ANSI mode
      docker run -d --rm --name ddb-conf-spark "${LABEL[@]}" -p 127.0.0.1::10000 "$SPARK_IMAGE" /opt/spark/bin/spark-submit \
        --class org.apache.spark.sql.hive.thriftserver.HiveThriftServer2 --name thrift /opt/spark/jars/spark-hive-thriftserver_2.13-4.0.0.jar \
        --hiveconf hive.server2.transport.mode=http --hiveconf hive.server2.thrift.http.port=10000 --hiveconf hive.server2.thrift.http.path=cliservice \
        --hiveconf hive.server2.thrift.bind.host=0.0.0.0 >/dev/null ;;
    bigquery) docker run -d --rm --name ddb-conf-bigquery "${LABEL[@]}" -p 127.0.0.1::9050 "$BIGQUERY_IMAGE" --project=ddb --port=9050 >/dev/null ;;
    flight)
      docker run -d --rm --name ddb-conf-flight "${LABEL[@]}" -e "SQLFLITE_PASSWORD=$FLIGHT_PASSWORD" -e TLS_ENABLED=0 -e PRINT_QUERIES=0 -p 127.0.0.1::31337 "$FLIGHT_IMAGE" >/dev/null
      docker run -d --rm --name ddb-conf-flight-tls "${LABEL[@]}" -e "SQLFLITE_PASSWORD=$FLIGHT_PASSWORD" -e TLS_ENABLED=1 -e PRINT_QUERIES=0 -p 127.0.0.1::31337 "$FLIGHT_IMAGE" >/dev/null ;;
    *) echo "unknown engine $1" >&2; exit 2 ;;
  esac
}

ready() {
  case "$1" in
    mssql) wait_for 90 "SQL Server" docker exec ddb-conf-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SA_PASSWORD" -C -Q "SELECT 1" ;;
    mssql2025) wait_for 120 "SQL Server 2025" docker exec ddb-conf-mssql2025 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SA_PASSWORD" -C -Q "SELECT 1" ;;
    pg) wait_for 60 "PostgreSQL" docker exec ddb-conf-pg pg_isready -U postgres ;;
    oracle) wait_for 240 "Oracle" bash -c 'docker logs ddb-conf-oracle 2>&1 | grep -q "DATABASE IS READY TO USE"' ;;
    spark) wait_for 120 "Spark" bash -c 'docker logs ddb-conf-spark 2>&1 | grep -q "Started ThriftHttpCLIService"' ;;
    bigquery) wait_for 60 "the BigQuery emulator" bash -c "curl -fs http://127.0.0.1:$(port ddb-conf-bigquery 9050)/bigquery/v2/projects/ddb/datasets" ;;
    flight) wait_for 90 "Flight SQL" bash -c 'docker logs ddb-conf-flight 2>&1 | grep -q "SQLFlite server - started" && docker logs ddb-conf-flight-tls 2>&1 | grep -q "SQLFlite server - started"' ;;
  esac
}

# The ODBC origin's tests (a copy from PostgreSQL through psqlodbc) need the driver manager (unixODBC) and PostgreSQL's ODBC driver on this machine: a driver unpacked by scripts/fetch-psqlodbc.sh, or one installed
# on the system (odbcinst lists `PostgreSQL Unicode`). Without either the tests skip and say why.
odbc_env() {
  local dir="${DDB_PSQLODBC_DIR:-$HOME/.cache/dbdatabuild/psqlodbc}" driver
  driver="$(find "$dir" -name psqlodbcw.so 2>/dev/null | head -1 || true)"
  if [ -n "$driver" ]; then
    echo "export DBDATABUILD_TEST_ODBC_PG_DRIVER='$driver'"
    echo "export LD_LIBRARY_PATH='$(dirname "$(dirname "$driver")")${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}'"
  elif command -v odbcinst >/dev/null 2>&1 && odbcinst -q -d 2>/dev/null | grep -qx '\[PostgreSQL Unicode\]'; then
    echo "export DBDATABUILD_TEST_ODBC_PG_DRIVER='PostgreSQL Unicode'"
  fi
}

case "${1:-}" in
  up)
    shift
    engines=("$@"); [ ${#engines[@]} -eq 0 ] && engines=(mssql pg)
    [ "${engines[0]}" = all ] && engines=(mssql pg mssql2025 oracle spark bigquery flight)
    for e in "${engines[@]}"; do start "$e"; done      # all are started first, so they come up together
    for e in "${engines[@]}"; do ready "$e"; done
    "$0" env
    ;;
  env)
    running ddb-conf-mssql && echo "export DBDATABUILD_TEST_MSSQL='Server=127.0.0.1,$(port ddb-conf-mssql 1433);User Id=sa;Password=$SA_PASSWORD;TrustServerCertificate=true;Encrypt=false'"
    running ddb-conf-mssql2025 && echo "export DBDATABUILD_TEST_MSSQL2025='Server=127.0.0.1,$(port ddb-conf-mssql2025 1433);User Id=sa;Password=$SA_PASSWORD;TrustServerCertificate=true;Encrypt=false'"
    running ddb-conf-pg && echo "export DBDATABUILD_TEST_PG='Host=127.0.0.1;Port=$(port ddb-conf-pg 5432);Username=postgres;Password=$PG_PASSWORD'"
    odbc_env
    running ddb-conf-oracle && echo "export DBDATABUILD_TEST_ORACLE='Host=127.0.0.1;Port=$(port ddb-conf-oracle 1521);User Id=system;Password=$ORACLE_PASSWORD;Service=FREEPDB1'"
    running ddb-conf-spark && echo "export DBDATABUILD_TEST_SPARK='Host=127.0.0.1;Port=$(port ddb-conf-spark 10000)'"
    if running ddb-conf-flight; then
      echo "export DBDATABUILD_TEST_FLIGHT='grpc://127.0.0.1:$(port ddb-conf-flight 31337);user=sqlflite_username;password=$FLIGHT_PASSWORD'"
    fi
    if running ddb-conf-flight-tls; then
      mkdir -p "$FLIGHT_DIR" && docker cp ddb-conf-flight-tls:/opt/sqlflite/tls/root-ca.pem "$FLIGHT_DIR/root-ca.pem" >/dev/null 2>&1 \
        && echo "export DBDATABUILD_TEST_FLIGHT_TLS='grpc+tls://localhost:$(port ddb-conf-flight-tls 31337);user=sqlflite_username;password=$FLIGHT_PASSWORD'" \
        && echo "export DBDATABUILD_TEST_FLIGHT_CA='$FLIGHT_DIR/root-ca.pem'"
    fi
    running ddb-conf-bigquery && echo "export DBDATABUILD_TEST_BIGQUERY='Host=127.0.0.1;Port=$(port ddb-conf-bigquery 9050)'"
    true
    ;;
  down)
    docker ps -q --filter label=dbdatabuild-conformance=1 | xargs -r docker stop >/dev/null
    ;;
  *) echo "usage: $0 up [mssql pg mssql2025 oracle spark bigquery flight | all] | env | down" >&2; exit 2 ;;
esac
