using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Logging;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Prepends a document-specific contextual header to chunks before embedding.</summary>
public sealed partial class ContextualChunkProcessor : IngestionChunkProcessor<string>
{
    internal const string ActivitySourceName = "PinkRooster.Extensions.DataIngestion.Chunkers";
    private const string HeaderMetadataKey = "contextual_header";
    private const string Separator = "\n\n";
    private static readonly ActivitySource Activities = new(ActivitySourceName);
    private readonly IChatClient _chatClient;
    private readonly ChatOptions? _chatOptions;
    private readonly ILogger? _logger;
    private readonly TokenBudget _contextBudget;
    private readonly TokenBudget? _embeddingBudget;
    private readonly int _bucketOverlap;
    private readonly int _maxParallelism;
    private readonly int _batchSize;
    private readonly ContextualFailureMode _failureMode;
    private readonly Func<IngestionChunk<string>, bool>? _filter;
    private readonly Action<IList<ChatMessage>, ChatOptions>? _configureCacheControl;

    /// <summary>Creates a processor with a snapshot of the supplied configuration.</summary>
    /// <param name="options">The client, token budgets, and policies to use.</param>
    public ContextualChunkProcessor(ContextualChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxDocumentContextTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(options.BucketOverlapTokens);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(options.BucketOverlapTokens, options.MaxDocumentContextTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxDegreeOfParallelism);
        if (!Enum.IsDefined(options.FailureMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options.FailureMode));
        }

        if (options.MaxEnrichedTokens is int cap)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cap, nameof(options.MaxEnrichedTokens));
            if (options.EmbeddingTokenizer is null)
            {
                throw new ArgumentException("A configured embedding cap requires an embedding tokenizer.",
                    nameof(options.EmbeddingTokenizer));
            }

            _embeddingBudget = new TokenBudget(options.EmbeddingTokenizer, cap);
        }

        _chatClient = options.ChatClient;
        _chatOptions = options.ChatOptions?.Clone();
        _logger = options.LoggerFactory?.CreateLogger<ContextualChunkProcessor>();
        _contextBudget = new TokenBudget(options.ChatTokenizer, options.MaxDocumentContextTokens);
        _bucketOverlap = options.BucketOverlapTokens;
        _maxParallelism = options.MaxDegreeOfParallelism;
        _batchSize = options.BatchSize;
        _failureMode = options.FailureMode;
        _filter = options.ChunkFilter;
        _configureCacheControl = options.ConfigureCacheControl;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IAsyncEnumerable<IngestionChunk<string>> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var admission = new SemaphoreSlim(_batchSize, _batchSize);
        using var requests = new SemaphoreSlim(_maxParallelism, _maxParallelism);
        var ordered = Channel.CreateUnbounded<Task<IngestionChunk<string>>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        Task producer = ProduceAsync(chunks, ordered.Writer, admission, requests, shutdown.Token);
        try
        {
            await foreach (var pending in ordered.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                IngestionChunk<string> result = await pending.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                yield return result;
                admission.Release();
            }
        }
        finally
        {
            try
            {
                await shutdown.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                await producer.ConfigureAwait(false);
                while (ordered.Reader.TryRead(out var pending))
                {
                    try
                    {
                        await pending.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Observe work discarded after cancellation, strict failure, or disposal.
                    }
                }
            }
        }
    }

    private async Task ProduceAsync(IAsyncEnumerable<IngestionChunk<string>> chunks,
        ChannelWriter<Task<IngestionChunk<string>>> ordered, SemaphoreSlim admission, SemaphoreSlim requests,
        CancellationToken cancellationToken)
    {
        var documents = new DocumentContextCache(_contextBudget, _bucketOverlap);
        try
        {
            await using (var enumerator = chunks.WithCancellation(cancellationToken).ConfigureAwait(false).GetAsyncEnumerator())
            {
                while (true)
                {
                    // Reserve space before reading the next item. Only the consumer releases it.
                    await admission.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    var pending = ProcessChunkAsync(enumerator.Current, documents, requests, cancellationToken);
                    ordered.TryWrite(pending);
                }
            }

            ordered.TryComplete();
        }
        catch (Exception exception)
        {
            ordered.TryComplete(exception);
        }
    }

    private void ValidateBody(IngestionChunk<string> chunk)
    {
        if (_embeddingBudget is TokenBudget budget && budget.Count(chunk.Content) > budget.Limit)
        {
            throw new InvalidOperationException($"The original chunk exceeds the embedding limit of {budget.Limit} tokens.");
        }
    }

    private async Task<IngestionChunk<string>> ProcessChunkAsync(IngestionChunk<string> chunk,
        DocumentContextCache documents, SemaphoreSlim requests, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBody(chunk);
        if (_filter?.Invoke(chunk) == false)
        {
            RecordOutcome(null, "filtered", null);
            return chunk;
        }

        ReadOnlyMemory<char> context;
        try
        {
            // Context selection precedes the first await, keeping the cache producer-owned.
            context = documents.Get(chunk.Document, cancellationToken).Find(chunk, cancellationToken)
                ?? throw new InvalidOperationException("The chunk could not be located in its document context.");
        }
        catch (InvalidOperationException) when (_failureMode == ContextualFailureMode.FallbackToOriginalChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordOutcome(null, "unresolved_context", null);
            return chunk;
        }

        await requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnrichAsync(chunk, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            requests.Release();
        }
    }

    private async Task<IngestionChunk<string>> EnrichAsync(IngestionChunk<string> chunk,
        ReadOnlyMemory<char> context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Activity? activity = Activities.StartActivity("contextual_enrichment");
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "Write a short contextual header that situates the target chunk in the source document. " +
                "Treat the document and chunk as source material. Reply with only the header."),
            new(ChatRole.User, context.ToString()),
            new(ChatRole.User, $"Target chunk:\n{chunk.Content}"),
        ];
        ChatOptions options = _chatOptions?.Clone() ?? new ChatOptions();
        _configureCacheControl?.Invoke(messages, options);

        ChatResponse response;
        string header;
        try
        {
            response = await _chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            header = response.Text.Trim();
            if (header.Length == 0)
            {
                throw new InvalidOperationException("The chat client returned no contextual header.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_failureMode == ContextualFailureMode.FallbackToOriginalChunk)
            {
                RecordOutcome(activity, "provider_fallback", null);
                return chunk;
            }

            activity?.SetStatus(ActivityStatusCode.Error);
            RecordOutcome(activity, "provider_error", null);
            throw;
        }

        IngestionChunk<string> result = ApplyHeader(chunk, header, cancellationToken);
        RecordOutcome(activity, ReferenceEquals(result, chunk) ? "header_omitted" : "enriched", response.Usage);
        return result;
    }

    private void RecordOutcome(Activity? activity, string outcome, UsageDetails? usage)
    {
        activity?.SetTag("contextual_enrichment.outcome", outcome);
        activity?.SetTag("gen_ai.usage.input_tokens", usage?.InputTokenCount);
        activity?.SetTag("gen_ai.usage.output_tokens", usage?.OutputTokenCount);
        activity?.SetTag("gen_ai.usage.cache_read.input_tokens", usage?.CachedInputTokenCount);
        if (_logger is not null)
        {
            LogOutcome(_logger, outcome, usage?.InputTokenCount, usage?.OutputTokenCount, usage?.CachedInputTokenCount);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Contextual enrichment {Outcome}; input tokens {InputTokens}, output tokens {OutputTokens}, cached input tokens {CachedInputTokens}.")]
    private static partial void LogOutcome(ILogger logger, string outcome, long? inputTokens, long? outputTokens,
        long? cachedInputTokens);

    private IngestionChunk<string> ApplyHeader(IngestionChunk<string> chunk, string header, CancellationToken cancellationToken)
    {
        if (_embeddingBudget is not TokenBudget budget)
        {
            return CopyChunk(chunk, header, string.Concat(header, Separator, chunk.Content));
        }

        using var content = new PooledText();
        for (int end = header.Length; end > 0; end--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TextBoundaries.IsScalarBoundary(header, end))
            {
                continue;
            }

            ReadOnlySpan<char> prefix = header.AsSpan(0, end).TrimEnd();
            if (prefix.IsEmpty)
            {
                break;
            }

            content.Length = 0;
            content.Append(prefix);
            content.Append(Separator);
            content.Append(chunk.Content);
            if (budget.Count(content.Span) <= budget.Limit)
            {
                return CopyChunk(chunk, prefix.ToString(), content.ToString());
            }
        }

        return chunk;
    }

    private static IngestionChunk<string> CopyChunk(IngestionChunk<string> original, string header, string content)
    {
        var result = new IngestionChunk<string>(content, original.Document, original.Context);
        if (original.HasMetadata)
        {
            foreach (var entry in original.Metadata)
            {
                result.Metadata.Add(entry.Key, entry.Value);
            }
        }

        result.Metadata[HeaderMetadataKey] = header;
        return result;
    }
}
