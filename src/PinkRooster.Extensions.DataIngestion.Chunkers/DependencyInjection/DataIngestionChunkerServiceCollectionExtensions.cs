using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;

namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>Registers the PinkRooster chunkers with a dependency injection container.</summary>
public static class DataIngestionChunkerServiceCollectionExtensions
{
    /// <summary>Registers a configured singleton recursive chunker as an ingestion chunker for strings.</summary>
    /// <param name="services">The service collection to add the chunker to.</param>
    /// <param name="options">The options used to construct and configure the chunker.</param>
    /// <returns>The service collection for chaining registrations.</returns>
    public static IServiceCollection AddRecursiveTokenChunker(
        this IServiceCollection services, RecursiveChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton<IngestionChunker<string>>(new RecursiveTokenChunker(options));
    }
}
