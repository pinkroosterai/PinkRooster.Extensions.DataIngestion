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
        ArgumentNullException.ThrowIfNull(options);
        return services.AddSingleton<IngestionChunker<string>>(new RoslynCodeChunker(options));
    }

    /// <summary>Registers a singleton Roslyn chunker using an options factory.</summary>
    /// <param name="services">The collection to which the chunker is added.</param>
    /// <param name="optionsFactory">Factory producing the chunker configuration from service provider.</param>
    /// <returns>The service collection for chaining registrations.</returns>
    public static IServiceCollection AddRoslynCodeChunker(
        this IServiceCollection services, Func<IServiceProvider, RoslynChunkerOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);
        return services.AddSingleton<IngestionChunker<string>>(sp => new RoslynCodeChunker(optionsFactory(sp)));
    }
}
