using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace QuerySpace;

internal sealed class QuerySpaceChatClient : IChatClient
{
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _complete;
    private readonly ChatClientMetadata _metadata;

    public QuerySpaceChatClient(
        string provider,
        string model,
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> complete)
    {
        _complete = complete;
        _metadata = new ChatClientMetadata(provider, null, model);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _complete(messages, options, cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await _complete(messages, options, cancellationToken);
        foreach (var message in response.Messages)
            yield return new ChatResponseUpdate(message.Role, message.Contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? _metadata :
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
