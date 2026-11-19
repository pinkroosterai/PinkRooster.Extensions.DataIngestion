using System.Text.Json;
using PinkRooster.Extensions.DataIngestion.Benchmarks;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancel;
try
{
    return await BenchmarkConsole.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancel;
}

internal static class BenchmarkConsole
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken)
    {
        if (args is not [SmokeEvaluation.Profile, var corpusRoot])
        {
            await error.WriteLineAsync("Usage: smoke <corpus-directory>");
            return 2;
        }

        BenchmarkReport report;
        int exitCode;
        try
        {
            report = await SmokeEvaluation.RunAsync(corpusRoot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            exitCode = 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            report = new BenchmarkReport("incomplete", SmokeEvaluation.Profile, Reason: "cancelled");
            exitCode = 130;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            report = new BenchmarkReport("failed", SmokeEvaluation.Profile, Reason: failure.Message);
            exitCode = 1;
        }

        await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions));
        return exitCode;
    }
}
