using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

internal sealed class DocumentContextCache(TokenBudget budget, int overlap)
{
    private IngestionDocument? _document;
    private DocumentContext? _context;

    internal DocumentContext Get(IngestionDocument document, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(_document, document))
        {
            _context = new DocumentContext(document, budget, overlap, cancellationToken);
            _document = document;
        }

        return _context!;
    }
}
