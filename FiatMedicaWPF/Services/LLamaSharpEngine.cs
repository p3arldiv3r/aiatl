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

namespace FiatMedica.Services;

public sealed class LlamaSharpEngine : ILlmEngine, IAsyncDisposable
{
    private readonly LlmSettings _settings;
    private LLamaWeights? _model;
    private LLamaContext? _context;
    private StatelessExecutor? _executor;

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
            _executor = new StatelessExecutor(_model, mp, null);

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
                    MaxTokens = _settings.MaxTokens
                };

                var buffer = new StringBuilder();
                int tokenCount = 0;
                int maxTokenSafety = 8000;

                // Stop markers to prevent looping
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
                        buffer.Length = earliestIndex; // trim before stop marker
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
                await Task.Yield(); // keep UI responsive
            }
        }
    }

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
