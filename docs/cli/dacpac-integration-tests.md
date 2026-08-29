# DACPAC PostgreSQL integration tests

The DACPAC deployment integration suite is intentionally separate from `Chimpiler.slnx`, matching
the repository's existing explicit integration-test convention.

It starts `postgres:17-alpine` with Testcontainers and verifies empty deployment, idempotence,
additive upgrades and data preservation, identity/defaults, constraints/indexes, destructive
rejection, dry-run/script behavior, transaction rollback, unsupported-object preflight, and
concurrent advisory locking.

```bash
dotnet test tests/Chimpiler.Dacpac.IntegrationTests/Chimpiler.Dacpac.IntegrationTests.csproj
```

Docker must be installed and running. CI runs this suite before publishing packages.
