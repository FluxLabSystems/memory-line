using Microsoft.Extensions.Logging;
using Polly;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// LLM service for any OpenAI-compatible chat-completions endpoint
/// (Ollama, LM Studio, vLLM, an OpenAI proxy, ...). The base URL comes from
/// <see cref="SettingKeys.LlmBaseUrl"/> (e.g. http://localhost:11434/v1 for
/// Ollama) and the model name from <see cref="SettingKeys.LlmModel"/>; both are
/// re-read on every call so Settings changes apply without a restart.
/// The extraction prompt and JSON handling are shared verbatim with
/// <see cref="AnthropicLlmService"/> via <see cref="LlmPromptSupport"/>.
/// Registered as an HttpClient typed client (transient) and resolved per call
/// by <see cref="RoutingLlmService"/>.
/// </summary>
public class OpenAiCompatibleLlmService : ILlmService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<OpenAiCompatibleLlmService> _logger;
    private readonly ResiliencePipeline<HttpResponseMessage> _retry;
    private string? _model;

    public string ProviderName => "OpenAI-compatible endpoint";
    public string ModelName => _model ?? "(model from settings)";

    /// <summary>
    /// The endpoint is a network service. It is often a LOCAL server (Ollama on
    /// localhost), but this class cannot know where the URL points, so it
    /// reports the conservative answer.
    /// </summary>
    public bool RequiresInternet => true;

    public OpenAiCompatibleLlmService(
        HttpClient httpClient,
        ISettingsService settingsService,
        ILogger<OpenAiCompatibleLlmService> logger)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _logger = logger;
        // A local Ollama that is still loading a model answers 5xx for a few
        // seconds; a bounded retry turns that into a slow call, not a failed item.
        _retry = HttpRetryPolicy.CreateForResponses(_logger, "OpenAI-compatible chat completion");
    }

    /// <summary>
    /// Extracts events from transcript text.
    /// </summary>
    public async Task<EventExtractionResult> ExtractEventsAsync(string transcript)
    {
        return await ExtractEventsAsync(transcript, null);
    }

    /// <summary>
    /// Extracts events from transcript with context. A missing base URL throws
    /// <see cref="ConfigurationException"/> (non-retryable for the queue);
    /// transport/parse failures return an unsuccessful result, mirroring the
    /// Anthropic path.
    /// </summary>
    public async Task<EventExtractionResult> ExtractEventsAsync(string transcript, ExtractionContext? context)
    {
        var stopwatch = Stopwatch.StartNew();

        // Configuration is validated OUTSIDE the failure-to-result catch so the
        // ConfigurationException reaches the queue's non-retryable handling.
        var (endpoint, model) = await ReadConfigurationAsync();

        try
        {
            _logger.LogInformation(
                "Starting event extraction via OpenAI-compatible endpoint {Endpoint} ({Length} chars)",
                endpoint, transcript.Length);

            var prompt = LlmPromptSupport.BuildExtractionPrompt(transcript, context);

            // llm_max_tokens / llm_temperature, re-read per call like the URL
            // and model (same reader as the Anthropic path, so both providers
            // honour the same rows).
            var generation = await LlmPromptSupport.ReadGenerationSettingsAsync(_settingsService);

            var request = new ChatCompletionRequest
            {
                Model = model,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "user", Content = prompt }
                },
                Temperature = generation.Temperature,
                MaxTokens = generation.MaxTokens,
                Stream = false
            };

            var (content, usage) = await SendChatCompletionAsync(endpoint, request, CancellationToken.None);

            var jsonResponse = LlmPromptSupport.ExtractJsonFromText(content);
            if (string.IsNullOrEmpty(jsonResponse))
            {
                return new EventExtractionResult
                {
                    Success = false,
                    ErrorMessage = "No valid JSON found in the model's response"
                };
            }

            LlmPromptSupport.EventExtractionResponse? extractedData;
            try
            {
                extractedData = JsonSerializer.Deserialize<LlmPromptSupport.EventExtractionResponse>(
                    jsonResponse, LlmPromptSupport.JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Malformed JSON in OpenAI-compatible extraction response");
                return new EventExtractionResult
                {
                    Success = false,
                    ErrorMessage = "The model returned malformed JSON that could not be parsed"
                };
            }

            if (extractedData == null)
            {
                return new EventExtractionResult
                {
                    Success = false,
                    ErrorMessage = "Failed to parse extraction response"
                };
            }

            stopwatch.Stop();

            var result = new EventExtractionResult
            {
                Events = extractedData.Events ?? new List<ExtractedEvent>(),
                OverallConfidence = extractedData.OverallConfidence,
                Success = true,
                ProcessingDurationSeconds = stopwatch.Elapsed.TotalSeconds,
                TokenUsage = new TokenUsage
                {
                    InputTokens = usage?.PromptTokens ?? 0,
                    OutputTokens = usage?.CompletionTokens ?? 0,
                    // Self-hosted / OpenAI-compatible endpoints have no fixed
                    // price list, so no cost estimate is made.
                    EstimatedCost = null
                }
            };

            _logger.LogInformation("Successfully extracted {Count} events (confidence: {Confidence})",
                result.Events.Count, result.OverallConfidence);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during event extraction via OpenAI-compatible endpoint");

            stopwatch.Stop();

            return new EventExtractionResult
            {
                Success = false,
                ErrorMessage = $"Extraction failed: {ex.Message}",
                ProcessingDurationSeconds = stopwatch.Elapsed.TotalSeconds
            };
        }
    }

    /// <summary>
    /// Runs a single free-form completion. Unlike the Anthropic path (whose SDK
    /// call shape only takes user messages), the chat-completions contract has
    /// first-class system messages, so the system prompt is sent as a proper
    /// "system" role message here.
    /// </summary>
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens = 2000, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var (endpoint, model) = await ReadConfigurationAsync();

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new ChatMessage { Role = "system", Content = systemPrompt });
        }
        messages.Add(new ChatMessage { Role = "user", Content = userPrompt });

        // The caller owns the output budget; the temperature comes from settings.
        var generation = await LlmPromptSupport.ReadGenerationSettingsAsync(_settingsService);

        var request = new ChatCompletionRequest
        {
            Model = model,
            Messages = messages,
            Temperature = generation.Temperature,
            MaxTokens = maxTokens,
            Stream = false
        };

        _logger.LogInformation(
            "Sending completion request to OpenAI-compatible endpoint {Endpoint} ({Length} chars)",
            endpoint, userPrompt.Length);

        var (content, _) = await SendChatCompletionAsync(endpoint, request, ct);

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("The model returned an empty response.");
        }

        return content.Trim();
    }

    #region Private Methods

    /// <summary>
    /// Re-reads the base URL and model from settings (applies without restart).
    /// </summary>
    /// <exception cref="ConfigurationException">No base URL is configured.</exception>
    private async Task<(string Endpoint, string Model)> ReadConfigurationAsync()
    {
        var baseUrl = await _settingsService.GetSettingAsync<string>(SettingKeys.LlmBaseUrl, string.Empty);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ConfigurationException(
                "LLM base URL not configured — add it in Settings (e.g. http://localhost:11434/v1 for Ollama)");
        }

        var model = await _settingsService.GetLlmModelAsync();
        _model = model;

        var endpoint = baseUrl.Trim().TrimEnd('/') + "/chat/completions";
        return (endpoint, model);
    }

    /// <summary>
    /// POSTs the chat-completion request and returns choices[0].message.content
    /// plus the reported usage.
    /// </summary>
    private async Task<(string? Content, ChatUsage? Usage)> SendChatCompletionAsync(
        string endpoint, ChatCompletionRequest request, CancellationToken ct)
    {
        // A fresh request per attempt: the retry pipeline re-invokes the
        // delegate, and the serialized body is rebuilt each time.
        var response = await _retry.ExecuteAsync(
            async token => await _httpClient.PostAsJsonAsync(endpoint, request, token), ct);
        response.EnsureSuccessStatusCode();

        var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: ct);

        var content = completion?.Choices?.FirstOrDefault()?.Message?.Content;
        return (content, completion?.Usage);
    }

    #endregion

    #region API DTOs

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = new();

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<ChatChoice>? Choices { get; set; }

        [JsonPropertyName("usage")]
        public ChatUsage? Usage { get; set; }
    }

    private sealed class ChatChoice
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class ChatUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }

    #endregion
}
