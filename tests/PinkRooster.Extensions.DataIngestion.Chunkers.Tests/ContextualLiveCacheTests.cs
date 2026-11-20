using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Xunit;
using static PinkRooster.Extensions.DataIngestion.Chunkers.Tests.ContextualChunkProcessorTests;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class ContextualLiveCacheTests(ITestOutputHelper output)
{
    public static bool LiveCacheEnabled => Environment.GetEnvironmentVariable("PINKROOSTER_RUN_LIVE_CACHE_TEST") == "1";

    [Fact(Skip = "The provider test is opt-in.", SkipUnless = nameof(LiveCacheEnabled))]
    public async Task LiveProviderReportsACacheReadForASharedPrefix()
    {
        string key = RequiredSetting("OPENAI_API_KEY");
        string model = RequiredSetting("PINKROOSTER_LIVE_CACHE_MODEL");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        List<long?> observations = [];
        using var client = new ContextualChatClient(async (messages, _, token) =>
        {
            var payload = new
            {
                model,
                messages = messages.Select(message => new { role = message.Role.Value, content = message.Text }).ToArray(),
                max_completion_tokens = 128,
            };
            using var response = await http.PostAsJsonAsync("https://api.openai.com/v1/chat/completions", payload, token);
            response.EnsureSuccessStatusCode();
            using var body = await response.Content.ReadAsStreamAsync(token);
            using var data = await JsonDocument.ParseAsync(body, cancellationToken: token);
            ChatResponse result = ReadResponse(data.RootElement);
            observations.Add(result.Usage?.CachedInputTokenCount);
            return result;
        });
        var options = Options(client);
        options.MaxDegreeOfParallelism = 1;
        options.BatchSize = 1;
        options.FailureMode = ContextualFailureMode.Throw;
        string source = string.Join('\n', Enumerable.Range(1, 160).Select(index =>
            $"Synthetic dispatch entry {index}: the blue route gathers sealed parcels at noon and returns unused containers before closing."));
        Count(source).Should().BeGreaterThan(2048);
        Count(source).Should().BeLessThan(options.MaxDocumentContextTokens);
        var document = Chunk(source).Document;
        var targets = Enumerable.Range(1, 4)
            .Select(index => new IngestionChunk<string>($"Synthetic dispatch entry {index}", document)).ToArray();
        int completed = 0;

        await foreach (var result in new ContextualChunkProcessor(options)
                           .ProcessAsync(Enumerate(targets), TestContext.Current.CancellationToken))
        {
            result.Metadata.Should().ContainKey(ChunkMetadataKeys.ContextualHeader);
            completed++;
            if (completed > 1 && observations[^1] > 0)
            {
                break;
            }
        }

        output.WriteLine($"OpenAI Chat Completions, model {model}, cached tokens by request: " +
            string.Join(", ", observations.Select(value => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable")));
        observations.Skip(1).Should().Contain(value => value > 0,
            "an eligible follow-up request must report a positive cached input token count");
    }

    [Theory]
    [InlineData("{\"cached_tokens\":37}", 37)]
    [InlineData("{\"cached_tokens\":0}", 0)]
    [InlineData("{}", null)]
    public void AdapterMapsProviderCacheAccounting(string details, int? expected)
    {
        using var doc = JsonDocument.Parse("{\"choices\":[{\"message\":{\"content\":\"Context\"}}]," +
            "\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":2,\"prompt_tokens_details\":" + details + "}}");
        ChatResponse response = ReadResponse(doc.RootElement);
        response.Text.Should().Be("Context");
        response.Usage!.InputTokenCount.Should().Be(100L);
        response.Usage.CachedInputTokenCount.Should().Be(expected);
    }

    private static ChatResponse ReadResponse(JsonElement data)
    {
        string? content = data.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, content))
        {
            Usage = data.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                ? new UsageDetails
                {
                    InputTokenCount = ReadCount(usage, "prompt_tokens"),
                    OutputTokenCount = ReadCount(usage, "completion_tokens"),
                    CachedInputTokenCount = usage.TryGetProperty("prompt_tokens_details", out var details) &&
                        details.ValueKind == JsonValueKind.Object ? ReadCount(details, "cached_tokens") : null,
                }
                : null,
        };
    }

    private static long? ReadCount(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static string RequiredSetting(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Set {name} before running the live test.") : value;
    }
}
