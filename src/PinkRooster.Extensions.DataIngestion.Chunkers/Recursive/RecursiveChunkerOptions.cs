using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Configures recursive splitting of ingestion documents.</summary>
/// <param name="tokenizer">The tokenizer used to enforce content and overlap budgets.</param>
public sealed class RecursiveChunkerOptions(Tokenizer tokenizer) : IngestionChunkerOptions(tokenizer)
{
    internal static IReadOnlyList<string> DefaultSeparators { get; } =
        Array.AsReadOnly(new[] { "\n\n", "\n", ". ", "? ", "! ", " ", "" });

    /// <summary>
    /// Gets or sets the separators in descending priority. An empty final separator requests
    /// token-based source slicing. Slicing is also the last resort for lists that omit it.
    /// </summary>
    public IReadOnlyList<string> Separators { get; set; } = DefaultSeparators;

    /// <summary>Gets or sets which fragment retains separators. The default is trailing.</summary>
    public SeparatorPlacement KeepSeparator { get; set; } = SeparatorPlacement.Trailing;

    /// <summary>Gets or sets the tokens reserved for downstream content outside this chunker.</summary>
    public int ReservedHeaderTokens { get; set; }

    /// <summary>Gets or sets whether to protect structured tables and closed fences.</summary>
    public bool PreserveAtomicElements { get; set; } = true;
}
