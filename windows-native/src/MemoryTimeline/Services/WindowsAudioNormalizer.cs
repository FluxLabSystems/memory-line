using MemoryTimeline.Core.Services;
using Microsoft.Extensions.Logging;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace MemoryTimeline.Services;

/// <summary>
/// Windows <see cref="IAudioNormalizer"/> over <see cref="MediaTranscoder"/>:
/// decodes whatever Media Foundation can read (AAC/.m4a from the iPhone,
/// MP3, WMA, FLAC, any-rate WAV) and writes 16 kHz mono 16-bit PCM WAV — the
/// one layout the bundled Whisper engine accepts. Registered as a singleton;
/// the transcoder is created per call because it is cheap and holds no state
/// worth sharing.
///
/// <para>A container Media Foundation has no decoder for (Ogg Vorbis/Opus
/// without a codec pack) is reported through
/// <see cref="PrepareTranscodeResult.CanTranscode"/> and comes back as a
/// failed result with the <see cref="TranscodeFailureReason"/>, never as a
/// throw — ingestion keeps the original and the queue shows the format
/// problem at transcription time.</para>
/// </summary>
public sealed class WindowsAudioNormalizer : IAudioNormalizer
{
    private readonly ILogger<WindowsAudioNormalizer> _logger;

    public WindowsAudioNormalizer(ILogger<WindowsAudioNormalizer> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AudioNormalizationResult> NormalizeAsync(
        string sourcePath, string destinationWavPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(sourcePath))
        {
            return new AudioNormalizationResult(false, null, $"Source audio not found: {Path.GetFileName(sourcePath)}");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationWavPath);
        if (string.IsNullOrEmpty(destinationDirectory))
        {
            return new AudioNormalizationResult(false, null, "Destination path has no directory.");
        }

        try
        {
            Directory.CreateDirectory(destinationDirectory);

            // StorageFile APIs work on plain paths in an unpackaged app; the
            // transcoder only speaks IStorageFile, so the destination is created
            // through its folder (ReplaceExisting: a retried ingestion lands on
            // the same keyed path).
            var source = await StorageFile.GetFileFromPathAsync(sourcePath);
            var folder = await StorageFolder.GetFolderFromPathAsync(destinationDirectory);
            var destination = await folder.CreateFileAsync(
                Path.GetFileName(destinationWavPath), CreationCollisionOption.ReplaceExisting);

            // CreateWav gives a WAV container with default PCM properties; the
            // Audio properties are replaced with the transcriber's exact layout
            // so the resampler and the channel mixer do the work here, once.
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Auto);
            profile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16); // sampleRate, channelCount, bitsPerSample

            var transcoder = new MediaTranscoder();
            var prepared = await transcoder.PrepareFileTranscodeAsync(source, destination, profile);
            if (!prepared.CanTranscode)
            {
                TryDelete(destinationWavPath);
                _logger.LogWarning(
                    "Media Foundation cannot transcode {FileName}: {Reason}",
                    Path.GetFileName(sourcePath), prepared.FailureReason);
                return new AudioNormalizationResult(
                    false, null, $"This audio format cannot be transcoded on this machine ({prepared.FailureReason}).");
            }

            await prepared.TranscodeAsync().AsTask(cancellationToken);

            _logger.LogInformation(
                "Transcoded {FileName} to 16 kHz mono PCM WAV ({Bytes} bytes)",
                Path.GetFileName(sourcePath), new FileInfo(destinationWavPath).Length);
            return new AudioNormalizationResult(true, destinationWavPath, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(destinationWavPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(destinationWavPath);
            _logger.LogWarning(ex, "Transcoding {FileName} failed", Path.GetFileName(sourcePath));
            return new AudioNormalizationResult(false, null, $"Audio transcoding failed: {ex.Message}");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete partial transcode output {Path}", path);
        }
    }
}
