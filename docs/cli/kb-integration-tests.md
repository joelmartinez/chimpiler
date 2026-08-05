# KB integration tests

The regular `Chimpiler.Tests` KB tests are deterministic unit tests and use the hash embedding
provider. They do not download or execute an ONNX model.

Run the separate, manually invoked integration suite to exercise the compiled `chimpiler kb`
command, the pinned BGE model download/cache, SQLite storage, indexing, and semantic retrieval:

```bash
dotnet test tests/Chimpiler.Kb.IntegrationTests/Chimpiler.Kb.IntegrationTests.csproj --filter "Category=Integration"
```

The suite installs or reuses the shared `~/.chimpiler/kb/models/bge-small-en-v1.5` cache and
indexes the fixture corpus into an isolated temporary database. It needs network access the first
time it runs and is intentionally not part of the default test command.

It includes a three-document semantic chain (`Atlas sunward hold` → `heliostat bearing offset` →
`sunTargetBias`) that verifies the answer is unavailable to both a single-result vector search and
one-hop graph traversal, but appears with `kb graph-search --depth 2`.
