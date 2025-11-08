namespace FiatMedica.Services;

public interface ILlmEngine
{
    bool IsLoaded { get; }
    Task InitializeAsync(CancellationToken ct = default);
    IAsyncEnumerable<string> StreamChatAsync(string userMessage, CancellationToken ct = default);
    void ResetConversation(string? newSystemPrompt = null);
}