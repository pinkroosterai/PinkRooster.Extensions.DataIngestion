![PinkRooster logo](https://raw.githubusercontent.com/pinkroosterai/PinkRooster/5b6d303a7ab57cf6374b210eaf0e36ca9c2ef403/assets/logo-120.png)

# PinkRooster.Extensions.DataIngestion.Chunkers

Structure-aware recursive token chunking and situational contextual enrichment for `Microsoft.Extensions.DataIngestion`.

Status: release, developed against the .NET 10 SDK with AOT and trimmability support.

```text
dotnet add package PinkRooster.Extensions.DataIngestion.Chunkers --prerelease
```

## Overview

`PinkRooster.Extensions.DataIngestion.Chunkers` extends Microsoft's RAG abstractions with production-grade text processing components:

- **`RecursiveTokenChunker`**: Hierarchical document chunker that respects document structure (headers, code blocks, tables, paragraphs) and uses stepped token budget windows (`MaxTokensPerChunk`, `ReservedHeaderTokens`, `OverlapTokens`). Slices text without intermediate allocations and snaps boundaries cleanly to word boundaries.
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

## Compatibility

- Target Framework: .NET 10 (`net10.0`)
- Dependencies: `Microsoft.Extensions.DataIngestion`, `Microsoft.ML.Tokenizers`
- Repository: [https://github.com/pinkroosterai/PinkRooster.Extensions.DataIngestion](https://github.com/pinkroosterai/PinkRooster.Extensions.DataIngestion)
