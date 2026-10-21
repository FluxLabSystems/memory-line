namespace MemoryTimeline.Core.Models;

/// <summary>
/// The format a RIFF/WAVE file declares in its <c>fmt </c> chunk, plus the
/// payload size from its <c>data</c> chunk. One parser for every place that
/// has to answer "can Whisper read this as-is?": capture ingestion (decide
/// whether to normalize), the WAV-only normalizer (decide whether a copy is
/// enough), audio import (show a duration), and the speech-to-text service
/// (reject what the decoder would silently mangle).
/// </summary>
/// <param name="AudioFormat">The <c>fmt </c> format tag; 1 is integer PCM.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="SampleRate">Samples per second per channel.</param>
/// <param name="BitsPerSample">Bits per sample per channel.</param>
/// <param name="ByteRate">Average bytes per second, as declared.</param>
/// <param name="DataByteLength">Bytes of sample data (declared, bounded by the file).</param>
public sealed record WavHeader(
    int AudioFormat,
    int Channels,
    int SampleRate,
    int BitsPerSample,
    int ByteRate,
    long DataByteLength)
{
    /// <summary>The <c>fmt </c> format tag for integer PCM.</summary>
    public const int PcmFormat = 1;

    /// <summary>The sample rate the transcription engine expects.</summary>
    public const int TranscriptionSampleRate = 16_000;

    /// <summary>The channel count the transcription engine expects.</summary>
    public const int TranscriptionChannels = 1;

    /// <summary>The sample width the transcription engine expects.</summary>
    public const int TranscriptionBitsPerSample = 16;

    /// <summary>
    /// True for 16 kHz, mono, 16-bit integer PCM — the one layout the bundled
    /// Whisper engine decodes, and what the Windows recorder writes.
    /// </summary>
    public bool IsTranscriptionReady =>
        AudioFormat == PcmFormat
        && SampleRate == TranscriptionSampleRate
        && Channels == TranscriptionChannels
        && BitsPerSample == TranscriptionBitsPerSample;

    /// <summary>
    /// Playing time from the declared byte rate; <see cref="TimeSpan.Zero"/>
    /// when the header declares none.
    /// </summary>
    public TimeSpan Duration => ByteRate > 0
        ? TimeSpan.FromSeconds((double)DataByteLength / ByteRate)
        : TimeSpan.Zero;

    /// <summary>
    /// Reads the header of <paramref name="path"/>. Returns null for a file
    /// that is missing, unreadable, or not a RIFF/WAVE container with a
    /// <c>fmt </c> chunk — never throws, because every caller treats "not a
    /// WAV I can read" as an answer, not an error.
    /// </summary>
    public static WavHeader? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryRead(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads the header from an open, seekable stream positioned at 0.</summary>
    public static WavHeader? TryRead(Stream stream)
    {
        try
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

            // "RIFF" <size> "WAVE"
            if (stream.Length < 12 || new string(reader.ReadChars(4)) != "RIFF")
            {
                return null;
            }

            reader.ReadUInt32(); // riff chunk size (unreliable; ignored)
            if (new string(reader.ReadChars(4)) != "WAVE")
            {
                return null;
            }

            int? audioFormat = null;
            int channels = 0, sampleRate = 0, bitsPerSample = 0, byteRate = 0;
            long dataLength = 0;
            var sawData = false;

            // Walk the chunk list for "fmt " and "data". Chunks are word-aligned
            // with a pad byte; a streamed writer can leave data's size as 0 or
            // 0xFFFFFFFF, in which case what is actually on disk counts.
            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = new string(reader.ReadChars(4));
                var chunkSize = reader.ReadUInt32();
                var chunkStart = stream.Position;

                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    audioFormat = reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    sampleRate = (int)reader.ReadUInt32();
                    byteRate = (int)reader.ReadUInt32();
                    reader.ReadUInt16(); // block align
                    bitsPerSample = reader.ReadUInt16();
                }
                else if (chunkId == "data")
                {
                    var remaining = stream.Length - chunkStart;
                    dataLength = chunkSize == 0 || chunkSize > remaining ? Math.Max(0, remaining) : chunkSize;
                    sawData = true;
                    break;
                }

                var next = chunkStart + chunkSize + (chunkSize % 2);
                if (next <= chunkStart || next > stream.Length)
                {
                    break;
                }

                stream.Position = next;
            }

            if (audioFormat == null)
            {
                return null;
            }

            return new WavHeader(audioFormat.Value, channels, sampleRate, bitsPerSample, byteRate,
                sawData ? dataLength : 0);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
