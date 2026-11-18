namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Controls how contextual enrichment handles unusable provider responses and provider failures.</summary>
public enum ContextualFailureMode
{
    /// <summary>Return the original chunk if it satisfies the configured embedding budget.</summary>
    FallbackToOriginalChunk,

    /// <summary>Propagate the enrichment failure to the consumer.</summary>
    Throw,
}
