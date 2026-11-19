using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;

namespace PinkRooster.Extensions.DataIngestion.CodeAnalysis;

/// <summary>Registers the C# ingestion chunker with a dependency injection container.</summary>
public static class CodeAnalysisChunkerServiceCollectionExtensions
{
    /// <summary>Registers a configured singleton Roslyn chunker for string ingestion pipelines.</summary>
    /// <param name="services">The collection to which the chunker is added.</param>
    /// <param name="options">The options for the registered chunker.</param>
    /// <returns>The service collection for chaining registrations.</returns>
    public static IServiceCollection AddRoslynCodeChunker(this IServiceCollection services, RoslynChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton<IngestionChunker<string>>(new RoslynCodeChunker(options));
    }
}
