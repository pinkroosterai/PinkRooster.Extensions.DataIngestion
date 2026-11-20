using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;
using PinkRooster.Extensions.DataIngestion.CodeAnalysis;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.CodeAnalysis.Tests;

/// <summary>
/// The Chunkers and CodeAnalysis packages each scan Markdown code fences. One case table, run through both packages'
/// public chunkers, keeps their closing-fence rules identical.
/// </summary>
public sealed class FenceParityTests
{
    private static readonly Tokenizer Tokenizer = TokenizerFixture.Instance;

    public static TheoryData<string, bool> ClosingTails => new()
    {
        { "", true },
        { "   ", true },
        { "\t", true },
        { " ", false },
        { " ", false },
        { " x", false },
    };

    // The table sits between the candidate closing line and a final fence line: it is outside the fence only when
    // the candidate line closes it.
    private static IngestionDocument Document(string tail)
    {
        var document = new IngestionDocument("fences.md");
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(
            "```csharp\npublic record Value(int Number);\n```" + tail + "\n| A |\n| --- |\n| 1 |\n```\n"));
        document.Sections.Add(section);
        return document;
    }

    [Theory]
    [MemberData(nameof(ClosingTails))]
    public async Task BothPackagesAgreeOnWhetherAFenceLineCloses(string tail, bool closes)
    {
        var token = TestContext.Current.CancellationToken;
        var tableChunker = new MarkdownTableChunker(new TableChunkerOptions(Tokenizer) { MaxTokensPerChunk = 200 });
        bool tableOutsideFence = false;
        await foreach (var chunk in tableChunker.ProcessAsync(Document(tail), token))
        {
            tableOutsideFence |= chunk.HasMetadata && chunk.Metadata.ContainsKey(ChunkMetadataKeys.IsTablePart);
        }

        var codeChunker = new RoslynCodeChunker(new RoslynChunkerOptions(Tokenizer) { MaxTokensPerChunk = 200, OverlapTokens = 0 });
        bool tableInsideCode = false;
        await foreach (var chunk in codeChunker.ProcessAsync(Document(tail), token))
        {
            tableInsideCode |= chunk.Content.Contains("| --- |", StringComparison.Ordinal);
        }

        tableOutsideFence.Should().Be(closes);
        tableInsideCode.Should().Be(!closes);
    }
}
