# `chimpiler dacpac`

Read a SQL Server Database Project DACPAC through the public DacFx model APIs and deploy a
strictly supported schema subset to PostgreSQL.

## Apply

```bash
chimpiler dacpac apply ./Database.dacpac \
  --provider postgresql \
  --connection-string "Host=localhost;Database=app;Username=app;Password=..."
```

The connection string can instead be supplied in `CHIMPILER_DACPAC_CONNECTION_STRING`.
Chimpiler never intentionally writes the connection string or password to output.

| Option | Description |
|---|---|
| `--provider` | Target provider. `postgresql` is supported (`postgres` and `pgsql` aliases); MySQL is a future extension point. |
| `--connection-string` | PostgreSQL connection string. Defaults to `CHIMPILER_DACPAC_CONNECTION_STRING`. |
| `--dry-run` | Acquire the advisory lock, introspect the target, print the SQL plan, and roll back without schema changes. |
| `--script <path>` | Write the SQL plan to a file and roll back without schema changes. |
| `--allow-destructive` | Permit reviewed drops and type changes. Without it, Chimpiler rejects destructive plans. |

Deployment is deterministic and state based. Chimpiler does not infer renames: a renamed object is
an addition plus a destructive removal. Every execution uses one PostgreSQL transaction and a
transaction-scoped advisory lock. An error rolls back the whole deployment.

## Supported subset

- Tables and schemas (`dbo` maps to PostgreSQL `public`)
- Columns using common scalar SQL Server types
- Nullability, safe literal/date/UUID defaults, and identity columns
- Primary keys, unique constraints, foreign keys, and ordinary indexes

Chimpiler fails closed for unsupported object kinds, computed/encrypted/sparse/FILESTREAM columns,
temporal/graph/memory-optimized tables, unsupported types or default expressions, filtered/hash/
clustered indexes, and DACPAC pre/post-deployment scripts. Resolve those incompatibilities before
connecting to the target.

## Boundary and licensing

This command is **not SqlPackage for PostgreSQL**. DacFx deployment APIs only target SQL Server, so
Chimpiler independently implements compatibility checks, target catalog introspection, planning,
PostgreSQL SQL generation, execution, rollback, and locking. It uses only the documented/public
Microsoft.SqlServer.DacFx APIs to read the package model. No Microsoft source code or generated
deployment implementation is copied or translated.

Microsoft.SqlServer.DacFx is distributed by Microsoft under its package license. Npgsql is
distributed under the PostgreSQL License. See `THIRD-PARTY-NOTICES.md`.
