# Third-party components

dbdatabuild is MIT licensed (see `LICENSE`). The executable it ships (`scripts/publish.sh`) contains the components below. All of them are under permissive licenses; each keeps its own copyright and license text, which are in the packages and repositories named here. This list was checked against the NuGet metadata of every package the build resolves on 2026-10-02; re-check it when dependencies change.

## Native libraries

| Component | License | Notes |
|---|---|---|
| [polyglot-sql](https://github.com/tobilg/polyglot) (`polyglot_sql_ffi`) | MIT | SQL parser, generator and transpiler, built from a pinned commit by `scripts/build-polyglot.sh`; the repository also carries license texts for the data it vendors |
| [DuckDB](https://duckdb.org) | MIT | through DuckDB.NET |
| Microsoft SqlClient SNI | Microsoft software license terms (redistributable runtime) | through Microsoft.Data.SqlClient |

## Managed packages that ship

| Package | License |
|---|---|
| DuckDB.NET.Data.Full, DuckDB.NET.Bindings.Full | MIT |
| Apache.Arrow, Apache.Arrow.Scalars, Apache.Arrow.Flight, Apache.Arrow.Flight.Sql | Apache-2.0 |
| Grpc.Net.Client, Grpc.Net.Common, Grpc.Core.Api, Google.Protobuf | Apache-2.0 (Grpc.*), BSD-3-Clause (Google.Protobuf) |
| Microsoft.Data.SqlClient and its Microsoft.IdentityModel, Microsoft.Extensions, System.* dependencies | MIT |
| Microsoft.SqlServer.TransactSql.ScriptDom | MIT |
| Npgsql | PostgreSQL License |
| System.CommandLine | MIT |
| YamlDotNet | MIT |
| Terminal.Gui and its dependencies (Markdig BSD-2-Clause; TextMateSharp, Onigwrap, Wcwidth, ColorHelper, Humanizer, System.IO.Abstractions, Testably.Abstractions: MIT) | MIT |
| JsonSchema.Net, JsonPointer.Net, Json.More.Net | MIT |

## Used only to build and test (not shipped)

xUnit and its analyzers (Apache-2.0), Xunit.SkippableFact (MS-PL), coverlet (MIT), Microsoft.NET.Test.Sdk (MIT), Validation (MS-PL).
