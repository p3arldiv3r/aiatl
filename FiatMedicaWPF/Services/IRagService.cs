namespace FiatMedica.Services;

public interface IRagService
{
    Task InitializeAsync(CancellationToken ct = default);
    Task AddPdfDocumentAsync(string filePath, CancellationToken ct = default);
    Task AddChatSummaryAsync(string sessionId, string summary, CancellationToken ct = default);
    Task<string> RetrieveContextAsync(string query, int topK = 3, CancellationToken ct = default);
    Task<bool> IsReadyAsync();
}