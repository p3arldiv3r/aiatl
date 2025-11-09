namespace FiatMedica.Domain;

public sealed class RagSettings
{
    public string DatabasePath { get; init; } = $"{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}\\FiatMedica\\rag_db.json";
    public string EmbeddingModelPath { get; init; } = $"{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}\\all-MiniLM-L6-v2.Q8_0.gguf";
    public int ChunkSize { get; init; } = 512;
    public int ChunkOverlap { get; init; } = 50;
    public int TopKResults { get; init; } = 3;
    public float SimilarityThreshold { get; init; } = 0.25f;
}