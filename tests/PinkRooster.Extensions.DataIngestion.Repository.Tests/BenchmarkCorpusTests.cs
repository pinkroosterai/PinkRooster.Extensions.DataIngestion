using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Benchmarks;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Repository.Tests;

public sealed class BenchmarkCorpusTests
{
    private static readonly string Seed = Path.Combine(AppContext.BaseDirectory, "Evaluation");
    private static readonly Tokenizer Tokenizer = TokenizerFixture.Instance;

    [Fact]
    public async Task SeedRunsThroughBothChunkersUsingTheSameDocuments()
    {
        var token = TestContext.Current.CancellationToken;
        var corpus = CorpusLoader.Load(Seed, token);
        corpus.Sources.Should().HaveCount(3);
        corpus.Questions.Should().HaveCount(2);
        corpus.Fingerprint.Should().Be(CorpusLoader.Load(Seed, token).Fingerprint);

        foreach (var source in corpus.Sources)
        {
            var document = SmokeChunking.Read(source);
            foreach (var chunker in new IngestionChunker<string>[]
                     { SmokeChunking.Baseline(Tokenizer, 64), SmokeChunking.Recursive(Tokenizer, 64) })
            {
                var chunks = await SmokeChunking.ChunkAsync(source, document, chunker, token);
                chunks.Should().NotBeEmpty().And.OnlyContain(chunk => chunk.SourceId == source.Id);
                chunks.Should().Contain(chunk => chunk.Source.HasValue);
                foreach (var chunk in chunks.Where(chunk => chunk.Source.HasValue))
                {
                    var slice = chunk.Source.GetValueOrDefault();
                    chunk.Payload.Substring(slice.PayloadOffset, slice.Length).Should()
                        .Be(source.Text.Substring(slice.SourceOffset, slice.Length));
                }
            }
        }
    }

    [Fact]
    public void DraftReferenceSuitesResolveWithoutRunningRetrieval()
    {
        var token = TestContext.Current.CancellationToken;
        var prose = CorpusLoader.Load(Path.Combine(Seed, "reference", "prose"), token);
        var csharp = CorpusLoader.Load(Path.Combine(Seed, "reference", "csharp"), token);
        prose.Sources.Should().HaveCount(3);
        csharp.Sources.Should().HaveCount(4);
        prose.Questions.Should().HaveCount(8);
        csharp.Questions.Should().HaveCount(8);
        prose.Questions.Should().NotContain(question => question.Evidence.Any(span => span.SourceId == "enum-format"));
        csharp.Questions.Should().NotContain(question =>
            question.Evidence.Any(span => span.SourceId == "runtime-time-provider"));
    }

    [Fact]
    public void OnlyVerifiedBodyTextMapsBackToItsOwnSource()
    {
        const string text = "repeat\n| column | value |\nrepeat\nfirst line\nsecond line";
        var source = new EvaluationSource("one", "fixtures/one.md", text, CorpusLoader.Digest(text));
        const string transformed = "first line\n| column | value |\nsynthetic delimiter";
        const string rawRow = "| column | value |";
        int bodyStart = transformed.IndexOf(rawRow, StringComparison.Ordinal);
        SourceAttribution.Match(source, transformed, 0, transformed.Length).Should().BeNull();
        SourceAttribution.Match(source, transformed, bodyStart, rawRow.Length).Should()
            .Be(new SourceSlice(bodyStart, text.IndexOf(rawRow, StringComparison.Ordinal), rawRow.Length));
        SourceAttribution.Match(source, "repeat", 0, 6).Should().BeNull();
        SourceAttribution.Match(source, "first line\n", 0, 11).Should()
            .Be(new SourceSlice(0, text.IndexOf("first line", StringComparison.Ordinal), 11));
        SourceAttribution.Match(source, "second line", 0, 11).Should()
            .Be(new SourceSlice(0, text.IndexOf("second line", StringComparison.Ordinal), 11));
        var other = source with { Id = "two", Text = "different" };
        SourceAttribution.Match(other, rawRow, 0, rawRow.Length).Should().BeNull();
    }

    [Theory]
    [InlineData("sources.json", "6aa3420bfe2a7fb4df3d0556abae9043f430618f51d6dcb48005e69468b7994f", "wrong", "digest")]
    [InlineData("sources.json", "fixtures/date-format-table.md", "fixtures/../date-format-table.md", "local path")]
    [InlineData("sources.json", "dotnet-csharp-identifier-fences", "dotnet-date-format-table", "Duplicate source")]
    [InlineData("questions.json", "dispatch-standard-delay", "dispatch-calendar-clock", "duplicate question")]
    [InlineData("questions.json", "\"evidence\": [", "\"unused\": [", "question")]
    [InlineData("questions.json", "\"endLine\": 6", "\"endLine\": 999", "Invalid evidence range")]
    [InlineData("questions.json", "DateOnly Today", "DateOnly Tomorrow", "Evidence text does not match")]
    [InlineData("questions.json", "authored-dispatch-calendar", "dotnet-date-format-table", "Evidence text does not match")]
    [InlineData("NOTICE.md", "fixtures/DispatchCalendar.cs", "no-attribution", "Missing attribution")]
    public void MalformedSeedInputsAreRejected(string path, string before, string after, string message)
    {
        using var fixture = new SeedCopy();
        string file = Path.Combine(fixture.Root, path);
        string original = File.ReadAllText(file);
        original.Should().Contain(before);
        File.WriteAllText(file, original.Replace(before, after, StringComparison.Ordinal));
        var token = TestContext.Current.CancellationToken;
        Action load = () => CorpusLoader.Load(fixture.Root, token);
        load.Should().Throw<InvalidDataException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void LineEndingChangesPreserveTheDigestAndEvidenceOffsets()
    {
        using var fixture = new SeedCopy();
        string path = Path.Combine(fixture.Root, "fixtures", "DispatchCalendar.cs");
        File.WriteAllText(path, File.ReadAllText(path).ReplaceLineEndings("\r\n"));
        var token = TestContext.Current.CancellationToken;
        var actual = CorpusLoader.Load(fixture.Root, token);
        var original = CorpusLoader.Load(Seed, token);
        actual.Fingerprint.Should().Be(original.Fingerprint);
        actual.Sources.Should().BeEquivalentTo(original.Sources);
        actual.Questions.Should().BeEquivalentTo(original.Questions);
    }

    private sealed class SeedCopy : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("retrieval-corpus-");

        internal SeedCopy()
        {
            foreach (string file in Directory.EnumerateFiles(Seed, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(Root, Path.GetRelativePath(Seed, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        internal string Root => _directory.FullName;

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
