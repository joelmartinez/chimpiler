# `chimpiler kb` — Local GraphRAG Knowledge Base

`chimpiler kb` builds and queries a **completely local, zero-cloud, zero-Python** knowledge base
that supports GraphRAG-style retrieval. Everything lives in a single SQLite file that you can
commit, copy, or delete.

```bash
chimpiler kb init
chimpiler kb add ./docs
chimpiler kb graph-search "how do I generate a dacpac?"
```

## Agent quick start

`kb` is intended to be used directly by local CLI agents and agent harnesses. Install the global
tool if it is not already available:

```bash
dotnet tool install --global Chimpiler
# or, if it is already installed:
dotnet tool update --global Chimpiler
```

If a configured package mirror is behind NuGet.org or `chimpiler kb --help` is unavailable after
the update, install directly from NuGet.org:

```bash
dotnet tool update --global Chimpiler --add-source https://api.nuget.org/v3/index.json --ignore-failed-sources
chimpiler kb --help
```

Then have the agent run:

```bash
chimpiler kb prompt
```

The command prints compact operating instructions for indexing, local model installation,
source verification, and agent-directed graph enrichment. The
repository README also provides a copy-paste bootstrap prompt for an agent that has not yet
installed Chimpiler.

## Design

| Concern | Abstraction | Default implementation |
|---------|-------------|------------------------|
| Embeddings | `IEmbeddingProvider` | `OnnxEmbeddingProvider` (local ONNX), `HashEmbeddingProvider` (offline fallback) |
| Vectors + documents | `IVectorStore` | `SqliteVectorStore` |
| Knowledge graph | `IGraphStore` | `SqliteGraphStore` |
| Chunking | `IChunker` | Markdown, code and plain-text chunkers |
| Tokenization | `IKbTokenizer` | `BertKbTokenizer` (WordPiece via `Microsoft.ML.Tokenizers`), `WhitespaceTokenizer` |
| Everything | `IKnowledgeBase` | `KnowledgeBase` |

New providers (Azure OpenAI, OpenAI, Ollama, LM Studio, …) only need to implement
`IEmbeddingProvider`; no other component changes.

## Database

The database defaults to `.chimpiler/kb.db` and uses a versioned schema applied through
migrations recorded in `MigrationHistory`.
This project-local state is ignored by Git and should not be committed: it is a binary database
that can contain indexed source content and cannot be merged meaningfully. Use `--db` to place a
KB elsewhere when needed; commit the source corpus rather than its generated database.

Tables: `Documents`, `Chunks`, `Embeddings`, `Nodes`, `Edges`, `NodeMetadata`, `Settings`,
`MigrationHistory`.

Embeddings are stored as little-endian float32 blobs together with their pre-computed L2 norm so
cosine similarity is a single dot product at query time.

## Knowledge graph

Indexing creates one `document` node per file and one `chunk` node per chunk, plus structural
nodes for headings and chunk order. It deliberately does **not** infer people, organizations,
aliases, or relationships from prose. Those judgments belong to the agent reviewing the retrieved
source, where ambiguity and context can be handled explicitly rather than through brittle local
rules.

```
Document
 └── Chunk
      └── agent-verified mention → Entity ← agent-verified evidence → Event
```

An agent can register an entity tied to exact text in an indexed source, then add a typed,
evidence-backed relationship between registered entities. `graph-search` starts from vector hits
and traverses only those agent-authored mention, evidence, subject, and object edges. It never
walks document containment, heading, or sibling-chunk edges, and returns at most one expanded chunk
per document (up to the requested direct-result count). Each graph result prints its entity and
predicate trail plus the cited source chunk.

An agent harness should first retrieve and read sources. It can parallelize that work by assigning
separate source themes to subagents; subagents should return only cited evidence and proposed
entity/relationship records to the orchestrator. The orchestrator verifies and records them:

```bash
chimpiler kb entity "person:bob-tagart" --kind person --surface "Bob Tagart" \
  --source ./briefing.md --evidence "Bob Tagart authorized Electronic Arts."
chimpiler kb entity "organization:electronic-arts" --kind organization --surface "Electronic Arts" \
  --source ./briefing.md --evidence "Bob Tagart authorized Electronic Arts."
chimpiler kb relate "person:bob-tagart" authorized "organization:electronic-arts" \
  --source ./briefing.md --evidence "Bob Tagart authorized Electronic Arts."
```

Use stable keys and inspect them with `kb entities`. An alias is just another evidence-backed
relationship (for example, `alias-candidate`); never treat it as proof without source review.
Evidence accepts an exact source span or a normalized Markdown equivalent (for example, link text
without its URL). The database stores the matching canonical source excerpt; a failed registration
prints excerpts from the indexed source to help the agent correct its citation.

## Search pipeline

```
Query → Embedding → Vector search → Top K chunks → Agent-authored graph expansion → Rank → Results
```

`kb search` stops after vector search. `kb graph-search` additionally pulls in a bounded, diverse
set of graph neighbours through agent-authored evidence only, ranked below direct matches. Its
`--depth` is **relationship hops**, not raw graph edges: one hop follows one relationship to a
connected source; two hops follows a shared intermediate concept. The default is two hops.

This is local GraphRAG: it answers source-specific and multi-hop questions with a compact,
auditable evidence path. Corpus-wide thematic synthesis and community summaries remain harness
responsibilities: delegate focused source review to subagents, then have the orchestrating agent
combine only their cited findings. This preserves the local/offline index while applying LLM
judgment where graph construction or global synthesis needs it.

## Embedding models

By default the KB uses a dependency-free hashing provider so that everything works with no
downloads at all. For real semantic quality, install a local ONNX model:

```bash
chimpiler kb models list
chimpiler kb models install default        # BAAI/bge-small-en-v1.5
chimpiler kb models remove bge-small-en-v1.5
```

| Model | Dimension | Notes |
|-------|-----------|-------|
| `bge-small-en-v1.5` (default) | 384 | BAAI bge-small-en-v1.5, ~130 MB, excellent English retrieval quality per byte |
Models are downloaded from a pinned Hugging Face revision into `~/.chimpiler/kb/models/<model-id>/`
and are reused by every project on the machine. Downloads are written to a temporary file first and
verified against the pinned SHA-256 checksum before installation. The model's WordPiece `vocab.txt`
is a fixed tokenizer vocabulary, not indexed data; it is loaded once and does not grow with the KB.
Chunking and inference share this exact tokenizer.

Use a model for a command with `--model`:

```bash
chimpiler kb add ./docs --model default
chimpiler kb search "vector store" --model default
```

> Embeddings from different providers are not comparable. The KB rejects mixed providers. If you switch models, run
> `chimpiler kb rebuild --model <id>`.

## Commands

### `chimpiler kb init`

Creates the SQLite database and applies pending migrations.

```bash
chimpiler kb init --db ./.chimpiler/kb.db
```

### `chimpiler kb add <path>`

Adds or updates a file, or every matching file in a directory.

| Option | Default | Description |
|--------|---------|-------------|
| `--pattern` | `*.*` | Glob used when `<path>` is a directory |
| `--max-tokens` | `256` | Maximum tokens per chunk |
| `--overlap` | `32` | Token overlap between adjacent chunks |

```bash
chimpiler kb add ./docs --pattern "*.md" --max-tokens 320 --overlap 48
```

### `chimpiler kb remove <path>`

Removes a document and all of its chunks, embeddings and graph nodes.

### `chimpiler kb list`

Lists indexed documents.

### `chimpiler kb entities`

Lists agent-registered entity keys for graph retrieval and enrichment.

```bash
chimpiler kb entities
```

### `chimpiler kb entity <key>`

Registers an entity only when an agent has reviewed exact evidence in an indexed source.

```bash
chimpiler kb entity "concept:root-cause-culture" --kind concept --surface "root cause culture" \
  --source ./root-cause.md --evidence "Root cause culture requires time to change the system."
```

### `chimpiler kb relate <subject> <predicate> <object>`

Adds a relationship that an agent has verified from evidence. The entity keys must already be
registered; provide the indexed source path, exact supporting text, and optional confidence score.

```bash
chimpiler kb relate "person:bob tagart" authorized "organization:electronic arts" \
  --source ./briefing.md --evidence "Bob Tagart authorized Electronic Arts." --confidence 0.9
```

### `chimpiler kb search <query>`

Pure vector search.

```bash
chimpiler kb search "how are dacpacs generated" --top 10
```

### `chimpiler kb graph-search <query>`

Vector search plus graph expansion.

```bash
chimpiler kb graph-search "how does requirements churn lead to burnout?" --top 3 --depth 2
```

Graph-expanded results include a path such as:

```text
trail: concept:requirements-churn → contributes-to → concept:burnout
```

### `chimpiler kb rebuild`

Re-chunks and re-embeds every indexed document. Documents whose files no longer exist are
dropped.

### `chimpiler kb optimize`

Runs `ANALYZE` and `VACUUM`.

### `chimpiler kb models <list|install|remove>`

Manages the local ONNX model cache.

### `chimpiler kb prompt`

Prints self-contained installation and operating guidance. Agent harnesses can inject the output
into an agent's context before it calls the CLI. It explains acquiring Chimpiler with `dotnet tool
install --global Chimpiler`, indexing, direct and graph retrieval, local model selection,
subagent delegation, and evidence requirements for agent-added relationships.

```bash
chimpiler kb prompt
```

## Global options

| Option | Default | Description |
|--------|---------|-------------|
| `--db`, `-d` | `.chimpiler/kb.db` | Path to the SQLite database |
| `--model` | *(none)* | Embedding model id, or `default`. Omit to use the offline hash provider |

## Embedding in your own app

```csharp
await using var provider = KnowledgeBaseFactory.Build(new KnowledgeBaseOptions
{
    DatabasePath = "kb.db",
    ModelId = "default"
});

var kb = provider.GetRequiredService<IKnowledgeBase>();
await kb.InitializeAsync();
await kb.AddDocumentAsync("README.md");
var hits = await kb.GraphSearchAsync("what is chimpiler?");
```
