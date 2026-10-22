using System.Text;
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
/// Tests for the F10 EXIF GPS backfill (spec: "EXIF GPS on an attached photo
/// populates the event's location coordinates when the location has none").
/// The rule itself is exercised through MediaService's internal
/// BackfillEventLocationCoordinatesAsync (InternalsVisibleTo), and end to end
/// through a full AttachAsync over a JPEG built in memory with a real EXIF
/// GPS IFD (<see cref="BuildJpegWithGps"/>), so MetadataExtractor's parse of
/// the coordinates is part of what is proven; the no-GPS path is covered the
/// same way.
/// </summary>
public class ExifBackfillTests : IDisposable
{
    private readonly TestDbContextFactory _contextFactory;
    private readonly MediaService _mediaService;
    private readonly string _mediaRoot;
    private readonly string _sourceDir;

    public ExifBackfillTests()
    {
        _contextFactory = TestDbContextFactory.CreateInMemory();
        var mediaRepository = new EventMediaRepository(_contextFactory);
        var thumbnailMock = new Mock<IThumbnailGenerator>();
        thumbnailMock
            .Setup(t => t.TryGenerateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _mediaRoot = Path.Combine(Path.GetTempPath(), $"ExifBackfillTests_Root_{Guid.NewGuid()}");
        _sourceDir = Path.Combine(Path.GetTempPath(), $"ExifBackfillTests_Src_{Guid.NewGuid()}");
        Directory.CreateDirectory(_sourceDir);

        _mediaService = new MediaService(
            mediaRepository,
            _contextFactory,
            thumbnailMock.Object,
            new Mock<ILogger<MediaService>>().Object,
            _mediaRoot);
    }

    [Fact]
    public async Task Backfill_EventWithLocatedAndUnlocatedLocations_FillsOnlyTheUnlocatedOne()
    {
        // Arrange - one linked location already has coordinates, one has none
        await SeedEventAsync("event-1");
        await SeedLocationAsync("loc-located", "Amsterdam", latitude: 52.37, longitude: 4.89,
            geocodedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedLocationAsync("loc-unlocated", "The beach house");
        await SeedLinkAsync("event-1", "loc-located");
        await SeedLinkAsync("event-1", "loc-unlocated");

        // Act - the photo says (48.8566, 2.3522)
        await _mediaService.BackfillEventLocationCoordinatesAsync("event-1", 48.8566, 2.3522);

        // Assert - only the coordinate-less location was filled
        var located = await GetLocationAsync("loc-located");
        located.Latitude.Should().Be(52.37, "existing coordinates are never overwritten");
        located.Longitude.Should().Be(4.89);
        located.GeocodedAt.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var unlocated = await GetLocationAsync("loc-unlocated");
        unlocated.Latitude.Should().Be(48.8566);
        unlocated.Longitude.Should().Be(2.3522);
        unlocated.GeocodedAt.Should().BeNull("EXIF coordinates do not come from a geocoder");
    }

    [Fact]
    public async Task Backfill_UnlinkedLocation_IsNeverTouched()
    {
        // Arrange - an unlocated location that belongs to a DIFFERENT event
        await SeedEventAsync("event-1");
        await SeedEventAsync("event-2");
        await SeedLocationAsync("loc-other", "Somewhere else");
        await SeedLinkAsync("event-2", "loc-other");

        // Act
        await _mediaService.BackfillEventLocationCoordinatesAsync("event-1", 10.0, 20.0);

        // Assert
        var other = await GetLocationAsync("loc-other");
        other.Latitude.Should().BeNull();
        other.Longitude.Should().BeNull();
    }

    [Fact]
    public async Task Backfill_HalfSetCoordinates_AreTreatedAsMissingAndOverwritten()
    {
        // Arrange - latitude without longitude is unusable; the pair is replaced
        await SeedEventAsync("event-1");
        await SeedLocationAsync("loc-half", "Half set", latitude: 1.0);
        await SeedLinkAsync("event-1", "loc-half");

        // Act
        await _mediaService.BackfillEventLocationCoordinatesAsync("event-1", -33.87, 151.21);

        // Assert
        var half = await GetLocationAsync("loc-half");
        half.Latitude.Should().Be(-33.87);
        half.Longitude.Should().Be(151.21);
    }

    [Fact]
    public async Task AttachAsync_ImageWithoutGps_LeavesLinkedLocationsUntouched()
    {
        // Arrange - a fake JPEG with no readable EXIF at all
        await SeedEventAsync("event-1");
        await SeedLocationAsync("loc-unlocated", "The beach house");
        await SeedLinkAsync("event-1", "loc-unlocated");
        var source = Path.Combine(_sourceDir, "no-gps.jpg");
        File.WriteAllBytes(source, Encoding.UTF8.GetBytes("pretend this is a jpeg"));

        // Act
        await _mediaService.AttachAsync("event-1", source);

        // Assert - no GPS, no backfill
        var location = await GetLocationAsync("loc-unlocated");
        location.Latitude.Should().BeNull();
        location.Longitude.Should().BeNull();
    }

    [Fact]
    public async Task AttachAsync_JpegWithExifGps_BackfillsTheUnlocatedLocationAndStoresTheCoordinates()
    {
        // Arrange - a linked location with no coordinates, and a JPEG whose
        // EXIF GPS block says 48.8566 N, 2.3522 E (Paris).
        await SeedEventAsync("event-1");
        await SeedLocationAsync("loc-unlocated", "The café");
        await SeedLinkAsync("event-1", "loc-unlocated");
        var source = Path.Combine(_sourceDir, "paris.jpg");
        File.WriteAllBytes(source, BuildJpegWithGps(48.8566, 2.3522));

        // Act - the real extraction path: MetadataExtractor reads the file
        await _mediaService.AttachAsync("event-1", source);

        // Assert - the media row carries the coordinates and the location was filled
        var media = (await _mediaService.GetForEventAsync("event-1")).Should().ContainSingle().Subject;
        media.Latitude.Should().BeApproximately(48.8566, 1e-6);
        media.Longitude.Should().BeApproximately(2.3522, 1e-6);

        var location = await GetLocationAsync("loc-unlocated");
        location.Latitude.Should().BeApproximately(48.8566, 1e-6);
        location.Longitude.Should().BeApproximately(2.3522, 1e-6);
        location.GeocodedAt.Should().BeNull("EXIF coordinates do not come from a geocoder");
    }

    [Fact]
    public async Task AttachAsync_JpegWithSouthernWesternGps_KeepsTheSigns()
    {
        // Arrange - Sydney: 33.8688 S, 151.2093 E becomes (-33.8688, 151.2093);
        // and a western longitude for the other sign.
        await SeedEventAsync("event-1");
        await SeedLocationAsync("loc-unlocated", "Harbour");
        await SeedLinkAsync("event-1", "loc-unlocated");
        var source = Path.Combine(_sourceDir, "sydney.jpg");
        File.WriteAllBytes(source, BuildJpegWithGps(-33.8688, -151.2093));

        // Act
        await _mediaService.AttachAsync("event-1", source);

        // Assert
        var location = await GetLocationAsync("loc-unlocated");
        location.Latitude.Should().BeApproximately(-33.8688, 1e-6);
        location.Longitude.Should().BeApproximately(-151.2093, 1e-6);
    }

    #region Helpers

    /// <summary>
    /// Builds the smallest JPEG MetadataExtractor will read: SOI, one APP1
    /// segment holding an Exif/TIFF block whose IFD0 points at a GPS IFD with
    /// version, latitude/longitude references and the two degree-minute-second
    /// rational triplets, then EOI. No image data is needed to read metadata.
    /// Little-endian TIFF ("II"); all offsets are relative to the TIFF header.
    /// </summary>
    internal static byte[] BuildJpegWithGps(double latitude, double longitude)
    {
        var tiff = new List<byte>();

        void U16(int value) { tiff.Add((byte)(value & 0xFF)); tiff.Add((byte)((value >> 8) & 0xFF)); }
        void U32(long value) { U16((int)(value & 0xFFFF)); U16((int)((value >> 16) & 0xFFFF)); }
        void Entry(int tag, int type, int count, Action valueOrOffset)
        {
            U16(tag); U16(type); U32(count);
            var before = tiff.Count;
            valueOrOffset();
            while (tiff.Count < before + 4) tiff.Add(0); // inline values are left-justified in 4 bytes
        }

        // TIFF header: "II", 42, offset of IFD0 (8).
        tiff.AddRange(new byte[] { 0x49, 0x49 }); U16(42); U32(8);

        // IFD0: one entry (GPS IFD pointer, tag 0x8825, LONG) + next-IFD 0.
        const int ifd0Offset = 8;
        const int gpsIfdOffset = ifd0Offset + 2 + 12 + 4; // 26
        U16(1);
        Entry(0x8825, 4, 1, () => U32(gpsIfdOffset));
        U32(0);

        // GPS IFD: 5 entries + next-IFD 0, then the rational data area.
        const int gpsEntryCount = 5;
        const int dataOffset = gpsIfdOffset + 2 + gpsEntryCount * 12 + 4; // 92
        const int rationalTripletBytes = 3 * 8;
        U16(gpsEntryCount);
        Entry(0x0000, 1, 4, () => tiff.AddRange(new byte[] { 2, 3, 0, 0 }));               // GPSVersionID
        Entry(0x0001, 2, 2, () => tiff.AddRange(new[] { (byte)(latitude < 0 ? 'S' : 'N'), (byte)0 }));  // GPSLatitudeRef
        Entry(0x0002, 5, 3, () => U32(dataOffset));                                          // GPSLatitude
        Entry(0x0003, 2, 2, () => tiff.AddRange(new[] { (byte)(longitude < 0 ? 'W' : 'E'), (byte)0 })); // GPSLongitudeRef
        Entry(0x0004, 5, 3, () => U32(dataOffset + rationalTripletBytes));                   // GPSLongitude
        U32(0);

        tiff.Count.Should().Be(dataOffset, "the data area must start where the entries point");
        WriteDms(Math.Abs(latitude));
        WriteDms(Math.Abs(longitude));

        void WriteDms(double value)
        {
            // Degrees and minutes as integers, seconds to 1/10000 so the
            // round trip is exact to the test's tolerance.
            var degrees = (int)Math.Floor(value);
            var minutesTotal = (value - degrees) * 60.0;
            var minutes = (int)Math.Floor(minutesTotal);
            var seconds = (minutesTotal - minutes) * 60.0;
            U32(degrees); U32(1);
            U32(minutes); U32(1);
            U32((long)Math.Round(seconds * 10000)); U32(10000);
        }

        // APP1 segment: marker, big-endian length (includes the length bytes), "Exif\0\0", TIFF block.
        var exif = new List<byte>(Encoding.ASCII.GetBytes("Exif"));
        exif.Add(0); exif.Add(0);
        exif.AddRange(tiff);
        var segmentLength = exif.Count + 2;

        var jpeg = new List<byte> { 0xFF, 0xD8 };                      // SOI
        jpeg.Add(0xFF); jpeg.Add(0xE1);                                // APP1
        jpeg.Add((byte)(segmentLength >> 8)); jpeg.Add((byte)(segmentLength & 0xFF));
        jpeg.AddRange(exif);
        jpeg.Add(0xFF); jpeg.Add(0xD9);                                // EOI
        return jpeg.ToArray();
    }

    private async Task SeedEventAsync(string eventId)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.Events.Add(new Event
        {
            EventId = eventId,
            Title = $"Event {eventId}",
            StartDate = new DateTime(2020, 6, 1),
            Category = EventCategory.Other
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedLocationAsync(
        string locationId,
        string name,
        double? latitude = null,
        double? longitude = null,
        DateTime? geocodedAt = null)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.Locations.Add(new Location
        {
            LocationId = locationId,
            Name = name,
            Latitude = latitude,
            Longitude = longitude,
            GeocodedAt = geocodedAt
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedLinkAsync(string eventId, string locationId)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.EventLocations.Add(new EventLocation
        {
            EventId = eventId,
            LocationId = locationId
        });
        await context.SaveChangesAsync();
    }

    private async Task<Location> GetLocationAsync(string locationId)
    {
        await using var context = _contextFactory.CreateDbContext();
        return context.Locations.Single(l => l.LocationId == locationId);
    }

    #endregion

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
