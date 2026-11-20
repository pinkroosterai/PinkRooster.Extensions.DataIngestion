using Microsoft.Extensions.DataIngestion;
using Xunit;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

internal static class ChunkerTestExtensions
{
    /// <summary>Runs a chunker over a document with the test's cancellation token and collects every chunk.</summary>
    internal static async Task<List<IngestionChunk<string>>> CollectAsync(this IngestionChunker<string> chunker, IngestionDocument document)
    {
        List<IngestionChunk<string>> chunks = [];
        await foreach (var chunk in chunker.ProcessAsync(document, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }
}
