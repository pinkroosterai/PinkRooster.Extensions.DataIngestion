namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>
/// Configuration options for <see cref="TextDocumentReader"/>.
/// </summary>
public sealed class TextDocumentReaderOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether Markdown heading lines outside of fenced code blocks
    /// are extracted as <see cref="Microsoft.Extensions.DataIngestion.IngestionDocumentHeader"/> elements.
    /// Default is <see langword="true"/>.
    /// </summary>
    public bool ExtractMarkdownHeadings { get; set; } = true;
}
