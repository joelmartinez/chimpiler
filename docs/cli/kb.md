# `chimpiler kb` — Local GraphRAG Knowledge Base

`chimpiler kb` builds and queries a **completely local, zero-cloud, zero-Python** knowledge base
that supports GraphRAG-style retrieval. Everything lives in a single SQLite file that you can
commit, copy, or delete.

```bash
chimpiler kb init
chimpiler kb add ./docs
chimpiler kb graph-search "how do I generate a dacpac?"
```

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

Tables: `Documents`, `Chunks`, `Embeddings`, `Nodes`, `Edges`, `NodeMetadata`, `Settings`,
`MigrationHistory`.

Embeddings are stored as little-endian float32 blobs together with their pre-computed L2 norm so
cosine similarity is a single dot product at query time.

## Knowledge graph

Indexing creates one `document` node per file and one `chunk` node per chunk, plus `section`
nodes for markdown headings and code declarations. Each chunk also links to its strongest
cross-document semantic neighbour when its cosine similarity is at least 0.55; small lexical
overlap weighting avoids generic semantic hubs bypassing a more specific concept. Edges express
`contains`, `child`, `section`, `semantic`, `parent`, `references`, `symbol` and `type`
relationships. Traversal loads the relevant edges and walks them in memory.

```
Document
 └── Chunk
      ├── references
      ├── parent
      ├── child
      ├── section
      ├── symbol
      └── type
```

## Search pipeline

```
Query → Embedding → Vector search → Top K chunks → Graph expansion → Rank → Results
```

`kb search` stops after the vector search. `kb graph-search` additionally pulls in graph
neighbours of the top hits and ranks them below the direct matches.

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

### `chimpiler kb search <query>`

Pure vector search.

```bash
chimpiler kb search "how are dacpacs generated" --top 10
```

### `chimpiler kb graph-search <query>`

Vector search plus graph expansion.

```bash
chimpiler kb graph-search "identity columns" --top 5 --depth 2
```

### `chimpiler kb rebuild`

Re-chunks and re-embeds every indexed document. Documents whose files no longer exist are
dropped.

### `chimpiler kb optimize`

Runs `ANALYZE` and `VACUUM`.

### `chimpiler kb models <list|install|remove>`

Manages the local ONNX model cache.

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
