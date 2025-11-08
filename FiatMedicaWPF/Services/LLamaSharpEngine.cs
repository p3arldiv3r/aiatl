using FiatMedica.Domain;
using LLama;
using LLama.Common;
using LLama.Sampling;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ChatSession = LLama.ChatSession;

namespace FiatMedica.Services;

public sealed class LlamaSharpEngine : ILlmEngine, IAsyncDisposable
{
    private readonly LlmSettings _settings;
    private LLamaWeights? _model;
    private LLamaContext? _context;
    private InteractiveExecutor? _executor;
    private ChatSession? _session;

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
            _session = new ChatSession(_executor);

            if (!string.IsNullOrWhiteSpace(_settings.SystemPrompt))
                _session.History.AddMessage(AuthorRole.System, _settings.SystemPrompt);
        }, ct);
    }

    public void ResetConversation(string? newSystemPrompt = null)
    {
        EnsureReady();
        _session = new ChatSession(_executor!);
        if (!string.IsNullOrWhiteSpace(newSystemPrompt ?? _settings.SystemPrompt))
            _session.History.AddMessage(AuthorRole.System, newSystemPrompt ?? _settings.SystemPrompt);
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureReady();

        // Channel decouples the async token iterator from async UI consumption
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        // Run the async token loop off the UI thread
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

                await foreach (var token in _session!.ChatAsync(new ChatHistory.Message(AuthorRole.User, userMessage), ip).WithCancellation(ct))
                {
                    channel.Writer.TryWrite(token);
                }
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
                // yield quickly so UI stays snappy
                await Task.Yield();
            }
        }
    }

    private void EnsureReady()
    {
        if (!IsLoaded || _executor is null || _session is null)
            throw new InvalidOperationException("LlamaSharpEngine not initialized. Call InitializeAsync() first.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is IDisposable d1) d1.Dispose();
        if (_executor is IDisposable d2) d2.Dispose();
        if (_context is IDisposable d3) d3.Dispose();
        if (_model is IDisposable d4) d4.Dispose();
        await Task.CompletedTask;
    }
}