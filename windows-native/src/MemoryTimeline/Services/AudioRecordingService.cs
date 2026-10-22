using Microsoft.Extensions.Logging;
using MemoryTimeline.Core.DTOs;
using MemoryTimeline.Core.Services;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Devices.Enumeration;
using WinRT;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MemoryTimeline.Services;

/// <summary>
/// COM access to the bytes behind an <c>IMemoryBufferReference</c> (the
/// documented way to read <c>AudioFrame</c> samples from C#). Resolved with
/// CsWinRT's <c>As&lt;T&gt;()</c> on the projected reference object.
/// </summary>
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMemoryBufferByteAccess
{
    void GetBuffer(out IntPtr buffer, out uint capacity);
}

/// <summary>
/// Windows-specific audio recording service implementation using MediaCapture API.
/// <para>
/// While recording, a <see cref="MediaFrameReader"/> on the capture's audio
/// frame source samples the microphone at ~20 Hz and raises
/// <see cref="AudioLevelChanged"/> with a dB-scaled peak (see
/// <see cref="AudioLevelMeter"/>) for the Queue page's level meter. The meter
/// is strictly best-effort: if the frame reader cannot be created or started
/// the recording proceeds without it, with one warning in the log.
/// </para>
/// </summary>
public class AudioRecordingService : IAudioRecordingService, IDisposable
{
    private readonly ILogger<AudioRecordingService> _logger;
    private MediaCapture? _mediaCapture;
    private StorageFile? _recordingFile;
    private AudioRecordingState _recordingState = AudioRecordingState.Idle;
    private readonly Stopwatch _recordingTimer = new();
    private AudioRecordingSettings? _currentSettings;
    private bool _disposed;

    // Level meter: frame reader over the capture's audio source, throttled to
    // ~20 Hz. Scratch buffers are reused across frames under _levelLock.
    private static readonly long LevelIntervalTicks = Stopwatch.Frequency / 20;
    private readonly object _levelLock = new();
    private MediaFrameReader? _levelFrameReader;
    private long _lastLevelTimestamp;
    private float[] _floatScratch = Array.Empty<float>();
    private short[] _shortScratch = Array.Empty<short>();

    public event EventHandler<AudioRecordingStateChangedEventArgs>? RecordingStateChanged;
    public event EventHandler<AudioLevelChangedEventArgs>? AudioLevelChanged;

    public AudioRecordingService(ILogger<AudioRecordingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets available audio input devices.
    /// </summary>
    public async Task<IEnumerable<AudioDeviceDto>> GetAvailableDevicesAsync()
    {
        try
        {
            var devices = new List<AudioDeviceDto>();

            // Find all audio capture devices
            var deviceInfo = await DeviceInformation.FindAllAsync(
                DeviceClass.AudioCapture);

            foreach (var info in deviceInfo)
            {
                devices.Add(new AudioDeviceDto
                {
                    DeviceId = info.Id,
                    Name = info.Name,
                    IsDefault = info.IsDefault,
                    IsEnabled = info.IsEnabled
                });
            }

            _logger.LogInformation("Found {Count} audio input devices", devices.Count);
            return devices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting audio devices");
            return Enumerable.Empty<AudioDeviceDto>();
        }
    }

    /// <summary>
    /// Starts audio recording with specified settings.
    /// </summary>
    public async Task<string> StartRecordingAsync(AudioRecordingSettings settings)
    {
        if (_recordingState != AudioRecordingState.Idle)
        {
            throw new InvalidOperationException("Recording already in progress");
        }

        try
        {
            _logger.LogInformation("Starting audio recording with settings: {SampleRate}Hz, {BitsPerSample}bit",
                settings.SampleRate, settings.BitsPerSample);

            _currentSettings = settings;

            // Initialize MediaCapture
            _mediaCapture = new MediaCapture();
            var captureSettings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,
                // CPU frames so the level-meter frame reader can read samples
                // directly (the documented MediaFrameReader audio setup).
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            };

            // Use specific device if provided
            if (!string.IsNullOrEmpty(settings.DeviceId))
            {
                captureSettings.AudioDeviceId = settings.DeviceId;
            }

            await _mediaCapture.InitializeAsync(captureSettings);

            // Create output file
            var fileName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
            var audioFolder = await GetAudioFolderAsync();
            _recordingFile = await audioFolder.CreateFileAsync(fileName,
                CreationCollisionOption.GenerateUniqueName);

            // Configure audio encoding
            var encodingProfile = CreateEncodingProfile(settings);

            // Start recording
            await _mediaCapture.StartRecordToStorageFileAsync(encodingProfile, _recordingFile);

            _recordingTimer.Restart();
            SetRecordingState(AudioRecordingState.Recording);

            // Best-effort level meter; never fails the recording.
            await StartLevelMeteringAsync();

            _logger.LogInformation("Recording started successfully to file: {FileName}", fileName);
            return _recordingFile.Path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting audio recording");
            await CleanupMediaCaptureAsync();
            throw;
        }
    }

    /// <summary>
    /// Stops the current recording and saves the file.
    /// </summary>
    public async Task<AudioRecordingDto> StopRecordingAsync()
    {
        if (_recordingState != AudioRecordingState.Recording && _recordingState != AudioRecordingState.Paused)
        {
            throw new InvalidOperationException("No recording in progress");
        }

        try
        {
            SetRecordingState(AudioRecordingState.Stopping);

            // Stop the level reader before the record sink so the pipeline
            // tears down in the order it was built.
            await StopLevelMeteringAsync();

            // Stop recording
            if (_mediaCapture != null)
            {
                await _mediaCapture.StopRecordAsync();
            }

            _recordingTimer.Stop();

            // Get file info
            var properties = await _recordingFile!.GetBasicPropertiesAsync();
            var duration = _recordingTimer.Elapsed.TotalSeconds;

            _logger.LogInformation("Recording stopped. Duration: {Duration}s, Size: {Size} bytes",
                duration, properties.Size);

            var dto = new AudioRecordingDto
            {
                QueueId = Guid.NewGuid().ToString(),
                AudioFilePath = _recordingFile.Path,
                Status = Data.Models.QueueStatus.Pending,
                DurationSeconds = duration,
                FileSizeBytes = (long)properties.Size,
                CreatedAt = DateTime.UtcNow
            };

            await CleanupMediaCaptureAsync();
            SetRecordingState(AudioRecordingState.Idle);

            return dto;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping audio recording");
            await CleanupMediaCaptureAsync();
            SetRecordingState(AudioRecordingState.Idle);
            throw;
        }
    }

    /// <summary>
    /// Pauses the current recording.
    /// </summary>
    public async Task PauseRecordingAsync()
    {
        if (_recordingState != AudioRecordingState.Recording)
        {
            throw new InvalidOperationException("No active recording to pause");
        }

        try
        {
            if (_mediaCapture != null)
            {
                await _mediaCapture.PauseRecordAsync(Windows.Media.Devices.MediaCapturePauseBehavior.RetainHardwareResources);
            }

            _recordingTimer.Stop();
            SetRecordingState(AudioRecordingState.Paused);

            _logger.LogInformation("Recording paused at {Duration}s", _recordingTimer.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing audio recording");
            throw;
        }
    }

    /// <summary>
    /// Resumes a paused recording.
    /// </summary>
    public async Task ResumeRecordingAsync()
    {
        if (_recordingState != AudioRecordingState.Paused)
        {
            throw new InvalidOperationException("No paused recording to resume");
        }

        try
        {
            if (_mediaCapture != null)
            {
                await _mediaCapture.ResumeRecordAsync();
            }

            _recordingTimer.Start();
            SetRecordingState(AudioRecordingState.Recording);

            _logger.LogInformation("Recording resumed at {Duration}s", _recordingTimer.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming audio recording");
            throw;
        }
    }

    /// <summary>
    /// Cancels the current recording without saving.
    /// </summary>
    public async Task CancelRecordingAsync()
    {
        if (_recordingState == AudioRecordingState.Idle)
        {
            return;
        }

        try
        {
            _logger.LogInformation("Cancelling recording");

            await StopLevelMeteringAsync();

            // Stop recording if active
            if (_recordingState == AudioRecordingState.Recording || _recordingState == AudioRecordingState.Paused)
            {
                if (_mediaCapture != null)
                {
                    await _mediaCapture.StopRecordAsync();
                }
            }

            // Delete the recording file
            if (_recordingFile != null)
            {
                try
                {
                    await _recordingFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    _logger.LogInformation("Recording file deleted");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error deleting recording file");
                }
            }

            _recordingTimer.Reset();
            await CleanupMediaCaptureAsync();
            SetRecordingState(AudioRecordingState.Idle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling audio recording");
            await CleanupMediaCaptureAsync();
            SetRecordingState(AudioRecordingState.Idle);
        }
    }

    /// <summary>
    /// Gets the current recording state.
    /// </summary>
    public AudioRecordingState GetRecordingState()
    {
        return _recordingState;
    }

    /// <summary>
    /// Gets the current recording duration in seconds.
    /// </summary>
    public double GetRecordingDuration()
    {
        return _recordingTimer.Elapsed.TotalSeconds;
    }

    #region Private Methods

    private void SetRecordingState(AudioRecordingState newState)
    {
        var oldState = _recordingState;
        _recordingState = newState;

        RecordingStateChanged?.Invoke(this, new AudioRecordingStateChangedEventArgs
        {
            OldState = oldState,
            NewState = newState
        });
    }

    private MediaEncodingProfile CreateEncodingProfile(AudioRecordingSettings settings)
    {
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);

        // Configure audio properties
        profile.Audio.BitsPerSample = (uint)settings.BitsPerSample;
        profile.Audio.SampleRate = (uint)settings.SampleRate;
        profile.Audio.ChannelCount = (uint)settings.Channels;

        return profile;
    }

    private async Task<StorageFolder> GetAudioFolderAsync()
    {
        // ApplicationData.Current requires package identity and throws in this
        // unpackaged app (WindowsPackageType=None). Build the path explicitly,
        // mirroring how AppDbContext resolves its database location.
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MemoryTimeline",
            "AudioRecordings");
        Directory.CreateDirectory(dir);
        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        return folder;
    }

    private async Task CleanupMediaCaptureAsync()
    {
        // Idempotent: already stopped on the normal stop/cancel paths, but the
        // failure paths reach here directly.
        await StopLevelMeteringAsync();

        if (_mediaCapture != null)
        {
            try
            {
                _mediaCapture.Dispose();
                _mediaCapture = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing MediaCapture");
            }
        }

        _recordingFile = null;
        _currentSettings = null;
        _recordingTimer.Reset();

        await Task.CompletedTask;
    }

    #endregion

    #region Level Meter

    /// <summary>
    /// Creates and starts a frame reader on the capture's audio frame source.
    /// Any failure (no audio frame source, API unavailable, start refused)
    /// is logged once per recording and leaves the recording untouched.
    /// </summary>
    private async Task StartLevelMeteringAsync()
    {
        if (_mediaCapture == null || _levelFrameReader != null)
        {
            return;
        }

        try
        {
            MediaFrameSource? audioSource = null;
            foreach (var source in _mediaCapture.FrameSources.Values)
            {
                if (source.Info.SourceKind == MediaFrameSourceKind.Audio)
                {
                    audioSource = source;
                    break;
                }
            }

            if (audioSource == null)
            {
                _logger.LogInformation("No audio frame source on this capture; level meter disabled for this recording");
                return;
            }

            var reader = await _mediaCapture.CreateFrameReaderAsync(audioSource);
            // Realtime: TryAcquireLatestFrame always yields the newest frame and
            // dropped frames are fine - the meter only needs a recent peak.
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnLevelFrameArrived;

            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                reader.FrameArrived -= OnLevelFrameArrived;
                reader.Dispose();
                _logger.LogWarning("Audio level meter could not start ({Status}); recording continues without it", status);
                return;
            }

            _lastLevelTimestamp = 0;
            _levelFrameReader = reader;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audio level meter unavailable; recording continues without it");
        }
    }

    /// <summary>Stops and disposes the level frame reader; safe to call repeatedly.</summary>
    private async Task StopLevelMeteringAsync()
    {
        var reader = _levelFrameReader;
        _levelFrameReader = null;
        if (reader == null)
        {
            return;
        }

        try
        {
            reader.FrameArrived -= OnLevelFrameArrived;
            await reader.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error stopping the audio level reader");
        }
        finally
        {
            try
            {
                reader.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing the audio level reader");
            }
        }
    }

    /// <summary>
    /// Fires on a MediaCapture worker thread for every audio frame. Samples at
    /// most every 50 ms, only while actually recording (paused sessions show an
    /// empty meter), and never throws: a bad frame is simply skipped.
    /// </summary>
    private void OnLevelFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_recordingState != AudioRecordingState.Recording || AudioLevelChanged == null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now - _lastLevelTimestamp < LevelIntervalTicks)
        {
            return;
        }
        _lastLevelTimestamp = now;

        try
        {
            using var frameReference = sender.TryAcquireLatestFrame();
            var audioMediaFrame = frameReference?.AudioMediaFrame;
            if (audioMediaFrame == null)
            {
                return;
            }

            double peak;
            lock (_levelLock)
            {
                peak = ReadPeak(audioMediaFrame);
            }

            if (peak < 0)
            {
                return;
            }

            AudioLevelChanged?.Invoke(this, new AudioLevelChangedEventArgs
            {
                Level = AudioLevelMeter.ToMeterLevel(peak)
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio level sample dropped");
        }
    }

    /// <summary>
    /// Reads the frame's samples (32-bit float or 16-bit PCM; anything else
    /// returns -1) and returns the linear peak 0..1. Caller holds _levelLock.
    /// </summary>
    private double ReadPeak(AudioMediaFrame audioMediaFrame)
    {
        var encoding = audioMediaFrame.AudioEncodingProperties;

        using var audioFrame = audioMediaFrame.GetAudioFrame();
        using var buffer = audioFrame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        reference.As<IMemoryBufferByteAccess>().GetBuffer(out var dataPointer, out var capacityInBytes);
        if (dataPointer == IntPtr.Zero || capacityInBytes == 0)
        {
            return -1;
        }

        // Length is the valid payload; Capacity is the allocation.
        var byteLength = (int)Math.Min(buffer.Length, capacityInBytes);

        if (string.Equals(encoding.Subtype, "Float", StringComparison.OrdinalIgnoreCase) && encoding.BitsPerSample == 32)
        {
            var count = byteLength / sizeof(float);
            if (_floatScratch.Length < count)
            {
                _floatScratch = new float[count];
            }

            Marshal.Copy(dataPointer, _floatScratch, 0, count);
            return AudioLevelMeter.PeakFromFloat32(new ReadOnlySpan<float>(_floatScratch, 0, count));
        }

        if (string.Equals(encoding.Subtype, "PCM", StringComparison.OrdinalIgnoreCase) && encoding.BitsPerSample == 16)
        {
            var count = byteLength / sizeof(short);
            if (_shortScratch.Length < count)
            {
                _shortScratch = new short[count];
            }

            Marshal.Copy(dataPointer, _shortScratch, 0, count);
            return AudioLevelMeter.PeakFromPcm16(new ReadOnlySpan<short>(_shortScratch, 0, count));
        }

        return -1;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Release the microphone / native capture resources. Disposing MediaCapture
        // stops any in-progress capture and frees the underlying hardware. We cannot
        // await StopRecordAsync from a synchronous Dispose, so rely on Dispose to
        // release the device deterministically.
        try
        {
            var reader = _levelFrameReader;
            _levelFrameReader = null;
            if (reader != null)
            {
                reader.FrameArrived -= OnLevelFrameArrived;
                reader.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing the audio level reader during Dispose");
        }

        try
        {
            _mediaCapture?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing MediaCapture during Dispose");
        }
        finally
        {
            _mediaCapture = null;
            _recordingFile = null;
            _currentSettings = null;
            _recordingTimer.Reset();
            _recordingState = AudioRecordingState.Idle;
        }

        GC.SuppressFinalize(this);
    }
}
