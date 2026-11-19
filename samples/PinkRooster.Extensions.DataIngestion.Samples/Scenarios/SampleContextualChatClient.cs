using Microsoft.Extensions.AI;

namespace PinkRooster.Extensions.DataIngestion.Samples.Scenarios;

/// <summary>
/// A simulated <see cref="IChatClient"/> providing deterministic, offline contextual headers
/// for sample demonstrations without requiring live API keys or network calls.
/// </summary>
public sealed class SampleContextualChatClient : IChatClient
{
    private int _callCount;

    /// <summary>Gets the number of completion calls received.</summary>
    public int CallCount => _callCount;

    /// <inheritdoc/>
    public ChatClientMetadata Metadata => new("PinkRoosterSampleClient");

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);

        string promptText = string.Join(" ", messages.Select(m => m.Text ?? string.Empty));
        string header = GenerateHeader(promptText);

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, header));
        return Task.FromResult(response);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Streaming is not required for sample chunk enrichment.");

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc/>
    public void Dispose() { }

    private static string GenerateHeader(string prompt)
    {
        if (prompt.Contains("failover", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("standby", StringComparison.OrdinalIgnoreCase))
        {
            return "Enterprise incident response protocol: secondary region failover and replication drain rules.";
        }

        if (prompt.Contains("escalation", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("outage", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("bridge", StringComparison.OrdinalIgnoreCase))
        {
            return "Enterprise incident response protocol: Sev-1 bridge notification SLAs and communication cadence.";
        }

        if (prompt.Contains("Logistics", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("dispatch", StringComparison.OrdinalIgnoreCase))
        {
            return "Logistics dispatch guide: standard cutoff deadlines and carrier fulfillment commitments.";
        }

        return "Enterprise operational guidelines: service-level continuity and system resilience policy.";
    }
}
