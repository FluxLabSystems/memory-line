using System.Security.Cryptography;
using MemoryTimeline.SyncApi.Application;

namespace MemoryTimeline.SyncApi.Infrastructure;

/// <summary>
/// <see cref="IArtifactStore"/> over the local filesystem. Layout under
/// {DataDir}/artifacts: parts at {guid}/part_00001..., assembled blob at
/// {guid}.bin. Part writes and assembly go through per-request unique temp
/// files so a crashed request never leaves a partially written final file and
/// a concurrent request's cleanup can never unlink another request's work.
/// </summary>
public sealed class FileSystemArtifactStore : IArtifactStore
{
    private const int BufferSize = 81920;

    private readonly string _artifactsDirectory;
    private readonly ILogger<FileSystemArtifactStore> _logger;

    public FileSystemArtifactStore(SyncServerEnvironment environment, ILogger<FileSystemArtifactStore> logger)
    {
        _artifactsDirectory = environment.ArtifactsDirectory;
        _logger = logger;
    }

    public string GetAssembledPath(Guid artifactId)
        => Path.Combine(_artifactsDirectory, $"{artifactId:D}.bin");

    public bool AssembledExists(Guid artifactId) => File.Exists(GetAssembledPath(artifactId));

    public bool PartExists(Guid artifactId, int partNumber) => File.Exists(GetPartPath(artifactId, partNumber));

    public int? FirstMissingPart(Guid artifactId, int partCount)
    {
        for (var part = 1; part <= partCount; part++)
        {
            if (!PartExists(artifactId, part))
            {
                return part;
            }
        }

        return null;
    }

    public async Task<long> WritePartAsync(
        Guid artifactId,
        int partNumber,
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var partPath = GetPartPath(artifactId, partNumber);
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        // Unique per request (CreateNew asserts it): a concurrent uploader of
        // the same part never shares this file, so the catch below can only
        // ever delete this request's own work.
        var tempPath = $"{partPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            long total = 0;
            await using (var output = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new ArtifactPartTooLargeException(maxBytes);
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, partPath, overwrite: true);
            return total;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public async Task<ArtifactAssemblyResult> AssembleToTempAsync(
        Guid artifactId,
        int partCount,
        CancellationToken cancellationToken)
    {
        // Unique per request (CreateNew asserts it): concurrent completes of the
        // same artifact assemble into separate files, so the catch below can
        // only ever delete this request's own work.
        var tempPath = Path.Combine(_artifactsDirectory, $"{artifactId:D}.bin.{Guid.NewGuid():N}.tmp");

        try
        {
            long total = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                for (var part = 1; part <= partCount; part++)
                {
                    await using var input = new FileStream(
                        GetPartPath(artifactId, part), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        total += read;
                    }
                }

                await output.FlushAsync(cancellationToken);
            }

            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return new ArtifactAssemblyResult(tempPath, total, sha256);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public void CommitAssembly(Guid artifactId, string tempPath)
        => File.Move(tempPath, GetAssembledPath(artifactId), overwrite: true);

    public void DiscardAssembly(string tempPath) => TryDelete(tempPath);

    public void DeleteParts(Guid artifactId)
    {
        var partDirectory = Path.Combine(_artifactsDirectory, artifactId.ToString("D"));
        if (!Directory.Exists(partDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(partDirectory, recursive: true);
        }
        catch (IOException ex)
        {
            // Orphaned parts waste disk but do not affect correctness; surfaced for cleanup.
            _logger.LogWarning(ex, "Failed to delete part directory for artifact {ArtifactId}.", artifactId);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to delete part directory for artifact {ArtifactId}.", artifactId);
        }
    }

    public bool DeleteAssembled(Guid artifactId)
    {
        DeleteParts(artifactId);

        var path = GetAssembledPath(artifactId);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to delete the blob of purged artifact {ArtifactId}.", artifactId);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to delete the blob of purged artifact {ArtifactId}.", artifactId);
        }

        return false;
    }

    public int SweepTemporaryFiles(DateTime olderThanUtc)
    {
        if (!Directory.Exists(_artifactsDirectory))
        {
            return 0;
        }

        var removed = 0;
        // Both layouts: "{guid}.bin.{n}.tmp" at the top level (assemblies) and
        // "{guid}/part_00001.{n}.tmp" inside part directories.
        foreach (var path in Directory.EnumerateFiles(_artifactsDirectory, "*.tmp", SearchOption.AllDirectories))
        {
            try
            {
                // A request still writing its file keeps bumping the write
                // time, so anything older than the grace window is orphaned.
                if (File.GetLastWriteTimeUtc(path) >= olderThanUtc)
                {
                    continue;
                }

                File.Delete(path);
                removed++;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to delete orphaned temporary file {Path}.", path);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Failed to delete orphaned temporary file {Path}.", path);
            }
        }

        return removed;
    }

    private string GetPartPath(Guid artifactId, int partNumber)
        => Path.Combine(_artifactsDirectory, artifactId.ToString("D"), $"part_{partNumber:D5}");

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to delete temporary file {Path}.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to delete temporary file {Path}.", path);
        }
    }
}
