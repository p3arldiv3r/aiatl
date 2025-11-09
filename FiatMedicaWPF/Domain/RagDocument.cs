namespace FiatMedica.Domain;

public sealed class RagDocument
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Content { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DocumentType Type { get; init; }
    public DateTime AddedAt { get; init; } = DateTime.Now;
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public enum DocumentType
{
    Pdf,
    ChatSummary,
    Text
}