namespace FiatMedica.Domain;

public sealed class LlmSettings
{
    public string ModelPath { get; init; } = $"{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}\\BioMistral-7B.Q8_0.gguf";
    public string PatientProfilePath { get; init; } = @"C:\Users\hugga\Downloads\PatientProfile.pdf";

    public uint ContextSize { get; init; } = 4096;
    public int GpuLayerCount { get; init; } = 20;
    public float Temperature { get; init; } = 0f;
    public int MaxTokens { get; init; } = 512;
    public float TopP { get; init; } = 0.95f;
    public int TopK { get; init; } = 40;
    public float RepeatPenalty { get; init; } = 1.1f;
    public string SystemPrompt { get; init; } = "You are BioMistral in a clinical QA app. Speak as a neutral third party addressing the patient directly. Use provided context if possible. If unsure, say so.";
}