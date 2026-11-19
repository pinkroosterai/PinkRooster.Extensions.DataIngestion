using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Samples.Scenarios;

/// <summary>
/// Represents the result and chunk output of a sample scenario execution.
/// </summary>
/// <param name="ScenarioName">The name of the executed scenario.</param>
/// <param name="ChunkCount">The total number of chunks produced.</param>
/// <param name="TotalTokens">The total token count across all chunks.</param>
/// <param name="Chunks">The collection of generated chunks.</param>
public sealed record ScenarioResult(
    string ScenarioName,
    int ChunkCount,
    int TotalTokens,
    IReadOnlyList<IngestionChunk<string>> Chunks);
