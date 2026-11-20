using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;
using PinkRooster.Extensions.DataIngestion.CodeAnalysis;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.CodeAnalysis.Tests;

public sealed class RoslynCodeChunkerTests
{
    private static readonly Tokenizer Tokenizer = TokenizerFixture.Instance;

    [Fact]
    public async Task ReaderPreservesCodeAndBothChunkersConsumeTheSameDocument()
    {
        var token = TestContext.Current.CancellationToken;
        string fixture = Path.Combine(AppContext.BaseDirectory, "Evaluation", "fixtures", "DispatchCalendar.cs");
        string code = "#nullable enable\r\n" + await File.ReadAllTextAsync(fixture, token);
        var document = await ReadAsync(code, token);
        document.Sections.Should().ContainSingle().Which.Elements.Should().ContainSingle().Which.GetMarkdown()
            .Should().Be(code);
        var baseline = new DocumentTokenChunker(new IngestionChunkerOptions(Tokenizer)
        {
            MaxTokensPerChunk = 48,
            OverlapTokens = 0,
        });

        var roslyn = new RoslynCodeChunker(Options(48));
        var baselineChunks = await CollectAsync(baseline, document, token);
        var roslynChunks = await CollectAsync(roslyn, document, token);
        baselineChunks.Should().NotBeEmpty().And.OnlyContain(chunk => ReferenceEquals(chunk.Document, document));
        roslynChunks.Should().NotBeEmpty().And.OnlyContain(chunk => ReferenceEquals(chunk.Document, document));
        roslynChunks.Should().Contain(chunk => chunk.Content.Contains("return clock.Today", StringComparison.Ordinal) &&
            chunk.Context!.Contains("DispatchCalendar(IDispatchClock clock)", StringComparison.Ordinal) &&
            chunk.Context.Contains("Namespace: Evaluation.Fulfillment", StringComparison.Ordinal));
        roslynChunks.Select(chunk => Tokenizer.CountTokens(chunk.Content, considerNormalization: false))
            .Should().OnlyContain(tokens => tokens <= 48);
    }

    [Fact]
    public async Task SplitTypesKeepPrimaryConstructorsMemberTriviaAndConditionalContext()
    {
        const string source = """
            #define FEATURE
            using System;
            namespace Example.Code;
            public class Sample(int value) : Base(value)
            {
                #region Methods
                #if FEATURE
                /// <summary>Returns the current value.</summary>
                [Obsolete]
                public int Current() => value;
                public int Adjusted()
                {
                    int first = value + 12345;
                    int second = first + 67890;
                    return second + 45678;
                }
                #endif
                #endregion
            }
            """;
        var token = TestContext.Current.CancellationToken;
        var document = await ReadAsync(source, token);
        var chunker = new RoslynCodeChunker(Options(39));
        var chunks = await CollectAsync(chunker, document, token);
        var current = chunks.Should().ContainSingle(chunk => chunk.Content.Contains("public int Current()", StringComparison.Ordinal)).Which;
        current.Content.Should().Contain("/// <summary>Returns the current value.</summary>").And.Contain("[Obsolete]");
        current.Context.Should().Contain("Namespace: Example.Code").And.Contain("Sample(int value) : Base(value)")
            .And.Contain("using System;");
        current.Metadata[ChunkMetadataKeys.ConditionalSymbols].Should().BeOfType<string[]>().Which.Should().ContainSingle().Which.Should().Be("FEATURE");
        chunks.Should().OnlyContain(chunk => !chunk.Content.Contains("#region", StringComparison.Ordinal) &&
            !chunk.Content.Contains("#endregion", StringComparison.Ordinal));
        string combined = string.Concat(chunks.Select(chunk => chunk.Content));
        combined.Should().Contain("int first = value + 12345;").And.Contain("return second + 45678;");
    }

    [Fact]
    public async Task OversizedMethodsPreferStatementsAndKeepUnicodeWithinBudget()
    {
        const string source = """
            public class Letters
            {
                public void Write()
                {
                    Console.WriteLine("alpha");
                    Console.WriteLine("beta");
                    Console.WriteLine("gamma");
                    Console.WriteLine("delta");
                    Console.WriteLine("東京🍀東京🍀東京🍀東京🍀東京🍀東京🍀東京🍀東京🍀");
                }
            }
            """;
        var token = TestContext.Current.CancellationToken;
        var document = await ReadAsync(source, token);
        var chunks = await CollectAsync(new RoslynCodeChunker(Options(20)), document, token);
        chunks.Count.Should().BeGreaterThan(2);
        chunks.Select(chunk => Tokenizer.CountTokens(chunk.Content, considerNormalization: false))
            .Should().OnlyContain(tokens => tokens <= 20);
        chunks.Should().OnlyContain(chunk => !chunk.Content.Contains('\uFFFD') &&
            !char.IsHighSurrogate(chunk.Content[chunk.Content.Length - 1]) &&
            !char.IsLowSurrogate(chunk.Content[0]));
        chunks.Should().Contain(chunk => chunk.Content.Contains("Console.WriteLine(\"beta\");", StringComparison.Ordinal));
        string actual = string.Concat(chunks.Select(chunk => chunk.Content));
        string[] expected = ["alpha", "beta", "gamma", "delta", "東京🍀東京🍀東京🍀東京🍀東京🍀東京🍀東京🍀東京🍀"];
        expected.Should().OnlyContain(text => actual.Contains(text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarkdownProcessesOnlyClosedCSharpFences()
    {
        var document = new IngestionDocument("mixed.md");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph("""
            Raw prose line.
            ````javascript
            ```csharp
            fake++;
            ```
            ````
            ~~~cs
            public delegate void Work();
            ~~~
            ```csharp
            public record Value(int Number);
            ```
            ```csharp
            Incomplete();
            """));
        document.Sections.Add(section);
        var token = TestContext.Current.CancellationToken;
        var chunks = await CollectAsync(new RoslynCodeChunker(Options(48)), document, token);
        chunks.Should().HaveCount(2);
        chunks[0].Content.Should().Contain("public delegate void Work();");
        chunks[1].Context.Should().Contain("Record: Value(int Number)");
        chunks.Should().OnlyContain(chunk => !chunk.Content.Contains("fake", StringComparison.Ordinal) &&
            !chunk.Content.Contains("Incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SupportsDisabledSignatureAndRegionOptionsAndCancellation()
    {
        const string source = "class Sample(int value) {\n#region Test\nint Read() => value;\n}";
        var token = TestContext.Current.CancellationToken;
        var document = await ReadAsync(source, token);
        var options = Options(48);
        options.IncludeEnclosingTypeSignature = false;
        options.StripRegionTrivia = false;
        using var services = new ServiceCollection().AddRoslynCodeChunker(options).BuildServiceProvider();
        var chunker = services.GetRequiredService<IngestionChunker<string>>();
        var chunks = await CollectAsync(chunker, document, token);
        chunks.Should().ContainSingle().Which.Context.Should().Be("Class: Sample");
        chunks[0].Content.Should().Contain("#region Test");

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        await cancelled.CancelAsync();
        Func<Task> chunk = () => CollectAsync(chunker, document, cancelled.Token);
        await chunk.Should().ThrowAsync<OperationCanceledException>();
        typeof(RecursiveTokenChunker).Assembly.GetReferencedAssemblies().Should()
            .NotContain(assembly => assembly.Name!.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OversizedTypePartitionDoesNotEmitIsolatedClosingBraceChunks()
    {
        const string source = """
            namespace Sample;
            public class LargeClass
            {
                public void MethodOne()
                {
                    System.Console.WriteLine("one");
                }
                public void MethodTwo()
                {
                    System.Console.WriteLine("two");
                }
            }
            """;
        var token = TestContext.Current.CancellationToken;
        var document = await ReadAsync(source, token);
        var chunker = new RoslynCodeChunker(Options(15));
        var chunks = await CollectAsync(chunker, document, token);
        chunks.Should().NotBeEmpty();
        chunks.Should().OnlyContain(chunk => chunk.Content.Trim() != "}");
    }

    [Fact]
    public async Task ElseBranchDoesNotInheritActiveConditionSymbols()
    {
        var token = TestContext.Current.CancellationToken;
        var document = await ReadAsync("""
            #if UNDEFINED_FEATURE
            public class FeatureEnabled { }
            #else
            public class FeatureDisabled { }
            #endif
            """, token);
        var chunks = await CollectAsync(new RoslynCodeChunker(Options(50)), document, token);

        chunks.Should().ContainSingle();
        chunks[0].Content.Should().Contain("FeatureDisabled");
        chunks[0].Metadata.ContainsKey(ChunkMetadataKeys.ConditionalSymbols).Should().BeFalse();
    }

    [Fact]
    public async Task NestedTypeTailBracesAndCommentsAreSuppressed()
    {
        var token = TestContext.Current.CancellationToken;
        var document = await ReadAsync("""
            namespace Sample;

            public class Outer
            {
                public class Inner
                {
                    public void Run() { }
                }
            } // end of outer
            """, token);
        var chunks = await CollectAsync(new RoslynCodeChunker(Options(50)), document, token);

        chunks.Should().NotBeEmpty();
        chunks.Should().NotContain(c => c.Content.Trim() == "}" || c.Content.Trim() == "}\n}" || c.Content.Trim().StartsWith("} //"));
    }

    [Fact]
    public async Task RawCSharpEmitsAccurateLocationCoordinatesAndMetadata()
    {
        var token = TestContext.Current.CancellationToken;
        const string code = """
            namespace Sample;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(code));
        var document = await new SourceCodeDocumentReader().ReadAsync(stream, "src/Calculator.cs", "text/x-csharp", token);
        var chunker = new RoslynCodeChunker(Options(20));
        var chunks = await CollectAsync(chunker, document, token);

        chunks.Should().NotBeEmpty();
        foreach (var chunk in chunks)
        {
            chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.FilePath);
            chunk.Metadata[ChunkMetadataKeys.FilePath].Should().Be("src/Calculator.cs");
            chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.StartLine);
            chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.EndLine);
            chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.LineRange);
            chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.StartColumn);
            chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.EndColumn);

            int startLine = (int)chunk.Metadata[ChunkMetadataKeys.StartLine];
            int endLine = (int)chunk.Metadata[ChunkMetadataKeys.EndLine];
            int startCol = (int)chunk.Metadata[ChunkMetadataKeys.StartColumn];
            int endCol = (int)chunk.Metadata[ChunkMetadataKeys.EndColumn];
            string lineRange = (string)chunk.Metadata[ChunkMetadataKeys.LineRange];

            startLine.Should().BeGreaterThanOrEqualTo(1);
            endLine.Should().BeGreaterThanOrEqualTo(startLine);
            startCol.Should().BeGreaterThanOrEqualTo(1);
            endCol.Should().BeGreaterThanOrEqualTo(1);
            lineRange.Should().Be($"{startLine}-{endLine}");
        }

        var methodChunk = chunks.FirstOrDefault(c => c.Content.Contains("int Add(int a, int b)", StringComparison.Ordinal));
        methodChunk.Should().NotBeNull();
        ((int)methodChunk!.Metadata[ChunkMetadataKeys.StartLine]).Should().Be(5);
        ((int)methodChunk.Metadata[ChunkMetadataKeys.EndLine]).Should().Be(8);
        ((string)methodChunk.Metadata[ChunkMetadataKeys.LineRange]).Should().Be("5-8");
    }

    [Fact]
    public async Task MarkdownFencesEmitDocumentRelativeLineNumbers()
    {
        var token = TestContext.Current.CancellationToken;
        var document = new IngestionDocument("docs/tutorial.md");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph("""
            # Getting Started

            Here is how you declare the model:

            ```csharp
            public record User(int Id, string Name);
            ```

            And here is a helper function:

            ```csharp
            public static class Extensions
            {
                public static bool IsAdmin(this User user) => user.Id == 1;
            }
            ```
            """));
        document.Sections.Add(section);
        var chunker = new RoslynCodeChunker(Options(100));
        var chunks = await CollectAsync(chunker, document, token);

        chunks.Should().HaveCount(2);

        var firstChunk = chunks[0];
        firstChunk.Metadata[ChunkMetadataKeys.FilePath].Should().Be("docs/tutorial.md");
        ((int)firstChunk.Metadata[ChunkMetadataKeys.StartLine]).Should().Be(6);
        ((int)firstChunk.Metadata[ChunkMetadataKeys.EndLine]).Should().Be(6);
        ((string)firstChunk.Metadata[ChunkMetadataKeys.LineRange]).Should().Be("6-6");

        var secondChunk = chunks[1];
        secondChunk.Metadata[ChunkMetadataKeys.FilePath].Should().Be("docs/tutorial.md");
        ((int)secondChunk.Metadata[ChunkMetadataKeys.StartLine]).Should().Be(12);
        ((int)secondChunk.Metadata[ChunkMetadataKeys.EndLine]).Should().Be(15);
        ((string)secondChunk.Metadata[ChunkMetadataKeys.LineRange]).Should().Be("12-15");
    }

    [Fact]
    public async Task OverlappingAndSplitMembersPreserveAccurateLineSpans()
    {
        var token = TestContext.Current.CancellationToken;
        const string code = """
            public class HeavyService
            {
                public void LongMethod()
                {
                    int step1 = 1 + 1;
                    int step2 = 2 + 2;
                    int step3 = 3 + 3;
                    int step4 = 4 + 4;
                    int step5 = 5 + 5;
                    int step6 = 6 + 6;
                    int step7 = 7 + 7;
                    int step8 = 8 + 8;
                }
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(code));
        var document = await new SourceCodeDocumentReader().ReadAsync(stream, "Service.cs", "text/x-csharp", token);
        var chunker = new RoslynCodeChunker(new RoslynChunkerOptions(Tokenizer)
        {
            MaxTokensPerChunk = 20,
            OverlapTokens = 5,
        });
        var chunks = await CollectAsync(chunker, document, token);

        chunks.Should().HaveCountGreaterThan(1);
        int lastStart = 0;
        foreach (var chunk in chunks)
        {
            int start = (int)chunk.Metadata[ChunkMetadataKeys.StartLine];
            int end = (int)chunk.Metadata[ChunkMetadataKeys.EndLine];
            start.Should().BeGreaterThanOrEqualTo(lastStart);
            end.Should().BeGreaterThanOrEqualTo(start);
            chunk.Metadata[ChunkMetadataKeys.LineRange].Should().Be($"{start}-{end}");
            lastStart = start;
        }
    }

    [Fact]
    public async Task MissingOrEmptyDocumentIdentifierOmitsFilePathKey()
    {
        var token = TestContext.Current.CancellationToken;
        const string code = "public record Point(int X, int Y);";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(code));
        var document = await new SourceCodeDocumentReader().ReadAsync(stream, "   ", "text/x-csharp", token);

        var chunker = new RoslynCodeChunker(Options(50));
        var chunks = await CollectAsync(chunker, document, token);

        chunks.Should().ContainSingle();
        var chunk = chunks[0];
        chunk.Metadata.Should().NotContainKey(ChunkMetadataKeys.FilePath);
        chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.StartLine);
        chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.EndLine);
        chunk.Metadata.Should().ContainKey(ChunkMetadataKeys.LineRange);
        ((int)chunk.Metadata[ChunkMetadataKeys.StartLine]).Should().Be(1);
        ((int)chunk.Metadata[ChunkMetadataKeys.EndLine]).Should().Be(1);
    }

    [Fact]
    public async Task RegionStrippedSourceMaintainsDiskAlignedLineCoordinates()
    {
        var token = TestContext.Current.CancellationToken;
        const string code = """
            namespace Sample;

            public class Example
            {
                #region UnpairedHeader
                public int Value1 => 1;
                public int Value2 => 2;
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(code));
        var document = await new SourceCodeDocumentReader().ReadAsync(stream, "Example.cs", "text/x-csharp", token);
        var chunker = new RoslynCodeChunker(Options(12));
        var chunks = await CollectAsync(chunker, document, token);

        var value2Chunk = chunks.FirstOrDefault(c => c.Content.Contains("Value2", StringComparison.Ordinal));
        value2Chunk.Should().NotBeNull();
        // In the original file on disk, Value2 is on line 7 even though #region on line 5 was stripped
        ((int)value2Chunk!.Metadata[ChunkMetadataKeys.StartLine]).Should().Be(7);
        ((int)value2Chunk.Metadata[ChunkMetadataKeys.EndLine]).Should().Be(7);
        ((string)value2Chunk.Metadata[ChunkMetadataKeys.LineRange]).Should().Be("7-7");
    }

    private static RoslynChunkerOptions Options(int limit) =>
        new(Tokenizer) { MaxTokensPerChunk = limit, OverlapTokens = 0, IncludeUsingDirectives = true };

    private static async Task<IngestionDocument> ReadAsync(string code, CancellationToken token)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(code));
        var document = await new SourceCodeDocumentReader().ReadAsync(stream, "Fixture.cs", "text/x-csharp", token);
        stream.CanRead.Should().BeTrue();
        return document;
    }

    private static async Task<List<IngestionChunk<string>>> CollectAsync(IngestionChunker<string> chunker,
        IngestionDocument document, CancellationToken token)
    {
        var chunks = new List<IngestionChunk<string>>();
        await foreach (var chunk in chunker.ProcessAsync(document, token))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }
}
