using MemoryTimeline.Data.Models;
using System.Text.Json;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Prompt and response-parsing helpers shared by EVERY <see cref="ILlmService"/>
/// implementation (Anthropic and OpenAI-compatible). The extraction prompt and
/// the JSON handling live here once so the providers can never drift apart:
/// a provider switch must change the transport, not the contract.
/// (Internal, but visible to the test project via InternalsVisibleTo.)
/// </summary>
internal static class LlmPromptSupport
{
    /// <summary>
    /// Shared serializer options; allocating JsonSerializerOptions per call is
    /// expensive (each instance builds and caches its own metadata).
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// The JSON payload shape every provider must return for extraction.
    /// </summary>
    internal sealed class EventExtractionResponse
    {
        public List<ExtractedEvent>? Events { get; set; }
        public double OverallConfidence { get; set; }
    }

    /// <summary>
    /// Builds the event-extraction prompt. This is THE prompt — both the
    /// Anthropic and the OpenAI-compatible services send exactly this text.
    /// </summary>
    internal static string BuildExtractionPrompt(string transcript, ExtractionContext? context)
    {
        var referenceDate = context?.ReferenceDate ?? DateTime.Now;

        // Offer the REAL canonical category list (EventCategory.AllCategories);
        // a hardcoded list here previously drifted from the stored values.
        var categoryList = string.Join(", ", EventCategory.AllCategories);

        var prompt = $@"You are an expert at extracting structured event information from transcribed speech. Your task is to analyze the following transcript and extract all mentioned events with their details.

# Instructions:
1. Identify ALL events mentioned in the transcript (meetings, milestones, accomplishments, significant occurrences)
2. For each event, extract:
   - Title (concise, descriptive)
   - Description (detailed information from the transcript)
   - Start date (parse relative dates like 'yesterday', 'last week', 'two months ago')
   - End date (if the event has duration)
   - Date precision (one of: exact, day, month, season, year, decade, unknown)
   - Category (one of: {categoryList})
   - Tags (relevant keywords)
   - People involved (names mentioned)
   - People details (for each person: their name, their relationship to the speaker if stated, and any other noteworthy details mentioned about them)
   - Locations mentioned
   - Confidence score (0.0 to 1.0 based on clarity of information)
   - Source text (the exact portion of transcript about this event)
   - Reasoning (brief explanation of why you extracted this as an event)

3. Parse dates relative to: {referenceDate:yyyy-MM-dd}
4. Be thorough but only extract genuine events, not hypotheticals or general discussions
5. Assign confidence scores based on:
   - 0.9-1.0: Explicit dates and clear details
   - 0.7-0.9: Clear event with approximate dates
   - 0.5-0.7: Event is clear but dates are vague
   - Below 0.5: Uncertain or ambiguous
6. Set datePrecision to the COARSEST unit actually justified by the transcript. NEVER guess a specific day when the speaker was vague:
   - exact: an explicit date AND time of day were stated
   - day: a specific calendar day is known
   - month: only the month and year are known (""in March 2019"") - set startDate to the 15th of that month
   - season: only a season is known (""that summer"") - set startDate to the middle of the season
   - year: only the year is known (""sometime in 2011"") - set startDate to July 1 of that year
   - decade: only the decade is known (""back in the 90s"") - set startDate to the middle year of the decade
   - unknown: no usable date information at all

# Context:";

        if (context?.RecentEvents != null && context.RecentEvents.Any())
        {
            prompt += "\nRecent events for reference:\n" + string.Join("\n", context.RecentEvents.Take(10).Select(e => $"- {e}"));
        }

        if (context?.AvailableTags != null && context.AvailableTags.Any())
        {
            prompt += "\n\nAvailable tags: " + string.Join(", ", context.AvailableTags.Take(20));
        }

        if (context?.KnownPeople != null && context.KnownPeople.Any())
        {
            prompt += "\n\nKnown people (use these canonical spellings when they match): " + string.Join(", ", context.KnownPeople.Take(100));
        }

        prompt += $@"

# Transcript:
{transcript}

# Output Format:
Return a JSON object with this exact structure (no markdown, just raw JSON):
{{
  ""events"": [
    {{
      ""title"": ""Event Title"",
      ""description"": ""Detailed description"",
      ""startDate"": ""2024-01-15T10:00:00Z"",
      ""endDate"": ""2024-01-15T12:00:00Z"",
      ""datePrecision"": ""day"",
      ""category"": ""work"",
      ""tags"": [""tag1"", ""tag2""],
      ""people"": [""Person Name""],
      ""peopleDetails"": [{{ ""name"": ""Person Name"", ""relationship"": ""sister"", ""details"": ""noteworthy details mentioned"" }}],
      ""locations"": [""Location Name""],
      ""confidence"": 0.95,
      ""sourceText"": ""relevant portion of transcript"",
      ""reasoning"": ""why this is an event""
    }}
  ],
  ""overallConfidence"": 0.85
}}

Notes on people fields: ""people"" must remain the flat list of person names. ""peopleDetails"" has one entry per person with the same name plus ""relationship"" and ""details"" set to null when not mentioned.

Now analyze the transcript and extract events:";

        return prompt;
    }

    /// <summary>
    /// Strips markdown code fences from a raw model response, returning the
    /// bare JSON text (or an empty string for a blank response).
    /// </summary>
    internal static string ExtractJsonFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();

        // Remove markdown code fences if present
        if (trimmed.StartsWith("```json"))
        {
            trimmed = trimmed.Substring(7);
        }
        else if (trimmed.StartsWith("```"))
        {
            trimmed = trimmed.Substring(3);
        }

        if (trimmed.EndsWith("```"))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 3);
        }

        return trimmed.Trim();
    }

    /// <summary>
    /// The output budget for extraction calls when <c>llm_max_tokens</c> is
    /// absent or unusable. The seed is 4000; this is the value the services
    /// hard-coded before the setting was read.
    /// </summary>
    internal const int DefaultMaxTokens = 4096;

    /// <summary>The sampling temperature when <c>llm_temperature</c> is absent or unusable.</summary>
    internal const double DefaultTemperature = 0.3;

    /// <summary>Generation parameters shared by both LLM providers.</summary>
    /// <param name="MaxTokens">Output budget for extraction calls.</param>
    /// <param name="Temperature">Sampling temperature for every call.</param>
    internal sealed record LlmGenerationSettings(int MaxTokens, double Temperature);

    /// <summary>
    /// Reads <c>llm_max_tokens</c> and <c>llm_temperature</c>, re-read on every
    /// call like the provider and model so a Settings change applies live.
    /// Both are parsed invariantly and validated: a missing, blank or
    /// out-of-range value (max tokens must be positive; temperature must be
    /// in 0..2, the range both APIs accept) falls back to the defaults, so a
    /// stray row can never silence the model with a zero budget. Settings
    /// read failures are already turned into the caller's default by the
    /// settings service.
    /// </summary>
    internal static async Task<LlmGenerationSettings> ReadGenerationSettingsAsync(ISettingsService settings)
    {
        var maxTokensText = await settings.GetSettingAsync<string>(SettingKeys.LlmMaxTokens, string.Empty);
        var temperatureText = await settings.GetSettingAsync<string>(SettingKeys.LlmTemperature, string.Empty);
        return new LlmGenerationSettings(ParseMaxTokens(maxTokensText), ParseTemperature(temperatureText));
    }

    internal static int ParseMaxTokens(string? text) =>
        int.TryParse(text?.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : DefaultMaxTokens;

    internal static double ParseTemperature(string? text) =>
        double.TryParse(text?.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
        && !double.IsNaN(value) && value >= 0.0 && value <= 2.0
            ? value
            : DefaultTemperature;
}
