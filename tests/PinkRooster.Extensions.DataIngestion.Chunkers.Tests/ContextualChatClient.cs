using Microsoft.Extensions.AI;

namespace PinkRooster.Extensions.DataIngestion.Chunkers.Tests;

internal sealed class ContextualChatClient(
    Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> respond) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => respond(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
