using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinkRooster.Extensions.DataIngestion.Benchmarks;

internal static class CorpusLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static EvaluationCorpus Load(string root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sourceManifest = ReadText(Path.Combine(root, "sources.json"));
        string questionManifest = ReadText(Path.Combine(root, "questions.json"));
        string notice = ReadText(Path.Combine(root, "NOTICE.md"));
        var sources = new Dictionary<string, EvaluationSource>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SourceEntry? entry in ReadEntries<SourceEntry>(sourceManifest, "sources.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || !IsFixturePath(entry.Path))
            {
                throw new InvalidDataException("Every source needs an ID and a local path under fixtures/.");
            }

            if (!paths.Add(entry.Path) || sources.ContainsKey(entry.Id))
            {
                throw new InvalidDataException($"Duplicate source ID or path for '{entry.Id}'.");
            }

            CheckAttribution(root, notice, entry);
            string text = ReadText(Path.Combine(root, entry.Path));
            string digest = Digest(text);
            if (string.IsNullOrWhiteSpace(text) || digest != entry.Sha256)
            {
                throw new InvalidDataException($"Missing content or digest mismatch for source '{entry.Id}'.");
            }

            sources.Add(entry.Id, new EvaluationSource(entry.Id, entry.Path, text, digest));
        }

        var questions = new List<EvaluationQuestion>();
        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (QuestionEntry? entry in ReadEntries<QuestionEntry>(questionManifest, "questions.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || !questionIds.Add(entry.Id) ||
                string.IsNullOrWhiteSpace(entry.Prompt) || string.IsNullOrWhiteSpace(entry.ExpectedAnswer) ||
                string.IsNullOrWhiteSpace(entry.SourceId) || !sources.TryGetValue(entry.SourceId, out var source) ||
                entry.Evidence is not { Length: > 0 })
            {
                throw new InvalidDataException($"Invalid or duplicate question '{entry?.Id ?? "(missing ID)"}'.");
            }

            var spans = entry.Evidence.Select(span => ResolveSpan(source, entry.Id, span)).Distinct().ToArray();
            questions.Add(new EvaluationQuestion(entry.Id, entry.Prompt, spans));
        }

        return new EvaluationCorpus(sources.Values.ToArray(), questions, Digest(
            JsonSerializer.Serialize(new[] { sourceManifest, questionManifest })));
    }

    private static T[] ReadEntries<T>(string text, string name)
    {
        try
        {
            return JsonSerializer.Deserialize<T[]>(text, JsonOptions) is { Length: > 0 } entries
                ? entries
                : throw new InvalidDataException($"No entries in {name}.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Invalid JSON in {name}.", error);
        }
    }

    private static EvidenceSpan ResolveSpan(EvaluationSource source, string questionId, EvidenceEntry? span)
    {
        string[] lines = source.Text.Split('\n');
        int lineCount = source.Text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        if (span is null || span.StartLine < 1 || span.EndLine < span.StartLine || span.EndLine > lineCount ||
            string.IsNullOrWhiteSpace(span.Text))
        {
            throw new InvalidDataException($"Invalid evidence range for '{questionId}' in '{source.Id}'.");
        }

        string evidence = string.Join('\n', lines, span.StartLine - 1, span.EndLine - span.StartLine + 1);
        if (evidence != span.Text.ReplaceLineEndings("\n"))
        {
            throw new InvalidDataException($"Evidence text does not match '{questionId}' in '{source.Id}'.");
        }

        int offset = 0;
        for (int index = 0; index < span.StartLine - 1; index++)
        {
            offset += lines[index].Length + 1;
        }

        return new EvidenceSpan(source.Id, offset, evidence.Length, span.StartLine, span.EndLine);
    }

    private static void CheckAttribution(string root, string notice, SourceEntry entry)
    {
        if (!notice.Contains($"`{entry.Path}`", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Missing attribution for '{entry.Id}' in NOTICE.md.");
        }

        string licenseFile;
        if (Uri.TryCreate(entry.Origin, UriKind.Absolute, out var origin))
        {
            if (origin.Scheme != Uri.UriSchemeHttps || entry.Revision is not { Length: 40 } revision ||
                !revision.All(Uri.IsHexDigit) || !origin.AbsolutePath.Contains($"/blob/{revision}/", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(entry.Changes))
            {
                throw new InvalidDataException($"Source '{entry.Id}' must identify a pinned HTTPS origin and note changes.");
            }

            licenseFile = entry.License switch
            {
                "CC-BY-4.0" => "NOTICE.md",
                "CC-BY-4.0; MIT for code" => "LICENSE-MICROSOFT-CODE",
                _ => throw new InvalidDataException($"Unsupported seed license for '{entry.Id}'."),
            };
        }
        else
        {
            if (entry.Origin?.StartsWith("Original fixture", StringComparison.Ordinal) != true || entry.License != "MIT")
            {
                throw new InvalidDataException($"Missing origin or license for '{entry.Id}'.");
            }

            licenseFile = "LICENSE-CORPUS";
        }

        string license = ReadText(Path.Combine(root, licenseFile));
        string expected = licenseFile == "NOTICE.md" ? "creativecommons.org/licenses/by/4.0" : "MIT License";
        if (!license.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Missing license text for '{entry.Id}' in {licenseFile}.");
        }
    }

    private static bool IsFixturePath(string? path) =>
        path is not null && path.StartsWith("fixtures/", StringComparison.Ordinal) &&
        path.Split('/').All(segment => segment is not ("" or "." or "..") &&
            !segment.Contains('\\') && !segment.Contains(':') && !segment.Contains('\0'));

    private static string ReadText(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    internal static string Digest(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed record SourceEntry(string Id, string Path, string License, string Origin, string Sha256,
        string? Revision, string? Changes);

    private sealed record QuestionEntry(string Id, string SourceId, [property: JsonPropertyName("question")] string Prompt,
        string ExpectedAnswer, EvidenceEntry?[] Evidence);

    private sealed record EvidenceEntry(int StartLine, int EndLine, string Text);
}
