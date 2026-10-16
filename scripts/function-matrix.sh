#!/usr/bin/env bash
# The function matrix (docs/research/function-matrix.md): every function the shipped DuckDB lists, run through the tool's own pipeline (lowering, target rules,
# matrix lint, transpile) for SQL Server and PostgreSQL and, for what lowers, on the real engines against DuckDB's answer.
#   scripts/function-matrix.sh [outdir]      outdir defaults to docs/research; writes function-matrix*.csv and function-matrix-meta.txt, then the report
# Needs the engines (scripts/test-engines.sh up) and the polyglot library (scripts/fetch-native.sh). Takes about ten minutes. Set DDB_FUNCTION_MATRIX_ONLY=age,round
# (a comma list of function names) for a quick look at a few functions; the report is not regenerated then.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
OUT="${1:-docs/research}"
if [ -z "${DBDATABUILD_TEST_MSSQL:-}" ] || [ -z "${DBDATABUILD_TEST_PG:-}" ]; then
  eval "$(scripts/test-engines.sh env)"
fi
[ -n "${DBDATABUILD_TEST_MSSQL:-}" ] && [ -n "${DBDATABUILD_TEST_PG:-}" ] || { echo "start the engines first: scripts/test-engines.sh up" >&2; exit 1; }
mkdir -p "$OUT"
DDB_FUNCTION_MATRIX_OUT="$(cd "$OUT" && pwd)" dotnet test tests/DbDataBuild.Tests.Conformance --filter "FullyQualifiedName~FunctionMatrixRun" --logger "console;verbosity=normal"
if [ -z "${DDB_FUNCTION_MATRIX_ONLY:-}" ]; then
  python3 scripts/function-matrix-report.py "$OUT"
fi
