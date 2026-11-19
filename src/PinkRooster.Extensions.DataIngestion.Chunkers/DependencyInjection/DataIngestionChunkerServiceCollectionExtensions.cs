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
        ArgumentNullException.ThrowIfNull(options);
        return services.AddSingleton<IngestionChunker<string>>(new RecursiveTokenChunker(options));
    }

    /// <summary>Registers a configured singleton Markdown table chunker as an ingestion chunker for strings.</summary>
    /// <param name="services">The service collection to add the chunker to.</param>
    /// <param name="options">The options used to construct and configure the chunker.</param>
    /// <returns>The service collection for chaining registrations.</returns>
    public static IServiceCollection AddMarkdownTableChunker(
        this IServiceCollection services, TableChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        return services.AddSingleton<IngestionChunker<string>>(new MarkdownTableChunker(options));
    }

    /// <summary>Registers a configured singleton contextual processor for string chunks.</summary>
    /// <param name="services">The service collection to add the processor to.</param>
    /// <param name="options">The options used to construct and configure the processor.</param>
    /// <returns>The service collection for chaining registrations.</returns>
    public static IServiceCollection AddContextualChunkProcessor(
        this IServiceCollection services, ContextualChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        return services.AddSingleton<IngestionChunkProcessor<string>>(new ContextualChunkProcessor(options));
    }
}
