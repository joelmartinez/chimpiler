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

It includes a three-document chain (`Atlas sunward hold` → `heliostat bearing offset` →
`sunTargetBias`) where the test agent registers source-backed entities and relationships. It
verifies that a single-result vector search finds the direct source, while two relationship hops
reach the configuration source only through those agent-authored facts. It asserts the displayed
entity/predicate traversal trail as well as the cited result.

It also includes disconnected personal-name fixtures. A direct vector query finds the Bob Tagart
document, while graph expansion reaches the Robert Tagart document only through an
agent-verified `alias-candidate` relationship.

The relationship fixture verifies agent-registered typed event traversal: a query for Bob Tagart
expands through an evidence-backed `authorized` relationship to Electronic Arts and reaches the
otherwise unrelated `NorthstarReceipt` record. The suite also verifies that the CLI prompt tells
agents to delegate focused source review to subagents and add only cited graph facts.
