# PinkRooster.Extensions.DataIngestion

Text chunking and contextual enrichment for .NET 10 ingestion pipelines. [Microsoft.Extensions.DataIngestion](https://www.nuget.org/packages/Microsoft.Extensions.DataIngestion)

- `RecursiveTokenChunker` splits technical documents into ordered, token-bounded chunks and preserves heading context, tables, and fenced code where they fit.
- `ContextualChunkProcessor` uses an `IChatClient` to add a short document-specific header before embedding.

## Use from source

From the repository root, add a project reference and the vocabulary used by the example. Replace `<app.csproj>` with the path to your .NET 10 application project. If your app uses Central Package Management, declare the vocabulary version in its `Directory.Packages.props`.

```bash
dotnet add <app.csproj> reference src/PinkRooster.Extensions.DataIngestion.Chunkers/PinkRooster.Extensions.DataIngestion.Chunkers.csproj
dotnet add <app.csproj> package Microsoft.ML.Tokenizers.Data.O200kBase --version 3.0.0
```

```csharp
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;

var options = new RecursiveChunkerOptions(TiktokenTokenizer.CreateForModel("gpt-4o"))
{
    MaxTokensPerChunk = 16,
    ReservedHeaderTokens = 4,
    OverlapTokens = 0,
};

var document = new IngestionDocument("dispatch-guide");
var section = new IngestionDocumentSection();
section.Elements.Add(new IngestionDocumentHeader("# Dispatch") { Level = 1 });
section.Elements.Add(new IngestionDocumentParagraph(
    "Standard orders ship within two business days. Express orders ship the same day."));
document.Sections.Add(section);

var chunker = new RecursiveTokenChunker(options);
await foreach (var chunk in chunker.ProcessAsync(document))
{
    Console.WriteLine(chunk.Context);
    Console.WriteLine(chunk.Content);
}
```

Pass the chunker as the `IngestionChunker<string>` in an `IngestionPipeline<string>` when you have a reader and writer. To register it with dependency injection, call `services.AddRecursiveTokenChunker(options)` on your `IServiceCollection`. The extension registers a singleton as `IngestionChunker<string>`.

## Chunking behavior

- The effective content limit is `MaxTokensPerChunk - ReservedHeaderTokens`. It includes overlap and any repeated markup. The heading breadcrumb lives separately in `chunk.Context`.
- The default separator order is paragraph breaks, line breaks, sentence punctuation, spaces, then token-based source slices. Set `Separators` to change the order and `KeepSeparator` to retain separators on the leading or trailing fragment or omit them.
- `OverlapTokens` limits repeated prose between adjacent chunks. Overlap shrinks to make room for new text and resets at heading, section, and protected-structure boundaries. Its value must be less than the effective content limit.
- `PreserveAtomicElements` defaults to `true`. Fitting structured table elements and explicitly closed fences within a single element stay intact. Oversized tables split by row with repeated headers, and code fences retain their wrappers when possible. If the structure cannot be preserved within the limit, the chunker falls back to lossless text slices. It throws `InvalidOperationException` if the text cannot fit in nonblank chunks.

## Contextual enrichment

Add a processor to your ingestion pipeline with the chat client and tokenizers for your selected models. This example caps final embedding content at 512 tokens, including the generated header and separator.

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;

static void AddContextualEnrichment(
    IngestionPipeline<string> pipeline,
    IChatClient chatClient,
    Tokenizer chatTokenizer,
    Tokenizer embeddingTokenizer)
{
    var options = new ContextualChunkerOptions(chatClient, chatTokenizer)
    {
        EmbeddingTokenizer = embeddingTokenizer,
        MaxEnrichedTokens = 512,
    };

    pipeline.ChunkProcessors.Add(new ContextualChunkProcessor(options));
}
```

For DI, register your options with `services.AddContextualChunkProcessor(options)`. Resolve `IngestionChunkProcessor<string>` and add it to the pipeline's `ChunkProcessors` collection.

- **Document context.** Large documents use fixed windows. `MaxDocumentContextTokens` defaults to 32,000 source tokens and `BucketOverlapTokens` to 2,000. Leave room in the chat model's context window for instructions, the target chunk, and the response.
- **Request ordering.** `MaxDegreeOfParallelism` defaults to four concurrent requests per enumeration. Results are yielded in input order. `BatchSize` limits how many chunks can be admitted before the consumer advances.
- **Output and failures.** Enriched chunks preserve the body, document, context, and existing metadata, and record the new header under `contextual_header`. The processor shortens or omits the header to meet the output cap. An original chunk that already exceeds the cap raises an error. For a chunk within the cap, the default failure mode returns the original if enrichment fails. Use `ContextualFailureMode.Throw` to propagate the failure or `ChunkFilter` to bypass selected chunks.
- **Prompt caching.** The selected document window and target chunk go to the configured client. Chunks in the same window share a stable request prefix. Use `ConfigureCacheControl` for provider-specific settings. The callback may run concurrently.

## C# source chunking

Use the `PinkRooster.Extensions.DataIngestion.CodeAnalysis` package for raw C# files or documents with fenced C# blocks. The reader preserves raw source and directives. The chunker uses Roslyn's syntax tree to group declarations and split oversized members at statement boundaries when the token budget allows.

```csharp
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.CodeAnalysis;

var reader = new SourceCodeDocumentReader();
var options = new RoslynChunkerOptions(TiktokenTokenizer.CreateForModel("gpt-4o"))
{
    MaxTokensPerChunk = 256,
    OverlapTokens = 0,
};
var chunker = new RoslynCodeChunker(options);
var document = await reader.ReadAsync(new FileInfo("Example.cs"));
await foreach (var chunk in chunker.ProcessAsync(document))
{
    Console.WriteLine(chunk.Context);
    Console.WriteLine(chunk.Content);
}
```

The chunk context carries the enclosing namespace, type signature and member names. Set `IncludeUsingDirectives` to include using directives in that context. `StripRegionTrivia` drops region directives whose partner is outside the chunk. For dependency injection, use `services.AddRoslynCodeChunker(options)`.

## Build and test

Use the .NET 10 SDK specified in [`global.json`](global.json). From the repository root:

```bash
dotnet build PinkRooster.Extensions.DataIngestion.slnx
dotnet test --solution PinkRooster.Extensions.DataIngestion.slnx
```

The tests cover token limits, Unicode boundaries, structure preservation, cancellation, pipeline integration, and the public API. The [CI workflow](.github/workflows/ci.yml) enforces 90% core line coverage. The [offline evaluation fixtures](benchmarks/evaluation/NOTICE.md) include attributed .NET documentation excerpts and C# questions with source evidence.

See the [project charter](docs/PID.md) for the roadmap.

### Live prompt cache check

The default test suite skips the live check. The opt-in check sends synthetic text to OpenAI and makes up to four billable requests. Set `OPENAI_API_KEY` in your local environment before running this command from the repository root.

```bash
PINKROOSTER_RUN_LIVE_CACHE_TEST=1 PINKROOSTER_LIVE_CACHE_MODEL=gpt-4.1-mini \
  dotnet test --project \
  tests/PinkRooster.Extensions.DataIngestion.Chunkers.Tests/PinkRooster.Extensions.DataIngestion.Chunkers.Tests.csproj \
  --output Detailed -- --show-live-output on \
  --filter-method '*ContextualLiveCacheTests.LiveProviderReportsACacheReadForASharedPrefix'
```

The test passes when a follow-up request with the same prefix reports a positive cached-token count. In the verified `gpt-4.1-mini` run, the warm-up reported 0 cached tokens and the follow-up reported 3,200.
