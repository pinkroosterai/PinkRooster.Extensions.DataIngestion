using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using static PinkRooster.Extensions.DataIngestion.Chunkers.Tests.ContextualChunkProcessorTests;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

public sealed class ContextualDiagnosticsTests
{
    [Fact]
    public async Task EveryOutcomeIsLoggedWhenALoggerFactoryIsSet()
    {
        using var client = Reply("Situating header");
        var loggers = new CollectingLoggerFactory();
        var options = Options(client);
        options.LoggerFactory = loggers;

        await ReadAsync(new ContextualChunkProcessor(options), Chunk("Parcels leave the north station at dawn."));

        loggers.Messages.Should().ContainSingle().Which.Should().Contain("enriched");
    }

    [Fact]
    public async Task AHeaderWhoseFirstScalarDoesNotFitIsOmitted()
    {
        // The rare character alone costs more than the single token left beside the chunk.
        using var client = Reply("\u2F9F\u2F9F situates the chunk");
        var original = Chunk("word");
        var options = Options(client);
        options.EmbeddingTokenizer = TokenizerFixture.Instance;
        options.MaxEnrichedTokens = Count(original.Content) + Count("\n\n") + 1;

        var result = (await ReadAsync(new ContextualChunkProcessor(options), original)).Should().ContainSingle().Which;

        result.Should().BeSameAs(original);
    }

    private sealed class CollectingLoggerFactory : ILoggerFactory
    {
        internal List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(Messages);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class CollectingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (messages)
            {
                messages.Add(formatter(state, exception));
            }
        }
    }
}
