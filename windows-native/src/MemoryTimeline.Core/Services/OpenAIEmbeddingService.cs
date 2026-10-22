using Microsoft.Extensions.Logging;
using Polly;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Embedding service using OpenAI Embeddings API.
/// </summary>
public class OpenAIEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAIEmbeddingService> _logger;
    private readonly ISettingsService _settingsService;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ResiliencePipeline<HttpResponseMessage> _retry;
    private string _apiKey = string.Empty;
    private string _model = DefaultModel;

    private const string OpenAIApiUrl = "https://api.openai.com/v1/embeddings";
    private const string DefaultModel = "text-embedding-3-small"; // 1536 dimensions
    private const int DefaultDimension = 1536;

    public int EmbeddingDimension => DefaultDimension;
    public string ModelName => _model;
    public bool RequiresInternet => true;
    public string ProviderKey => EmbeddingProviderKeys.OpenAi;

    public OpenAIEmbeddingService(
        HttpClient httpClient,
        ILogger<OpenAIEmbeddingService> logger,
        ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settingsService = settingsService;
        // API key and model are loaded lazily on first use to avoid blocking
        // (sync-over-async) during DI construction, which can deadlock on a
        // captured synchronization context and stalls service resolution.
        _retry = HttpRetryPolicy.CreateForResponses(_logger, "OpenAI embeddings request");
    }

    /// <summary>
    /// Loads the API key and model from settings and configures the HttpClient.
    /// Re-reads settings on every call (the settings service caches in memory,
    /// so this is cheap) and reconfigures when the key or model changed — the
    /// user can therefore fix the embedding key in Settings without restarting.
    /// Keys are read via <see cref="SettingKeys"/> so the Settings UI writer and
    /// this reader can never drift apart again.
    /// </summary>
    private async Task EnsureInitializedAsync()
    {
        var apiKey = await _settingsService.GetSettingAsync<string>(SettingKeys.EmbeddingApiKey, string.Empty).ConfigureAwait(false) ?? string.Empty;
        var model = await _settingsService.GetSettingAsync<string>(SettingKeys.EmbeddingModel, DefaultModel).ConfigureAwait(false) ?? DefaultModel;

        if (apiKey == _apiKey && model == _model)
            return;

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _apiKey = apiKey;
            _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;

            _httpClient.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(_apiKey)
                ? null
                : new AuthenticationHeaderValue("Bearer", _apiKey);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Returns true when an embedding API key is configured. Lets the
    /// Connections UI show a call-to-action instead of failing silently.
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var apiKey = await _settingsService.GetSettingAsync<string>(SettingKeys.EmbeddingApiKey, string.Empty).ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(apiKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking embedding service availability");
            return false;
        }
    }

    /// <summary>
    /// Generates an embedding for a single text.
    /// </summary>
    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        var embeddings = await GenerateEmbeddingsAsync(new[] { text });
        return embeddings.First();
    }

    /// <summary>
    /// Generates embeddings for multiple texts in a batch.
    /// </summary>
    public async Task<List<float[]>> GenerateEmbeddingsAsync(IEnumerable<string> texts)
    {
        try
        {
            await EnsureInitializedAsync();

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new ConfigurationException("Embedding API key not configured — add it in Settings");
            }

            var textList = texts.ToList();
            _logger.LogInformation("Generating embeddings for {Count} texts using {Model}", textList.Count, _model);

            var request = new OpenAIEmbeddingRequest
            {
                Model = _model,
                Input = textList
            };

            // Bounded retry on 429/5xx/transport failures (HttpRetryPolicy):
            // a batch re-embed must not lose an event to one rate-limit answer.
            var response = await _retry.ExecuteAsync(
                async token => await _httpClient.PostAsJsonAsync(OpenAIApiUrl, request, token),
                CancellationToken.None);
            response.EnsureSuccessStatusCode();

            var apiResponse = await response.Content.ReadFromJsonAsync<OpenAIEmbeddingResponse>();

            if (apiResponse?.Data == null || apiResponse.Data.Count == 0)
            {
                throw new Exception("Empty response from OpenAI Embeddings API");
            }

            var embeddings = apiResponse.Data
                .OrderBy(d => d.Index)
                .Select(d => d.Embedding)
                .ToList();

            _logger.LogInformation("Successfully generated {Count} embeddings", embeddings.Count);
            return embeddings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating embeddings");
            throw;
        }
    }

    /// <summary>
    /// Calculates cosine similarity between two embeddings.
    /// </summary>
    public double CalculateCosineSimilarity(float[] embedding1, float[] embedding2)
    {
        if (embedding1 == null || embedding2 == null)
        {
            throw new ArgumentNullException(embedding1 == null ? nameof(embedding1) : nameof(embedding2));
        }

        if (embedding1.Length != embedding2.Length)
        {
            throw new ArgumentException("Embeddings must have the same dimension");
        }

        double dotProduct = 0;
        double norm1 = 0;
        double norm2 = 0;

        for (int i = 0; i < embedding1.Length; i++)
        {
            dotProduct += embedding1[i] * embedding2[i];
            norm1 += embedding1[i] * embedding1[i];
            norm2 += embedding2[i] * embedding2[i];
        }

        if (norm1 == 0 || norm2 == 0)
        {
            return 0;
        }

        return dotProduct / (Math.Sqrt(norm1) * Math.Sqrt(norm2));
    }

    /// <summary>
    /// Finds K nearest neighbors using cosine similarity.
    /// </summary>
    public List<SimilarityResult> FindKNearestNeighbors(
        float[] queryEmbedding,
        IEnumerable<(string id, float[] embedding)> candidateEmbeddings,
        int k,
        double threshold = 0.0)
    {
        var similarities = new List<SimilarityResult>();

        if (queryEmbedding == null || queryEmbedding.Length == 0)
        {
            _logger.LogWarning("Query embedding is null or empty; returning no neighbors");
            return similarities;
        }

        foreach (var (id, embedding) in candidateEmbeddings)
        {
            // Skip candidates with missing or dimension-mismatched embeddings rather than
            // aborting the whole search (e.g. corrupt row or an embedding-model change).
            if (embedding == null || embedding.Length != queryEmbedding.Length)
            {
                _logger.LogDebug(
                    "Skipping candidate {Id}: embedding null or dimension mismatch (expected {Expected})",
                    id, queryEmbedding.Length);
                continue;
            }

            var similarity = CalculateCosineSimilarity(queryEmbedding, embedding);

            if (similarity >= threshold)
            {
                similarities.Add(new SimilarityResult
                {
                    Id = id,
                    Similarity = similarity
                });
            }
        }

        return similarities
            .OrderByDescending(s => s.Similarity)
            .Take(k)
            .ToList();
    }

    #region API DTOs

    private class OpenAIEmbeddingRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("input")]
        public List<string> Input { get; set; } = new();
    }

    private class OpenAIEmbeddingResponse
    {
        [JsonPropertyName("object")]
        public string? Object { get; set; }

        [JsonPropertyName("data")]
        public List<EmbeddingData> Data { get; set; } = new();

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("usage")]
        public Usage? Usage { get; set; }
    }

    private class EmbeddingData
    {
        [JsonPropertyName("object")]
        public string? Object { get; set; }

        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();

        [JsonPropertyName("index")]
        public int Index { get; set; }
    }

    private class Usage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }

    #endregion
}
