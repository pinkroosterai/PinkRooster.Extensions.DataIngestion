using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Configures contextual enrichment of ingestion chunks.</summary>
/// <param name="chatClient">The client that generates contextual headers.</param>
/// <param name="chatTokenizer">The tokenizer used to budget document context.</param>
public sealed class ContextualChunkerOptions(IChatClient chatClient, Tokenizer chatTokenizer) : EnricherOptions(chatClient)
{
    /// <summary>Gets the tokenizer used to measure document context sent to the client.</summary>
    public Tokenizer ChatTokenizer { get; } = chatTokenizer ?? throw new ArgumentNullException(nameof(chatTokenizer));

    /// <summary>Gets or sets the tokenizer used to measure final embedding input.</summary>
    public Tokenizer? EmbeddingTokenizer { get; set; }

    /// <summary>Gets or sets the document text budget per context window. The default is 32,000.</summary>
    public int MaxDocumentContextTokens { get; set; } = 32_000;

    /// <summary>Gets or sets the overlap between adjacent context windows. The default is 2,000.</summary>
    public int BucketOverlapTokens { get; set; } = 2_000;

    /// <summary>Gets or sets the maximum size of final content, including its header and separator.</summary>
    public int? MaxEnrichedTokens { get; set; }

    /// <summary>Gets or sets the maximum number of chat calls per enumeration. The default is four.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>Gets or sets the policy for failed enrichment. The default returns the original chunk.</summary>
    public ContextualFailureMode FailureMode { get; set; } = ContextualFailureMode.FallbackToOriginalChunk;

    /// <summary>Gets or sets a predicate that identifies chunks eligible for enrichment.</summary>
    public Func<IngestionChunk<string>, bool>? ChunkFilter { get; set; }

    /// <summary>Gets or sets a callback that configures provider-specific caching for one request.</summary>
    /// <remarks>
    /// The callback may run concurrently. It owns the request messages and option collections.
    /// Nested values supplied in the original chat options are shared and should be treated as read-only.
    /// </remarks>
    public Action<IList<ChatMessage>, ChatOptions>? ConfigureCacheControl { get; set; }
}
