using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Xunit;
using static PinkRooster.Extensions.DataIngestion.Chunkers.Tests.ContextualChunkProcessorTests;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class ContextualPromptTests
{
    [Fact]
    public async Task SameBucketSharesStaticMessagesAndDifferentBucketsUseTheirOwnContext()
    {
        const string Source = "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima " +
            "mike november oscar papa quebec romeo sierra tango";
        var document = Chunk(Source).Document;
        List<ChatMessage[]> sent = [];
        using var client = Capturing((messages, _) => sent.Add(messages.ToArray()));
        var options = Options(client);
        options.MaxDocumentContextTokens = 12;
        options.BucketOverlapTokens = 2;

        var results = await ReadAsync(new(options),
            new("alpha", document), new("bravo", document), new("tango", document));

        results.Should().HaveCount(3);
        sent[0].Take(2).Select(message => (message.Role, message.Text))
            .Should().Equal(sent[1].Take(2).Select(message => (message.Role, message.Text)));
        sent[0][2].Text.Should().Contain("alpha");
        sent[1][2].Text.Should().Contain("bravo");
        sent[2][1].Text.Should().Contain("tango").And.NotBe(sent[0][1].Text);
    }

    [Fact]
    public async Task HookAndClientMutationsStayWithinOneRequest()
    {
        List<ChatMessage> sent = [];
        List<ChatOptions> configured = [];
        var original = new ChatOptions { AdditionalProperties = new() { ["baseline"] = true } };
        using var client = Capturing((messages, options) =>
        {
            sent.Add(messages.First());
            configured.Add(options!);
            options!.AdditionalProperties!.Add("client mutation", true);
        });
        var options = Options(client);
        options.ChatOptions = original;
        int count = 0;
        options.ConfigureCacheControl = (messages, request) =>
        {
            messages[0].AdditionalProperties.Should().BeNull();
            messages[0].AdditionalProperties = new() { ["cache_control"] = "ephemeral" };
            request.AdditionalProperties!.Add("request", ++count);
        };
        var chunk = Chunk("isolated prompt");

        await ReadAsync(new(options), chunk, chunk);

        sent[0].Should().NotBeSameAs(sent[1]);
        sent[0].AdditionalProperties!["cache_control"].Should().Be("ephemeral");
        configured[0].Should().NotBeSameAs(configured[1]);
        configured.Select(option => option.AdditionalProperties!["request"]).Should().Equal(1, 2);
        original.AdditionalProperties.Should().ContainSingle();
    }

    [Fact]
    public async Task CacheHookErrorsPropagateBeforeChat()
    {
        using var client = Capturing((_, _) => throw new NotSupportedException("Unexpected chat request"));
        var options = Options(client);
        options.ConfigureCacheControl = (_, _) => throw new FormatException("bad cache configuration");

        Func<Task> run = () => ReadAsync(new(options), Chunk("valid document"));
        await run.Should().ThrowAsync<FormatException>().WithMessage("bad cache configuration");
    }

    [Fact]
    public async Task TraceKeepsMissingUsageDistinctFromPositiveCacheRead()
    {
        using var origin = new Activity("contextual test").Start();
        ConcurrentQueue<Activity> traces = new();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ContextualChunkProcessor.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.ParentId == origin.Id)
                {
                    traces.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        int calls = 0;
        using var client = new ContextualChatClient((_, _, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Context"))
            {
                Usage = ++calls == 1 ? null : new UsageDetails
                {
                    InputTokenCount = 100,
                    OutputTokenCount = 3,
                    CachedInputTokenCount = 37,
                },
            }));
        var chunk = Chunk("do not log this body");

        await ReadAsync(new(Options(client)), chunk, chunk);

        var observed = traces.ToArray();
        observed.Should().HaveCount(2);
        observed[0].GetTagItem("gen_ai.usage.cache_read.input_tokens").Should().BeNull();
        observed[1].GetTagItem("gen_ai.usage.cache_read.input_tokens").Should().Be(37L);
        observed[1].GetTagItem("gen_ai.usage.input_tokens").Should().Be(100L);
        observed.SelectMany(trace => trace.TagObjects).Select(tag => tag.Value?.ToString())
            .Should().NotContain(chunk.Content);
    }

    private static ContextualChatClient Capturing(Action<IEnumerable<ChatMessage>, ChatOptions?> observe) =>
        new((messages, options, _) =>
        {
            observe(messages, options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Context")));
        });
}
