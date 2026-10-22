using MemoryTimeline.Core.Models;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Converts a recording in any container the platform can decode into the one
/// layout the transcription engine reads: 16 kHz, mono, 16-bit PCM WAV
/// (design §7.2 "normalize format if required", §8.5). A platform port in the
/// <c>IThumbnailGenerator</c> shape: declared here, implemented in the head
/// (<c>WindowsAudioNormalizer</c> over <c>Windows.Media.Transcoding</c>), with
/// <see cref="WavOnlyAudioNormalizer"/> as the portable default for tests and
/// for a head that has no transcoder yet.
///
/// <para>Called by <see cref="CaptureIngestionService"/> for every source that
/// is not already transcription-ready — an iPhone's AAC <c>.m4a</c>, an
/// imported MP3, a 44.1 kHz stereo WAV. The original artifact is kept; the
/// output becomes an <c>audio_normalized</c> artifact and the file the queue
/// item transcribes.</para>
/// </summary>
public interface IAudioNormalizer
{
    /// <summary>
    /// Writes <paramref name="sourcePath"/> as 16 kHz mono 16-bit PCM WAV at
    /// <paramref name="destinationWavPath"/>. Never throws for a format or
    /// decoder problem — that is a failed result with a user-readable
    /// <see cref="AudioNormalizationResult.Error"/>; only cancellation propagates.
    /// </summary>
    Task<AudioNormalizationResult> NormalizeAsync(
        string sourcePath, string destinationWavPath, CancellationToken cancellationToken);
}

/// <summary>Outcome of one normalization.</summary>
/// <param name="Succeeded">True when <paramref name="OutputPath"/> holds a transcription-ready WAV.</param>
/// <param name="OutputPath">The written file, when successful.</param>
/// <param name="Error">User-readable reason, when not.</param>
public sealed record AudioNormalizationResult(bool Succeeded, string? OutputPath, string? Error);

/// <summary>
/// The portable default: it can copy a WAV that is already 16 kHz mono 16-bit
/// PCM and nothing else. A head without a transcoder gets exactly the
/// behaviour it had before normalization existed — WAV recordings work,
/// compressed captures fail with a reason — while the pipeline, the artifact
/// rows and the tests stay the same on every platform. Register the platform
/// implementation instead wherever one exists.
/// </summary>
public sealed class WavOnlyAudioNormalizer : IAudioNormalizer
{
    /// <summary>The failure every non-WAV source gets from this implementation.</summary>
    public const string NoTranscoderError = "no audio transcoder is available on this platform";

    /// <inheritdoc />
    public async Task<AudioNormalizationResult> NormalizeAsync(
        string sourcePath, string destinationWavPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var header = WavHeader.TryRead(sourcePath);
        if (header is not { IsTranscriptionReady: true })
        {
            return new AudioNormalizationResult(false, null, NoTranscoderError);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationWavPath)!);
            await using (var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true))
            await using (var destination = new FileStream(
                destinationWavPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            return new AudioNormalizationResult(true, destinationWavPath, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AudioNormalizationResult(false, null, $"The recording could not be copied: {ex.Message}");
        }
    }
}
