using FiatMedica.Domain;
using LLama;
using LLama.Common;
using System.IO;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;

namespace FiatMedica.Services;

public sealed class RagService : IRagService, IAsyncDisposable
{
    private readonly RagSettings _settings;
    private LLamaWeights? _embeddingModel;
    private LLamaEmbedder? _embedder;
    private List<RagEntry> _ragEntries = new();
    private bool _isInitialized;
    private bool _isDisposed;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _embeddingLock = new(1, 1);

    public RagService(RagSettings settings) => _settings = settings;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_isInitialized) return;

        await _lock.WaitAsync(ct);
        try
        {
            if (_isInitialized) return;

            // Create directory if needed
            var dbDir = Path.GetDirectoryName(_settings.DatabasePath);
            if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            {
                Directory.CreateDirectory(dbDir);
            }

            // Load existing database
            if (File.Exists(_settings.DatabasePath))
            {
                var json = await File.ReadAllTextAsync(_settings.DatabasePath, ct);
                _ragEntries = JsonSerializer.Deserialize<List<RagEntry>>(json) ?? new();
            }

            // Initialize embedding model if available and valid
            if (!string.IsNullOrEmpty(_settings.EmbeddingModelPath) &&
                File.Exists(_settings.EmbeddingModelPath))
            {
                try
                {
                    // DO NOT USE Task.Run - load directly on this thread
                    var modelParams = new ModelParams(_settings.EmbeddingModelPath)
                    {
                        ContextSize = 512,
                        GpuLayerCount = 20,
                        Embeddings = true,
                        Threads = Math.Max(1, Environment.ProcessorCount / 4),
                        BatchSize = 512,
                        UBatchSize = 512
                    };

                    _embeddingModel = LLamaWeights.LoadFromFile(modelParams);
                    _embedder = new LLamaEmbedder(_embeddingModel, modelParams);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load embedding model: {ex.Message}");
                    _embeddingModel?.Dispose();
                    _embeddingModel = null;
                    _embedder = null;
                }
            }

            _isInitialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsReadyAsync()
    {
        await Task.CompletedTask;
        return _isInitialized && !_isDisposed;
    }

    public async Task AddPdfDocumentAsync(string filePath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!_isInitialized)
            throw new InvalidOperationException("RAG service not initialized");

        await _lock.WaitAsync(ct);
        try
        {
            // Extract text from PDF (offload to thread pool)
            var pdfText = await Task.Run(() => ExtractTextFromPdf(filePath), ct);

            // Split into chunks
            var chunks = SplitIntoChunks(pdfText, _settings.ChunkSize, _settings.ChunkOverlap);

            var fileName = Path.GetFileName(filePath);

            // Process chunks sequentially to avoid overwhelming native library
            foreach (var (chunk, index) in chunks.Select((c, i) => (c, i)))
            {
                ct.ThrowIfCancellationRequested();

                var doc = new RagDocument
                {
                    Id = $"{Guid.NewGuid()}",
                    Content = chunk,
                    Source = fileName,
                    Type = DocumentType.Pdf,
                    AddedAt = DateTime.Now,
                    Metadata = new Dictionary<string, string>
                    {
                        ["ChunkIndex"] = index.ToString(),
                        ["TotalChunks"] = chunks.Count.ToString(),
                        ["FilePath"] = filePath
                    }
                };

                var embedding = _embedder != null
                    ? await ComputeEmbeddingAsync(chunk, ct)
                    : Array.Empty<float>();

                _ragEntries.Add(new RagEntry
                {
                    Document = doc,
                    Embedding = embedding
                });
            }

            await SaveDatabaseAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddChatSummaryAsync(string sessionId, string summary, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!_isInitialized)
            throw new InvalidOperationException("RAG service not initialized");

        await _lock.WaitAsync(ct);
        try
        {
            var doc = new RagDocument
            {
                Id = $"summary_{sessionId}",
                Content = summary,
                Source = $"Chat Session {sessionId}",
                Type = DocumentType.ChatSummary,
                AddedAt = DateTime.Now,
                Metadata = new Dictionary<string, string>
                {
                    ["SessionId"] = sessionId
                }
            };

            var embedding = _embedder != null
                ? await ComputeEmbeddingAsync(summary, ct)
                : Array.Empty<float>();

            _ragEntries.Add(new RagEntry
            {
                Document = doc,
                Embedding = embedding
            });

            await SaveDatabaseAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> RetrieveContextAsync(string query, int topK = 3, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!_isInitialized)
            throw new InvalidOperationException("RAG service not initialized");

        await _lock.WaitAsync(ct);
        try
        {
            if (_ragEntries.Count == 0)
                return string.Empty;

            // If embeddings are available, use semantic search
            if (_embedder != null)
            {
                var queryEmbedding = await ComputeEmbeddingAsync(query, ct);

                var results = _ragEntries
                    .Select(entry => new
                    {
                        Entry = entry,
                        Similarity = CosineSimilarity(queryEmbedding, entry.Embedding)
                    })
                    .Where(r => r.Similarity >= _settings.SimilarityThreshold)
                    .OrderByDescending(r => r.Similarity)
                    .Take(topK)
                    .Select(r => r.Entry.Document)
                    .ToList();

                return FormatContext(results);
            }
            else
            {
                // Fallback: simple keyword matching
                var lowerQuery = query.ToLowerInvariant();
                var results = _ragEntries
                    .Where(e => e.Document.Content.ToLowerInvariant().Contains(lowerQuery))
                    .Take(topK)
                    .Select(e => e.Document)
                    .ToList();

                return FormatContext(results);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private string ExtractTextFromPdf(string filePath)
    {
        var sb = new StringBuilder();

        using var document = PdfDocument.Open(filePath);
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(page.Text);
        }

        return sb.ToString();
    }

    private List<string> SplitIntoChunks(string text, int chunkSize, int overlap)
    {
        var chunks = new List<string>();
        var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < words.Length; i += chunkSize - overlap)
        {
            var chunk = string.Join(" ", words.Skip(i).Take(chunkSize));
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                chunks.Add(chunk);
            }
        }

        return chunks;
    }

    private async Task<float[]> ComputeEmbeddingAsync(string text, CancellationToken ct)
    {
        if (_embedder == null || _isDisposed)
            return Array.Empty<float>();

        // Truncate text if too long to prevent buffer overruns
        const int maxLength = 8192; // Safe limit for most embedding models
        if (text.Length > maxLength)
        {
            text = text.Substring(0, maxLength);
        }

        // Serialize all embedding calls
        await _embeddingLock.WaitAsync(ct);
        try
        {
            // Call directly without Task.Run for thread safety
            var embeddings = await _embedder.GetEmbeddings(text, ct);
            return embeddings.Count > 0 ? embeddings[0] : Array.Empty<float>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Embedding error: {ex.Message}");
            return Array.Empty<float>();
        }
        finally
        {
            _embeddingLock.Release();
        }
    }

    private float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            return 0f;

        float dot = 0f, magA = 0f, magB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var magnitude = (float)Math.Sqrt(magA) * (float)Math.Sqrt(magB);
        return magnitude > 0 ? dot / magnitude : 0f;
    }

    private string FormatContext(List<RagDocument> documents)
    {
        if (documents.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("=== RETRIEVED CONTEXT ===");

        foreach (var doc in documents)
        {
            sb.AppendLine($"\n[Source: {doc.Source} ({doc.Type})]");
            sb.AppendLine(doc.Content);
            sb.AppendLine("---");
        }

        return sb.ToString();
    }

    private async Task SaveDatabaseAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_ragEntries, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(_settings.DatabasePath, json, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

        // Try to acquire lock but don't wait forever
        var acquired = await _lock.WaitAsync(TimeSpan.FromSeconds(5));
        if (!acquired) return; // Avoid deadlock during shutdown

        try
        {
            if (_isDisposed) return;

            _isDisposed = true;

            // Dispose in correct order
            try
            {
                _embedder?.Dispose();
            }
            catch { /* Ignore disposal errors */ }
            finally
            {
                _embedder = null;
            }

            try
            {
                _embeddingModel?.Dispose();
            }
            catch { /* Ignore disposal errors */ }
            finally
            {
                _embeddingModel = null;
            }
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
            _embeddingLock.Dispose();
        }

        await Task.CompletedTask;
    }

    private class RagEntry
    {
        public RagDocument Document { get; set; } = null!;
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }
}