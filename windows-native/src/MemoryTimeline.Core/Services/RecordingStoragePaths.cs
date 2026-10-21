namespace MemoryTimeline.Core.Services;

/// <summary>
/// Where recording files live, in one place. Three trees under the app data
/// folder, all owned by the pipeline and all swept by the recordings cleanup:
/// <list type="bullet">
/// <item><description><see cref="RecordingsRoot"/> — WAVs the Windows recorder
/// writes and the copies audio import makes;</description></item>
/// <item><description><see cref="SyncedAudioRoot"/> — artifacts downloaded from
/// the sync service, named <c>{captureId}_{artifactId}{ext}</c>;</description></item>
/// <item><description><see cref="NormalizedAudioRoot"/> — the 16 kHz WAVs
/// <see cref="IAudioNormalizer"/> produces, named
/// <c>{captureId}_{artifactId}.wav</c> after the artifact they were made
/// from.</description></item>
/// </list>
/// Production uses <see cref="Default"/>; tests construct one over a temp
/// directory. Services take it as an optional constructor parameter so the DI
/// container, which cannot build it from a string, falls back to the default.
/// </summary>
public sealed class RecordingStoragePaths
{
    /// <summary>Folder name of the recorder/import tree under the app root.</summary>
    public const string RecordingsDirectoryName = "AudioRecordings";

    /// <summary>Folder name of the synced-artifact cache under the app root.</summary>
    public const string SyncedAudioDirectoryName = "SyncedAudio";

    /// <summary>Folder name of the normalized-WAV tree under the app root.</summary>
    public const string NormalizedAudioDirectoryName = "NormalizedAudio";

    /// <summary>Creates the three trees under <paramref name="appRoot"/>.</summary>
    public RecordingStoragePaths(string appRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appRoot);
        AppRoot = appRoot;
        RecordingsRoot = Path.Combine(appRoot, RecordingsDirectoryName);
        SyncedAudioRoot = Path.Combine(appRoot, SyncedAudioDirectoryName);
        NormalizedAudioRoot = Path.Combine(appRoot, NormalizedAudioDirectoryName);
    }

    /// <summary>
    /// The production layout: <c>%LOCALAPPDATA%\MemoryTimeline\...</c>, the same
    /// root <c>AppDbContext</c>, the recorder, audio import, the backup service
    /// and the sync applier already use.
    /// </summary>
    public static RecordingStoragePaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MemoryTimeline"));

    /// <summary>The app data root the trees hang off.</summary>
    public string AppRoot { get; }

    /// <summary>Recorder output and audio-import copies.</summary>
    public string RecordingsRoot { get; }

    /// <summary>Artifacts downloaded from the sync service.</summary>
    public string SyncedAudioRoot { get; }

    /// <summary>Normalized 16 kHz WAVs produced for transcription.</summary>
    public string NormalizedAudioRoot { get; }

    /// <summary>All three trees, for sweeps.</summary>
    public IReadOnlyList<string> AllRoots => new[] { RecordingsRoot, SyncedAudioRoot, NormalizedAudioRoot };

    /// <summary>
    /// The normalized WAV for an artifact. Keyed by capture and artifact
    /// identity like the synced cache, so a replay of the same artifact lands
    /// on the same file and a conflicting artifact for the same capture does
    /// not overwrite it.
    /// </summary>
    public string BuildNormalizedPath(string captureId, string artifactId) =>
        Path.Combine(NormalizedAudioRoot, $"{captureId}_{artifactId}.wav");

    /// <summary>
    /// The cache file for a synced artifact; <paramref name="extension"/>
    /// includes its leading dot.
    /// </summary>
    public string BuildSyncedAudioPath(string captureId, string artifactId, string extension) =>
        Path.Combine(SyncedAudioRoot, $"{captureId}_{artifactId}{extension}");

    /// <summary>True when <paramref name="path"/> lies inside one of the three trees.</summary>
    public bool Contains(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in AllRoots)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (full.StartsWith(rootFull, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
