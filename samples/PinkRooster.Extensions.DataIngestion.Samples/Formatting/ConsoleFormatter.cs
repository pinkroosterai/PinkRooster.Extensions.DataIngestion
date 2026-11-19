using System.Text.RegularExpressions;
using Microsoft.Extensions.DataIngestion;
using PinkRooster.Extensions.DataIngestion.Samples.Scenarios;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace PinkRooster.Extensions.DataIngestion.Samples.Formatting;

/// <summary>
/// Provides rich, structured ANSI/Unicode console card formatting for chunk inspection and scenario summaries.
/// Built with Spectre.Console for responsive layouts, token capacity heat meters, and contextual callout panels.
/// </summary>
public static partial class ConsoleFormatter
{
    [GeneratedRegex(@"^#{1,6}\s*")]
    private static partial Regex HeadingRegex();

    /// <summary>
    /// Prints the application showcase banner.
    /// </summary>
    public static void PrintAppBanner()
    {
        int width = GetClampedWidth();

        var bannerGrid = new Grid();
        bannerGrid.AddColumn(new GridColumn().Centered());
        bannerGrid.AddRow(new Markup("[bold cyan1]PinkRooster.Extensions.DataIngestion[/] [dim]•[/] [bold white]RAG Chunker Showcase[/] [dim](.NET 10)[/]"));
        bannerGrid.AddRow(new Markup("[grey78]Structure-Aware Recursive Chunking, Contextual Retrieval & Roslyn AST Slicing[/]"));

        var bannerPanel = new Panel(bannerGrid)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.SteelBlue),
            Padding = new Padding(1, 0, 1, 0),
            Width = width,
        };

        AnsiConsole.WriteLine();
        AnsiConsole.Write(bannerPanel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Prints the header and description for an individual scenario.
    /// </summary>
    /// <param name="name">Scenario name.</param>
    /// <param name="description">Scenario description.</param>
    public static void PrintScenarioHeader(string name, string description)
    {
        var rule = new Rule($"[bold yellow]SCENARIO: {Markup.Escape(name.ToUpperInvariant())}[/]")
        {
            Justification = Justify.Left,
            Style = new Style(Color.Gold3_1),
        };

        AnsiConsole.WriteLine();
        AnsiConsole.Write(rule);
        AnsiConsole.MarkupLine($"[grey]Description:[/] [italic white]{Markup.Escape(description)}[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Prints a formatted card representing an individual chunk.
    /// </summary>
    /// <param name="scenario">Scenario name or chunker category.</param>
    /// <param name="index">1-based chunk index.</param>
    /// <param name="chunk">The ingestion chunk.</param>
    /// <param name="tokenCount">Calculated token count.</param>
    /// <param name="maxTokens">Configured token ceiling.</param>
    public static void PrintChunk(string scenario, int index, IngestionChunk<string> chunk, int tokenCount, int maxTokens)
    {
        int width = GetClampedWidth();

        // 1. Build Token Capacity Heat-Meter
        double ratio = maxTokens > 0 ? (double)tokenCount / maxTokens : 0;
        string tokenColor = ratio switch
        {
            > 0.95 => "bold red",
            > 0.75 => "bold yellow",
            _ => "bold green",
        };

        int barTotal = 8;
        int barFilled = Math.Clamp((int)Math.Round(ratio * barTotal), 0, barTotal);
        string bar = new string('█', barFilled) + new string('░', barTotal - barFilled);
        string tokenBadge = $"[{tokenColor}][[{bar}]] {tokenCount}/{maxTokens} tok ({ratio:P0})[/]";

        // 2. Prepare card body items
        var items = new List<IRenderable>();

        // 2a. Context Breadcrumbs
        if (!string.IsNullOrWhiteSpace(chunk.Context))
        {
            string formattedContext = FormatBreadcrumb(chunk.Context);
            items.Add(new Markup($"[dim cyan]Context:[/] {formattedContext}"));
            items.Add(new Text(string.Empty));
        }

        // 2b. Situational Context Callout (Anthropic Contextual Retrieval)
        if (chunk.HasMetadata && chunk.Metadata.TryGetValue("contextual_header", out var headerObj) && headerObj is string headerText && !string.IsNullOrWhiteSpace(headerText))
        {
            var calloutGrid = new Grid();
            calloutGrid.AddColumn();
            calloutGrid.AddRow(new Text(headerText.Trim(), new Style(Color.White, decoration: Decoration.Italic)));

            var situationalPanel = new Panel(calloutGrid)
            {
                Header = new PanelHeader("[bold cyan1]💡 Situational Retrieval Context (LLM Enriched)[/]"),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.DarkCyan),
                Padding = new Padding(1, 0, 1, 0),
            };

            items.Add(situationalPanel);
            items.Add(new Text(string.Empty));
        }

        // 2c. Table Continuation & Metadata Callout
        if (chunk.HasMetadata && chunk.Metadata.TryGetValue("IsTablePart", out var isTable) && isTable is true)
        {
            var isContinuation = chunk.Metadata.TryGetValue("IsTableContinuation", out var isCont) && isCont is true;
            var partIndex = chunk.Metadata.TryGetValue("TablePartIndex", out var part) ? part : 0;
            var range = chunk.Metadata.TryGetValue("TableRowRange", out var r) ? r : "all";
            string tableLabel = isContinuation
                ? $"[bold orange3]📊 Table Part #{partIndex} (Continuation • Rows: {range})[/]"
                : $"[bold springgreen3]📊 Table Part #{partIndex} (Initial • Rows: {range})[/]";
            items.Add(new Markup(tableLabel));
            items.Add(new Text(string.Empty));
        }

        // 2c. Chunk Content
        string content = chunk.Content.TrimEnd();
        items.Add(new Text(content, new Style(Color.White)));

        // 2d. Metadata Chips / Badges
        if (chunk.HasMetadata && chunk.Metadata.Count > 0)
        {
            var metaChips = new List<string>();
            foreach (var kvp in chunk.Metadata)
            {
                // Skip contextual_header since it is showcased in its own callout panel
                if (kvp.Key.Equals("contextual_header", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string valueStr = kvp.Value switch
                {
                    IEnumerable<string> metaList => string.Join(", ", metaList),
                    _ => kvp.Value?.ToString() ?? "null",
                };

                if (valueStr.Length > 50)
                {
                    valueStr = valueStr[..47] + "...";
                }

                metaChips.Add($"[grey70 on grey23] {Markup.Escape(kvp.Key)}: [/][bold white on grey23]{Markup.Escape(valueStr)} [/]");
            }

            if (metaChips.Count > 0)
            {
                items.Add(new Text(string.Empty));
                items.Add(new Markup($"[dim grey]Metadata:[/] {string.Join("  ", metaChips)}"));
            }
        }

        // 3. Construct Outer Card Panel
        var cardHeader = new PanelHeader($" [bold cyan]{Markup.Escape(scenario)}[/] [dim]•[/] [bold yellow]Chunk #{index}[/]  {tokenBadge} ");
        var cardPanel = new Panel(new Rows(items))
        {
            Header = cardHeader,
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey42),
            Padding = new Padding(1, 0, 1, 0),
            Width = width,
        };

        AnsiConsole.WriteLine();
        AnsiConsole.Write(cardPanel);
    }

    /// <summary>
    /// Prints a scenario completion summary badge.
    /// </summary>
    /// <param name="result">The scenario result.</param>
    /// <param name="elapsed">Execution elapsed time.</param>
    public static void PrintScenarioSummary(ScenarioResult result, TimeSpan elapsed)
    {
        double avgTokens = result.ChunkCount > 0 ? (double)result.TotalTokens / result.ChunkCount : 0;
        string timing = elapsed > TimeSpan.Zero ? $" in [bold cyan]{elapsed.TotalMilliseconds:F1}ms[/]" : string.Empty;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[bold green]✔[/] [bold white]{Markup.Escape(result.ScenarioName)}[/]{timing}: " +
            $"[bold yellow]{result.ChunkCount}[/] chunks, [bold green]{result.TotalTokens}[/] tokens " +
            $"[dim](avg [bold magenta]{avgTokens:F1}[/] tokens/chunk)[/]");
    }

    /// <summary>
    /// Prints an overall execution summary table across all executed scenarios.
    /// </summary>
    /// <param name="results">List of all scenario results.</param>
    /// <param name="totalElapsed">Total elapsed time.</param>
    public static void PrintOverallSummary(IReadOnlyList<ScenarioResult> results, TimeSpan totalElapsed)
    {
        int totalChunks = results.Sum(r => r.ChunkCount);
        int totalTokens = results.Sum(r => r.TotalTokens);
        double overallAvg = totalChunks > 0 ? (double)totalTokens / totalChunks : 0;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey42)
            .Title("[bold white]RAG Chunker Showcase Summary[/]", new Style(Color.Gold3_1));

        table.AddColumn(new TableColumn("[bold cyan]Scenario[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold yellow]Chunks[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold green]Tokens[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold magenta]Avg Tokens/Chunk[/]").RightAligned());

        foreach (var r in results)
        {
            double avg = r.ChunkCount > 0 ? (double)r.TotalTokens / r.ChunkCount : 0;
            table.AddRow(
                $"[white]{Markup.Escape(r.ScenarioName)}[/]",
                $"[yellow]{r.ChunkCount}[/]",
                $"[green]{r.TotalTokens}[/]",
                $"[magenta]{avg:F1}[/]");
        }

        table.AddEmptyRow();
        table.AddRow(
            "[bold white]TOTAL[/]",
            $"[bold yellow]{totalChunks}[/]",
            $"[bold green]{totalTokens}[/]",
            $"[bold magenta]{overallAvg:F1}[/]");

        table.Caption($"[dim]Total execution time:[/] [bold cyan]{totalElapsed.TotalMilliseconds:F1}ms[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static int GetClampedWidth()
    {
        int terminalWidth = AnsiConsole.Profile.Width;
        if (terminalWidth <= 0)
        {
            terminalWidth = 85;
        }

        return Math.Clamp(terminalWidth, 80, 110);
    }

    private static string FormatBreadcrumb(string rawContext)
    {
        // Convert Markdown headers or pipe-delimited context into clean stylized chevrons
        string[] parts = rawContext.Split(new[] { " > ", " | " }, StringSplitOptions.RemoveEmptyEntries);
        var cleanParts = parts.Select(p =>
        {
            string trimmed = HeadingRegex().Replace(p.Trim(), string.Empty);
            return $"[bold]{Markup.Escape(trimmed)}[/]";
        });

        return string.Join(" [dim cyan]›[/] ", cleanParts);
    }
}
