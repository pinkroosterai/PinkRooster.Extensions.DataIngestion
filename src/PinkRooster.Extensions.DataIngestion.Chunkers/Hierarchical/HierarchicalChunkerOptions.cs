using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>
/// Configures streaming dual-granularity chunking with parent-child linkage.
/// </summary>
public sealed class HierarchicalChunkerOptions : IngestionChunkerOptions
{
    private int _parentMaxTokens = 1024;
    private int _parentOverlapTokens;
    private HierarchicalEmissionMode _emissionMode = HierarchicalEmissionMode.Interleaved;

    /// <summary>
    /// Initializes a new instance of the <see cref="HierarchicalChunkerOptions"/> class.
    /// </summary>
    /// <param name="tokenizer">The tokenizer used to calculate token budgets and chunk boundaries.</param>
    public HierarchicalChunkerOptions(Tokenizer tokenizer) : base(tokenizer)
    {
        MaxTokensPerChunk = 256;
        OverlapTokens = 32;
    }

    /// <summary>
    /// Gets or sets the maximum token budget for each parent chunk.
    /// The default is 1024 tokens. Must be greater than or equal to <see cref="IngestionChunkerOptions.MaxTokensPerChunk"/>.
    /// </summary>
    public int ParentMaxTokens
    {
        get => _parentMaxTokens;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            _parentMaxTokens = value;
        }
    }

    /// <summary>
    /// Gets or sets the token overlap between adjacent parent chunks.
    /// The default is 0 tokens. Must be strictly less than <see cref="ParentMaxTokens"/>.
    /// </summary>
    public int ParentOverlapTokens
    {
        get => _parentOverlapTokens;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _parentOverlapTokens = value;
        }
    }

    /// <summary>
    /// Gets or sets the strategy used to emit parent and child chunks.
    /// The default is <see cref="HierarchicalEmissionMode.Interleaved"/>.
    /// </summary>
    public HierarchicalEmissionMode EmissionMode
    {
        get => _emissionMode;
        set => _emissionMode = value;
    }

    /// <summary>
    /// Validates that parent and child token budgets satisfy hierarchical constraints.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="ParentMaxTokens"/> is less than <see cref="IngestionChunkerOptions.MaxTokensPerChunk"/>
    /// or when <see cref="ParentOverlapTokens"/> is greater than or equal to <see cref="ParentMaxTokens"/>.
    /// </exception>
    public void Validate()
    {
        if (_parentMaxTokens < MaxTokensPerChunk)
        {
            throw new InvalidOperationException(
                $"ParentMaxTokens ({_parentMaxTokens}) must be greater than or equal to child MaxTokensPerChunk ({MaxTokensPerChunk}).");
        }

        if (_parentOverlapTokens >= _parentMaxTokens)
        {
            throw new InvalidOperationException(
                $"ParentOverlapTokens ({_parentOverlapTokens}) must be strictly less than ParentMaxTokens ({_parentMaxTokens}).");
        }
    }
}
