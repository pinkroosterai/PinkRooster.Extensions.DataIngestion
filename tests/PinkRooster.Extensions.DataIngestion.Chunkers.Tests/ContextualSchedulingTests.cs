using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Xunit;
using static PinkRooster.Extensions.DataIngestion.Chunkers.Tests.ContextualChunkProcessorTests;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class ContextualSchedulingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task BoundsAdmissionAndProviderWorkWhileRestoringInputOrder()
    {
        int admitted = 0;
        int active = 0;
        int peak = 0;
        object gate = new();
        var started = Enumerable.Range(0, 5).Select(_ => Signal()).ToArray();
        var responses = Enumerable.Range(0, 5).Select(_ => Signal()).ToArray();
        using var client = new ContextualChatClient(async (messages, _, cancellationToken) =>
        {
            int index = int.Parse(messages.Last().Text!.Split('\n')[^1], System.Globalization.CultureInfo.InvariantCulture);
            lock (gate)
            {
                peak = Math.Max(peak, ++active);
            }

            started[index].SetResult();
            try
            {
                await responses[index].Task.WaitAsync(cancellationToken);
                return Response("context " + index);
            }
            finally
            {
                lock (gate)
                {
                    active--;
                }
            }
        });

        async IAsyncEnumerable<IngestionChunk<string>> Input()
        {
            await Task.CompletedTask;
            var document = Chunk("0 1 2 3 4").Document;
            for (int i = 0; i < 5; i++)
            {
                Interlocked.Increment(ref admitted);
                yield return new IngestionChunk<string>(i.ToString(System.Globalization.CultureInfo.InvariantCulture), document);
            }
        }

        var options = Options(client);
        options.BatchSize = 3;
        options.MaxDegreeOfParallelism = 2;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var reader = new ContextualChunkProcessor(options).ProcessAsync(Input(), cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        Task<bool> first = reader.MoveNextAsync().AsTask();

        await started[1].Task.WaitAsync(Timeout, cancellationToken);
        started[2].Task.IsCompleted.Should().BeFalse();
        responses[1].SetResult();
        await started[2].Task.WaitAsync(Timeout, cancellationToken);
        responses[2].SetResult();
        first.IsCompleted.Should().BeFalse();
        Volatile.Read(ref admitted).Should().Be(3);

        responses[0].SetResult();
        (await first.WaitAsync(Timeout, cancellationToken)).Should().BeTrue();
        Volatile.Read(ref admitted).Should().Be(3);
        List<string> headers = [(string)reader.Current.Metadata["contextual_header"]];
        responses[3].SetResult();
        responses[4].SetResult();
        while (await reader.MoveNextAsync())
        {
            headers.Add((string)reader.Current.Metadata["contextual_header"]);
        }

        headers.Should().Equal("context 0", "context 1", "context 2", "context 3", "context 4");
        peak.Should().Be(2);
    }

    [Fact]
    public async Task YieldsBeforeAStalledInputAndDisposalCancelsTheInputRead()
    {
        var waiting = Signal();
        var disposed = Signal();
        var chunk = Chunk("first");
        async IAsyncEnumerable<IngestionChunk<string>> Input(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return chunk;
                waiting.SetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            }
            finally
            {
                disposed.SetResult();
            }
        }

        using var client = Reply("context");
        var token = TestContext.Current.CancellationToken;
        var reader = new ContextualChunkProcessor(Options(client)).ProcessAsync(Input(token), token)
            .GetAsyncEnumerator(token);
        try
        {
            (await reader.MoveNextAsync().AsTask().WaitAsync(Timeout, token)).Should().BeTrue();
            await waiting.Task.WaitAsync(Timeout, token);
            reader.Current.Content.Should().EndWith("first");
        }
        finally
        {
            await reader.DisposeAsync();
        }

        disposed.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task ConsumerDisposalCancelsPendingProviderCalls()
    {
        var started = Signal();
        var stopped = Signal();
        using var client = new ContextualChatClient(async (messages, _, token) =>
        {
            if (messages.Last().Text!.EndsWith("later", StringComparison.Ordinal))
            {
                started.SetResult();
                try
                {
                    await Task.Delay(System.Threading.Timeout.Infinite, token);
                }
                finally
                {
                    stopped.SetResult();
                }
            }

            return Response("context");
        });

        var cancellation = TestContext.Current.CancellationToken;
        var reader = new ContextualChunkProcessor(Options(client))
            .ProcessAsync(Enumerate(Chunk("first"), Chunk("later")), cancellation)
            .GetAsyncEnumerator(cancellation);
        try
        {
            (await reader.MoveNextAsync()).Should().BeTrue();
            await started.Task.WaitAsync(Timeout, cancellation);
        }
        finally
        {
            await reader.DisposeAsync();
        }

        stopped.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task SimultaneousEnumerationsKeepTheirOwnDocumentContext()
    {
        var both = Signal();
        ConcurrentQueue<string> prefixes = new();
        using var client = new ContextualChatClient(async (messages, _, token) =>
        {
            string prefix = messages.ElementAt(1).Text!;
            prefixes.Enqueue(prefix);
            if (prefixes.Count == 2)
            {
                both.TrySetResult();
            }

            await both.Task.WaitAsync(Timeout, token);
            return Response(prefix);
        });

        var processor = new ContextualChunkProcessor(Options(client));
        var one = Chunk("one document");
        var two = Chunk("another document");
        one.Document.Identifier.Should().Be(two.Document.Identifier);
        var result = await Task.WhenAll(ReadAsync(processor, one), ReadAsync(processor, two));

        result[0].Single().Content.Should().Be("one document\n\none document");
        result[1].Single().Content.Should().Be("another document\n\nanother document");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilterBypassesChatAndFailuresUseTheSelectedMode(bool unusableResponse)
    {
        int calls = 0;
        using var client = new ContextualChatClient((_, _, _) =>
        {
            calls++;
            return unusableResponse ? Task.FromResult(Response("  ")) :
                Task.FromException<ChatResponse>(new FormatException("provider error"));
        });
        var skipped = Chunk("skip");
        var input = Chunk("enrich");
        var options = Options(client);
        options.ChunkFilter = chunk => chunk.Content != "skip";
        (await ReadAsync(new(options), skipped, input)).Should().Equal(skipped, input);
        calls.Should().Be(1);

        options.FailureMode = ContextualFailureMode.Throw;
        Func<Task> strict = () => ReadAsync(new(options), input);
        if (unusableResponse)
        {
            await strict.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no contextual header*");
        }
        else
        {
            await strict.Should().ThrowAsync<FormatException>().WithMessage("provider error");
        }

        options.ChunkFilter = _ => throw new ArgumentException("invalid filter");
        options.FailureMode = ContextualFailureMode.FallbackToOriginalChunk;
        Func<Task> filter = () => ReadAsync(new(options), input);
        await filter.Should().ThrowAsync<ArgumentException>().WithMessage("invalid filter");
        calls.Should().Be(2);
    }

    [Theory]
    [InlineData(ContextualFailureMode.FallbackToOriginalChunk)]
    [InlineData(ContextualFailureMode.Throw)]
    public async Task CallerCancellationPropagatesWhileChatIsPending(ContextualFailureMode mode)
    {
        var entered = Signal();
        using var client = new ContextualChatClient(async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return Response("unreachable");
        });
        var options = Options(client);
        options.FailureMode = mode;
        var token = TestContext.Current.CancellationToken;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var reader = new ContextualChunkProcessor(options)
            .ProcessAsync(Enumerate(Chunk("cancel")), token).GetAsyncEnumerator(cancelled.Token);
        Task<bool> pending = reader.MoveNextAsync().AsTask();
        await entered.Task.WaitAsync(Timeout, token);
        await cancelled.CancelAsync();
        Func<Task> finish = async () => { await pending.WaitAsync(Timeout, token); };

        await finish.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task InputFaultFollowsItsEarlierAdmittedChunk()
    {
        async IAsyncEnumerable<IngestionChunk<string>> Broken()
        {
            await Task.CompletedTask;
            yield return Chunk("before input fault");
            throw new InvalidDataException("input failed");
        }

        using var client = Reply("context");
        var token = TestContext.Current.CancellationToken;
        await using var reader = new ContextualChunkProcessor(Options(client)).ProcessAsync(Broken(), token)
            .GetAsyncEnumerator(token);
        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.Content.Should().EndWith("before input fault");
        Func<Task> finish = async () => { await reader.MoveNextAsync(); };
        await finish.Should().ThrowAsync<InvalidDataException>().WithMessage("input failed");
    }

    [Fact]
    public async Task CallerCancellationPropagatesWhileInputIsPending()
    {
        var entered = Signal();
        async IAsyncEnumerable<IngestionChunk<string>> Input(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            entered.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            yield break;
        }

        using var client = Reply("unused");
        var token = TestContext.Current.CancellationToken;
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var reader = new ContextualChunkProcessor(Options(client))
            .ProcessAsync(Input(token), token).GetAsyncEnumerator(shutdown.Token);
        Task<bool> pending = reader.MoveNextAsync().AsTask();
        await entered.Task.WaitAsync(Timeout, token);
        await shutdown.CancelAsync();
        Func<Task> finish = async () => { await pending.WaitAsync(Timeout, token); };

        await finish.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task StrictFailureCancelsLaterWorkAfterEarlierOutput()
    {
        var tailStarted = Signal();
        var tailStopped = Signal();
        using var client = new ContextualChatClient(async (messages, _, token) =>
        {
            string context = messages.ElementAt(1).Text!;
            if (context == "broken document")
            {
                await tailStarted.Task.WaitAsync(Timeout, token);
                throw new FormatException("strict item failed");
            }

            if (context == "later document")
            {
                tailStarted.SetResult();
                try
                {
                    await Task.Delay(System.Threading.Timeout.Infinite, token);
                }
                finally
                {
                    tailStopped.SetResult();
                }
            }

            return Response(context);
        });

        var options = Options(client);
        options.FailureMode = ContextualFailureMode.Throw;
        var cancel = TestContext.Current.CancellationToken;
        await using var reader = new ContextualChunkProcessor(options)
            .ProcessAsync(Enumerate(Chunk("first document"), Chunk("broken document"), Chunk("later document")), cancel)
            .GetAsyncEnumerator(cancel);
        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.Content.Should().Be("first document\n\nfirst document");
        Func<Task> finish = async () => { await reader.MoveNextAsync(); };
        await finish.Should().ThrowAsync<FormatException>().WithMessage("strict item failed");
        tailStopped.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ChatResponse Response(string text) => new(new ChatMessage(ChatRole.Assistant, text));
}
