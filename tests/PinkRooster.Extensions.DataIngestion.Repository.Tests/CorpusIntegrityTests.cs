using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Repository.Tests;

public sealed class CorpusIntegrityTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "Evaluation");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void SourcesAreLocalPinnedAndAttributed()
    {
        var sources = Read<Source[]>("sources.json");
        sources.Should().NotBeEmpty().And.OnlyHaveUniqueItems(source => source.Id)
            .And.OnlyHaveUniqueItems(source => source.Path);
        var notice = File.ReadAllText(Path.Combine(Corpus, "NOTICE.md"));

        foreach (var source in sources)
        {
            source.Id.Should().NotBeNullOrWhiteSpace();
            source.Path.Should().StartWith("fixtures/");
            string path = ResolveFixture(source.Path);
            string text = File.ReadAllText(path).ReplaceLineEndings("\n");
            string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            digest.Should().Be(source.Sha256, "the checkout must contain the fixture named by {0}", source.Id);
            notice.Should().Contain(source.Path);

            if (Uri.TryCreate(source.Origin, UriKind.Absolute, out Uri? origin))
            {
                origin.Scheme.Should().Be("https");
                source.Revision.Should().MatchRegex("^[a-f0-9]{40}$");
                origin.AbsolutePath.Should().Contain($"/blob/{source.Revision}/");
                origin.Fragment.Should().MatchRegex("^#L[0-9]+-L[0-9]+$");
                source.Changes.Should().NotBeNullOrWhiteSpace();
                source.License.Should().Contain("CC-BY-4.0");
                if (source.License.Contains("MIT", StringComparison.Ordinal))
                {
                    File.ReadAllText(Path.Combine(Corpus, "LICENSE-MICROSOFT-CODE"))
                        .Should().Contain("Copyright (c) Microsoft Corporation");
                }
            }
            else
            {
                source.Origin.Should().StartWith("Original fixture");
                source.License.Should().Be("MIT");
                File.ReadAllText(Path.Combine(Corpus, "LICENSE-CORPUS")).Should().Contain("The MIT License");
            }
        }

        File.ReadAllText(ResolveFixture("fixtures/date-format-table.md"))
            .Should().Contain("| Format specifier | Description | Examples |");
        File.ReadAllText(ResolveFixture("fixtures/csharp-identifier-fences.md"))
            .Should().Contain("### Pascal case").And.Contain("```csharp");
    }

    [Fact]
    public void QuestionsHaveUniqueIdsAndResolveExactEvidenceOffline()
    {
        var sources = Read<Source[]>("sources.json").ToDictionary(source => source.Id, StringComparer.Ordinal);
        var questions = Read<Question[]>("questions.json");
        questions.Should().NotBeEmpty().And.OnlyHaveUniqueItems(question => question.Id);

        foreach (var question in questions)
        {
            question.Id.Should().NotBeNullOrWhiteSpace();
            question.Prompt.Should().NotBeNullOrWhiteSpace();
            question.ExpectedAnswer.Should().NotBeNullOrWhiteSpace();
            question.Evidence.Should().NotBeEmpty();
            sources.Should().ContainKey(question.SourceId);
            string[] body = File.ReadAllLines(ResolveFixture(sources[question.SourceId].Path));

            foreach (var span in question.Evidence)
            {
                span.StartLine.Should().BeInRange(1, body.Length);
                span.EndLine.Should().BeInRange(span.StartLine, body.Length);
                string.Join('\n', body[(span.StartLine - 1)..span.EndLine]).Should().Be(span.Text);
            }
        }
    }

    private static string ResolveFixture(string path)
    {
        string fixtures = Path.GetFullPath(Path.Combine(Corpus, "fixtures")) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(Corpus, path));
        fullPath.Should().StartWith(fixtures);
        return fullPath;
    }

    private static T Read<T>(string filename) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Corpus, filename)), JsonOptions)
        ?? throw new InvalidDataException(filename);

    private sealed record Source(string Id, string Path, string License, string Origin, string Sha256,
        string? Revision, string? Changes);

    private sealed record Question(string Id, string SourceId,
        [property: System.Text.Json.Serialization.JsonPropertyName("question")] string Prompt,
        string ExpectedAnswer, Span[] Evidence);

    private sealed record Span(int StartLine, int EndLine, string Text);
}
