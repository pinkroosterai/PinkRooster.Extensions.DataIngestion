using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;
using PinkRooster.Extensions.DataIngestion.Chunkers;
using PinkRooster.Extensions.DataIngestion.CodeAnalysis;

namespace PinkRooster.Extensions.DataIngestion.Benchmarks;

/// <summary>
/// Micro-benchmarks comparing throughput and allocation profiles of chunkers.
/// </summary>
[MemoryDiagnoser]
public class ChunkerThroughputBenchmarks
{
    private Tokenizer _tokenizer = null!;
    private IngestionDocument _proseDocument = null!;
    private IngestionDocument _csharpDocument = null!;
    private DocumentTokenChunker _baselineProseChunker = null!;
    private RecursiveTokenChunker _recursiveChunker = null!;
    private DocumentTokenChunker _baselineCodeChunker = null!;
    private RoslynCodeChunker _roslynChunker = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

        _proseDocument = new IngestionDocument("bench-prose");
        var proseSection = new IngestionDocumentSection();
        proseSection.Elements.Add(new IngestionDocumentHeader("# Benchmark Technical Specification") { Level = 1 });
        proseSection.Elements.Add(new IngestionDocumentParagraph(
            "The data ingestion pipeline provides streaming token-bounded chunking across arbitrary document representations. " +
            "High-throughput enterprise pipelines require zero-allocation span slicing with predictable memory overhead under large ingestion loads."));
        proseSection.Elements.Add(new IngestionDocumentParagraph(
            "Standard orders ship within two business days. Express orders ship the same day when received before noon UTC. " +
            "Special handling applies for international and hazmat consignments."));
        _proseDocument.Sections.Add(proseSection);

        const string csharpSource = """
            namespace Benchmark.Sample;

            public interface IWorkerClock
            {
                DateTimeOffset UtcNow { get; }
            }

            public sealed class TaskScheduler(IWorkerClock clock)
            {
                private readonly List<string> _tasks = [];

                public void Schedule(string name)
                {
                    ArgumentNullException.ThrowIfNull(name);
                    _tasks.Add(name);
                }

                public int ExecuteAll()
                {
                    int count = _tasks.Count;
                    _tasks.Clear();
                    return count;
                }
            }
            """;

        _csharpDocument = new IngestionDocument("bench-code");
        var codeSection = new IngestionDocumentSection();
        codeSection.Elements.Add(new IngestionDocumentParagraph(csharpSource));
        _csharpDocument.Sections.Add(codeSection);

        _baselineProseChunker = new DocumentTokenChunker(new IngestionChunkerOptions(_tokenizer)
        {
            MaxTokensPerChunk = 64,
            OverlapTokens = 0,
        });

        _recursiveChunker = new RecursiveTokenChunker(new RecursiveChunkerOptions(_tokenizer)
        {
            MaxTokensPerChunk = 64,
            ReservedHeaderTokens = 8,
            OverlapTokens = 0,
        });

        _baselineCodeChunker = new DocumentTokenChunker(new IngestionChunkerOptions(_tokenizer)
        {
            MaxTokensPerChunk = 64,
            OverlapTokens = 0,
        });

        _roslynChunker = new RoslynCodeChunker(new RoslynChunkerOptions(_tokenizer)
        {
            MaxTokensPerChunk = 64,
            OverlapTokens = 0,
        });
    }

    [Benchmark(Baseline = true, Description = "DocumentTokenChunker (Prose)")]
    public async Task<int> BaselineProse()
    {
        int count = 0;
        await foreach (var chunk in _baselineProseChunker.ProcessAsync(_proseDocument))
        {
            count += chunk.Content.Length;
        }
        return count;
    }

    [Benchmark(Description = "RecursiveTokenChunker (Prose)")]
    public async Task<int> RecursiveProse()
    {
        int count = 0;
        await foreach (var chunk in _recursiveChunker.ProcessAsync(_proseDocument))
        {
            count += chunk.Content.Length;
        }
        return count;
    }

    [Benchmark(Description = "RoslynCodeChunker (C#)")]
    public async Task<int> RoslynCode()
    {
        int count = 0;
        await foreach (var chunk in _roslynChunker.ProcessAsync(_csharpDocument))
        {
            count += chunk.Content.Length;
        }
        return count;
    }
}
