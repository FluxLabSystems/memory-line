using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Tests;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Tests for ExportService.ExportNarrativeAsync (F5): Markdown/HTML content,
/// the atomic temp-file write (no .tmp leftovers, no partial file on
/// failure), HTML encoding of model text, the cited-events footer, and the
/// attached-media references written when the narrative asked for media.
/// </summary>
public class NarrativeExportTests : IDisposable
{
    private readonly TestDbContextFactory _contextFactory;
    private readonly ExportService _exportService;
    private readonly List<string> _tempPaths = new();
    private readonly string _mediaRoot;

    public NarrativeExportTests()
    {
        _contextFactory = TestDbContextFactory.CreateInMemory();

        // The export only REFERENCES media files, so the root need not exist;
        // the mock resolves stored relative paths exactly as MediaService does.
        _mediaRoot = Path.Combine(Path.GetTempPath(), $"NarrativeExportTests_Media_{Guid.NewGuid()}");
        var mediaServiceMock = new Mock<IMediaService>();
        mediaServiceMock.Setup(m => m.MediaRoot).Returns(_mediaRoot);
        mediaServiceMock
            .Setup(m => m.GetAbsolutePath(It.IsAny<EventMedia>()))
            .Returns<EventMedia>(media => Path.Combine(_mediaRoot, media.FilePath));

        _exportService = new ExportService(
            _contextFactory,
            new Mock<ILogger<ExportService>>().Object,
            mediaServiceMock.Object);
    }

    #region Markdown

    [Fact]
    public async Task ExportNarrativeAsync_Markdown_WritesTitleSectionsAndSourcesFooter()
    {
        // Arrange: a real cited event so the footer can resolve it.
        await SeedEventAsync("e1", "First day", new DateTime(2019, 3, 14));
        var narrative = BuildNarrative(
            title: "My Year",
            heading: "2019",
            prose: "It began. [event:e1]",
            sourceIds: new List<string> { "e1" });
        var path = NewTempPath(".md");

        // Act
        await _exportService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Markdown);

        // Assert: file written, no temp leftover.
        File.Exists(path).Should().BeTrue();
        File.Exists(path + ".tmp").Should().BeFalse();

        var content = await File.ReadAllTextAsync(path);
        content.Should().Contain("# My Year");
        content.Should().Contain("## 2019");
        content.Should().Contain("It began. [event:e1]");

        // Footer resolves the citation to a real event id with title and
        // precision-honest display date.
        content.Should().Contain("## Sources");
        content.Should().Contain("`e1`");
        content.Should().Contain("First day");
        content.Should().Contain("14 Mar 2019");
    }

    [Fact]
    public async Task ExportNarrativeAsync_YearPrecisionCitedEvent_FooterShowsYearOnly()
    {
        // Arrange: a Year-precision event must never be exported with a
        // fabricated exact date.
        await SeedEventAsync("e-year", "Ski trip", new DateTime(1998, 7, 1), DatePrecision.Year);
        var narrative = BuildNarrative(
            title: "Old Times",
            heading: "1998",
            prose: "We skied. [event:e-year]",
            sourceIds: new List<string> { "e-year" });
        var path = NewTempPath(".md");

        // Act
        await _exportService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Markdown);

        // Assert
        var content = await File.ReadAllTextAsync(path);
        content.Should().Contain("Ski trip (1998)");
        content.Should().NotContain("1 Jul 1998");
        content.Should().NotContain("1998-07-01");
    }

    [Fact]
    public async Task ExportNarrativeAsync_NoMarkersInProse_FooterListsSourceEventIds()
    {
        // Arrange: citations were stripped at generation time; the footer
        // falls back to the narrative's source-event provenance.
        await SeedEventAsync("e1", "First day", new DateTime(2019, 3, 14));
        var narrative = BuildNarrative(
            title: "My Year",
            heading: "2019",
            prose: "It began, plainly.",
            sourceIds: new List<string> { "e1" });
        var path = NewTempPath(".md");

        // Act
        await _exportService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Markdown);

        // Assert
        var content = await File.ReadAllTextAsync(path);
        content.Should().Contain("## Sources");
        content.Should().Contain("`e1`");
    }

    [Fact]
    public async Task ExportNarrativeAsync_CitedEventMissingFromDb_FooterSaysSo()
    {
        // Arrange: prose cites an id that no longer exists.
        var narrative = BuildNarrative(
            title: "My Year",
            heading: "2019",
            prose: "Something. [event:ghost-1]",
            sourceIds: new List<string> { "ghost-1" });
        var path = NewTempPath(".md");

        // Act
        await _exportService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Markdown);

        // Assert
        var content = await File.ReadAllTextAsync(path);
        content.Should().Contain("`ghost-1`");
        content.Should().Contain("(event no longer in the archive)");
    }

    #endregion

    #region HTML

    [Fact]
    public async Task ExportNarrativeAsync_Html_WritesSelfContainedEncodedDocument()
    {
        // Arrange: title and prose contain markup-hostile characters that
        // MUST come out HTML-encoded (model text is never trusted as markup).
        await SeedEventAsync("e1", "First day", new DateTime(2019, 3, 14));
        var narrative = BuildNarrative(
            title: "Tom & Jerry",
            heading: "2019 <early>",
            prose: "It began with <b>bold</b> plans. [event:e1]",
            sourceIds: new List<string> { "e1" });
        var path = NewTempPath(".html");

        // Act
        await _exportService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Html);

        // Assert
        File.Exists(path).Should().BeTrue();
        File.Exists(path + ".tmp").Should().BeFalse();

        var content = await File.ReadAllTextAsync(path);
        content.Should().StartWith("<!DOCTYPE html>");
        content.Should().Contain("<style>");
        content.Should().Contain("Tom &amp; Jerry");
        content.Should().Contain("2019 &lt;early&gt;");
        content.Should().Contain("&lt;b&gt;bold&lt;/b&gt;");
        content.Should().NotContain("<b>bold</b>");

        // Footer present with the cited event.
        content.Should().Contain("Sources");
        content.Should().Contain("First day");
    }

    #endregion

    #region Media references (IncludeMedia)

    [Fact]
    public async Task ExportNarrativeAsync_IncludeMedia_Markdown_ReferencesImagesWithCaptions()
    {
        // Arrange: a captioned photo attached to the cited event.
        await SeedEventAsync("e1", "First day", new DateTime(2019, 3, 14));
        await SeedMediaAsync("m1", "e1", Path.Combine("2019", "03", "photo.jpg"), MediaType.Image, "Our first morning");
        var narrative = BuildNarrative("My Year", "2019", "It began. [event:e1]", new List<string> { "e1" });
        narrative.IncludeMedia = true;
        var path = NewTempPath(".md");

        // Act
        await _exportService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Markdown);

        // Assert: an image reference to the file's absolute location plus the
        // caption and the owning event's title.
        var content = await File.ReadAllTextAsync(path);
        var expectedReference = new Uri(Path.Combine(_mediaRoot, "2019", "03", "photo.jpg")).AbsoluteUri;
        content.Should().Contain($"![Our first morning]({expectedReference})");
        content.Should().Contain("*Our first morning* — First day");
    }

    [Fact]
    public async Task ExportNarrativeAsync_IncludeMediaFalse_OmitsAttachments()
    {
        // Arrange: the same attachment, but the narrative did not ask for media.
        await SeedEventAsync("e1", "First day", new DateTime(2019, 3, 14));
        await SeedMediaAsync("m1", "e1", Path.Combine("2019", "03", "photo.jpg"), MediaType.Image, "Our first morning");
        var narrative = BuildNarrative("My Year", "2019", "It began. [event:e1]", new List<string> { "e1" });
        narrative.IncludeMedia = false;
        var path = NewTempPath(".md");

        // Act
        await _exportService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Markdown);

        // Assert
        var content = await File.ReadAllTextAsync(path);
        content.Should().NotContain("photo.jpg");
        content.Should().NotContain("Our first morning");
    }

    [Fact]
    public async Task ExportNarrativeAsync_IncludeMedia_UncaptionedImageUsesEventTitle_OtherMediaBecomesLink()
    {
        // Arrange: an uncaptioned photo and a captioned document on the same event.
        await SeedEventAsync("e1", "First day", new DateTime(2019, 3, 14));
        await SeedMediaAsync("m1", "e1", Path.Combine("2019", "03", "photo.jpg"), MediaType.Image, caption: null, sortOrder: 0);
        await SeedMediaAsync("m2", "e1", Path.Combine("2019", "03", "notes.pdf"), MediaType.Document, "Scanned notes", sortOrder: 1);
        var narrative = BuildNarrative("My Year", "2019", "It began. [event:e1]", new List<string> { "e1" });
        narrative.IncludeMedia = true;
        var path = NewTempPath(".md");

        // Act
        await _exportService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Markdown);

        // Assert: the image falls back to the event title; the document is a
        // plain link (never an image reference), with its caption.
        var content = await File.ReadAllTextAsync(path);
        var photoReference = new Uri(Path.Combine(_mediaRoot, "2019", "03", "photo.jpg")).AbsoluteUri;
        var notesReference = new Uri(Path.Combine(_mediaRoot, "2019", "03", "notes.pdf")).AbsoluteUri;
        content.Should().Contain($"![First day]({photoReference})");
        content.Should().Contain($"- [notes.pdf]({notesReference}) — *Scanned notes* — First day");
        content.Should().NotContain("![notes.pdf]");
        content.IndexOf("photo.jpg", StringComparison.Ordinal).Should()
            .BeLessThan(content.IndexOf("notes.pdf", StringComparison.Ordinal), "sort order is preserved");
    }

    [Fact]
    public async Task ExportNarrativeAsync_IncludeMedia_Html_RendersEncodedFigure()
    {
        // Arrange: a caption with markup-hostile characters.
        await SeedEventAsync("e1", "First day", new DateTime(2019, 3, 14));
        await SeedMediaAsync("m1", "e1", Path.Combine("2019", "03", "photo.jpg"), MediaType.Image, "Tom & Jerry's <first> walk");
        var narrative = BuildNarrative("My Year", "2019", "It began. [event:e1]", new List<string> { "e1" });
        narrative.IncludeMedia = true;
        var path = NewTempPath(".html");

        // Act
        await _exportService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Html);

        // Assert
        var content = await File.ReadAllTextAsync(path);
        var reference = new Uri(Path.Combine(_mediaRoot, "2019", "03", "photo.jpg")).AbsoluteUri;
        content.Should().Contain($"<figure class=\"media\"><img src=\"{reference}\" alt=\"Tom &amp; Jerry&#39;s &lt;first&gt; walk\"/>");
        content.Should().Contain("<figcaption>Tom &amp; Jerry&#39;s &lt;first&gt; walk — First day</figcaption>");
        content.Should().NotContain("<first>");
    }

    [Fact]
    public async Task ExportNarrativeAsync_IncludeMedia_WithoutMediaService_WritesRelativePath()
    {
        // Arrange: no IMediaService registered (the two-argument constructor),
        // so the stored media-root-relative path is written with forward slashes.
        var bareService = new ExportService(_contextFactory, new Mock<ILogger<ExportService>>().Object);
        await SeedEventAsync("e1", "First day", new DateTime(2019, 3, 14));
        await SeedMediaAsync("m1", "e1", Path.Combine("2019", "03", "photo.jpg"), MediaType.Image, "Morning");
        var narrative = BuildNarrative("My Year", "2019", "It began. [event:e1]", new List<string> { "e1" });
        narrative.IncludeMedia = true;
        var path = NewTempPath(".md");

        // Act
        await bareService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Markdown);

        // Assert
        var content = await File.ReadAllTextAsync(path);
        content.Should().Contain("![Morning](2019/03/photo.jpg)");
    }

    #endregion

    #region Atomic write / failure behavior

    [Fact]
    public async Task ExportNarrativeAsync_TargetDirectoryMissing_ThrowsAndLeavesNoFiles()
    {
        // Arrange: a path whose directory does not exist makes the temp-file
        // write itself fail — neither the target nor a .tmp may remain.
        var narrative = BuildNarrative("T", "H", "P", new List<string>());
        var missingDir = Path.Combine(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid()}");
        var path = Path.Combine(missingDir, "story.md");

        // Act
        Func<Task> act = () => _exportService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Markdown);

        // Assert
        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        File.Exists(path).Should().BeFalse();
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task ExportNarrativeAsync_OverwritesExistingFileAtomically()
    {
        // Arrange: pickers pre-create the chosen file; the atomic move must
        // overwrite it rather than fail.
        var narrative = BuildNarrative("New Story", "H", "Fresh prose.", new List<string>());
        var path = NewTempPath(".md");
        await File.WriteAllTextAsync(path, "old placeholder content");

        // Act
        await _exportService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Markdown);

        // Assert
        var content = await File.ReadAllTextAsync(path);
        content.Should().Contain("# New Story");
        content.Should().NotContain("old placeholder content");
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task ExportNarrativeAsync_EmptyNarrative_WritesHonestPlaceholder()
    {
        // Arrange: a zero-event scope produced an empty narrative; exporting
        // it must not fabricate content.
        var narrative = new Narrative
        {
            Title = "The story of 2020",
            Scope = NarrativeScope.DateRange,
            ScopeLabel = "2020",
            GeneratedAt = DateTime.UtcNow,
            Voice = NarrativeVoice.Reflective
        };
        var path = NewTempPath(".md");

        // Act
        await _exportService.ExportNarrativeAsync(narrative, path, NarrativeFormat.Markdown);

        // Assert
        var content = await File.ReadAllTextAsync(path);
        content.Should().Contain("No memories were found for this scope.");
        content.Should().NotContain("## Sources");
    }

    #endregion

    #region Helpers

    private static Narrative BuildNarrative(string title, string heading, string prose, List<string> sourceIds)
    {
        return new Narrative
        {
            Title = title,
            Scope = NarrativeScope.DateRange,
            ScopeLabel = "a test scope",
            GeneratedAt = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc),
            Voice = NarrativeVoice.Reflective,
            Sections = new List<NarrativeSection>
            {
                new()
                {
                    Heading = heading,
                    Prose = prose,
                    SourceEventIds = new List<string>(sourceIds)
                }
            },
            SourceEventIds = new List<string>(sourceIds)
        };
    }

    private async Task SeedMediaAsync(
        string mediaId, string eventId, string filePath, MediaType mediaType, string? caption, int sortOrder = 0)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.EventMedia.Add(new EventMedia
        {
            MediaId = mediaId,
            EventId = eventId,
            FilePath = filePath,
            MediaType = mediaType,
            Caption = caption,
            SortOrder = sortOrder
        });
        await context.SaveChangesAsync();
    }

    private string NewTempPath(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"NarrativeExportTests_{Guid.NewGuid()}{extension}");
        _tempPaths.Add(path);
        return path;
    }

    private async Task SeedEventAsync(
        string eventId, string title, DateTime startDate, DatePrecision precision = DatePrecision.Day)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.Events.Add(new Event
        {
            EventId = eventId,
            Title = title,
            StartDate = startDate,
            DatePrecision = precision,
            Category = "other"
        });
        await context.SaveChangesAsync();
    }

    #endregion

    public void Dispose()
    {
        using (var context = _contextFactory.CreateDbContext())
        {
            context.Database.EnsureDeleted();
        }

        foreach (var path in _tempPaths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (File.Exists(path + ".tmp"))
            {
                File.Delete(path + ".tmp");
            }
        }
    }
}
