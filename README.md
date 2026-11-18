# PinkRooster.Extensions.DataIngestion

Structure-aware text chunking for [Microsoft.Extensions.DataIngestion](https://www.nuget.org/packages/Microsoft.Extensions.DataIngestion) on .NET 10. Use `RecursiveTokenChunker` in an ingestion pipeline to split technical documents into ordered, token-bounded chunks while preserving heading context, tables, and fenced code where they fit.

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

## Build and test

Use the .NET 10 SDK specified in [`global.json`](global.json). From the repository root:

```bash
dotnet build PinkRooster.Extensions.DataIngestion.slnx
dotnet test --solution PinkRooster.Extensions.DataIngestion.slnx
```

The tests cover token limits, Unicode boundaries, structure preservation, cancellation, pipeline integration, and the public API. The [CI workflow](.github/workflows/ci.yml) enforces 90% core line coverage. The [offline evaluation fixtures](benchmarks/evaluation/NOTICE.md) include attributed .NET documentation excerpts and C# questions with source evidence.

See the [project charter](docs/PID.md) for the roadmap.
