# PinkRooster.Extensions.DataIngestion Samples

A runnable console application demonstrating structure-aware recursive chunking, situational contextual enrichment, and syntax-aware C# AST chunking for `Microsoft.Extensions.DataIngestion`.

## Running the Sample

Run all showcase scenarios sequentially (default):

```text
dotnet run --project samples/PinkRooster.Extensions.DataIngestion.Samples
```

Run an individual chunker showcase scenario:

```text
# Showcase RecursiveTokenChunker (prose, heading breadcrumbs, atomic fences, repeated table headers)
dotnet run --project samples/PinkRooster.Extensions.DataIngestion.Samples -- recursive

# Showcase ContextualChunkProcessor (situational retrieval, prompt caching, token caps, chunk filtering)
dotnet run --project samples/PinkRooster.Extensions.DataIngestion.Samples -- contextual

# Showcase RoslynCodeChunker (C# syntax, primary constructors, trivia, conditional symbols, statement fallback)
dotnet run --project samples/PinkRooster.Extensions.DataIngestion.Samples -- roslyn
```

---

## Showcase Scenarios

### 1. `recursive` (`RecursiveTokenChunker`)

Showcases hierarchical Markdown document chunking:
- **Heading Hierarchies:** Propagates hierarchical breadcrumbs into `chunk.Context` (e.g. `# Logistics Guide > ## Freight Schedules > ### Regional Matrix`).
- **Atomic Elements:** Preserves small tables and fenced code blocks (` ```json `) intact when they fit within the token budget.
- **Table Continuation with Repeated Headers:** When a multi-row table exceeds `MaxTokensPerChunk`, splits rows across continuation chunks while automatically repeating the column header and delimiter rows on each chunk.
- **Word-Boundary Snapping:** Snaps `OverlapTokens` to word boundaries to avoid slicing mid-word.

### 2. `contextual` (`ContextualChunkProcessor`)

Showcases Anthropic-style Contextual Retrieval:
- **Situational Headers:** Previews document-situated context prepended to each chunk and stored in `chunk.Metadata["contextual_header"]`.
- **Prompt Cache Alignment:** Demonstrates provider-specific prompt-cache hooks (`ConfigureCacheControl`) for ephemeral caching.
- **Selective Filtering:** Uses `ChunkFilter` to bypass enrichment on structured tables or short headings, optimizing indexing costs.
- **Downstream Token Budgeting:** Uses `MaxEnrichedTokens` and `EmbeddingTokenizer` to guarantee chunks never exceed downstream vector embedding limits.

### 3. `roslyn` (`RoslynCodeChunker` & `SourceCodeDocumentReader`)

Showcases syntax-aware C# source code chunking:
- **Direct Source Ingestion:** Reads raw `.cs` files with `SourceCodeDocumentReader` without misinterpreting `#region` or `#nullable` directives as Markdown headers.
- **AST Node Traversal:** Splits along natural syntax boundaries: namespaces, types, interfaces, records, and methods.
- **Primary Constructor Preservation:** Preserves C# 12+ primary constructor parameter lists in `chunk.Context` across member chunks (`IncludeEnclosingTypeSignature = true`).
- **Trivia & Attribute Attachment:** Keeps XML documentation comments (`/// <summary>`) and attributes (`[Obsolete]`) attached to their member bodies.
- **Statement Fallback:** When a method body exceeds `MaxTokensPerChunk`, falls back to statement-level splitting (`if`, `foreach`, `try`) rather than arbitrary token cuts.
- **Preprocessor Directives:** Captures active `#if DEBUG` conditional compilation symbols in `chunk.Metadata["conditional_symbols"]` and strips unmatched `#region` directives (`StripRegionTrivia = true`).
- **Embedded Markdown C#:** Extracts and syntax-chunks fenced ````csharp ```` code blocks directly from mixed Markdown documentation.

---

## Architecture & Code Structure

```text
samples/PinkRooster.Extensions.DataIngestion.Samples/
├── Program.cs                                    # Entry point, CLI argument parsing & quickstart anchor
├── Formatting/
│   └── ConsoleFormatter.cs                       # Visual chunk card formatting and summary tables
└── Scenarios/
    ├── ISampleScenario.cs                        # Scenario runner contract
    ├── ScenarioResult.cs                         # Execution results and generated chunks
    ├── SampleContextualChatClient.cs             # Deterministic, offline simulated IChatClient
    ├── RecursiveChunkerScenario.cs               # RecursiveTokenChunker showcase
    ├── ContextualEnrichmentScenario.cs           # ContextualChunkProcessor showcase
    └── RoslynCodeChunkerScenario.cs              # RoslynCodeChunker & SourceCodeDocumentReader showcase
```
