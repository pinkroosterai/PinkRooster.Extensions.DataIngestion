using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class DependencyContractTests
{
    [Fact]
    public void PreviewExposesTheDocumentAndPipelineContracts()
    {
        var section = new IngestionDocumentSection();
        var header = new IngestionDocumentHeader("## Heading") { Level = 2 };
        var table = new IngestionDocumentTable("| Name |\n| --- |\n| Value |",
            new IngestionDocumentElement?[,] { { new IngestionDocumentParagraph("Name") }, { null } });
        section.Elements.Add(header);
        section.Elements.Add(table);
        var document = new IngestionDocument("fixture");
        document.Sections.Add(section);
        var options = new IngestionChunkerOptions(TokenizerFixture.Instance);

        typeof(IngestionPipeline<string>).Should().NotBeNull();
        typeof(DocumentTokenChunker).Should().BeDerivedFrom<IngestionChunker<string>>();
        options.Tokenizer.Should().BeSameAs(TokenizerFixture.Instance);
        document.EnumerateContent().Should().Equal(header, table);
        header.Level.Should().Be(2);
        table.Cells[1, 0].Should().BeNull();
    }

    [Fact]
    public void TokenBudgetCountsAndSlicesOriginalText()
    {
        var budget = new TokenBudget(TokenizerFixture.Instance, 1);
        const string Text = "hello world";

        budget.Limit.Should().Be(1);
        budget.Count(Text).Should().Be(2);
        budget.PrefixIndex(Text, 1).Should().Be(5);
        budget.SuffixIndex(Text, 1).Should().Be(5);
    }
}
