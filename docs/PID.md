# Project Initiation Document (PID)

## Extended RAG Chunkers for `Microsoft.Extensions.DataIngestion`

| Metadata Field | Details |
| --- | --- |
| **Document ID** | PID-RAG-CHUNKERS-001 |
| **Version** | 1.2 (Aligned with PinkRooster Standards) |
| **Status** | Proposed / Ready for Kickoff |
| **Target Release** | 1.0.0 (Open Source, MIT License) |
| **Primary Ecosystem** | .NET 10 (`net10.0`), C# 14 |
| **Backwards Compatibility** | None (initial design and development phase) |
| **Repository Layout** | Dual-repository overlay (`pinkroosterai/PinkRooster.Extensions.DataIngestion` + `pinkroosterai/PinkRooster.Extensions.DataIngestion-internal`) |

**Core Upstream Dependencies:**

* **Data Ingestion:** `Microsoft.Extensions.DataIngestion` and `Microsoft.Extensions.DataIngestion.Abstractions`
* **AI & Tokenization:** `Microsoft.Extensions.AI.Abstractions` and `Microsoft.ML.Tokenizers`
* **Diagnostics & DI:** `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`, and `Microsoft.Extensions.DependencyInjection.Abstractions`

## 1. Executive Summary & Business Case

### 1.1 Problem Statement

Microsoft's `Microsoft.Extensions.DataIngestion` libraries establish a unified, composable ETL pipeline (`IngestionDocumentReader` to `IngestionChunker<T>` to `IngestionChunkProcessor<T>` to `IngestionChunkWriter<T>`) for Retrieval-Augmented Generation (RAG) in .NET. However, out-of-the-box chunking capabilities currently cover only fixed-size token slicing (`DocumentTokenChunker`), Markdown and HTML header splitting (`HeaderChunker`), section and page splitting (`SectionChunker`), and embedding-based cosine splitting (`SemanticSimilarityChunker`).

Consequently, .NET teams building production RAG systems face three immediate gaps:

1. **No Native Recursive Text Splitter:** Teams must pull in heavy external orchestrators (such as `Microsoft.SemanticKernel.Core`) just to get paragraph-to-sentence recursive splitting with token overlap and table/code-block preservation.
2. **No Document-Aware Contextual Retrieval:** The built-in `SummaryEnricher` summarizes chunks in isolation, leaving pronoun-heavy or ambiguous enterprise passages detached from parent document context.
3. **No Syntax-Aware Code Ingestion & Chunking:** Raw C# source files parsed through standard Markdown readers have preprocessor directives (`#region`, `#if`, `#nullable`) misinterpreted as document headers, and token-window chunkers split source code arbitrarily mid-method or mid-class.

### 1.2 Strategic Opportunity

Developing a lightweight, open-source C# library suite that implements the three highest-ROI missing chunking strategies directly against `Microsoft.Extensions.DataIngestion` and `Microsoft.Extensions.AI.Abstractions` fills these gaps with minimal dependency overhead. By adhering strictly to Microsoft's options and abstraction patterns (`IngestionChunkerOptions` and `EnricherOptions`), every component plugs natively into `IngestionPipeline<string>` and interoperates with any `IChatClient`, `IEmbeddingGenerator`, or `VectorStoreWriter`.

## 2. Project Objectives & Success Criteria

| Objective | Target Metric | Validation Method |
| --- | --- | --- |
| **Zero-Bloat Architecture** | Zero orchestrator SDKs | Dependency graph audit |
| **Pipeline Interoperability** | 100% contract compliance | End-to-end pipeline integration tests |
| **Retrieval Lift** | At least 15% Recall@5 and MRR gain | Benchmark suite vs `DocumentTokenChunker` |
| **Production Resilience** | At least 90% line coverage | Automated xUnit and concurrency suite |
| **Turnkey Developer Experience** | Full XML docs and SourceLink | NuGet package validation check |

**Success Criteria Details:**

* **Zero-Bloat Core Architecture:** Core package depends strictly on Microsoft's data ingestion, AI, tokenization, logging, and dependency injection abstractions—zero dependency on Semantic Kernel, Kernel Memory, or concrete vector store SDKs.
* **Seamless Pipeline Interoperability:** Full compatibility with `IngestionPipeline<string>`, preserving immutable `IngestionChunk<string>` contracts (`Content`, `Document`, `Context`, and `Metadata` dictionary entries) and downstream embedding token budgets.
* **Measurable Retrieval Lift:** Demonstrate at least a 15% improvement in Recall@5 and Mean Reciprocal Rank (MRR) on ambiguous enterprise prose (via `ContextualChunkProcessor`) and C# codebase Q&A (via `RoslynCodeChunker`) compared to `DocumentTokenChunker` baselines.
* **Production Resilience & Streaming:** Full `IAsyncEnumerable<IngestionChunk<string>>` streaming execution, `CancellationToken` propagation, bounded LLM concurrency, and at least 90% unit test line coverage.
* **Turnkey Developer Experience:** Published on NuGet.org under a reserved package prefix with XML documentation comments, SourceLink, quickstart documentation, and runnable sample pipelines.

## 3. Scope Definition

### 3.1 In Scope (Release 1.0.0 — Highest ROI Deliverables)

1. **`RecursiveTokenChunker` (Core Package):** Deterministic, separator-hierarchy text chunker (paragraphs to lines to sentence boundaries to words to span-level token slices) backed by `Microsoft.ML.Tokenizers.Tokenizer` with configurable `MaxTokensPerChunk`, `OverlapTokens`, `ReservedHeaderTokens`, separator retention, and atomic table/code-block handling.
2. **`ContextualChunkProcessor` (Core Package):** An `IngestionChunkProcessor<string>` implementing Anthropic-style Contextual Retrieval. Uses `IChatClient` to prepend document-situated context to each chunk prior to embedding, featuring stepped bucket windowing for oversized documents and provider-specific prompt-caching hooks.
3. **`SourceCodeDocumentReader` & `RoslynCodeChunker` (Satellite Package):** A pass-through C# `IngestionDocumentReader` and an Abstract Syntax Tree (AST) chunker powered by `Microsoft.CodeAnalysis.CSharp` that splits C# source files along namespace, type, and member boundaries while preserving XML documentation comments, conditional compilation context, primary constructor signatures, and symbol breadcrumbs.

### 3.2 Out of Scope (Deferred / Excluded from Release 1.0.0)

* **Parent-Child (Small-to-Big) & Sentence-Window Retrieval:** Deferred to release 1.1.0. Requires custom `IngestionChunkWriter<string>` and dual-collection retrieval helpers to avoid duplicating large parent payloads across vector records.
* **Proposition (Atomic) & Agentic LLM Chunkers:** Deferred to release 1.2.0 as an optional satellite package due to high indexing token costs and niche applicability.
* **Multi-Language Tree-sitter Code Chunkers (Python, TypeScript, Go):** Deferred to release 1.2.0 to avoid native binary packaging complexity in the initial release.
* **Late Chunking:** Explicitly excluded. Requires token-level hidden-state tensor access via `Microsoft.ML.OnnxRuntime`, bypassing `IEmbeddingGenerator<string, Embedding<float>>` and hosted embedding APIs.

## 4. Target Architecture & NuGet Package Topology

To inherit `IngestionChunkerOptions` and `EnricherOptions` while avoiding heavy orchestrator dependencies, the core library references `Microsoft.Extensions.DataIngestion` alongside Microsoft's AI, tokenization, and DI abstractions. Heavy compiler dependencies (`Microsoft.CodeAnalysis.CSharp`) are isolated in a satellite package so prose-only consumers incur zero Roslyn overhead. The official package prefix `PinkRooster.Extensions.DataIngestion.*` provides clear namespace ownership within the PinkRooster ecosystem.

### 4.1 Package Matrix

| Package Name | Target Frameworks | Primary Role | Roslyn Dependency |
| --- | --- | --- | --- |
| **PinkRooster.Extensions.DataIngestion.Chunkers** | net10.0 | Recursive chunking and contextual enrichment | None |
| **PinkRooster.Extensions.DataIngestion.CodeAnalysis** | net10.0 | C# source reader and Roslyn AST chunker | Required |

**Package Dependencies & Public Surface:**

* **PinkRooster.Extensions.DataIngestion.Chunkers:**
  * **Dependencies:** `Microsoft.Extensions.DataIngestion`, `Microsoft.Extensions.DataIngestion.Abstractions`, `Microsoft.Extensions.AI.Abstractions`, `Microsoft.ML.Tokenizers`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.DependencyInjection.Abstractions`.
  * **Public Types:** `RecursiveTokenChunker`, `RecursiveChunkerOptions`, `SeparatorPlacement`, `ContextualChunkProcessor`, `ContextualChunkerOptions`, `ContextualFailureMode`, `DataIngestionChunkerServiceCollectionExtensions`.

* **PinkRooster.Extensions.DataIngestion.CodeAnalysis:**
  * **Dependencies:** `PinkRooster.Extensions.DataIngestion.Chunkers`, `Microsoft.CodeAnalysis.CSharp`.
  * **Public Types:** `SourceCodeDocumentReader`, `RoslynCodeChunker`, `RoslynChunkerOptions`, `CodeAnalysisChunkerServiceCollectionExtensions`.

### 4.2 Repository & Solution Structure

```text
/
├── .github/workflows/          # CI build, test, benchmark smoke check, and NuGet publish
├── src/
│   ├── PinkRooster.Extensions.DataIngestion.Chunkers/
│   │   ├── DependencyInjection/# IServiceCollection extensions
│   │   ├── Recursive/          # RecursiveTokenChunker, separator logic, span token slicing
│   │   ├── Contextual/         # ContextualChunkProcessor, prompt templates, bucket windowing
│   │   └── Internal/           # ReadOnlySpan<char> helpers, table/code-block atomicity utilities
│   └── PinkRooster.Extensions.DataIngestion.CodeAnalysis/
│       └── Roslyn/             # SourceCodeDocumentReader, RoslynCodeChunker, SyntaxWalker
├── tests/
│   ├── PinkRooster.Extensions.DataIngestion.Chunkers.Tests/
│   ├── PinkRooster.Extensions.DataIngestion.CodeAnalysis.Tests/
│   └── PinkRooster.Extensions.DataIngestion.Repository.Tests/ # Public API snapshot & pack tests
├── benchmarks/
│   └── PinkRooster.Extensions.DataIngestion.Benchmarks/      # BenchmarkDotNet & Recall@K evaluation
├── samples/
│   └── PinkRooster.Extensions.DataIngestion.Samples/         # Console sample with IngestionPipeline<string>
├── Directory.Build.props       # Central SDK settings, net10.0, Nullable=enable, TreatWarningsAsErrors
├── Directory.Packages.props    # Central Package Management (CPM)
└── PinkRooster.Extensions.DataIngestion.slnx
```

## 5. Detailed Functional & Technical Specifications

### 5.1 Component 1: `RecursiveTokenChunker`

* **Base Class:** `IngestionChunker<string>`
* **Options Class:** `RecursiveChunkerOptions : IngestionChunkerOptions` (adds `Separators`, `KeepSeparator`, `ReservedHeaderTokens`, and `PreserveAtomicElements` to the base `MaxTokensPerChunk`, `OverlapTokens`, and `Tokenizer` properties).
* **Primary Method:** `IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(IngestionDocument document, CancellationToken cancellationToken = default)`
* **Core Behavior:**
1. **Effective Token Budget:** Computes `EffectiveMaxTokens = MaxTokensPerChunk - ReservedHeaderTokens` so chunks leave deterministic token headroom when paired downstream with `ContextualChunkProcessor`.
2. **Element & Structural Integrity:** Iterates through `document.Sections` and their elements while preserving section hierarchy in `chunk.Context`. Treats `IngestionDocumentTable` and fenced code block elements as atomic units unless a single element exceeds `EffectiveMaxTokens`. When an oversized table must be split across chunks, repeats the Markdown table header and delimiter rows on each continuation chunk.
3. **Separator Hierarchy & Retention:** Splits prose recursively using an ordered list of separators (default: `["\n\n", "\n", ". ", "? ", "! ", " ", ""]`). Applies configurable `SeparatorPlacement` (default `Trailing`), keeping terminal punctuation attached to the preceding sentence and avoiding false splits on common abbreviations (`e.g.`, `i.e.`, `Dr.`, `Mr.`, and numeric version strings).
4. **BPE-Aware Greedy Merging:** Uses pre-counted segment token sums as a fast sub-additive upper bound ($Count(A) + Count(B) \ge Count(A + B)$). When adding the next segment would exceed `EffectiveMaxTokens` by the heuristic sum, performs an exact `Tokenizer.CountTokens(ReadOnlySpan<char>)` check on the combined span to prevent chunk under-filling while guaranteeing `EffectiveMaxTokens` is never exceeded.
5. **Trailing Overlap & Span Slicing:** Calculates trailing overlap using `Tokenizer.GetIndexByTokenCountFromEnd(ReadOnlySpan<char>, ...)` and snaps to the nearest word or separator boundary within the budget. If a single contiguous span (such as a base64 string or minified line) exceeds `EffectiveMaxTokens`, slices directly on `ReadOnlySpan<char>` via `Tokenizer.GetIndexByTokenCount` rather than `EncodeToIds` and `Decode`, avoiding intermediate array allocations and multi-byte UTF-8 replacement character corruption.



### 5.2 Component 2: `ContextualChunkProcessor`

* **Base Class:** `IngestionChunkProcessor<string>`
* **Options Class:** `ContextualChunkerOptions : EnricherOptions` (inherits `IChatClient`, `ILoggerFactory`, and `BatchSize` from `EnricherOptions`; adds `MaxDocumentContextTokens`, `BucketOverlapTokens`, `MaxEnrichedTokens`, `MaxDegreeOfParallelism`, `FailureMode`, `ChunkFilter`, and `ConfigureCacheControl`).
* **Primary Method:** `IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(IAsyncEnumerable<IngestionChunk<string>> chunks, CancellationToken cancellationToken = default)`
* **Core Behavior:**
1. **Upstream Interoperability & Single-Document Cache:** Consumes chunks produced by any `IngestionChunker<string>` (`HeaderChunker`, `SectionChunker`, `RecursiveTokenChunker`, or `RoslynCodeChunker`). Reconstructs the parent document text from `chunk.Document` and holds it in a single-entry reference cache keyed by `chunk.Document.Identifier`.
2. **Provider Prompt Caching Alignment:** Structures `ChatMessage` payloads so the static document prefix appears first, followed by the target chunk instruction. Exposes an optional `Action<IList<ChatMessage>, ChatOptions>? ConfigureCacheControl` delegate on `ContextualChunkerOptions` so callers using Anthropic can attach explicit `cache_control` metadata while OpenAI and Azure OpenAI consumers benefit automatically from prefix caching.
3. **Stepped Bucket Windowing for Large Documents:** When a parent `IngestionDocument` exceeds `MaxDocumentContextTokens` (default 32,000 tokens), partitions the document into fixed, quantized token buckets (for example, 32,000-token windows with 2,000-token overlap) rather than per-chunk sliding windows. All chunks resolved to the same bucket share an identical static prompt prefix, preserving high prompt-cache hit rates on massive documents.
4. **Chunk-to-Bucket Resolution:** Resolves each chunk's target bucket without requiring custom chunker metadata by matching `chunk.Context` section headers first, falling back to normalized anchor-span lookup against the cached document sections.
5. **Bounded Concurrency & Resilience:** Processes chunks in bounded parallel windows (`MaxDegreeOfParallelism`, default 4) using `SemaphoreSlim` while yielding results in deterministic input order. Supports `ContextualFailureMode.FallbackToOriginalChunk` vs. `Throw`, plus a `Func<IngestionChunk<string>, bool>? ChunkFilter` predicate to bypass enrichment on short or self-explanatory chunks.
6. **Immutable Chunk Reconstruction & Token Cap:** Because `IngestionChunk<string>.Content` is read-only, instantiates a new `IngestionChunk<string>(enrichedContent, chunk.Document, chunk.Context)`, copies all existing key-value pairs from the source `chunk.Metadata` dictionary, and sets `Metadata["contextual_header"]`. If `MaxEnrichedTokens` is set, truncates the generated situational header via `Tokenizer.GetIndexByTokenCount` so the combined chunk never exceeds downstream embedding limits.



### 5.3 Component 3: `SourceCodeDocumentReader` & `RoslynCodeChunker`

* **Reader Class:** `SourceCodeDocumentReader : IngestionDocumentReader`
* Reads raw source files into a single code-preserving `IngestionDocumentSection` without invoking Markdown parsers that misinterpret `#region`, `#if`, or `#nullable` directives as document headings.


* **Chunker Base Class:** `IngestionChunker<string>`
* **Options Class:** `RoslynChunkerOptions : IngestionChunkerOptions` (adds `IncludeUsingDirectives`, `IncludeEnclosingTypeSignature`, and `StripRegionTrivia`).
* **Primary Method:** `IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(IngestionDocument document, CancellationToken cancellationToken = default)`
* **Core Behavior:**
1. **Dual Input Support:** Extracts source text directly from `SourceCodeDocumentReader` documents or selectively parses fenced `csharp` code blocks embedded inside mixed Markdown `IngestionDocument` instances.
2. **Fault-Tolerant Syntax Parsing:** Parses C# source text using `CSharpSyntaxTree.ParseText(..., cancellationToken: cancellationToken)` with zero semantic-model or compilation overhead.
3. **Comprehensive AST Node Traversal:** Walks `BaseNamespaceDeclarationSyntax` (block and file-scoped), `BaseTypeDeclarationSyntax` (classes, structs, interfaces, records, record structs, and `EnumDeclarationSyntax`), `DelegateDeclarationSyntax`, `GlobalStatementSyntax` (top-level statements), and all `MemberDeclarationSyntax` subtypes (methods, constructors, properties, indexers, events, operators, and field groups).
4. **Trivia & Preprocessor Hygiene:** Keeps leading `/// <summary>` XML documentation comments and `[Attributes]` attached to their owning declaration node. Strips unmatched `#region` and `#endregion` directives when splitting between members, and records active `#if` conditional compilation symbols in `chunk.Metadata["conditional_symbols"]`.
5. **Primary Constructor & Type Context Preservation:** Populates `IngestionChunk<string>.Context` with the fully qualified symbol breadcrumb (for example, `Namespace: Acme.Billing | Class: InvoiceCalculator(ITaxRateProvider taxProvider) : ICalculator | Member: CalculateTaxAsync`). When a type is split across multiple chunks, preserves C# 12+ primary constructor parameter lists and base-type signatures in `Context` (or as a lightweight stub wrapper) so member bodies retain dependency context.
6. **Greedy Member Merging & Statement Fallback:** Greedily merges consecutive small members within the same parent type up to `MaxTokensPerChunk` using `Tokenizer.CountTokens(ReadOnlySpan<char>)`. Delegates single methods exceeding `MaxTokensPerChunk` to an internal statement-level block splitter (`if`, `foreach`, `switch`, `try` blocks) rather than truncating syntax mid-line.



## 6. Non-Functional Requirements (NFRs)

* **Memory & Throughput:** Use `ReadOnlySpan<char>`, `Tokenizer.GetIndexByTokenCount`, and `StringBuilder` pooling in `RecursiveTokenChunker` to avoid intermediate string or token-ID array allocations on multi-megabyte documents.
* **Observability:** Accept optional `ILoggerFactory` via `EnricherOptions` / constructor injection (`Microsoft.Extensions.Logging.Abstractions`) for structured logging of chunk counts, token distributions, and LLM fallback events, and expose `System.Diagnostics.ActivitySource` spans for OpenTelemetry tracing.
* **API Ergonomics:** Support both direct constructor instantiation (matching Microsoft's `IngestionChunkerOptions` and `EnricherOptions` patterns) and optional `IServiceCollection` registration helpers (`AddRecursiveTokenChunker`, `AddContextualChunkProcessor`, `AddRoslynCodeChunker`) backed by `Microsoft.Extensions.DependencyInjection.Abstractions`. Because chat and embedding pipelines frequently use different tokenizers, `Tokenizer` instances are configured explicitly per options instance rather than resolved as a single ambient DI singleton.
* **AOT & Trimming Readiness:** Enable `<IsAotCompatible>true</IsAotCompatible>` and `<IsTrimmable>true</IsTrimmable>` on the core chunkers package, avoiding runtime reflection.

## 7. Delivery Roadmap & Milestones

| Phase | Timeline | Core Focus | Exit Gate |
| --- | --- | --- | --- |
| **Phase 1** | Week 1 | Foundation, benchmark dataset, and `RecursiveTokenChunker` | All span-slicing, table/code atomicity, and overlap tests pass |
| **Phase 2** | Week 2 | `ContextualChunkProcessor` and stepped bucket caching | Verified with mocked `IChatClient` and live provider cache test |
| **Phase 3** | Week 3 | `SourceCodeDocumentReader` and `RoslynCodeChunker` | Tested on real-world C# repos including primary constructors |
| **Phase 4** | Week 4 | Benchmarks, Recall@5 evaluation, docs, and NuGet release | Packages published with SourceLink and benchmark report |

**Milestone Deliverables:**

* **Phase 1 (Foundation, Evaluation Corpus & `RecursiveTokenChunker`):** Solution scaffolding, Central Package Management (Directory.Packages.props), CI pipeline, curation of the baseline .NET documentation and C# Q&A evaluation dataset, and full `RecursiveTokenChunker` implementation with unit tests.
* **Phase 2 (`ContextualChunkProcessor`):** `ContextualChunkProcessor` implementation, stepped bucket windowing, `ConfigureCacheControl` hook, immutable chunk metadata cloning, `SemaphoreSlim` throttling, and resilience policies.
* **Phase 3 (`SourceCodeDocumentReader` & `RoslynCodeChunker`):** Satellite project `PinkRooster.Extensions.DataIngestion.CodeAnalysis`, pass-through source reader, full Roslyn syntax walker, `#region`/`#if` trivia handling, and primary constructor context breadcrumbs.
* **Phase 4 (Benchmarks, Docs & 1.0.0 Release):** `BenchmarkDotNet` memory/throughput suite, automated Recall@5 and MRR comparison script against `DocumentTokenChunker`, console sample application, documentation, and NuGet.org publication.

## 8. Risks, Assumptions & Mitigations

| Risk Area | Impact | Likelihood | Primary Mitigation |
| --- | --- | --- | --- |
| **Upstream Preview Churn** | Medium | Medium | Pin baseline in CPM and isolate behind thin internal adapters |
| **High LLM Indexing Cost** | High | High | Stepped bucket prefix caching, `ConfigureCacheControl`, and `ChunkFilter` |
| **Post-Enrichment Overflow** | Medium | Medium | `ReservedHeaderTokens` in chunkers and `MaxEnrichedTokens` cap |
| **Large Document Overflow** | Medium | Medium | Fixed-bucket token windowing with configurable overlap |
| **Roslyn Package Weight** | Medium | Low | Isolate Roslyn in the `CodeAnalysis` satellite NuGet package |

**Risk Mitigation Details:**

* **Upstream Preview API Churn:** `Microsoft.Extensions.DataIngestion` is currently in preview. Pin a tested preview version in Directory.Packages.props, suppress `NU5104` only where verified, and ship prompt minor updates if upstream signatures shift.
* **High LLM Cost & Rate Limits in `ContextualChunkProcessor`:** Use stepped bucket windowing and the `ConfigureCacheControl` callback to achieve 70–90% prompt cache savings across Anthropic, OpenAI, and Azure OpenAI; throttle concurrency via `MaxDegreeOfParallelism`; use `ChunkFilter` to skip trivial chunks.
* **Post-Enrichment Token Budget Overflow:** Prepending context headers can push chunks over downstream embedding limits. Coordinate `RecursiveChunkerOptions.ReservedHeaderTokens` with `ContextualChunkerOptions.MaxEnrichedTokens` to guarantee hard token ceiling compliance.
* **Document Context Overflow:** Partition documents exceeding `MaxDocumentContextTokens` into fixed stepped token buckets rather than failing on model context limits or invalidating prefix caches with per-chunk sliding windows.
* **Roslyn Assembly Weight:** Keep `Microsoft.CodeAnalysis.CSharp` strictly inside the satellite package so prose ingestion pipelines remain lightweight.

## 9. Quality Assurance & Governance

* **Automated Testing (`xUnit`):**
* Deterministic token-budget assertions using shared static `TiktokenTokenizer.CreateForModel("gpt-4o")` test fixtures.
* Span-level boundary integrity tests verifying multi-byte UTF-8 safety, exact token limits under BPE merges, atomic table header repetition, and Roslyn AST trivia preservation across `#region` and `#if` blocks.
* Immutability tests verifying that `ContextualChunkProcessor` preserves all upstream `IngestionChunk<string>.Metadata` entries and respects `MaxEnrichedTokens`.
* Concurrency and cancellation tests verifying deterministic output ordering and clean `OperationCanceledException` propagation under parallel `ContextualChunkProcessor` execution.


* **Retrieval Evaluation Harness:** Include a reproducible benchmark in the `benchmarks` directory (seeded in Phase 1) comparing `DocumentTokenChunker` against `RecursiveTokenChunker`, `ContextualChunkProcessor`, and `RoslynCodeChunker` on a curated .NET documentation and C# repository corpus using Recall@5 and MRR.
* **Open-Source Governance:**
* **License:** MIT License for maximum enterprise and community adoption.
* **Versioning & Packaging:** Semantic Versioning (SemVer 2.0.0) under a reserved community NuGet prefix with deterministic builds and SourceLink enabled.
* **Contribution Workflow:** Pull request templates requiring unit tests, zero build warnings, and XML documentation for all public APIs.

## 10. Document History & Changelog

| Version | Date | Author / Agent | Summary of Changes and Key Decisions |
| --- | --- | --- | --- |
| **1.0** | 2026-11-15 | RAG Working Group | Initial baseline proposal for extended chunkers on `Microsoft.Extensions.DataIngestion`. |
| **1.1** | 2026-11-17 | Architecture Review | Added stepped bucket windowing, BPE sub-additive greedy merge, and AST trivia preservation specifications. |
| **1.2** | 2026-11-18 | Antigravity / Jan Versteeg | Standardized on PinkRooster repo standard: package prefix finalized to `PinkRooster.Extensions.DataIngestion.*`; target framework standardized to `net10.0` (C# 14); backwards compatibility policy set to `none`; adopted PinkRooster dual-repository overlay architecture (`PinkRooster.Extensions.DataIngestion` + `PinkRooster.Extensions.DataIngestion-internal`). |

