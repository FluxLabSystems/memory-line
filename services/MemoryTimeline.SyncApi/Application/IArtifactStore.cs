namespace MemoryTimeline.SyncApi.Application;

/// <summary>
/// Filesystem storage for artifact parts and assembled blobs. All members take
/// a <see cref="Guid"/> (never a raw client string) because the artifact ID
/// names directories and files on disk.
/// </summary>
public interface IArtifactStore
{
    /// <summary>Absolute path of the assembled blob ({artifacts}/{guid}.bin).</summary>
    string GetAssembledPath(Guid artifactId);

    /// <summary>Whether the assembled blob exists on disk.</summary>
    bool AssembledExists(Guid artifactId);

    /// <summary>Whether the given 1-based part exists on disk.</summary>
    bool PartExists(Guid artifactId, int partNumber);

    /// <summary>Lowest part number in 1..<paramref name="partCount"/> missing on disk, or null when all exist.</summary>
    int? FirstMissingPart(Guid artifactId, int partCount);

    /// <summary>
    /// Streams one part to disk, replacing any previous upload of the same part
    /// number (retry-safe). Throws <see cref="ArtifactPartTooLargeException"/>
    /// once more than <paramref name="maxBytes"/> bytes arrive. Returns the
    /// byte count written.
    /// </summary>
    Task<long> WritePartAsync(Guid artifactId, int partNumber, Stream content, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Concatenates parts 1..<paramref name="partCount"/> in order into a
    /// temporary file, computing total length and SHA-256 while streaming.
    /// The caller decides to <see cref="CommitAssembly"/> or
    /// <see cref="DiscardAssembly"/> after validating the result.
    /// </summary>
    Task<ArtifactAssemblyResult> AssembleToTempAsync(Guid artifactId, int partCount, CancellationToken cancellationToken);

    /// <summary>Promotes a validated temporary assembly to the final blob path.</summary>
    void CommitAssembly(Guid artifactId, string tempPath);

    /// <summary>Deletes a rejected temporary assembly (part files are kept for retry).</summary>
    void DiscardAssembly(string tempPath);

    /// <summary>Deletes the part directory after a successful completion. Best-effort; failures are logged.</summary>
    void DeleteParts(Guid artifactId);

    /// <summary>
    /// Deletes the assembled blob and any leftover parts of a purged artifact.
    /// Best-effort; returns true when a blob was actually removed.
    /// </summary>
    bool DeleteAssembled(Guid artifactId);

    /// <summary>
    /// Deletes temporary part/assembly files (<c>*.tmp</c>) last written before
    /// <paramref name="olderThanUtc"/> — the leftovers of requests that crashed
    /// mid-write. Returns the number removed.
    /// </summary>
    int SweepTemporaryFiles(DateTime olderThanUtc);
}

/// <summary>Outcome of assembling artifact parts into a temporary blob.</summary>
public sealed record ArtifactAssemblyResult(string TempPath, long ByteLength, string Sha256Hex);

/// <summary>Thrown when an uploaded part exceeds the configured maximum part size.</summary>
public sealed class ArtifactPartTooLargeException : Exception
{
    public ArtifactPartTooLargeException(long maxBytes)
        : base($"Part exceeds the maximum part size of {maxBytes} bytes.")
    {
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }
}
