#!/usr/bin/env bash
# DuckDB's age() against PostgreSQL's native age() and a T-SQL formulation, in every shape a model uses it in (docs/research/age-function.md).
#   scripts/age-probe.sh [outfile]      defaults to docs/research/age-function-results.txt. Needs the engines (scripts/test-engines.sh up).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
OUT="${1:-docs/research/age-function-results.txt}"
if [ -z "${DBDATABUILD_TEST_MSSQL:-}" ] || [ -z "${DBDATABUILD_TEST_PG:-}" ]; then
  eval "$(scripts/test-engines.sh env)"
fi
DDB_AGE_PROBE_OUT="$(cd "$(dirname "$OUT")" && pwd)/$(basename "$OUT")" dotnet test tests/DbDataBuild.Tests.Conformance --filter "FullyQualifiedName~AgeProbe"
