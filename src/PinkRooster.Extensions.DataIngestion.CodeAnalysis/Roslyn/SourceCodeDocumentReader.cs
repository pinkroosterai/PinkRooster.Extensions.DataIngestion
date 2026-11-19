using Microsoft.Extensions.DataIngestion;

namespace PinkRooster.Extensions.DataIngestion.CodeAnalysis;

/// <summary>Reads a C# source stream into a single section without interpreting it as Markdown.</summary>
public sealed class SourceCodeDocumentReader : IngestionDocumentReader
{
    internal const string SourceMarker = "pinkrooster_raw_csharp";

    /// <inheritdoc/>
    public override async Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        using StreamReader reader = new StreamReader(source, leaveOpen: true);
        string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        IngestionDocument document = new IngestionDocument(identifier);
        IngestionDocumentSection section = new IngestionDocumentSection();
        IngestionDocumentParagraph element = new IngestionDocumentParagraph(text);
        element.Metadata[SourceMarker] = true;
        section.Elements.Add(element);
        document.Sections.Add(section);
        return document;
    }
}
