using AwesomeAssertions;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static PinkRooster.Extensions.DataIngestion.Chunkers.Tests.RecursiveTokenChunkerTests;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class PipelineTests
{
    [Fact]
    public async Task DirectAndRegisteredChunkersRunInTheLocalIngestionPipeline()
    {
        var token = TestContext.Current.CancellationToken;
        var staging = Directory.CreateTempSubdirectory("recursive-ingestion-");
        try
        {
            string path = Path.Combine(staging.FullName, "fixture.md");
            await File.WriteAllTextAsync(path, "# Dispatch\nThe dispatch calendar calculates the next date.", token);
            var options = Options(5);
            using var services = new ServiceCollection().AddRecursiveTokenChunker(options).BuildServiceProvider();
            var configured = services.GetRequiredService<IngestionChunker<string>>();

            var direct = await RunAsync(new RecursiveTokenChunker(options), new FileInfo(path), token);
            var registered = await RunAsync(configured, new FileInfo(path), token);

            direct.Should().HaveCountGreaterThan(1);
            registered.Select(chunk => (chunk.Content, chunk.Context)).Should()
                .Equal(direct.Select(chunk => (chunk.Content, chunk.Context)));
            registered.Select(chunk => chunk.Context).Should().OnlyContain(context => context == "# Dispatch");
            AssertBudget(registered, 5);
        }
        finally
        {
            staging.Delete(recursive: true);
        }
    }

    [Fact]
    public void RegistrationValidatesItsArguments()
    {
        Action missingServices = () => DataIngestionChunkerServiceCollectionExtensions
            .AddRecursiveTokenChunker(null!, Options(5));
        Action missingOptions = () => new ServiceCollection().AddRecursiveTokenChunker((RecursiveChunkerOptions)null!);
        Action missingFactory = () => new ServiceCollection().AddRecursiveTokenChunker((Func<IServiceProvider, RecursiveChunkerOptions>)null!);

        missingServices.Should().Throw<ArgumentNullException>().WithParameterName("services");
        missingOptions.Should().Throw<ArgumentNullException>().WithParameterName("options");
        missingFactory.Should().Throw<ArgumentNullException>().WithParameterName("optionsFactory");
    }

    [Fact]
    public void FactoryRegistrationsResolveConfiguredInstancesFromContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.ML.Tokenizers.Tokenizer>(TokenizerFixture.Instance);
        services.AddRecursiveTokenChunker(sp => new RecursiveChunkerOptions(sp.GetRequiredService<Microsoft.ML.Tokenizers.Tokenizer>()));
        services.AddMarkdownTableChunker(sp => new TableChunkerOptions(sp.GetRequiredService<Microsoft.ML.Tokenizers.Tokenizer>()));
        services.AddHierarchicalTokenChunker(sp => new HierarchicalChunkerOptions(sp.GetRequiredService<Microsoft.ML.Tokenizers.Tokenizer>()));
        using var provider = services.BuildServiceProvider();

        var chunkers = provider.GetServices<IngestionChunker<string>>().ToList();
        chunkers.Should().HaveCount(3);
        chunkers[0].Should().BeOfType<RecursiveTokenChunker>();
        chunkers[1].Should().BeOfType<MarkdownTableChunker>();
        chunkers[2].Should().BeOfType<HierarchicalTokenChunker>();
    }

    [Fact]
    public async Task DirectAndRegisteredContextProcessorsEnrichTheLocalPipeline()
    {
        var token = TestContext.Current.CancellationToken;
        var staging = Directory.CreateTempSubdirectory("contextual-ingestion-");
        try
        {
            var file = new FileInfo(Path.Combine(staging.FullName, "fixture.md"));
            await File.WriteAllTextAsync(file.FullName,
                "# Dispatch\nThe dispatch calendar calculates the next date.", token);
            using var client = ContextualChunkProcessorTests.Reply("Dispatch policy");
            var options = ContextualChunkProcessorTests.Options(client);
            options.EmbeddingTokenizer = TokenizerFixture.Instance;
            options.MaxEnrichedTokens = 12;
            using var services = new ServiceCollection().AddContextualChunkProcessor(options).BuildServiceProvider();
            var registered = services.GetRequiredService<IngestionChunkProcessor<string>>();

            var direct = await RunAsync(NewChunker(5), file, token, new ContextualChunkProcessor(options));
            var injected = await RunAsync(NewChunker(5), file, token, registered);

            injected.Select(chunk => (chunk.Content, chunk.Context)).Should()
                .Equal(direct.Select(chunk => (chunk.Content, chunk.Context)));
            injected.Should().OnlyContain(chunk => chunk.Content.StartsWith("Dispatch policy\n\n", StringComparison.Ordinal));
            AssertBudget(injected, 12);
        }
        finally
        {
            staging.Delete(recursive: true);
        }
    }

    [Fact]
    public void ContextualRegistrationValidatesItsArguments()
    {
        using var client = ContextualChunkProcessorTests.Reply("unused");
        Action missingServices = () => DataIngestionChunkerServiceCollectionExtensions
            .AddContextualChunkProcessor(null!, ContextualChunkProcessorTests.Options(client));
        Action missingOptions = () => new ServiceCollection().AddContextualChunkProcessor((ContextualChunkerOptions)null!);
        Action missingFactory = () => new ServiceCollection().AddContextualChunkProcessor((Func<IServiceProvider, ContextualChunkerOptions>)null!);

        missingServices.Should().Throw<ArgumentNullException>().WithParameterName("services");
        missingOptions.Should().Throw<ArgumentNullException>().WithParameterName("options");
        missingFactory.Should().Throw<ArgumentNullException>().WithParameterName("optionsFactory");
    }

    private static async Task<IReadOnlyList<IngestionChunk<string>>> RunAsync(
        IngestionChunker<string> chunker, FileInfo file, CancellationToken cancellationToken,
        IngestionChunkProcessor<string>? processor = null)
    {
        var reader = new FixtureReader();
        var writer = new CapturingWriter();
        using var pipeline = new IngestionPipeline<string>(reader, chunker, writer);
        if (processor is not null)
        {
            pipeline.ChunkProcessors.Add(processor);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, TestContext.Current.CancellationToken);
        var results = new List<IngestionResult>();
        await foreach (var result in pipeline.ProcessAsync([file], linked.Token))
        {
            results.Add(result);
        }

        results.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
        writer.Chunks.Should().NotBeEmpty().And.OnlyContain(chunk => ReferenceEquals(chunk.Document, reader.Document));
        return writer.Chunks;
    }

    private sealed class FixtureReader : IngestionDocumentReader
    {
        internal IngestionDocument? Document { get; private set; }

        public override async Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType,
            CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(source, leaveOpen: true);
            string heading = await reader.ReadLineAsync(cancellationToken)
                ?? throw new InvalidDataException("The fixture requires a heading.");
            string body = await reader.ReadToEndAsync(cancellationToken);
            var section = new IngestionDocumentSection();
            section.Elements.Add(new IngestionDocumentHeader(heading) { Level = 1 });
            section.Elements.Add(new IngestionDocumentParagraph(body));
            var document = new IngestionDocument(identifier);
            document.Sections.Add(section);
            Document = document;
            return document;
        }
    }

    private sealed class CapturingWriter : IngestionChunkWriter<string>
    {
        internal List<IngestionChunk<string>> Chunks { get; } = [];

        public override async Task WriteAsync(IAsyncEnumerable<IngestionChunk<string>> chunks,
            CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                Chunks.Add(chunk);
            }
        }
    }
}
