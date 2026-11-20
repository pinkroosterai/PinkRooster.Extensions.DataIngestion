using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.CodeAnalysis.Tests;

public sealed class RoslynOptionsTests
{
    private static readonly Tokenizer Tokenizer = TokenizerFixture.Instance;

    private static async Task<List<IngestionChunk<string>>> ChunkAsync(RoslynChunkerOptions options, string code)
    {
        var token = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(code));
        var document = await new SourceCodeDocumentReader().ReadAsync(stream, "Fixture.cs", "text/x-csharp", token);
        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in new RoslynCodeChunker(options).ProcessAsync(document, token))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    [Fact]
    public async Task OverlapKeepsEveryChunkWithinBudgetAndAlwaysAdvances()
    {
        const int Limit = 60;
        string statements = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"        int value{i} = {i} * 31;"));
        string code = $"public class Large\n{{\n    public int Run()\n    {{\n{statements}\n        return 0;\n    }}\n}}\n";

        var chunks = await ChunkAsync(new RoslynChunkerOptions(Tokenizer) { MaxTokensPerChunk = Limit, OverlapTokens = 15 }, code);

        chunks.Count.Should().BeGreaterThan(2);
        chunks.Should().AllSatisfy(c => Tokenizer.CountTokens(c.Content, considerNormalization: false).Should().BeLessThanOrEqualTo(Limit));
        for (int i = 1; i <= 40; i++)
        {
            chunks.Should().Contain(c => c.Content.Contains($"int value{i} =", StringComparison.Ordinal));
        }

        int[] firstStatement = [.. chunks.Select(c => Enumerable.Range(1, 40)
            .FirstOrDefault(i => c.Content.Contains($"int value{i} =", StringComparison.Ordinal)))
            .Where(i => i > 0)];
        firstStatement.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        chunks.Zip(chunks.Skip(1)).Any(pair => SharesALine(pair.First.Content, pair.Second.Content))
            .Should().BeTrue("overlap must repeat some text of the previous chunk");

        static bool SharesALine(string previous, string next) =>
            previous.Split('\n').Any(line => line.Trim().Length > 0 && next.Contains(line.Trim(), StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidBudgetsAreRejected()
    {
        Action overlapAtLimit = () => _ = new RoslynCodeChunker(new RoslynChunkerOptions(Tokenizer) { MaxTokensPerChunk = 10, OverlapTokens = 10 });
        Action negativeOverlap = () => _ = new RoslynCodeChunker(new RoslynChunkerOptions(Tokenizer) { MaxTokensPerChunk = 10, OverlapTokens = -1 });

        overlapAtLimit.Should().Throw<ArgumentException>();
        negativeOverlap.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task UsingDirectivesAreLeftOutOfTheContextByDefault()
    {
        const string Code = "using System.Text;\nnamespace Sample;\npublic class Plain { public int Run() => 1; }\n";
        var defaults = new RoslynChunkerOptions(Tokenizer) { MaxTokensPerChunk = 200, OverlapTokens = 0 };
        defaults.IncludeUsingDirectives.Should().BeFalse();

        var withoutUsings = await ChunkAsync(defaults, Code);
        var withUsings = await ChunkAsync(new RoslynChunkerOptions(Tokenizer) { MaxTokensPerChunk = 200, OverlapTokens = 0, IncludeUsingDirectives = true }, Code);

        withoutUsings.Should().NotBeEmpty().And.OnlyContain(c => c.Context == null || !c.Context.Contains("using System.Text;", StringComparison.Ordinal));
        withUsings.Should().Contain(c => c.Context != null && c.Context.Contains("using System.Text;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ElifBranchRecordsOnlyItsOwnSymbol()
    {
        const string Code = "#define SECOND\n#if FIRST\npublic class A { }\n#elif SECOND\npublic class B { }\n#endif\n";

        var chunks = await ChunkAsync(new RoslynChunkerOptions(Tokenizer) { MaxTokensPerChunk = 200, OverlapTokens = 0 }, Code);

        var b = chunks.Should().ContainSingle(c => c.Content.Contains("class B", StringComparison.Ordinal)).Which;
        b.Metadata[ChunkMetadataKeys.ConditionalSymbols].Should().BeOfType<string[]>().Which.Should().Equal("SECOND");
    }

    [Fact]
    public void RegistrationsRejectNullArgumentsAndResolveFromAFactory()
    {
        var options = new RoslynChunkerOptions(Tokenizer) { MaxTokensPerChunk = 100, OverlapTokens = 0 };
        Action nullServices = () => CodeAnalysisChunkerServiceCollectionExtensions.AddRoslynCodeChunker(null!, options);
        Action nullOptions = () => new ServiceCollection().AddRoslynCodeChunker((RoslynChunkerOptions)null!);
        Action nullFactory = () => new ServiceCollection().AddRoslynCodeChunker((Func<IServiceProvider, RoslynChunkerOptions>)null!);
        nullServices.Should().Throw<ArgumentNullException>();
        nullOptions.Should().Throw<ArgumentNullException>();
        nullFactory.Should().Throw<ArgumentNullException>();

        using var provider = new ServiceCollection().AddSingleton(Tokenizer)
            .AddRoslynCodeChunker(sp => new RoslynChunkerOptions(sp.GetRequiredService<Tokenizer>()) { MaxTokensPerChunk = 100, OverlapTokens = 0 })
            .BuildServiceProvider();

        provider.GetRequiredService<IngestionChunker<string>>().Should().BeOfType<RoslynCodeChunker>();
    }
}
