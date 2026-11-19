using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;
using PinkRooster.Extensions.DataIngestion.CodeAnalysis;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Repository.Tests;

public sealed class RoslynCodeChunkerTests
{
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

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
        current.Metadata["conditional_symbols"].Should().BeOfType<string[]>().Which.Should().ContainSingle().Which.Should().Be("FEATURE");
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
        chunks[0].Metadata.ContainsKey("conditional_symbols").Should().BeFalse();
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
