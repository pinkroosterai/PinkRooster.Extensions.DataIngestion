namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>
/// Specifies the chunk emission strategy for hierarchical parent-child chunking.
/// </summary>
public enum HierarchicalEmissionMode
{
    /// <summary>
    /// Emits each parent chunk immediately before all of its sequential child chunks.
    /// This is the default strategy for single-pass streaming ingestion.
    /// </summary>
    Interleaved = 0,

    /// <summary>
    /// Emits only the child chunks. Each child retains a reference to its parent chunk identifier.
    /// </summary>
    ChildrenOnly = 1,

    /// <summary>
    /// Emits only the parent chunks.
    /// </summary>
    ParentsOnly = 2,
}
