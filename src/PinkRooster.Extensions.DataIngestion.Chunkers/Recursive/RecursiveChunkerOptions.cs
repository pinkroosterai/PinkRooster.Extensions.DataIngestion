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

    /// <summary>
    /// Gets or sets whether tables (document tables and pipe tables inside other elements) and closed code fences are
    /// protected regions, kept whole when they fit and split structurally when they do not. The default is
    /// <see langword="true"/>. When <see langword="false"/>, their text is split like any other prose.
    /// </summary>
    public bool PreserveAtomicElements { get; set; } = true;

    /// <summary>
    /// Gets or sets how a protected table that exceeds the budget is split: into row windows that each repeat the
    /// header row (<see langword="true"/>, the default), or into consecutive slices of its text without repeated
    /// headers (<see langword="false"/>). Has no effect when <see cref="PreserveAtomicElements"/> is
    /// <see langword="false"/>.
    /// </summary>
    public bool RepeatTableHeaders { get; set; } = true;
}
