using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;

namespace PinkRooster.Extensions.DataIngestion.Benchmarks;

internal sealed record EvaluationChunk(string SourceId, int Ordinal, string Payload, SourceSlice? Source, string? Context = null);

internal static class SmokeChunking
{
    internal static IngestionDocument Read(EvaluationSource source)
    {
        var document = new IngestionDocument(source.Id);
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(source.Text));
        document.Sections.Add(section);
        return document;
    }

    internal static DocumentTokenChunker Baseline(Tokenizer tokenizer, int maxTokens) =>
        new(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = maxTokens, OverlapTokens = 0 });

    internal static RecursiveTokenChunker Recursive(Tokenizer tokenizer, int maxTokens) =>
        new(new RecursiveChunkerOptions(tokenizer)
        {
            MaxTokensPerChunk = maxTokens,
            OverlapTokens = 0,
            PreserveAtomicElements = false,
        });

    internal static async Task<IReadOnlyList<EvaluationChunk>> ChunkAsync(EvaluationSource source,
        IngestionDocument document, IngestionChunker<string> chunker, CancellationToken cancellationToken)
    {
        var chunks = new List<EvaluationChunk>();
        await foreach (var chunk in chunker.ProcessAsync(document, cancellationToken).WithCancellation(cancellationToken))
        {
            if (!ReferenceEquals(chunk.Document, document))
            {
                throw new InvalidDataException($"Chunk source changed while processing '{source.Id}'.");
            }

            string payload = chunk.Content;
            SourceSlice? verified = SourceAttribution.Match(source, payload, 0, payload.Length);
            if (verified is null)
            {
                ReadOnlySpan<char> trimmed = payload.AsSpan().TrimStart();
                int start = payload.Length - trimmed.Length;
                verified = SourceAttribution.Match(source, payload, start, trimmed.TrimEnd().Length);
            }

            chunks.Add(new EvaluationChunk(source.Id, chunks.Count, payload, verified, chunk.Context));
        }

        return chunks;
    }
}
