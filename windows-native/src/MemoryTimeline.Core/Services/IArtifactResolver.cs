using MemoryTimeline.Core.DTOs;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Resolves a capture artifact's opaque storage locator to a local, readable
/// file path that the transcription pipeline can consume. The sync client
/// (RemoteChangeApplier) downloads each remote artifact into the SyncedAudio
/// cache through IArtifactTransferClient before it builds the envelope, so
/// the locator ingestion sees is already a local path and the registered
/// resolver, <see cref="LocalFileArtifactResolver"/>, only has to find the
/// file. A resolver that fetched a locator itself would plug in here without
/// touching ingestion.
/// </summary>
public interface IArtifactResolver
{
    Task<ArtifactResolution> ResolveAsync(RemoteCaptureEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of resolving an artifact locator to a local file.
/// </summary>
public class ArtifactResolution
{
    public bool Success { get; init; }

    /// <summary>Local file path holding the artifact bytes, when successful.</summary>
    public string? LocalPath { get; init; }

    public string? Error { get; init; }

    public static ArtifactResolution Resolved(string localPath) => new() { Success = true, LocalPath = localPath };

    public static ArtifactResolution Failed(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// The production resolver: the storage locator is a local file path. Local
/// recordings and imports hand their file over directly, and a capture from
/// another device is downloaded by the sync layer (ArtifactTransferClient,
/// driven by RemoteChangeApplier) to a local file BEFORE ingestion, so its
/// locator is a local path too. Phase 1 added the download step in front of
/// this resolver rather than replacing it.
/// </summary>
public class LocalFileArtifactResolver : IArtifactResolver
{
    public Task<ArtifactResolution> ResolveAsync(RemoteCaptureEnvelope envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var locator = envelope.StorageLocator;
        if (string.IsNullOrWhiteSpace(locator))
        {
            return Task.FromResult(ArtifactResolution.Failed("Storage locator is empty."));
        }

        return Task.FromResult(File.Exists(locator)
            ? ArtifactResolution.Resolved(locator)
            : ArtifactResolution.Failed($"Artifact file not found: {locator}"));
    }
}
