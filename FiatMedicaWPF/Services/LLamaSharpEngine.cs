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
using FiatMedica.Services;

namespace FiatMedicaWPF.Services;

public sealed class LlamaSharpEngine : ILlmEngine, IAsyncDisposable
{
    private readonly LlmSettings _settings;
    private LLamaWeights? _model;
    private LLamaContext? _context;
    private InteractiveExecutor? _executor;

    // Keep a simple prompt history string (no ChatSession)
    private StringBuilder _chatHistory = new StringBuilder();

    public bool IsLoaded => _model is not null;

    public LlamaSharpEngine(LlmSettings settings) => _settings = settings;

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
            _executor = new InteractiveExecutor(_context);

            // Initialize history with system prompt
            _chatHistory.Clear();
            if (!string.IsNullOrWhiteSpace(_settings.SystemPrompt))
            {
                _chatHistory.Append($"<s>[INST] {_settings.SystemPrompt} [/INST]</s>");
            }
        }, ct);
    }

    public void ResetConversation(string? newSystemPrompt = null)
    {
        EnsureReady();
        _chatHistory.Clear();
        _chatHistory.Append($"<s>[INST] {newSystemPrompt ?? _settings.SystemPrompt} [/INST]</s>");
    }

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

        // Turn-based prompt
        string userPrompt = $"<s>[INST] {userMessage} [/INST]";
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
                    MaxTokens = _settings.MaxTokens,
                    // Only use anti-prompts that indicate a NEW turn, not part of the current prompt
                    AntiPrompts = new List<string>
                    {
                        "</s>",
                        "<s>[INST]",  // New user turn starting
                        "\n[INST]",   // Alternative new turn format
                        "User:",
                        "\nUser:"
                    }
                };

                var buffer = new StringBuilder();
                int tokenCount = 0;

                await foreach (var token in _executor!.InferAsync(fullPrompt, ip).WithCancellation(ct))
                {
                    tokenCount++;
                    buffer.Append(token);
                    channel.Writer.TryWrite(token);

                    // Respect MaxTokens strictly
                    if (tokenCount >= _settings.MaxTokens)
                    {
                        break;
                    }

                    // Check for stop sequences that indicate the model is starting a new turn
                    string accumulated = buffer.ToString();
                    if (accumulated.Contains("</s>") ||
                        accumulated.Contains("<s>[INST]") ||
                        accumulated.Contains("\n[INST]") ||
                        accumulated.EndsWith("\nUser:") ||
                        accumulated.EndsWith("User:"))
                    {
                        // Trim at the stop marker
                        int endIdx = accumulated.IndexOf("</s>");
                        if (endIdx < 0) endIdx = accumulated.IndexOf("<s>[INST]");
                        if (endIdx < 0) endIdx = accumulated.IndexOf("\n[INST]");
                        if (endIdx < 0) endIdx = accumulated.LastIndexOf("\nUser:");
                        if (endIdx < 0) endIdx = accumulated.LastIndexOf("User:");

                        if (endIdx >= 0)
                        {
                            buffer.Length = endIdx;
                        }
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
                await Task.Yield(); // keep UI responsive
            }
        }
    }

    private void EnsureReady()
    {
        if (!IsLoaded || _executor is null)
            throw new InvalidOperationException("LlamaSharpEngine not initialized. Call InitializeAsync() first.");
    }

    public async Task<string> SummarizeChatAsync(string chatHistory, CancellationToken ct = default)
    {
        EnsureReady();

        var summaryPrompt = $"<s>[INST] Summarize the following conversation in 2-3 concise sentences:\n\n{chatHistory}\n\nSummary: [/INST]";

        var ip = new InferenceParams
        {
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.3f,
                TopP = 0.9f,
                TopK = 40
            },
            MaxTokens = 150,
            AntiPrompts = new List<string> { "</s>", "<s>[INST]", "\n[INST]" }
        };

        var summary = new StringBuilder();

        await foreach (var token in _executor!.InferAsync(summaryPrompt, ip).WithCancellation(ct))
        {
            summary.Append(token);

            if (summary.ToString().Contains("</s>") || summary.ToString().Contains("<s>[INST]"))
                break;
        }

        return summary.ToString().Replace("</s>", "").Replace("<s>", "").Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (_executor is IDisposable d2) d2.Dispose();
        if (_context is IDisposable d3) d3.Dispose();
        if (_model is IDisposable d4) d4.Dispose();
        await Task.CompletedTask;
    }
}