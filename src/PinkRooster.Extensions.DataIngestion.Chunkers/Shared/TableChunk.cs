namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Internal representation of a chunk produced from a Markdown table.</summary>
internal readonly record struct TableChunk(
    string Content,
    int PartIndex,
    bool IsContinuation,
    int StartRow,
    int EndRow);
