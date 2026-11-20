![PinkRooster logo](https://raw.githubusercontent.com/pinkroosterai/PinkRooster/5b6d303a7ab57cf6374b210eaf0e36ca9c2ef403/assets/logo-120.png)

# PinkRooster.Extensions.DataIngestion.Chunkers

Structure-aware chunking (recursive, table and hierarchical) and situational contextual enrichment for `Microsoft.Extensions.DataIngestion`.

Status: release, developed against the .NET 10 SDK with AOT and trimmability support.

```text
dotnet add package PinkRooster.Extensions.DataIngestion.Chunkers --prerelease
```

## Overview

`PinkRooster.Extensions.DataIngestion.Chunkers` extends Microsoft's RAG abstractions with production-grade text processing components:

- **`RecursiveTokenChunker`**: Hierarchical document chunker that respects document structure (headers, code blocks, tables, paragraphs) and uses stepped token budget windows (`MaxTokensPerChunk`, `ReservedHeaderTokens`, `OverlapTokens`). Slices text without intermediate allocations and snaps boundaries cleanly to word boundaries.
- **`MarkdownTableChunker`**: Splits Markdown and document tables into row windows that each repeat the header row, with part index, continuation flag and row range in the chunk metadata. Prose around the tables can be emitted or skipped (`EmitNonTableContent`).
- **`HierarchicalTokenChunker`**: Streams large parent chunks and the smaller child chunks cut from each, linked by `chunk_id`/`parent_id` metadata for small-to-big retrieval. `EmissionMode` selects parents, children, or both interleaved.
- **`ContextualChunkProcessor`**: Enriches chunks with document-level situational context using an `IChatClient` (e.g. Azure OpenAI, Ollama, or custom models), boosting retrieval precision and MRR in dense and hybrid vector search pipelines.

## Quickstart

```csharp
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;

var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

var options = new RecursiveChunkerOptions(tokenizer)
{
    MaxTokensPerChunk = 256,
    OverlapTokens = 32,
    PreserveAtomicElements = true,
};

var chunker = new RecursiveTokenChunker(options);

var document = new IngestionDocument("guide.md");
var section = new IngestionDocumentSection();
section.Elements.Add(new IngestionDocumentHeader("# Architecture Overview") { Level = 1 });
section.Elements.Add(new IngestionDocumentParagraph("This guide covers the core pipeline flow."));
document.Sections.Add(section);

await foreach (IngestionChunk<string> chunk in chunker.ProcessAsync(document))
{
    Console.WriteLine($"Chunk ({chunk.Content.Length} chars): {chunk.Content}");
}
```

## Features

- **Zero-allocation slicing**: Uses `ReadOnlySpan<char>` and `Tokenizer.GetIndexByTokenCount` for deterministic token evaluation.
- **Atomic element preservation**: Prevents tables, formulas, and demarcated code blocks from being split into illegible fragments.
- **Situational enrichment**: Prepends document-level context summaries via `ContextualChunkProcessor` with token budget guards.
- **AOT and Trimmable**: Fully validated with `<IsAotCompatible>true</IsAotCompatible>` and `<IsTrimmable>true</IsTrimmable>`.

## Chunk metadata

Every key below is a constant on `ChunkMetadataKeys`; read metadata through the constants rather than string literals.

| Constant | Key | Value | Written by |
|---|---|---|---|
| `IsTablePart` | `is_table_part` | `bool`, always `true` | chunks cut from a table |
| `TablePartIndex` | `table_part_index` | `int`, zero-based part within the table | chunks cut from a table |
| `IsTableContinuation` | `is_table_continuation` | `bool`, `true` after the first part | chunks cut from a table |
| `TableRowRange` | `table_row_range` | `string`, one-based inclusive data rows such as `3-7`, or `0-0` | chunks cut from a table |
| `ChunkRole` | `chunk_role` | `string`, `ParentRole` (`parent`) or `ChildRole` (`child`) | `HierarchicalTokenChunker` |
| `ChunkId` | `chunk_id` | `string`, identifier within the document | `HierarchicalTokenChunker` |
| `ParentId` | `parent_id` | `string`, the parent's `chunk_id` | `HierarchicalTokenChunker` children |
| `ChildIndex` | `child_index` | `int`, zero-based position within the parent | `HierarchicalTokenChunker` children |
| `ChildCount` | `child_count` | `int`, number of children of the parent | `HierarchicalTokenChunker` |
| `ParentTokenCount` | `parent_token_count` | `int`, token count of the parent | `HierarchicalTokenChunker` |
| `ContextualHeader` | `contextual_header` | `string`, the prepended header | `ContextualChunkProcessor` |
| `ConditionalSymbols` | `conditional_symbols` | `string[]`, active preprocessor symbols | `RoslynCodeChunker` (CodeAnalysis package) |

## Telemetry

`ContextualChunkProcessor` emits one `contextual_enrichment` activity per enriched chunk from the `ActivitySource` named by `ContextualChunkProcessor.ActivitySourceName` (`PinkRooster.Extensions.DataIngestion.Chunkers`). Register that name with your tracer (for OpenTelemetry, `AddSource(ContextualChunkProcessor.ActivitySourceName)`). Each activity carries these tags:

| Tag | Value |
|---|---|
| `contextual_enrichment.outcome` | `enriched`, `header_omitted`, `provider_fallback` or `provider_error` |
| `gen_ai.usage.input_tokens` | input tokens reported by the chat client |
| `gen_ai.usage.output_tokens` | output tokens reported by the chat client |
| `gen_ai.usage.cache_read.input_tokens` | cached input tokens reported by the chat client |

When `LoggerFactory` is set, every outcome and its token counts are logged at `Debug` level, including the two outcomes that start no activity: `filtered` (excluded by `ChunkFilter`) and `unresolved_context` (fallback when the chunk cannot be located in its document).

## Compatibility

- Target Framework: .NET 10 (`net10.0`)
- Dependencies: `Microsoft.Extensions.DataIngestion`, `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.ML.Tokenizers`
- Repository: [https://github.com/pinkroosterai/PinkRooster.Extensions.DataIngestion](https://github.com/pinkroosterai/PinkRooster.Extensions.DataIngestion)
