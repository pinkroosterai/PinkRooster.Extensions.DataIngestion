using System.Runtime.CompilerServices;
using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>
/// Streams dual-granularity RAG chunks consisting of large parent chunks and smaller child chunks linked via deterministic metadata.
/// </summary>
public sealed class HierarchicalTokenChunker : IngestionChunker<string>
{
    private readonly TokenBudget _parentBudget;
    private readonly HierarchicalEmissionMode _emissionMode;
    private readonly RecursiveTokenChunker _parentChunker;
    private readonly RecursiveTokenChunker _childChunker;

    /// <summary>
    /// Initializes a new instance of the <see cref="HierarchicalTokenChunker"/> class.
    /// </summary>
    /// <param name="options">
    /// Configuration options establishing parent and child token budgets and emission strategy. The chunker takes a
    /// snapshot of the options, so later changes to <paramref name="options"/> do not affect it.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="HierarchicalChunkerOptions.EmissionMode"/> is not a defined value,
    /// <see cref="HierarchicalChunkerOptions.ParentMaxTokens"/> is less than the child
    /// <see cref="IngestionChunkerOptions.MaxTokensPerChunk"/>, or an overlap is not less than its token budget.
    /// </exception>
    public HierarchicalTokenChunker(HierarchicalChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Tokenizer);
        if (!Enum.IsDefined(options.EmissionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.EmissionMode,
                $"{nameof(options.EmissionMode)} is not a defined {nameof(HierarchicalEmissionMode)} value.");
        }

        if (options.ParentMaxTokens < options.MaxTokensPerChunk)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ParentMaxTokens,
                $"{nameof(options.ParentMaxTokens)} ({options.ParentMaxTokens}) must be greater than or equal to " +
                $"{nameof(options.MaxTokensPerChunk)} ({options.MaxTokensPerChunk}).");
        }

        if (options.ParentOverlapTokens >= options.ParentMaxTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ParentOverlapTokens,
                $"{nameof(options.ParentOverlapTokens)} ({options.ParentOverlapTokens}) must be less than " +
                $"{nameof(options.ParentMaxTokens)} ({options.ParentMaxTokens}).");
        }

        // The child overlap must stay below the child budget; IngestionChunkerOptions enforces that itself.

        _parentBudget = new TokenBudget(options.Tokenizer, options.ParentMaxTokens);
        _emissionMode = options.EmissionMode;

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
            string parentId = $"{docId}#p{parentIndex}";
            int parentTokenCount = _parentBudget.Count(parentChunk.Content);

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

            IngestionChunk<string> parentOut = ChunkCopy.With(parentChunk, parentChunk.Content, document, parentChunk.Context);
            parentOut.Metadata[ChunkMetadataKeys.ChunkRole] = ChunkMetadataKeys.ParentRole;
            parentOut.Metadata[ChunkMetadataKeys.ChunkId] = parentId;
            parentOut.Metadata[ChunkMetadataKeys.ChildCount] = childChunks.Count;
            parentOut.Metadata[ChunkMetadataKeys.ParentTokenCount] = parentTokenCount;

            List<IngestionChunk<string>> enrichedChildren = new(childChunks.Count);
            for (int c = 0; c < childChunks.Count; c++)
            {
                IngestionChunk<string> rawChild = childChunks[c];
                string childId = $"{docId}#p{parentIndex}_c{c}";

                IngestionChunk<string> childOut = ChunkCopy.With(rawChild, rawChild.Content, document, parentChunk.Context);
                AlignTableMetadata(childOut.Metadata, parentChunk.Metadata);
                childOut.Metadata[ChunkMetadataKeys.ChunkRole] = ChunkMetadataKeys.ChildRole;
                childOut.Metadata[ChunkMetadataKeys.ChunkId] = childId;
                childOut.Metadata[ChunkMetadataKeys.ParentId] = parentId;
                childOut.Metadata[ChunkMetadataKeys.ChildIndex] = c;
                childOut.Metadata[ChunkMetadataKeys.ChildCount] = childChunks.Count;
                childOut.Metadata[ChunkMetadataKeys.ParentTokenCount] = parentTokenCount;

                enrichedChildren.Add(childOut);
            }

            switch (_emissionMode)
            {
                case HierarchicalEmissionMode.Interleaved:
                    yield return parentOut;
                    foreach (IngestionChunk<string> child in enrichedChildren)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return child;
                    }
                    break;

                case HierarchicalEmissionMode.ChildrenOnly:
                    foreach (IngestionChunk<string> child in enrichedChildren)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Children are cut from the parent text alone, so any table keys they carry describe the parent text rather than
    /// the source table. A child of a table part inherits the parent's table position instead, with its row range
    /// shifted to the parent's rows. Every table part carries all four table keys.
    /// </summary>
    private static void AlignTableMetadata(IDictionary<string, object> child, IDictionary<string, object> parent)
    {
        if (!parent.TryGetValue(ChunkMetadataKeys.IsTablePart, out object? isTablePart) || isTablePart is not true)
        {
            return;
        }

        bool childContinues = child.TryGetValue(ChunkMetadataKeys.IsTableContinuation, out object? continuation) &&
            continuation is true;
        child[ChunkMetadataKeys.IsTablePart] = true;
        child[ChunkMetadataKeys.IsTableContinuation] = parent[ChunkMetadataKeys.IsTableContinuation] is true || childContinues;
        child[ChunkMetadataKeys.TablePartIndex] = parent[ChunkMetadataKeys.TablePartIndex];

        string parentRange = (string)parent[ChunkMetadataKeys.TableRowRange];
        (int parentFirst, _) = ParseRange(parentRange);
        (int childFirst, int childLast) = child.TryGetValue(ChunkMetadataKeys.TableRowRange, out object? childRange)
            ? ParseRange((string)childRange)
            : (0, 0);
        child[ChunkMetadataKeys.TableRowRange] = parentFirst > 0 && childFirst > 0
            ? $"{parentFirst + childFirst - 1}-{parentFirst + childLast - 1}"
            : parentRange;
    }

    private static (int First, int Last) ParseRange(string range)
    {
        int dash = range.IndexOf('-', StringComparison.Ordinal);
        return (int.Parse(range.AsSpan(0, dash), System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(range.AsSpan(dash + 1), System.Globalization.CultureInfo.InvariantCulture));
    }
}
