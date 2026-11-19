using AwesomeAssertions;
using PinkRooster.Extensions.DataIngestion.Samples;

namespace PinkRooster.Extensions.DataIngestion.Samples.Tests;

public sealed class SampleTests
{
    [Fact]
    public async Task RunAsync_ExecutesPipelineSuccessfully()
    {
        int totalChunks = await Program.RunAsync();
        totalChunks.Should().BeGreaterThan(0);
    }
}
