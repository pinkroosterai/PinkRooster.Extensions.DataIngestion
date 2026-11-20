using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>
/// Rebuilds chunks, which are immutable, with new content or placement while carrying over all of their metadata.
/// </summary>
internal static class ChunkCopy
{
    internal static IngestionChunk<string> With(IngestionChunk<string> source, string content,
        IngestionDocument document, string? context)
    {
        IngestionChunk<string> copy = new(content, document, context);
        if (source.HasMetadata)
        {
            foreach (KeyValuePair<string, object> entry in source.Metadata)
            {
                copy.Metadata[entry.Key] = entry.Value;
            }
        }

        return copy;
    }
}
