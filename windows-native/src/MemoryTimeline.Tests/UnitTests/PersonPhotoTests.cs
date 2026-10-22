using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using MemoryTimeline.Tests;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Contact photos: <see cref="MediaService.ImportPersonPhotoAsync"/> copies a
/// chosen image into the managed tree's people folder and returns the
/// media-root-relative path stored in people.photo_path, and the Clear
/// Cache sweep keeps every photo a person row still references while
/// collecting the rest.
/// </summary>
public class PersonPhotoTests : IDisposable
{
    private readonly TestDbContextFactory _contextFactory;
    private readonly string _mediaRoot;
    private readonly string _sourceDir;
    private readonly MediaService _mediaService;

    public PersonPhotoTests()
    {
        _contextFactory = TestDbContextFactory.CreateInMemory();
        _mediaRoot = Path.Combine(Path.GetTempPath(), $"PersonPhotoTests_Root_{Guid.NewGuid()}");
        _sourceDir = Path.Combine(Path.GetTempPath(), $"PersonPhotoTests_Src_{Guid.NewGuid()}");
        Directory.CreateDirectory(_sourceDir);
        _mediaService = new MediaService(
            new EventMediaRepository(_contextFactory),
            _contextFactory,
            new Mock<IThumbnailGenerator>().Object,
            new Mock<ILogger<MediaService>>().Object,
            mediaRootOverride: _mediaRoot);
    }

    [Fact]
    public async Task ImportPersonPhotoAsync_CopiesTheImageUnderPeopleAndReturnsARelativePath()
    {
        // Arrange
        var source = Path.Combine(_sourceDir, "Portrait.JPG");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4, 5 });

        // Act
        var relative = await _mediaService.ImportPersonPhotoAsync(source);

        // Assert: relative to the root, under people/, lowercase extension,
        // the source untouched, the copy byte-identical.
        Path.IsPathRooted(relative).Should().BeFalse();
        relative.Replace('\\', '/').Should().StartWith(MediaService.PeoplePhotosDirectoryName + "/");
        relative.Should().EndWith(".jpg");
        var absolute = _mediaService.GetAbsolutePersonPhotoPath(relative)!;
        absolute.Should().Be(Path.Combine(_mediaRoot, relative));
        File.ReadAllBytes(absolute).Should().Equal(1, 2, 3, 4, 5);
        File.Exists(source).Should().BeTrue();
    }

    [Fact]
    public async Task ImportPersonPhotoAsync_TwoImportsOfTheSameFile_GetDistinctPaths()
    {
        // Two people may share the same source picture; each gets its own copy.
        var source = Path.Combine(_sourceDir, "shared.png");
        File.WriteAllBytes(source, new byte[] { 9 });

        var first = await _mediaService.ImportPersonPhotoAsync(source);
        var second = await _mediaService.ImportPersonPhotoAsync(source);

        first.Should().NotBe(second);
        File.Exists(Path.Combine(_mediaRoot, first)).Should().BeTrue();
        File.Exists(Path.Combine(_mediaRoot, second)).Should().BeTrue();
    }

    [Fact]
    public async Task ImportPersonPhotoAsync_NonImage_IsRejectedWithAUserMessage()
    {
        var source = Path.Combine(_sourceDir, "notes.pdf");
        File.WriteAllText(source, "not a picture");

        var act = () => _mediaService.ImportPersonPhotoAsync(source);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a supported image*");
        Directory.Exists(Path.Combine(_mediaRoot, MediaService.PeoplePhotosDirectoryName)).Should().BeFalse();
    }

    [Fact]
    public async Task ImportPersonPhotoAsync_MissingSource_Throws()
    {
        var act = () => _mediaService.ImportPersonPhotoAsync(Path.Combine(_sourceDir, "gone.jpg"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public void GetAbsolutePersonPhotoPath_ResolvesRelativeKeepsAbsoluteAndMapsBlankToNull()
    {
        _mediaService.GetAbsolutePersonPhotoPath(null).Should().BeNull();
        _mediaService.GetAbsolutePersonPhotoPath("  ").Should().BeNull();
        _mediaService.GetAbsolutePersonPhotoPath(Path.Combine("people", "x.jpg"))
            .Should().Be(Path.Combine(_mediaRoot, "people", "x.jpg"));
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere.jpg");
        _mediaService.GetAbsolutePersonPhotoPath(absolute).Should().Be(absolute);
    }

    [Fact]
    public async Task CleanupOrphansAsync_KeepsReferencedPersonPhotosAndSweepsTheRest()
    {
        // Arrange: one photo referenced by a person row, one left behind by a
        // replaced photo; both older than the grace window.
        var keptRelative = Path.Combine(MediaService.PeoplePhotosDirectoryName, "kept.jpg");
        var orphanRelative = Path.Combine(MediaService.PeoplePhotosDirectoryName, "replaced.jpg");
        WriteAgedFile(Path.Combine(_mediaRoot, keptRelative));
        WriteAgedFile(Path.Combine(_mediaRoot, orphanRelative));

        await using (var context = _contextFactory.CreateDbContext())
        {
            context.People.Add(new Person
            {
                PersonId = "p1",
                Name = "Photo Person",
                PhotoPath = keptRelative,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        // Act
        var result = await _mediaService.CleanupOrphansAsync();

        // Assert
        File.Exists(Path.Combine(_mediaRoot, keptRelative)).Should().BeTrue("a person still references it");
        File.Exists(Path.Combine(_mediaRoot, orphanRelative)).Should().BeFalse("nothing references it any more");
        result.MediaFilesDeleted.Should().Be(1);
    }

    [Fact]
    public async Task CleanupOrphansAsync_KeepsAPersonPhotoStoredAsAnAbsolutePathUnderTheRoot()
    {
        // Arrange: a legacy row holding the absolute path instead of the relative one.
        var relative = Path.Combine(MediaService.PeoplePhotosDirectoryName, "legacy.png");
        var absolute = Path.Combine(_mediaRoot, relative);
        WriteAgedFile(absolute);
        await using (var context = _contextFactory.CreateDbContext())
        {
            context.People.Add(new Person
            {
                PersonId = "p2",
                Name = "Legacy Path",
                PhotoPath = absolute,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        // Act
        await _mediaService.CleanupOrphansAsync();

        // Assert
        File.Exists(absolute).Should().BeTrue();
    }

    private static void WriteAgedFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "photo bytes");
        File.SetCreationTimeUtc(path, DateTime.UtcNow - MediaService.CleanupGraceWindow - TimeSpan.FromMinutes(5));
    }

    public void Dispose()
    {
        using (var context = _contextFactory.CreateDbContext())
        {
            context.Database.EnsureDeleted();
        }

        foreach (var directory in new[] { _mediaRoot, _sourceDir })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
