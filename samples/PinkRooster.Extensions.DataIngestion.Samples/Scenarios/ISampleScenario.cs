namespace PinkRooster.Extensions.DataIngestion.Samples.Scenarios;

/// <summary>
/// Defines a runnable sample scenario showcasing data ingestion chunking capabilities.
/// </summary>
public interface ISampleScenario
{
    /// <summary>Gets the unique command-line identifier for this scenario.</summary>
    string Name { get; }

    /// <summary>Gets a concise description of what this scenario showcases.</summary>
    string Description { get; }

    /// <summary>Executes the scenario pipeline.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing summary metrics and generated chunks.</returns>
    Task<ScenarioResult> RunAsync(CancellationToken cancellationToken = default);
}
