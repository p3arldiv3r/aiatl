namespace FiatMedica.Domain;

public sealed class LlmSettings
{
    public string ModelPath { get; init; } = @"C:\models\BioMistral-7B-Q4_K_M.gguf";
    public uint ContextSize { get; init; } = 4096;
    public int GpuLayerCount { get; init; } = 20; // 0 = CPU only; tune for your VRAM
    public float Temperature { get; init; } = 0.2f;
    public int MaxTokens { get; init; } = 512;
    public float TopP { get; init; } = 0.95f;
    public int TopK { get; init; } = 40;
    public float RepeatPenalty { get; init; } = 1.1f;
    public string SystemPrompt { get; init; } = "You are BioMistral in a clinical QA app. Use only provided context. If unsure, say so.";
}