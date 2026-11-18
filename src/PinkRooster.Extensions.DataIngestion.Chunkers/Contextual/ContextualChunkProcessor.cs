using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Prepends a document-specific contextual header to chunks before embedding.</summary>
public sealed class ContextualChunkProcessor : IngestionChunkProcessor<string>
{
    private const string HeaderMetadataKey = "contextual_header";
    private const string Separator = "\n\n";
    private readonly IChatClient _chatClient;
    private readonly ChatOptions? _chatOptions;
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
        await foreach (IngestionChunk<string> chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateBody(chunk);
            yield return await EnrichAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ValidateBody(IngestionChunk<string> chunk)
    {
        if (_embeddingBudget is TokenBudget budget && budget.Count(chunk.Content) > budget.Limit)
        {
            throw new InvalidOperationException($"The original chunk exceeds the embedding limit of {budget.Limit} tokens.");
        }
    }

    private async Task<IngestionChunk<string>> EnrichAsync(IngestionChunk<string> chunk, CancellationToken cancellationToken)
    {
        var messages = new[] { new ChatMessage(ChatRole.User, $"Summarize the following chunk briefly:\n{chunk.Content}") };
        var options = _chatOptions?.Clone();
        string header;
        try
        {
            ChatResponse response = await _chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            header = response.Text.Trim();
            if (header.Length == 0)
            {
                throw new InvalidOperationException("The chat client returned no contextual header.");
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && _failureMode == ContextualFailureMode.FallbackToOriginalChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return chunk;
        }

        return ApplyHeader(chunk, header, cancellationToken);
    }

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
