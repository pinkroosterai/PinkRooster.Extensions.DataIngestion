namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>
/// The <see cref="Microsoft.Extensions.DataIngestion.IngestionChunk{T}.Metadata"/> keys and values written by the
/// chunkers and processors in this package family. Keys are lowercase snake_case, like the upstream enrichers' keys.
/// </summary>
public static class ChunkMetadataKeys
{
    /// <summary>Key whose <see cref="bool"/> value is <see langword="true"/> on every chunk cut from a table.</summary>
    public const string IsTablePart = "is_table_part";

    /// <summary>Key whose <see cref="int"/> value is the zero-based index of a table chunk within its table.</summary>
    public const string TablePartIndex = "table_part_index";

    /// <summary>Key whose <see cref="bool"/> value is <see langword="true"/> on every table chunk after the first.</summary>
    public const string IsTableContinuation = "is_table_continuation";

    /// <summary>
    /// Key whose <see cref="string"/> value is the one-based, inclusive range of data rows in a table chunk, such as
    /// <c>3-7</c>, or <c>0-0</c> when the chunk holds no complete data row.
    /// </summary>
    public const string TableRowRange = "table_row_range";

    /// <summary>Key whose <see cref="string"/> value is <see cref="ParentRole"/> or <see cref="ChildRole"/>.</summary>
    public const string ChunkRole = "chunk_role";

    /// <summary>Key whose <see cref="string"/> value identifies a hierarchical chunk within its document.</summary>
    public const string ChunkId = "chunk_id";

    /// <summary>Key whose <see cref="string"/> value is the <see cref="ChunkId"/> of a child chunk's parent.</summary>
    public const string ParentId = "parent_id";

    /// <summary>Key whose <see cref="int"/> value is the zero-based position of a child within its parent.</summary>
    public const string ChildIndex = "child_index";

    /// <summary>Key whose <see cref="int"/> value is the number of children of a parent chunk.</summary>
    public const string ChildCount = "child_count";

    /// <summary>Key whose <see cref="int"/> value is the token count of the parent chunk.</summary>
    public const string ParentTokenCount = "parent_token_count";

    /// <summary>The <see cref="ChunkRole"/> value of a parent chunk.</summary>
    public const string ParentRole = "parent";

    /// <summary>The <see cref="ChunkRole"/> value of a child chunk.</summary>
    public const string ChildRole = "child";

    /// <summary>Key whose <see cref="string"/> value is the contextual header prepended to an enriched chunk.</summary>
    public const string ContextualHeader = "contextual_header";

    /// <summary>
    /// Key whose value lists the preprocessor symbols under which a C# chunk is active, written by the Roslyn chunker.
    /// </summary>
    public const string ConditionalSymbols = "conditional_symbols";
}
