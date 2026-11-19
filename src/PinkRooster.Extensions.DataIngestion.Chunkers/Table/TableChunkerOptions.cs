using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Configures structure-aware row-window chunking of Markdown and document tables.</summary>
/// <param name="tokenizer">The tokenizer used to calculate token budgets and chunk boundaries.</param>
public sealed class TableChunkerOptions(Tokenizer tokenizer) : IngestionChunkerOptions(tokenizer)
{
    /// <summary>Gets or sets the token budget reserved for document headings or breadcrumb prefixes.</summary>
    public int ReservedHeaderTokens { get; set; }

    /// <summary>
    /// Gets or sets whether non-table prose surrounding tables is emitted as chunks.
    /// The default is <see langword="true"/>.
    /// </summary>
    public bool EmitNonTableContent { get; set; } = true;
}
