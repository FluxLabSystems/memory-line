using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Models;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Data.Models;
using MemoryTimeline.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// The normalization step of <see cref="CaptureIngestionService"/> (design
/// §7.2 "normalize format if required", §8.5): a source the transcriber cannot
/// read as-is is converted through <see cref="IAudioNormalizer"/>, stored as an
/// <c>audio_normalized</c> artifact beside the original, and the queue item
/// transcribes the WAV. Over REAL repositories and EF InMemory with a fake
/// normalizer that writes a genuine 16 kHz WAV, plus the portable
/// <see cref="WavOnlyAudioNormalizer"/>, the <see cref="WavHeader"/> reader
/// and the import service's format acceptance, which all hang off the same
/// rule.
/// </summary>
public class CaptureIngestionNormalizationTests : IDisposable
{
    private readonly TestDbContextFactory _contextFactory = TestDbContextFactory.CreateInMemory();
    private readonly string _root;
    private readonly RecordingStoragePaths _paths;

    public CaptureIngestionNormalizationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"IngestionNormalization_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _paths = new RecordingStoragePaths(Path.Combine(_root, "app"));
    }

    /// <summary>
    /// Stand-in for the Windows transcoder: records what it was asked to do and
    /// writes a real 16 kHz mono 16-bit WAV of the configured length, or fails.
    /// </summary>
    private sealed class FakeAudioNormalizer : IAudioNormalizer
    {
        public List<(string Source, string Destination)> Calls { get; } = new();

        public bool Fail { get; set; }

        public double OutputSeconds { get; set; } = 2.0;

        public Task<AudioNormalizationResult> NormalizeAsync(
            string sourcePath, string destinationWavPath, CancellationToken cancellationToken)
        {
            Calls.Add((sourcePath, destinationWavPath));
            if (Fail)
            {
                return Task.FromResult(new AudioNormalizationResult(false, null, "decoder said no"));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationWavPath)!);
            File.WriteAllBytes(destinationWavPath, BuildWav(16_000, 1, 16, OutputSeconds));
            return Task.FromResult(new AudioNormalizationResult(true, destinationWavPath, null));
        }
    }

    private CaptureIngestionService CreateService(IAudioNormalizer? normalizer) => new(
        new CaptureRepository(_contextFactory),
        new RecordingQueueRepository(_contextFactory),
        new LocalFileArtifactResolver(),
        NullLogger<CaptureIngestionService>.Instance,
        statusPublisher: null,
        audioNormalizer: normalizer,
        storagePaths: _paths);

    #region Local recordings

    [Fact]
    public async Task IngestLocalRecordingAsync_NonWavSource_NormalizesAndQueuesTheWav()
    {
        // Arrange - an imported .m4a (not a RIFF container at all)
        var source = WriteFile("imported.m4a", FakeM4aBytes());
        var normalizer = new FakeAudioNormalizer { OutputSeconds = 3.0 };
        var service = CreateService(normalizer);

        // Act
        var result = await service.IngestLocalRecordingAsync(new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = source,
            CreatedAt = DateTime.UtcNow,
        });

        // Assert - ingested, converted once, from the original to the keyed path
        result.Success.Should().BeTrue();
        result.AlreadyIngested.Should().BeFalse();
        normalizer.Calls.Should().ContainSingle();
        normalizer.Calls[0].Source.Should().Be(source);
        normalizer.Calls[0].Destination.Should().Be(_paths.BuildNormalizedPath(result.CaptureId!, result.ArtifactId!));

        await using var context = _contextFactory.CreateDbContext();

        // The queue item transcribes the WAV and carries its duration...
        var queueItem = await context.RecordingQueues.SingleAsync();
        queueItem.AudioFilePath.Should().Be(normalizer.Calls[0].Destination);
        File.Exists(queueItem.AudioFilePath).Should().BeTrue();
        queueItem.DurationSeconds.Should().BeApproximately(3.0, 0.01);
        // ...while idempotency stays about the bytes the user imported.
        queueItem.ContentSha256.Should().Be(Sha256Hex(File.ReadAllBytes(source)));
        queueItem.AudioArtifactId.Should().Be(result.ArtifactId);

        // Both artifacts, written together.
        var artifacts = await context.CaptureArtifacts.Where(a => a.CaptureId == result.CaptureId).ToListAsync();
        artifacts.Should().HaveCount(2);

        var original = artifacts.Single(a => a.ArtifactType == CaptureArtifactType.AudioOriginal);
        original.ArtifactId.Should().Be(result.ArtifactId);
        original.StorageLocator.Should().Be(source);
        original.MediaType.Should().Be("audio/mp4");
        original.Sha256.Should().Be(queueItem.ContentSha256);

        var normalized = artifacts.Single(a => a.ArtifactType == CaptureArtifactType.AudioNormalized);
        normalized.StorageLocator.Should().Be(queueItem.AudioFilePath);
        normalized.MediaType.Should().Be("audio/wav");
        normalized.UploadState.Should().Be(CaptureArtifactUploadState.Local);
        normalized.ByteLength.Should().Be(new FileInfo(queueItem.AudioFilePath).Length);
        normalized.Sha256.Should().Be(Sha256Hex(File.ReadAllBytes(queueItem.AudioFilePath)));
        WavHeader.TryRead(normalized.StorageLocator)!.IsTranscriptionReady.Should().BeTrue();

        // The outbox describes the original: that is what the device knows.
        var outbox = await context.SyncOutboxEntries.SingleAsync();
        outbox.PayloadJson.Should().Contain(original.ArtifactId);
        outbox.PayloadJson.Should().Contain(original.Sha256!);
    }

    [Fact]
    public async Task IngestLocalRecordingAsync_TranscriptionReadyWav_SkipsNormalizationAndFillsDuration()
    {
        // Arrange - the recorder's own output, handed over without a duration
        var source = WriteFile("recording.wav", BuildWav(16_000, 1, 16, 1.5));
        var normalizer = new FakeAudioNormalizer();
        var service = CreateService(normalizer);

        // Act
        var result = await service.IngestLocalRecordingAsync(new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = source,
            CreatedAt = DateTime.UtcNow,
        });

        // Assert
        result.Success.Should().BeTrue();
        normalizer.Calls.Should().BeEmpty("a 16 kHz mono 16-bit WAV is already what the transcriber reads");

        await using var context = _contextFactory.CreateDbContext();
        var queueItem = await context.RecordingQueues.SingleAsync();
        queueItem.AudioFilePath.Should().Be(source);
        queueItem.DurationSeconds.Should().BeApproximately(1.5, 0.01);
        (await context.CaptureArtifacts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task IngestLocalRecordingAsync_KnownDuration_IsNotOverwritten()
    {
        // Arrange - the recorder measured 42 s; the header is not consulted
        var source = WriteFile("recording.wav", BuildWav(16_000, 1, 16, 1.0));
        var service = CreateService(new FakeAudioNormalizer());

        // Act
        await service.IngestLocalRecordingAsync(new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = source,
            DurationSeconds = 42,
            CreatedAt = DateTime.UtcNow,
        });

        // Assert
        await using var context = _contextFactory.CreateDbContext();
        (await context.RecordingQueues.SingleAsync()).DurationSeconds.Should().Be(42);
    }

    [Fact]
    public async Task IngestLocalRecordingAsync_NormalizerFails_KeepsTheOriginalInTheQueue()
    {
        // Arrange - a container the platform cannot decode
        var source = WriteFile("voice.ogg", FakeM4aBytes());
        var normalizer = new FakeAudioNormalizer { Fail = true };
        var service = CreateService(normalizer);

        // Act
        var result = await service.IngestLocalRecordingAsync(new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = source,
            CreatedAt = DateTime.UtcNow,
        });

        // Assert - the capture is not lost: it is queued on the original and the
        // transcription step will surface the format problem
        result.Success.Should().BeTrue();
        normalizer.Calls.Should().ContainSingle();

        await using var context = _contextFactory.CreateDbContext();
        var queueItem = await context.RecordingQueues.SingleAsync();
        queueItem.AudioFilePath.Should().Be(source);
        queueItem.Status.Should().Be(QueueStatus.Pending);
        (await context.CaptureArtifacts.CountAsync()).Should().Be(1);
        File.Exists(normalizer.Calls[0].Destination).Should().BeFalse("a failed conversion leaves no file behind");
    }

    [Fact]
    public async Task IngestLocalRecordingAsync_NoNormalizerRegistered_KeepsTheOriginalInTheQueue()
    {
        // Arrange - the portable stack before any head registers a transcoder
        var source = WriteFile("imported.m4a", FakeM4aBytes());
        var service = CreateService(normalizer: null);

        // Act
        var result = await service.IngestLocalRecordingAsync(new AudioRecordingDto
        {
            QueueId = Guid.NewGuid().ToString(),
            AudioFilePath = source,
            CreatedAt = DateTime.UtcNow,
        });

        // Assert
        result.Success.Should().BeTrue();
        await using var context = _contextFactory.CreateDbContext();
        (await context.RecordingQueues.SingleAsync()).AudioFilePath.Should().Be(source);
        (await context.CaptureArtifacts.CountAsync()).Should().Be(1);
    }

    #endregion

    #region Remote captures

    [Fact]
    public async Task IngestRemoteCaptureAsync_IphoneM4a_NormalizesAndQueuesTheWav()
    {
        // Arrange - what RemoteChangeApplier hands over for an iOS capture
        var bytes = FakeM4aBytes();
        var source = WriteFile("cap-1_art-1.m4a", bytes);
        var normalizer = new FakeAudioNormalizer();
        var service = CreateService(normalizer);
        var envelope = BuildEnvelope("cap-1", "art-1", source, bytes);

        // Act
        var result = await service.IngestRemoteCaptureAsync(envelope);

        // Assert
        result.Success.Should().BeTrue();
        result.AlreadyIngested.Should().BeFalse();
        normalizer.Calls.Should().ContainSingle();
        normalizer.Calls[0].Destination.Should().Be(_paths.BuildNormalizedPath("cap-1", "art-1"));

        await using var context = _contextFactory.CreateDbContext();
        var queueItem = await context.RecordingQueues.SingleAsync();
        queueItem.SourceCaptureId.Should().Be("cap-1");
        queueItem.SourcePlatform.Should().Be(CapturePlatform.Ios);
        queueItem.AudioFilePath.Should().Be(normalizer.Calls[0].Destination);
        queueItem.ContentSha256.Should().Be(envelope.ExpectedSha256);

        var artifacts = await context.CaptureArtifacts.Where(a => a.CaptureId == "cap-1").ToListAsync();
        artifacts.Select(a => a.ArtifactType).Should().BeEquivalentTo(
            new[] { CaptureArtifactType.AudioOriginal, CaptureArtifactType.AudioNormalized });
        artifacts.Single(a => a.ArtifactType == CaptureArtifactType.AudioOriginal).UploadState
            .Should().Be(CaptureArtifactUploadState.Downloaded);
        artifacts.Single(a => a.ArtifactType == CaptureArtifactType.AudioNormalized).UploadState
            .Should().Be(CaptureArtifactUploadState.Local);
    }

    [Fact]
    public async Task IngestRemoteCaptureAsync_Replay_IsDuplicateAndDoesNotNormalizeAgain()
    {
        // Arrange
        var bytes = FakeM4aBytes();
        var source = WriteFile("cap-2_art-2.m4a", bytes);
        var normalizer = new FakeAudioNormalizer();
        var service = CreateService(normalizer);
        var envelope = BuildEnvelope("cap-2", "art-2", source, bytes);

        // Act
        var first = await service.IngestRemoteCaptureAsync(envelope);
        var second = await service.IngestRemoteCaptureAsync(envelope);

        // Assert - the pre-check short-circuits before the normalizer runs
        first.AlreadyIngested.Should().BeFalse();
        second.AlreadyIngested.Should().BeTrue();
        second.QueueId.Should().Be(first.QueueId);
        normalizer.Calls.Should().ContainSingle();

        await using var context = _contextFactory.CreateDbContext();
        (await context.CaptureArtifacts.CountAsync()).Should().Be(2);
        Directory.GetFiles(_paths.NormalizedAudioRoot).Should().ContainSingle();
    }

    #endregion

    #region WavOnlyAudioNormalizer

    [Fact]
    public async Task WavOnlyAudioNormalizer_TranscriptionReadyWav_CopiesToDestination()
    {
        // Arrange
        var source = WriteFile("ready.wav", BuildWav(16_000, 1, 16, 0.5));
        var destination = Path.Combine(_root, "out", "ready-copy.wav");

        // Act
        var result = await new WavOnlyAudioNormalizer().NormalizeAsync(source, destination, CancellationToken.None);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.OutputPath.Should().Be(destination);
        File.ReadAllBytes(destination).Should().Equal(File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("voice.m4a", false)]
    [InlineData("cd-quality.wav", true)]
    public async Task WavOnlyAudioNormalizer_AnythingElse_FailsWithTheNoTranscoderError(string name, bool isWav)
    {
        // Arrange - a compressed container, and a WAV at the wrong rate/layout
        var source = WriteFile(name, isWav ? BuildWav(44_100, 2, 16, 0.5) : FakeM4aBytes());
        var destination = Path.Combine(_root, "out", "never.wav");

        // Act
        var result = await new WavOnlyAudioNormalizer().NormalizeAsync(source, destination, CancellationToken.None);

        // Assert - the exact wording the port documents
        result.Succeeded.Should().BeFalse();
        result.OutputPath.Should().BeNull();
        result.Error.Should().Be("no audio transcoder is available on this platform");
        File.Exists(destination).Should().BeFalse();
    }

    #endregion

    #region WavHeader

    [Fact]
    public void WavHeader_TryRead_ParsesFormatAndDuration()
    {
        // Arrange
        var path = WriteFile("parse.wav", BuildWav(16_000, 1, 16, 2.25));

        // Act
        var header = WavHeader.TryRead(path);

        // Assert
        header.Should().NotBeNull();
        header!.AudioFormat.Should().Be(WavHeader.PcmFormat);
        header.SampleRate.Should().Be(16_000);
        header.Channels.Should().Be(1);
        header.BitsPerSample.Should().Be(16);
        header.ByteRate.Should().Be(32_000);
        header.IsTranscriptionReady.Should().BeTrue();
        header.Duration.TotalSeconds.Should().BeApproximately(2.25, 0.001);
    }

    [Fact]
    public void WavHeader_TryRead_StereoCdQuality_IsNotTranscriptionReady()
    {
        var path = WriteFile("cd.wav", BuildWav(44_100, 2, 16, 1.0));

        var header = WavHeader.TryRead(path);

        header.Should().NotBeNull();
        header!.SampleRate.Should().Be(44_100);
        header.Channels.Should().Be(2);
        header.IsTranscriptionReady.Should().BeFalse();
    }

    [Fact]
    public void WavHeader_TryRead_NonWavOrMissing_ReturnsNull()
    {
        WavHeader.TryRead(WriteFile("not.wav", FakeM4aBytes())).Should().BeNull();
        WavHeader.TryRead(WriteFile("short.wav", "RIFF"u8.ToArray())).Should().BeNull();
        WavHeader.TryRead(Path.Combine(_root, "missing.wav")).Should().BeNull();
    }

    #endregion

    #region AudioImportService format acceptance

    [Fact]
    public void AudioImportService_WithPlatformNormalizer_AcceptsEveryAdvertisedFormat()
    {
        var service = CreateImportService(new FakeAudioNormalizer());

        foreach (var extension in new AudioImportOptions().SupportedFormats)
        {
            service.IsSupportedFormat($"clip{extension}").Should().BeTrue(extension);
        }

        service.IsSupportedFormat("clip.txt").Should().BeFalse();
        // The per-call options can still narrow the list.
        service.IsSupportedFormat("clip.m4a", new AudioImportOptions { SupportedFormats = new[] { ".wav" } })
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AudioImportService_WithoutATranscoder_AcceptsWavOnly(bool wavOnlyNormalizer)
    {
        // The portable default converts nothing, so it must not widen the list
        var service = CreateImportService(wavOnlyNormalizer ? new WavOnlyAudioNormalizer() : null);

        service.IsSupportedFormat("clip.wav").Should().BeTrue();
        service.IsSupportedFormat("clip.WAV").Should().BeTrue();
        service.IsSupportedFormat("clip.m4a").Should().BeFalse();
        service.IsSupportedFormat("clip.mp3").Should().BeFalse();
    }

    [Fact]
    public async Task AudioImportService_GetFileInfo_ReadsWavDurationAndLeavesCompressedUnknown()
    {
        var service = CreateImportService(new FakeAudioNormalizer());

        var wav = await service.GetFileInfoAsync(WriteFile("info.wav", BuildWav(16_000, 1, 16, 4.0)));
        var m4a = await service.GetFileInfoAsync(WriteFile("info.m4a", FakeM4aBytes()));

        wav.Duration.Should().NotBeNull();
        wav.Duration!.Value.TotalSeconds.Should().BeApproximately(4.0, 0.001);
        m4a.Duration.Should().BeNull("a compressed file's duration is known once ingestion has normalized it");
    }

    private AudioImportService CreateImportService(IAudioNormalizer? normalizer) => new(
        _contextFactory,
        new Mock<IQueueService>().Object,
        new Mock<ICaptureIngestionService>().Object,
        NullLogger<AudioImportService>.Instance,
        audioNormalizer: normalizer);

    #endregion

    #region Helpers

    private static RemoteCaptureEnvelope BuildEnvelope(string captureId, string artifactId, string localPath, byte[] bytes) => new()
    {
        CaptureId = captureId,
        SourceDeviceId = "iphone-1",
        SourcePlatform = CapturePlatform.Ios,
        CaptureType = CaptureTypes.Memory,
        CapturedAtUtc = DateTime.UtcNow,
        ArtifactId = artifactId,
        OriginalFileName = Path.GetFileName(localPath),
        MediaType = "audio/mp4",
        ExpectedByteLength = bytes.Length,
        ExpectedSha256 = Sha256Hex(bytes),
        StorageLocator = localPath,
    };

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Not a RIFF container; the ftyp box an MP4/M4A starts with, then noise.</summary>
    private static byte[] FakeM4aBytes()
    {
        var bytes = new byte[2048];
        new Random(7).NextBytes(bytes);
        Encoding.ASCII.GetBytes("\0\0\0\u0018ftypM4A ").CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>A canonical 44-byte-header PCM WAV with silent sample data.</summary>
    internal static byte[] BuildWav(int sampleRate, short channels, short bitsPerSample, double seconds)
    {
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;
        var dataLength = (int)(byteRate * seconds);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        writer.Flush();
        return stream.ToArray();
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    #endregion
}
