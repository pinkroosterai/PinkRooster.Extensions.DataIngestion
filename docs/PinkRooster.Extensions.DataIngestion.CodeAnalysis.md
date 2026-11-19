![PinkRooster logo](https://raw.githubusercontent.com/pinkroosterai/PinkRooster/5b6d303a7ab57cf6374b210eaf0e36ca9c2ef403/assets/logo-120.png)

# PinkRooster.Extensions.DataIngestion.CodeAnalysis

Roslyn-based C# source code document reader and syntax-aware AST chunker for `Microsoft.Extensions.DataIngestion`.

Status: release, developed against the .NET 10 SDK and Roslyn `Microsoft.CodeAnalysis.CSharp`.

```text
dotnet add package PinkRooster.Extensions.DataIngestion.CodeAnalysis --prerelease
```

## Overview

`PinkRooster.Extensions.DataIngestion.CodeAnalysis` brings syntax-aware source code intelligence to Microsoft RAG ingestion pipelines:

- **`SourceCodeDocumentReader`**: Ingestion document reader for C# files (`.cs`), extracting root type hierarchies, documentation comments, and code structures into an `IngestionDocument`.
- **`RoslynCodeChunker`**: Syntax-aware code chunker leveraging the Roslyn Abstract Syntax Tree (AST). It splits methods, properties, records, and types on natural grammatical boundaries rather than arbitrary line counts, while embedding symbol breadcrumbs (`Namespace.Class.Method`) into `chunk.Context` for accurate retrieval matching.

## Quickstart

```csharp
using System.Text;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.CodeAnalysis;

var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

var reader = new SourceCodeDocumentReader();
const string sourceCode = """
    namespace MyApp.Services;

    public sealed class OrderProcessor
    {
        public bool ProcessOrder(int orderId) => orderId > 0;
    }
    """;

using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sourceCode));
IngestionDocument doc = await reader.ReadAsync(stream, "OrderProcessor.cs", "text/x-csharp");

var chunkerOptions = new RoslynChunkerOptions(tokenizer)
{
    MaxTokensPerChunk = 128,
    IncludeEnclosingTypeSignature = true,
};

var chunker = new RoslynCodeChunker(chunkerOptions);

await foreach (IngestionChunk<string> chunk in chunker.ProcessAsync(doc))
{
    Console.WriteLine($"Symbol: {chunk.Context}");
    Console.WriteLine($"Content:\n{chunk.Content}");
}
```

## Features

- **Grammar-Aware Splitting**: Preserves syntax node integrity (method signatures, type declarations, attributes, and doc comments).
- **Symbol Breadcrumbs**: Automatically annotates each chunk with its fully-qualified member context (`chunk.Context`).
- **Enclosing Type Context**: Optionally retains enclosing type declaration signatures when sub-chunking oversized method bodies.

## Compatibility

- Target Framework: .NET 10 (`net10.0`)
- Dependencies: `Microsoft.CodeAnalysis.CSharp`, `PinkRooster.Extensions.DataIngestion.Chunkers`
- Repository: [https://github.com/pinkroosterai/PinkRooster.Extensions.DataIngestion](https://github.com/pinkroosterai/PinkRooster.Extensions.DataIngestion)
