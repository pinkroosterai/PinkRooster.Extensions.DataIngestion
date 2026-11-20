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

    /// <summary>Registers a singleton recursive chunker using an options factory.</summary>
    /// <param name="services">The service collection to add the chunker to.</param>
    /// <param name="optionsFactory">Factory producing the chunker configuration from service provider.</param>
    /// <returns>The service collection for chaining registrations.</returns>
    public static IServiceCollection AddRecursiveTokenChunker(
        this IServiceCollection services, Func<IServiceProvider, RecursiveChunkerOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);
        return services.AddSingleton<IngestionChunker<string>>(sp => new RecursiveTokenChunker(optionsFactory(sp)));
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

    /// <summary>Registers a singleton Markdown table chunker using an options factory.</summary>
    /// <param name="services">The service collection to add the chunker to.</param>
    /// <param name="optionsFactory">Factory producing the chunker configuration from service provider.</param>
    /// <returns>The service collection for chaining registrations.</returns>
    public static IServiceCollection AddMarkdownTableChunker(
        this IServiceCollection services, Func<IServiceProvider, TableChunkerOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);
        return services.AddSingleton<IngestionChunker<string>>(sp => new MarkdownTableChunker(optionsFactory(sp)));
    }

    /// <summary>Registers a configured singleton hierarchical parent-child chunker as an ingestion chunker for strings.</summary>
    /// <param name="services">The service collection to add the chunker to.</param>
    /// <param name="options">The options used to construct and configure the chunker.</param>
    /// <returns>The service collection for chaining registrations.</returns>
    public static IServiceCollection AddHierarchicalTokenChunker(
        this IServiceCollection services, HierarchicalChunkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        return services.AddSingleton<IngestionChunker<string>>(new HierarchicalTokenChunker(options));
    }

    /// <summary>Registers a singleton hierarchical parent-child chunker using an options factory.</summary>
    /// <param name="services">The service collection to add the chunker to.</param>
    /// <param name="optionsFactory">Factory producing the chunker configuration from service provider.</param>
    /// <returns>The service collection for chaining registrations.</returns>
    public static IServiceCollection AddHierarchicalTokenChunker(
        this IServiceCollection services, Func<IServiceProvider, HierarchicalChunkerOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);
        return services.AddSingleton<IngestionChunker<string>>(sp => new HierarchicalTokenChunker(optionsFactory(sp)));
    }

    /// <summary>Registers a configured singleton contextual processor for string chunks.</summary>
    /// <param name="services">The service collection to add the processor to.</param>
    /// <param name="options">The options used to construct and configure the processor.</param>
    /// <returns>The service collection for chaining registrations.</returns>
    public static IServiceCollection AddContextualChunkProcessor(
        this IServiceCollection services, ContextualChunkProcessorOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        return services.AddSingleton<IngestionChunkProcessor<string>>(new ContextualChunkProcessor(options));
    }

    /// <summary>Registers a singleton contextual processor using an options factory.</summary>
    /// <param name="services">The service collection to add the processor to.</param>
    /// <param name="optionsFactory">Factory producing the processor configuration from service provider.</param>
    /// <returns>The service collection for chaining registrations.</returns>
    public static IServiceCollection AddContextualChunkProcessor(
        this IServiceCollection services, Func<IServiceProvider, ContextualChunkProcessorOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);
        return services.AddSingleton<IngestionChunkProcessor<string>>(sp => new ContextualChunkProcessor(optionsFactory(sp)));
    }
}
