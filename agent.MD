# Chimpiler Agent Guidelines

This document captures conventions and lessons learned to guide future AI-assisted development on this repository.

---

## Core responsibility

Chimpiler translates EF Core `DbContext` models into SQL Server DACPAC files.  Every non-trivial feature in the SQL Server DDL must be represented faithfully in the output DACPAC.  Tests must assert on the **DACPAC model objects** (tables, columns, primary keys, foreign keys, indexes, views, default constraints), not merely on file existence.

---

## Test coverage requirements

### 1. Identity columns (IDENTITY(1,1))

**Why it matters:** Auto-increment integer primary keys are the most common pattern in SQL Server.  If `IDENTITY(1,1)` is missing from the DACPAC, all inserted rows receive `Id = 0`, breaking the application silently.

**What to test:**
- Every `int` or `long` convention-based PK column must have `Column.IsIdentity == true` in the DACPAC model.
- Non-PK `int` columns (e.g. foreign keys, counters) must have `Column.IsIdentity == false`.
- An `int` column with `HasDefaultValue(n)` must have `Column.IsIdentity == false` (it has a DEFAULT constraint, not identity).

**Regression note (PR #13):** The `isIdentity` check originally used `property.GetDefaultValue() == null`.  EF Core returns the CLR default (`0`) for value-type properties even when no default is configured, so this condition was always `false` for `int`/`long` columns and IDENTITY was never emitted.  The correct approach is to check the SQL Server-specific value generation strategy:

```csharp
var isIdentity = property.GetValueGenerationStrategy()
    == SqlServerValueGenerationStrategy.IdentityColumn;
```

### 2. Primary keys

**Why it matters:** Missing PKs produce invalid DACPACs that cannot be deployed.

**What to test:**
- Every non-view entity type must have exactly one `PrimaryKeyConstraint` object in the DACPAC.
- Composite PKs (multiple columns) must include all configured columns.

### 3. Foreign keys

**Why it matters:** Missing or incorrect FKs break referential integrity enforcement in the deployed database.

**What to test:**
- All `HasOne`/`HasMany` relationships must produce a `ForeignKeyConstraint` object in the DACPAC.
- The constraint must reference the correct principal table.
- `OnDelete(DeleteBehavior.Cascade)` must produce `ON DELETE CASCADE`; `Restrict` must produce `ON DELETE NO ACTION`.
- JSON-owned types (`.ToJson()`) must **not** produce FK constraints – the owned data lives in a column, not a separate table.

### 4. DEFAULT constraints

**Why it matters:** Adding a new NOT NULL column to a table that already has rows requires a DEFAULT constraint; without one, the deployment fails.

**What to test:**
- `HasDefaultValue("pending")` → `DEFAULT ('pending')` constraint present, column is **not** identity.
- `HasDefaultValueSql("GETDATE()")` → `DEFAULT (GETDATE())` constraint present.
- `HasDefaultValue(1)` on an `int` column → `DEFAULT (1)` constraint present, column is **not** identity.
- Property initializers (e.g. `public string Status { get; set; } = "active";`) without an explicit `HasDefaultValue()` → reflected default emitted.

### 5. Columns

**What to test:**
- Column names match the mapped column name (respects `HasColumnName()`).
- Column types match (e.g. `decimal(18,2)`, `nvarchar(max)`, `bigint`).
- Nullable columns are `NULL`; non-nullable columns are `NOT NULL`.
- JSON-owned navigation properties must produce an `nvarchar(max)` column on the owning table.

---

## How to assert on DACPAC model objects

```csharp
using var model = TSqlModel.LoadFromDacpac(outputPath, new ModelLoadOptions());

// Tables
var tables = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Table).ToList();

// Columns (children of a table)
var columns = table.GetChildren()
    .Where(c => c.ObjectType == ModelSchema.Column).ToList();
var isIdentity = column.GetProperty<bool>(Column.IsIdentity);
// Note: IdentitySeed and IdentityIncrement are stored as strings in the DACPAC model
var seed = column.GetProperty<string>(Column.IdentitySeed);       // e.g. "1000"
var increment = column.GetProperty<string>(Column.IdentityIncrement); // e.g. "10"

// Primary keys
var pks = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.PrimaryKeyConstraint).ToList();

// Foreign keys
var fks = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.ForeignKeyConstraint).ToList();

// DEFAULT constraints
var defaults = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.DefaultConstraint).ToList();

// Indexes
var indexes = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.Index).ToList();

// Views
var views = model.GetObjects(DacQueryScopes.UserDefined, ModelSchema.View).ToList();
```

---

## EF Core SQL Server quirks

| Scenario | Symptom if missed |
|----------|-------------------|
| `GetDefaultValue()` returns `0` for `int` with no explicit default | Identity detection always false; `IDENTITY(1,1)` never emitted |
| `HasDefaultValue(n)` sets `ValueGenerated.OnAdd` automatically | An `int` column with a DEFAULT can look like an identity column if checked naively |
| `HasDefaultValueSql(...)` sets `ValueGenerated.OnAdd` automatically | Same as above |
| Owned types with `.ToJson()` have no separate table | FK from owner to owned type must be skipped |
| `GetDefaultValue()` returns CLR default for value types | Use `GetValueGenerationStrategy()` for reliable identity detection |
| `GetIdentitySeed()` / `GetIdentityIncrement()` throw on the runtime model | Use the design-time model via `context.GetInfrastructure().GetService<IDesignTimeModel>()` |
| `Column.IdentitySeed` / `Column.IdentityIncrement` in the DACPAC model are `string`, not numeric | Use `GetProperty<string>(Column.IdentitySeed)` in tests |

---

## Build and test commands

```bash
# Build
dotnet build Chimpiler.slnx

# Run all tests
dotnet test tests/Chimpiler.Tests/Chimpiler.Tests.csproj

# Run a subset of tests
dotnet test tests/Chimpiler.Tests/Chimpiler.Tests.csproj --filter "FullyQualifiedName~DacpacGenerator"
```
