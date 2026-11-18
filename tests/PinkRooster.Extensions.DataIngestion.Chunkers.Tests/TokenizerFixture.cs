using Microsoft.ML.Tokenizers;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

internal static class TokenizerFixture
{
    internal static TiktokenTokenizer Instance { get; } = TiktokenTokenizer.CreateForModel("gpt-4o");
}
