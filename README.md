<div align="center">

<img src="assets/logo.png" alt="PinkRooster logo" width="160" />

# PinkRooster.Extensions.DataIngestion

**Structure-aware recursive token chunking and contextual enrichment for Microsoft.Extensions.DataIngestion**

[![CI](https://github.com/pinkroosterai/PinkRooster.Extensions.DataIngestion/actions/workflows/ci.yml/badge.svg)](https://github.com/pinkroosterai/PinkRooster.Extensions.DataIngestion/actions/workflows/ci.yml)
[![NuGet Chunkers](https://img.shields.io/nuget/vpre/PinkRooster.Extensions.DataIngestion.Chunkers.svg?label=NuGet%20Chunkers)](https://www.nuget.org/packages/PinkRooster.Extensions.DataIngestion.Chunkers)
[![NuGet CodeAnalysis](https://img.shields.io/nuget/vpre/PinkRooster.Extensions.DataIngestion.CodeAnalysis.svg?label=NuGet%20CodeAnalysis)](https://www.nuget.org/packages/PinkRooster.Extensions.DataIngestion.CodeAnalysis)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

[The idea](#the-idea) · [Architecture](#architecture) · [Install](#install) · [Quickstart](#quickstart) · [Dependency Injection](#dependency-injection) · [Packages](#packages) · [Samples](#samples) · [Benchmarks](#benchmarks--evaluation) · [Docs](#docs) · [Contributing](#feedback-contributing-security-and-license)

</div>

---

## Status

Preview (`1.0.0-preview.1`), developed against the .NET 10 SDK on Linux and Windows.

## The idea

`Microsoft.Extensions.DataIngestion` offers standard primitives for RAG ingestion pipelines, but naive token chunking frequently shatters structured elements (Markdown tables, code blocks, section hierarchies) or severs semantic code boundaries in C# files.

`PinkRooster.Extensions.DataIngestion` delivers high-performance, structure-aware chunkers:
- **`RecursiveTokenChunker`**: Hierarchical document chunker with stepped token budget windows (`MaxTokensPerChunk`, `ReservedHeaderTokens`, `OverlapTokens`), zero-allocation text slicing on `ReadOnlySpan<char>`, boundary snapping to scalar word boundaries, and atomic preservation of tables and code fences.
- **`MarkdownTableChunker`**: Splits Markdown and document tables by row windows while automatically repeating column header and delimiter rows on every continuation chunk, preserving tabular semantics for vector search.
- **`HierarchicalTokenChunker`**: Streams parent and child chunks linked by `parent_id`/`chunk_id` metadata for small-to-big retrieval, supporting parent-only, child-only, or interleaved emission modes.
- **`ContextualChunkProcessor`**: Situational document-level context generator using `IChatClient` (`Microsoft.Extensions.AI`) with stepped bucket sliding windows, bounded concurrency, and prefix prompt caching.
- **`RoslynCodeChunker` & `SourceCodeDocumentReader`**: Roslyn AST-powered chunking that splits C# types, records, and methods at grammar boundaries and propagates symbol breadcrumbs (`Namespace.Class.Method`) into `chunk.Context`.

Empirical evaluation against standard technical documentation and C# source corpora demonstrates over 20% retrieval lift (MRR) compared to baseline token chunking.

## Architecture

```text
┌────────────────────────┐
│   Source Documents     │  Markdown files, C# source code, technical documentation
└───────────┬────────────┘
            │
            ▼
┌────────────────────────┐
│  IngestionDocument     │  Sections, Headings, Paragraphs, Code Fences, Tables
│ (or C# Source Syntax)  │  Document Readers (e.g. SourceCodeDocumentReader)
└───────────┬────────────┘
            │
            ▼
┌────────────────────────┐
│  PinkRooster Chunkers  │  RecursiveTokenChunker, MarkdownTableChunker,
│  & Processors          │  HierarchicalTokenChunker, RoslynCodeChunker,
└───────────┬────────────┘  ContextualChunkProcessor (IChatClient)
            │
            ▼
┌────────────────────────┐
│ IngestionChunk<string> │  Token-bounded, atomic elements, breadcrumb Context,
│                        │  table header continuation metadata, parent/child IDs
└───────────┬────────────┘
            │
            ▼
┌────────────────────────┐
│  Vector Store Writer   │  Microsoft.Extensions.VectorData (Qdrant, Azure AI Search, etc.)
└────────────────────────┘
```

## Install

```bash
dotnet add package PinkRooster.Extensions.DataIngestion.Chunkers --prerelease
dotnet add package PinkRooster.Extensions.DataIngestion.CodeAnalysis --prerelease
```

## Quickstart

<!-- quickstart:start -->
```csharp
var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

var recursiveOptions = new RecursiveChunkerOptions(tokenizer)
{
    MaxTokensPerChunk = 64,
    ReservedHeaderTokens = 12,
    OverlapTokens = 8,
    PreserveAtomicElements = true,
};

var recursiveChunker = new RecursiveTokenChunker(recursiveOptions);
```
<!-- quickstart:end -->

Process an `IngestionDocument` and stream the resulting chunks:

```csharp
var document = new IngestionDocument("guide.md");
var section = new IngestionDocumentSection();
section.Elements.Add(new IngestionDocumentHeader("# Logistics Guide") { Level = 1 });
section.Elements.Add(new IngestionDocumentParagraph("Standard customer orders ship within two business days."));
document.Sections.Add(section);

await foreach (IngestionChunk<string> chunk in recursiveChunker.ProcessAsync(document))
{
    Console.WriteLine($"[{chunk.Context}] {chunk.Content}");
}
```

## Dependency Injection

Register chunkers into `IServiceCollection` using the Microsoft.Extensions dependency injection extensions:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;

services.AddSingleton<Tokenizer>(_ => TiktokenTokenizer.CreateForModel("gpt-4o"));

// Register RecursiveTokenChunker as IngestionChunker<string>
services.AddRecursiveTokenChunker(sp => new RecursiveChunkerOptions(sp.GetRequiredService<Tokenizer>())
{
    MaxTokensPerChunk = 256,
    ReservedHeaderTokens = 32,
    OverlapTokens = 16,
    PreserveAtomicElements = true,
});
```

## Packages

| Package | What it adds | Target / AOT | Dependencies |
|---|---|---|---|
| [`PinkRooster.Extensions.DataIngestion.Chunkers`](https://www.nuget.org/packages/PinkRooster.Extensions.DataIngestion.Chunkers) | Recursive token chunker, table row windowing, hierarchical parent-child chunking, contextual enrichment | `net10.0`<br/>✅ AOT & Trimmable | `Microsoft.Extensions.DataIngestion`<br/>`Microsoft.ML.Tokenizers`<br/>`Microsoft.Extensions.AI.Abstractions` |
| [`PinkRooster.Extensions.DataIngestion.CodeAnalysis`](https://www.nuget.org/packages/PinkRooster.Extensions.DataIngestion.CodeAnalysis) | Roslyn C# source document reader and syntax-aware AST chunker | `net10.0`<br/>⚠️ Roslyn AST | `Microsoft.CodeAnalysis.CSharp`<br/>`PinkRooster.Extensions.DataIngestion.Chunkers` |

## Samples

A comprehensive console sample showcasing all chunk types, structure-aware table continuation, contextual enrichment, and Roslyn AST code chunking is available in [`samples/PinkRooster.Extensions.DataIngestion.Samples`](samples/PinkRooster.Extensions.DataIngestion.Samples).

Run all showcase scenarios sequentially:

```bash
dotnet run --project samples/PinkRooster.Extensions.DataIngestion.Samples
```

Run an individual chunker scenario:

```bash
# Hierarchical Markdown prose, heading breadcrumbs, atomic fences & repeated table headers
dotnet run --project samples/PinkRooster.Extensions.DataIngestion.Samples -- recursive

# Contextual enrichment, prompt cache alignment, token capping & chunk filtering
dotnet run --project samples/PinkRooster.Extensions.DataIngestion.Samples -- contextual

# Syntax-aware C# AST chunking, primary constructors, trivia, conditional symbols & statement fallback
dotnet run --project samples/PinkRooster.Extensions.DataIngestion.Samples -- roslyn
```

## Benchmarks & Evaluation

Micro-benchmarks and retrieval quality evaluations are located in [`benchmarks/PinkRooster.Extensions.DataIngestion.Benchmarks`](benchmarks/PinkRooster.Extensions.DataIngestion.Benchmarks):

- **Throughput & Allocations**: Run `dotnet run --project benchmarks/PinkRooster.Extensions.DataIngestion.Benchmarks -c Release -- --filter *ChunkerThroughputBenchmarks*` to execute the BenchmarkDotNet suite.
- **Retrieval Lift**: On reference suites (`benchmarks/evaluation/reference`), `RecursiveTokenChunker` achieves 0.6875 MRR compared to 0.5625 for baseline `DocumentTokenChunker` (>20% lift).
- **Smoke Verification**: Run `dotnet run --project benchmarks/PinkRooster.Extensions.DataIngestion.Benchmarks -c Debug -- smoke benchmarks/evaluation` for an offline evaluation check.

## Docs

- [PinkRooster.Extensions.DataIngestion.Chunkers](docs/PinkRooster.Extensions.DataIngestion.Chunkers.md)
- [PinkRooster.Extensions.DataIngestion.CodeAnalysis](docs/PinkRooster.Extensions.DataIngestion.CodeAnalysis.md)

## Compatibility

- .NET 10 SDK (`net10.0`)
- AOT and trimming validated for `PinkRooster.Extensions.DataIngestion.Chunkers`
- Tested on Ubuntu 24.04 and Windows

## Build and test

```bash
dotnet build PinkRooster.Extensions.DataIngestion.slnx -c Release
dotnet test --solution PinkRooster.Extensions.DataIngestion.slnx -c Release
```

## Feedback, contributing, security and license

Read [CONTRIBUTING.md](CONTRIBUTING.md) first for local build workflows, testing guidelines, and coding standards. Report a vulnerability as [SECURITY.md](SECURITY.md) describes. The code is [MIT](LICENSE) licensed.
