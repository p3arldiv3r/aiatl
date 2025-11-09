using FiatMedica.Domain;
using LLama;
using LLama.Common;
using LLama.Sampling;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Collections.Generic;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UglyToad.PdfPig; // PDF parsing library
using System.Linq;

namespace FiatMedica.Services;

public sealed class LlamaSharpEngine : ILlmEngine, IAsyncDisposable
{
    private readonly LlmSettings _settings;
    private LLamaWeights? _model;
    private LLamaContext? _context;
    private StatelessExecutor? _executor;

    // --- Keep a simple prompt history ---
    private StringBuilder _chatHistory = new StringBuilder();

    // --- RAG-related fields ---
    private List<string> _ragChunks = new List<string>();
    private List<float[]> _ragEmbeddings = new List<float[]>(); // embedding vectors
    private int _chunkSize = 500; // chars per chunk
    private int _embeddingDim = 768; // embedding dimension (example)

    public bool IsLoaded => _model is not null;

    public LlamaSharpEngine(LlmSettings settings) => _settings = settings;

    // ------------------ Initialization ------------------
    public Task InitializeAsync(CancellationToken ct = default)
    {
        if (IsLoaded) return Task.CompletedTask;

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var mp = new ModelParams(_settings.ModelPath)
            {
                ContextSize = _settings.ContextSize,
                GpuLayerCount = _settings.GpuLayerCount
            };

            _model = LLamaWeights.LoadFromFile(mp);
            _context = _model.CreateContext(mp);
            _executor = new StatelessExecutor(_model, mp, null);

            // Initialize history with system prompt
            _chatHistory.Clear();
            if (!string.IsNullOrWhiteSpace(_settings.SystemPrompt))
                _chatHistory.Append($"<s>[INST] {_settings.SystemPrompt} [/INST]</s>");

            // --- Load patient profile for RAG ---
            LoadPatientProfile(_settings.PatientProfilePath);

        }, ct);
    }

    // ------------------ Reset conversation ------------------
    public void ResetConversation(string? newSystemPrompt = null)
    {
        EnsureReady();
        _chatHistory.Clear();
        _chatHistory.Append($"<s>[INST] {newSystemPrompt ?? _settings.SystemPrompt} [/INST]</s>");
    }

    // ------------------ Load PDF and vectorize ------------------
    private void LoadPatientProfile(string pdfPath)
    {
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException($"Patient profile PDF not found: {pdfPath}");

        _ragChunks.Clear();
        _ragEmbeddings.Clear();

        var sb = new StringBuilder();
        using var pdf = PdfDocument.Open(pdfPath);
        foreach (var page in pdf.GetPages())
        {
            sb.AppendLine(page.Text);
        }

        string text = sb.ToString().Replace("\r\n", " ").Replace("\n", " ");

        // Chunk and embed
        for (int i = 0; i < text.Length; i += _chunkSize)
        {
            int len = Math.Min(_chunkSize, text.Length - i);
            string chunk = text.Substring(i, len);
            _ragChunks.Add(chunk);

            // --- Embedding ---
            var vec = EmbedText(chunk);
            _ragEmbeddings.Add(vec);
        }

        Console.WriteLine($"📄 Patient profile loaded: {pdfPath} ({_ragChunks.Count} chunks with embeddings)");
    }

    // ------------------ Simple embedding function ------------------
    private float[] EmbedText(string text)
    {
        // --- Replace this with a real embedding model if available ---
        // For demonstration: generate a dummy vector
        var vec = new float[_embeddingDim];
        for (int i = 0; i < _embeddingDim; i++)
            vec[i] = (float)((text.GetHashCode() + i) % 1000) / 1000f;
        return vec;
    }

    // ------------------ Semantic search ------------------
    private string RetrieveRelevantContext(string query)
    {
        var queryVec = EmbedText(query);

        var similarities = _ragEmbeddings
            .Select((vec, idx) => new { Index = idx, Score = CosineSimilarity(vec, queryVec) })
            .OrderByDescending(x => x.Score)
            .Take(3) // top 3 chunks
            .Select(x => _ragChunks[x.Index]);

        return string.Join(" ", similarities);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return dot / ((float)Math.Sqrt(normA) * (float)Math.Sqrt(normB) + 1e-8f);
    }

    // ------------------ Stream chat with RAG ------------------
    public async IAsyncEnumerable<string> StreamChatAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureReady();

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        // --- Retrieve semantic context ---
        string context = RetrieveRelevantContext(userMessage);

        // Build the prompt including context
        string userPrompt = $"<s>[INST] {context} {userMessage} [/INST]";
        string fullPrompt = _chatHistory.ToString() + userPrompt;

        _ = Task.Run(async () =>
        {
            try
            {
                var ip = new InferenceParams
                {
                    SamplingPipeline = new DefaultSamplingPipeline
                    {
                        Temperature = _settings.Temperature,
                        TopP = _settings.TopP,
                        TopK = _settings.TopK,
                        RepeatPenalty = _settings.RepeatPenalty
                    },
                    MaxTokens = _settings.MaxTokens
                };

                var buffer = new StringBuilder();
                int tokenCount = 0;
                int maxTokenSafety = 8000;

                // Stop markers
                var stopMarkers = new List<string>
                {
                    "\nUser:", "User:", "\nAssistant:", "Assistant:",
                    "<s>", "[INST]", "[/INST]", "</s>"
                };

                await foreach (var token in _executor!.InferAsync(fullPrompt, ip).WithCancellation(ct))
                {
                    tokenCount++;
                    buffer.Append(token);
                    channel.Writer.TryWrite(token);

                    // Stop if any stop marker appears
                    string accumulated = buffer.ToString();
                    int earliestIndex = int.MaxValue;
                    bool stop = false;

                    foreach (var marker in stopMarkers)
                    {
                        int idx = accumulated.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0 && idx < earliestIndex)
                        {
                            earliestIndex = idx;
                            stop = true;
                        }
                    }

                    if (stop)
                    {
                        buffer.Length = earliestIndex;
                        break;
                    }

                    if (tokenCount >= maxTokenSafety)
                    {
                        channel.Writer.TryWrite("\n[stopped: safety token limit reached]\n");
                        break;
                    }
                }

                // Append only clean user + assistant text to history
                _chatHistory.Append(userPrompt);
                _chatHistory.Append(buffer.ToString().Trim());
                _chatHistory.Append("</s>");
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite($"\n[engine error: {ex.Message}]\n");
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var tok))
            {
                yield return tok;
                await Task.Yield();
            }
        }
    }

    // ------------------ Helpers ------------------
    private void EnsureReady()
    {
        if (!IsLoaded || _executor is null)
            throw new InvalidOperationException("LlamaSharpEngine not initialized. Call InitializeAsync() first.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_executor is IDisposable d2) d2.Dispose();
        if (_context is IDisposable d3) d3.Dispose();
        if (_model is IDisposable d4) d4.Dispose();
        await Task.CompletedTask;
    }
}
