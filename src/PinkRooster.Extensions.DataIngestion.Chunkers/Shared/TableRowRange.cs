namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Formats and parses 1-based table row range strings (<c>"1-5"</c>, <c>"0-0"</c>).</summary>
internal static class TableRowRange
{
    internal const string None = "0-0";

    internal static string Format(int startRow, int endRow) =>
        startRow > 0 ? $"{startRow}-{endRow}" : None;

    internal static (int First, int Last) Parse(string? range)
    {
        if (string.IsNullOrEmpty(range))
        {
            return (0, 0);
        }

        int dash = range.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || dash == range.Length - 1)
        {
            return (0, 0);
        }

        if (int.TryParse(range.AsSpan(0, dash), System.Globalization.CultureInfo.InvariantCulture, out int first) &&
            int.TryParse(range.AsSpan(dash + 1), System.Globalization.CultureInfo.InvariantCulture, out int last))
        {
            return (first, last);
        }

        return (0, 0);
    }
}
