using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Export service implementation for timeline data.
/// Creates a short-lived DbContext per operation via IDbContextFactory.
/// </summary>
public class ExportService : IExportService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<ExportService> _logger;
    private readonly IMediaService? _mediaService;

    /// <summary>
    /// Creates the service. <paramref name="mediaService"/> is optional and
    /// trailing: with it, a narrative exported with
    /// <see cref="Narrative.IncludeMedia"/> references each attachment by its
    /// absolute location under the managed media root; without it the stored
    /// media-root-relative path is written as-is.
    /// </summary>
    public ExportService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<ExportService> logger,
        IMediaService? mediaService = null)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _mediaService = mediaService;
    }

    /// <summary>
    /// Exports events to JSON format.
    /// </summary>
    public async Task ExportToJsonAsync(string filePath, DateTime? startDate = null, DateTime? endDate = null, IProgress<int>? progress = null)
    {
        try
        {
            _logger.LogInformation("Exporting to JSON: {FilePath}", filePath);

            var events = await GetFilteredEventsAsync(startDate, endDate);
            progress?.Report(50);

            var exportData = new
            {
                ExportDate = DateTime.UtcNow,
                EventCount = events.Count,
                Events = events.Select(e => new
                {
                    e.EventId,
                    e.Title,
                    e.Description,
                    e.StartDate,
                    e.EndDate,
                    e.Category,
                    e.Location,
                    // Project names — serializing Tag entities would cycle via Tag.EventTags
                    Tags = e.Tags?.Select(t => t.TagName).ToList(),
                    e.CreatedAt,
                    e.UpdatedAt
                }).ToList()
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(exportData, options);
            await File.WriteAllTextAsync(filePath, json);

            progress?.Report(100);
            _logger.LogInformation("Exported {Count} events to JSON", events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting to JSON");
            throw;
        }
    }

    /// <summary>
    /// Exports events to CSV format.
    /// </summary>
    public async Task ExportToCsvAsync(string filePath, DateTime? startDate = null, DateTime? endDate = null, IProgress<int>? progress = null)
    {
        try
        {
            _logger.LogInformation("Exporting to CSV: {FilePath}", filePath);

            var events = await GetFilteredEventsAsync(startDate, endDate);
            progress?.Report(50);

            var csv = new StringBuilder();

            // Header
            csv.AppendLine("EventId,Title,Description,StartDate,EndDate,Category,Location,Tags,CreatedAt");

            // Rows
            foreach (var evt in events)
            {
                var tags = string.Join(";", evt.Tags?.Select(t => t.Name) ?? Array.Empty<string>());
                csv.AppendLine($"\"{evt.EventId}\",\"{EscapeCsv(evt.Title)}\",\"{EscapeCsv(evt.Description)}\",\"{evt.StartDate:yyyy-MM-dd}\",\"{evt.EndDate:yyyy-MM-dd}\",\"{evt.Category}\",\"{EscapeCsv(evt.Location)}\",\"{tags}\",\"{evt.CreatedAt:yyyy-MM-dd HH:mm:ss}\"");
            }

            await File.WriteAllTextAsync(filePath, csv.ToString());

            progress?.Report(100);
            _logger.LogInformation("Exported {Count} events to CSV", events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting to CSV");
            throw;
        }
    }

    /// <summary>
    /// Exports events to Markdown format.
    /// </summary>
    public async Task ExportToMarkdownAsync(string filePath, DateTime? startDate = null, DateTime? endDate = null, IProgress<int>? progress = null)
    {
        try
        {
            _logger.LogInformation("Exporting to Markdown: {FilePath}", filePath);

            var events = await GetFilteredEventsAsync(startDate, endDate);
            progress?.Report(50);

            var md = new StringBuilder();
            md.AppendLine("# Memory Timeline Export");
            md.AppendLine();
            md.AppendLine($"**Exported:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            md.AppendLine($"**Event Count:** {events.Count}");
            md.AppendLine();
            md.AppendLine("---");
            md.AppendLine();

            // Group by year
            var eventsByYear = events
                .OrderBy(e => e.StartDate)
                .GroupBy(e => e.StartDate.Year);

            foreach (var yearGroup in eventsByYear)
            {
                md.AppendLine($"## {yearGroup.Key}");
                md.AppendLine();

                foreach (var evt in yearGroup)
                {
                    md.AppendLine($"### {evt.Title}");
                    md.AppendLine();
                    md.AppendLine($"**Date:** {evt.StartDate:yyyy-MM-dd}");

                    if (evt.EndDate.HasValue && evt.EndDate != evt.StartDate)
                    {
                        md.AppendLine($"**End Date:** {evt.EndDate:yyyy-MM-dd}");
                    }

                    md.AppendLine($"**Category:** {evt.Category}");

                    if (!string.IsNullOrWhiteSpace(evt.Location))
                    {
                        md.AppendLine($"**Location:** {evt.Location}");
                    }

                    if (evt.Tags?.Any() == true)
                    {
                        md.AppendLine($"**Tags:** {string.Join(", ", evt.Tags.Select(t => t.Name))}");
                    }

                    if (!string.IsNullOrWhiteSpace(evt.Description))
                    {
                        md.AppendLine();
                        md.AppendLine(evt.Description);
                    }

                    md.AppendLine();
                    md.AppendLine("---");
                    md.AppendLine();
                }
            }

            await File.WriteAllTextAsync(filePath, md.ToString());

            progress?.Report(100);
            _logger.LogInformation("Exported {Count} events to Markdown", events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting to Markdown");
            throw;
        }
    }

    /// <summary>
    /// Exports the entire database (events, eras, tags, etc.).
    /// </summary>
    public async Task ExportFullDatabaseAsync(string filePath, IProgress<int>? progress = null)
    {
        try
        {
            _logger.LogInformation("Exporting full database to: {FilePath}", filePath);

            await using var dbContext = await _contextFactory.CreateDbContextAsync();

            progress?.Report(10);
            // Event.Tags is [NotMapped]; include the real junction + navigation
            var events = await dbContext.Events
                .Include(e => e.EventTags)
                .ThenInclude(et => et.Tag)
                .AsNoTracking()
                .ToListAsync();

            progress?.Report(30);
            var eras = await dbContext.Eras
                .AsNoTracking()
                .ToListAsync();

            progress?.Report(50);
            var tags = await dbContext.Tags
                .AsNoTracking()
                .ToListAsync();

            progress?.Report(70);
            var exportData = new
            {
                ExportDate = DateTime.UtcNow,
                Version = "1.0",
                Events = events.Select(e => new
                {
                    e.EventId,
                    e.Title,
                    e.Description,
                    e.StartDate,
                    e.EndDate,
                    e.Category,
                    e.Location,
                    e.Confidence,
                    Tags = e.Tags?.Select(t => t.Name).ToList(),
                    e.CreatedAt,
                    e.UpdatedAt
                }).ToList(),
                Eras = eras.Select(era => new
                {
                    era.EraId,
                    era.Name,
                    era.Description,
                    era.StartDate,
                    era.EndDate,
                    era.Color,
                    era.CreatedAt
                }).ToList(),
                Tags = tags.Select(t => new
                {
                    t.TagId,
                    t.Name,
                    t.Color,
                    t.CreatedAt
                }).ToList()
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(exportData, options);
            await File.WriteAllTextAsync(filePath, json);

            progress?.Report(100);
            _logger.LogInformation("Exported full database: {Events} events, {Eras} eras, {Tags} tags",
                events.Count, eras.Count, tags.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting full database");
            throw;
        }
    }

    /// <summary>
    /// Exports a generated narrative as Markdown or a single self-contained
    /// HTML document. Atomic write: the content is written to a sibling
    /// ".tmp" file and moved over the target, so a failure mid-write never
    /// leaves a partial export; the temp file is deleted in a finally block.
    /// </summary>
    public async Task ExportNarrativeAsync(Narrative narrative, string filePath, NarrativeFormat format)
    {
        if (narrative == null)
        {
            throw new ArgumentNullException(nameof(narrative));
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        _logger.LogInformation("Exporting narrative '{Title}' as {Format}: {FilePath}",
            narrative.Title, format, filePath);

        var citedIds = CollectCitedEventIds(narrative);
        var citedEvents = await LoadCitedEventsAsync(citedIds);

        // Attached media (only when the request asked for it). The events that
        // own media but were never cited still need their titles for captions.
        var media = await LoadNarrativeMediaAsync(narrative);
        var uncitedMediaEventIds = media.Keys.Where(id => !citedEvents.ContainsKey(id)).ToList();
        foreach (var pair in await LoadCitedEventsAsync(uncitedMediaEventIds))
        {
            citedEvents[pair.Key] = pair.Value;
        }

        var content = format == NarrativeFormat.Html
            ? BuildNarrativeHtml(narrative, citedIds, citedEvents, media)
            : BuildNarrativeMarkdown(narrative, citedIds, citedEvents, media);

        var tempPath = filePath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content);
            File.Move(tempPath, filePath, overwrite: true);
            _logger.LogInformation("Exported narrative ({Sections} sections, {Sources} source events)",
                narrative.Sections.Count, narrative.SourceEventIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting narrative to {FilePath}", filePath);
            throw;
        }
        finally
        {
            // On success the temp file no longer exists (it was moved); on
            // failure this removes the partial write.
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Could not delete temp export file: {TempPath}", tempPath);
                }
            }
        }
    }

    #region Private Methods

    // Same marker shape the narrative/ask features emit.
    private static readonly Regex NarrativeCitationRegex = new(@"\[event:([^\]\s]+)\]", RegexOptions.Compiled);

    /// <summary>
    /// Ids cited inline in the prose, in first-appearance order. When the
    /// narrative was generated without citations (no markers), falls back to
    /// the full source-event list so the footer still documents provenance.
    /// </summary>
    private static List<string> CollectCitedEventIds(Narrative narrative)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var section in narrative.Sections)
        {
            foreach (Match match in NarrativeCitationRegex.Matches(section.Prose ?? string.Empty))
            {
                var id = match.Groups[1].Value;
                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        if (ids.Count == 0)
        {
            foreach (var id in narrative.SourceEventIds)
            {
                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private async Task<Dictionary<string, Event>> LoadCitedEventsAsync(List<string> ids)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<string, Event>(StringComparer.Ordinal);
        }

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        var events = await dbContext.Events
            .AsNoTracking()
            .Where(e => ids.Contains(e.EventId))
            .ToListAsync();

        return events.ToDictionary(e => e.EventId, StringComparer.Ordinal);
    }

    /// <summary>
    /// Loads the media attached to the narrative's source events, grouped by
    /// event id in display order. Empty when the narrative was generated
    /// without media (<see cref="Narrative.IncludeMedia"/> false) or no source
    /// event has attachments. Files are referenced where they live — never
    /// copied or embedded — so an export stays a small document and a moved
    /// media library breaks the links rather than the export.
    /// </summary>
    private async Task<Dictionary<string, List<EventMedia>>> LoadNarrativeMediaAsync(Narrative narrative)
    {
        var grouped = new Dictionary<string, List<EventMedia>>(StringComparer.Ordinal);
        if (!narrative.IncludeMedia)
        {
            return grouped;
        }

        var ids = narrative.Sections
            .SelectMany(s => s.SourceEventIds)
            .Concat(narrative.SourceEventIds)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return grouped;
        }

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        var rows = await dbContext.EventMedia
            .AsNoTracking()
            .Where(m => ids.Contains(m.EventId))
            .OrderBy(m => m.SortOrder)
            .ThenBy(m => m.CreatedAt)
            .ToListAsync();

        foreach (var row in rows)
        {
            if (!grouped.TryGetValue(row.EventId, out var list))
            {
                list = new List<EventMedia>();
                grouped[row.EventId] = list;
            }

            list.Add(row);
        }

        return grouped;
    }

    /// <summary>
    /// Where a document should point for an attachment: an absolute file URI
    /// when the media root is known (<c>file:///C:/Users/.../Media/2019/03/x.jpg</c>),
    /// otherwise the stored media-root-relative path with forward slashes.
    /// </summary>
    private string ResolveMediaReference(EventMedia media)
    {
        var path = _mediaService?.GetAbsolutePath(media) ?? media.FilePath;
        if (Path.IsPathRooted(path))
        {
            try
            {
                return new Uri(path).AbsoluteUri;
            }
            catch (UriFormatException)
            {
                // Fall through to the plain path.
            }
        }

        return path.Replace('\\', '/');
    }

    /// <summary>The attachments of one section's source events, in source order.</summary>
    private static IEnumerable<(EventMedia Media, string EventTitle)> SectionMedia(
        NarrativeSection section,
        Dictionary<string, List<EventMedia>> media,
        Dictionary<string, Event> events)
    {
        foreach (var eventId in section.SourceEventIds.Distinct(StringComparer.Ordinal))
        {
            if (!media.TryGetValue(eventId, out var items))
            {
                continue;
            }

            var title = events.TryGetValue(eventId, out var evt) ? evt.Title : eventId;
            foreach (var item in items)
            {
                yield return (item, title);
            }
        }
    }

    private static string EscapeMarkdownLinkText(string text) =>
        text.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");

    private void AppendMarkdownMedia(
        StringBuilder md,
        NarrativeSection section,
        Dictionary<string, List<EventMedia>> media,
        Dictionary<string, Event> events)
    {
        foreach (var (item, eventTitle) in SectionMedia(section, media, events))
        {
            var reference = ResolveMediaReference(item);
            var caption = string.IsNullOrWhiteSpace(item.Caption) ? null : item.Caption.Trim();
            var captionLine = caption == null
                ? $"*{EscapeMarkdownLinkText(eventTitle)}*"
                : $"*{EscapeMarkdownLinkText(caption)}* — {EscapeMarkdownLinkText(eventTitle)}";

            if (item.MediaType == MediaType.Image)
            {
                md.AppendLine($"![{EscapeMarkdownLinkText(caption ?? eventTitle)}]({reference})");
                md.AppendLine();
                md.AppendLine(captionLine);
            }
            else
            {
                var fileName = Path.GetFileName(item.FilePath);
                md.AppendLine($"- [{EscapeMarkdownLinkText(fileName)}]({reference}) — {captionLine}");
            }

            md.AppendLine();
        }
    }

    private void AppendHtmlMedia(
        StringBuilder html,
        NarrativeSection section,
        Dictionary<string, List<EventMedia>> media,
        Dictionary<string, Event> events)
    {
        static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

        foreach (var (item, eventTitle) in SectionMedia(section, media, events))
        {
            var reference = E(ResolveMediaReference(item));
            var caption = string.IsNullOrWhiteSpace(item.Caption) ? null : item.Caption.Trim();
            var captionText = caption == null ? E(eventTitle) : $"{E(caption)} — {E(eventTitle)}";

            if (item.MediaType == MediaType.Image)
            {
                html.AppendLine(
                    $"<figure class=\"media\"><img src=\"{reference}\" alt=\"{E(caption ?? eventTitle)}\"/>" +
                    $"<figcaption>{captionText}</figcaption></figure>");
            }
            else
            {
                var fileName = Path.GetFileName(item.FilePath);
                html.AppendLine(
                    $"<p class=\"media-link\"><a href=\"{reference}\">{E(fileName)}</a> — {captionText}</p>");
            }
        }
    }

    private string BuildNarrativeMarkdown(
        Narrative narrative,
        List<string> citedIds,
        Dictionary<string, Event> citedEvents,
        Dictionary<string, List<EventMedia>> media)
    {
        var md = new StringBuilder();

        md.AppendLine($"# {narrative.Title}");
        md.AppendLine();
        if (!string.IsNullOrWhiteSpace(narrative.ScopeLabel))
        {
            md.AppendLine($"**Scope:** {narrative.ScopeLabel}");
        }
        md.AppendLine($"**Voice:** {narrative.Voice}");
        md.AppendLine($"**Generated:** {narrative.GeneratedAt:yyyy-MM-dd HH:mm} UTC");
        md.AppendLine();
        md.AppendLine("---");
        md.AppendLine();

        if (narrative.Sections.Count == 0)
        {
            md.AppendLine("_No memories were found for this scope._");
            md.AppendLine();
        }

        foreach (var section in narrative.Sections)
        {
            md.AppendLine($"## {section.Heading}");
            md.AppendLine();
            md.AppendLine(section.Prose);
            md.AppendLine();
            AppendMarkdownMedia(md, section, media, citedEvents);
        }

        if (citedIds.Count > 0)
        {
            md.AppendLine("---");
            md.AppendLine();
            md.AppendLine("## Sources");
            md.AppendLine();
            foreach (var id in citedIds)
            {
                md.AppendLine($"- `{id}` — {DescribeCitedEvent(id, citedEvents)}");
            }
        }

        return md.ToString();
    }

    private string BuildNarrativeHtml(
        Narrative narrative,
        List<string> citedIds,
        Dictionary<string, Event> citedEvents,
        Dictionary<string, List<EventMedia>> media)
    {
        // All model-derived text is HTML-encoded — the narrative prose is LLM
        // output and must never be emitted as raw markup.
        static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\"/>");
        html.AppendLine($"<title>{E(narrative.Title)}</title>");
        html.AppendLine("<style>");
        html.AppendLine("body { font-family: Georgia, 'Times New Roman', serif; max-width: 46rem; margin: 2rem auto; padding: 0 1rem; line-height: 1.6; color: #222; background: #fdfdfb; }");
        html.AppendLine("h1 { font-size: 1.9rem; margin-bottom: 0.25rem; }");
        html.AppendLine("h2 { font-size: 1.3rem; margin-top: 2rem; }");
        html.AppendLine(".meta { color: #666; font-size: 0.9rem; margin-bottom: 1.5rem; }");
        html.AppendLine(".sources { margin-top: 2.5rem; border-top: 1px solid #ccc; padding-top: 1rem; font-size: 0.85rem; color: #444; }");
        html.AppendLine(".sources code { background: #eee; padding: 0 0.25rem; border-radius: 3px; }");
        html.AppendLine("figure.media { margin: 1rem 0; }");
        html.AppendLine("figure.media img { max-width: 100%; height: auto; border-radius: 4px; }");
        html.AppendLine("figure.media figcaption, .media-link { color: #666; font-size: 0.85rem; }");
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine($"<h1>{E(narrative.Title)}</h1>");

        var metaParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(narrative.ScopeLabel))
        {
            metaParts.Add(E(narrative.ScopeLabel));
        }
        metaParts.Add(E(narrative.Voice.ToString()) + " voice");
        metaParts.Add("generated " + E(narrative.GeneratedAt.ToString("yyyy-MM-dd HH:mm")) + " UTC");
        html.AppendLine($"<p class=\"meta\">{string.Join(" · ", metaParts)}</p>");

        if (narrative.Sections.Count == 0)
        {
            html.AppendLine("<p><em>No memories were found for this scope.</em></p>");
        }

        foreach (var section in narrative.Sections)
        {
            html.AppendLine($"<h2>{E(section.Heading)}</h2>");
            var paragraphs = (section.Prose ?? string.Empty)
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            foreach (var paragraph in paragraphs)
            {
                html.AppendLine($"<p>{E(paragraph.Trim()).Replace("\n", "<br/>")}</p>");
            }

            AppendHtmlMedia(html, section, media, citedEvents);
        }

        if (citedIds.Count > 0)
        {
            html.AppendLine("<div class=\"sources\">");
            html.AppendLine("<h2>Sources</h2>");
            html.AppendLine("<ul>");
            foreach (var id in citedIds)
            {
                html.AppendLine($"<li><code>{E(id)}</code> — {E(DescribeCitedEvent(id, citedEvents))}</li>");
            }
            html.AppendLine("</ul>");
            html.AppendLine("</div>");
        }

        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    /// <summary>
    /// Footer text for a cited event: title plus its precision-honest display
    /// date (a Year-precision event renders as "1998", never a fabricated day).
    /// </summary>
    private static string DescribeCitedEvent(string id, Dictionary<string, Event> citedEvents)
    {
        if (!citedEvents.TryGetValue(id, out var evt))
        {
            return "(event no longer in the archive)";
        }

        var dateText = DateDisplay.FormatPrecise(evt.StartDate, evt.DatePrecision, evt.EndDate);
        return $"{evt.Title} ({dateText})";
    }

    private async Task<List<Event>> GetFilteredEventsAsync(DateTime? startDate, DateTime? endDate)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        // Event.Tags is [NotMapped]; include the real junction + navigation
        var query = dbContext.Events
            .Include(e => e.EventTags)
            .ThenInclude(et => et.Tag)
            .AsNoTracking()
            .AsQueryable();

        if (startDate.HasValue)
        {
            query = query.Where(e => e.StartDate >= startDate.Value);
        }

        if (endDate.HasValue)
        {
            query = query.Where(e => e.StartDate <= endDate.Value);
        }

        return await query
            .OrderBy(e => e.StartDate)
            .ToListAsync();
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Replace("\"", "\"\"");
    }

    #endregion
}
