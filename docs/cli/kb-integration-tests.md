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

It also includes disconnected personal-name and organization fixtures. A direct vector query finds
the Bob Tagart or EA document, while depth-three graph expansion reaches the Robert Tagart or
Electronic Arts document only through a candidate alias edge.

The relationship fixture verifies query-side alias extraction and typed event traversal: a query for
Robert Tagart expands through Bob Tagart's `authorized` relationship to Electronic Arts and reaches
the otherwise unrelated `NorthstarReceipt` record.
