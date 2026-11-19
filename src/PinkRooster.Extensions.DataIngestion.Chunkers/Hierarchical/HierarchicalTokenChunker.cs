using System.Runtime.CompilerServices;
using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>
/// Streams dual-granularity RAG chunks consisting of large parent chunks and smaller child chunks linked via deterministic metadata.
/// </summary>
public sealed class HierarchicalTokenChunker : IngestionChunker<string>
{
    private readonly HierarchicalChunkerOptions _options;
    private readonly RecursiveTokenChunker _parentChunker;
    private readonly RecursiveTokenChunker _childChunker;

    /// <summary>
    /// Initializes a new instance of the <see cref="HierarchicalTokenChunker"/> class.
    /// </summary>
    /// <param name="options">Configuration options establishing parent and child token budgets and emission strategy.</param>
    public HierarchicalTokenChunker(HierarchicalChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Tokenizer);
        options.Validate();

        _options = options;

        RecursiveChunkerOptions parentOptions = new(options.Tokenizer)
        {
            MaxTokensPerChunk = options.ParentMaxTokens,
            OverlapTokens = options.ParentOverlapTokens,
            PreserveAtomicElements = true,
            RepeatTableHeaders = true,
        };
        _parentChunker = new RecursiveTokenChunker(parentOptions);

        RecursiveChunkerOptions childOptions = new(options.Tokenizer)
        {
            MaxTokensPerChunk = options.MaxTokensPerChunk,
            OverlapTokens = options.OverlapTokens,
            PreserveAtomicElements = true,
            RepeatTableHeaders = true,
        };
        _childChunker = new RecursiveTokenChunker(childOptions);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<IngestionChunk<string>> ProcessAsync(
        IngestionDocument document, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        string docId = string.IsNullOrEmpty(document.Identifier) ? "doc" : document.Identifier;
        int parentIndex = 0;

        await foreach (IngestionChunk<string> parentChunk in _parentChunker.ProcessAsync(document, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(parentChunk.Content))
            {
                continue;
            }

            string parentId = $"{docId}#p{parentIndex}";
            int parentTokenCount = _options.Tokenizer.CountTokens(parentChunk.Content);

            IngestionDocument parentDoc = new(parentId);
            IngestionDocumentSection section = new();
            section.Elements.Add(new IngestionDocumentParagraph(parentChunk.Content));
            parentDoc.Sections.Add(section);

            List<IngestionChunk<string>> childChunks = [];
            await foreach (IngestionChunk<string> child in _childChunker.ProcessAsync(parentDoc, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(child.Content))
                {
                    childChunks.Add(child);
                }
            }

            IngestionChunk<string> parentOut = new(parentChunk.Content, document, parentChunk.Context);
            foreach (KeyValuePair<string, object> entry in parentChunk.Metadata)
            {
                parentOut.Metadata[entry.Key] = entry.Value;
            }
            parentOut.Metadata["ChunkRole"] = "Parent";
            parentOut.Metadata["ChunkId"] = parentId;
            parentOut.Metadata["ChildCount"] = childChunks.Count;
            parentOut.Metadata["ParentTokenCount"] = parentTokenCount;

            List<IngestionChunk<string>> enrichedChildren = new(childChunks.Count);
            for (int c = 0; c < childChunks.Count; c++)
            {
                IngestionChunk<string> rawChild = childChunks[c];
                string childId = $"{docId}#p{parentIndex}_c{c}";

                IngestionChunk<string> childOut = new(rawChild.Content, document, parentChunk.Context);
                foreach (KeyValuePair<string, object> entry in rawChild.Metadata)
                {
                    childOut.Metadata[entry.Key] = entry.Value;
                }
                childOut.Metadata["ChunkRole"] = "Child";
                childOut.Metadata["ChunkId"] = childId;
                childOut.Metadata["ParentId"] = parentId;
                childOut.Metadata["ChildIndex"] = c;
                childOut.Metadata["ChildCount"] = childChunks.Count;
                childOut.Metadata["ParentTokenCount"] = parentTokenCount;

                enrichedChildren.Add(childOut);
            }

            switch (_options.EmissionMode)
            {
                case HierarchicalEmissionMode.Interleaved:
                    yield return parentOut;
                    foreach (IngestionChunk<string> child in enrichedChildren)
                    {
                        yield return child;
                    }
                    break;

                case HierarchicalEmissionMode.ChildrenOnly:
                    foreach (IngestionChunk<string> child in enrichedChildren)
                    {
                        yield return child;
                    }
                    break;

                case HierarchicalEmissionMode.ParentsOnly:
                    yield return parentOut;
                    break;
            }

            parentIndex++;
        }
    }
}
