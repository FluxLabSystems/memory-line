using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MemoryTimeline.Data;
using MemoryTimeline.Data.Models;
using System.Text.Json;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Service interface for application settings.
/// </summary>
public interface ISettingsService
{
    Task<T?> GetSettingAsync<T>(string key);
    Task<T?> GetSettingAsync<T>(string key, T defaultValue);
    Task SetSettingAsync<T>(string key, T value);
    Task<Dictionary<string, string>> GetAllSettingsAsync();
    Task<bool> SettingExistsAsync(string key);
    Task DeleteSettingAsync(string key);

    // Typed getters for common settings
    Task<string> GetThemeAsync();
    Task SetThemeAsync(string theme);
    Task<string> GetDefaultZoomLevelAsync();
    Task<string> GetLlmProviderAsync();
    Task<string> GetLlmModelAsync();
}

/// <summary>
/// Settings service implementation using database storage.
///
/// <para><b>Secrets.</b> The keys in <see cref="SecretProtection.SecretKeys"/>
/// (provider API keys, sync tokens) pass through the <see cref="ISecretProtector"/>
/// on the way in and out: callers read and write plaintext as before, the row
/// holds whatever the protector produced. With the portable
/// <see cref="PassthroughSecretProtector"/> (the default when none is injected)
/// that is still plaintext; with the Windows DPAPI protector it is
/// <c>dpapi:v1:</c>-prefixed ciphertext. A legacy plaintext row is read as-is
/// and re-written in protected form the first time it is read while the
/// protector is protecting, so existing installations migrate on their own.
/// Values are never logged — keys only.</para>
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly ILogger<SettingsService> _logger;
    private readonly ISecretProtector _secretProtector;
    private readonly Dictionary<string, string> _cache;
    private bool _cacheInitialized;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>
    /// Creates the service. <paramref name="secretProtector"/> is optional and
    /// trailing so every existing construction keeps compiling: production
    /// injects the registered implementation, tests and the portable stack get
    /// the passthrough.
    /// </summary>
    public SettingsService(
        IDbContextFactory<AppDbContext> contextFactory,
        ILogger<SettingsService> logger,
        ISecretProtector? secretProtector = null)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _secretProtector = secretProtector ?? new PassthroughSecretProtector();
        _cache = new Dictionary<string, string>();
        _cacheInitialized = false;
    }

    /// <summary>
    /// Gets a setting value with optional default.
    /// </summary>
    public async Task<T?> GetSettingAsync<T>(string key)
    {
        return await GetSettingAsync<T>(key, default(T)!);
    }

    /// <summary>
    /// Gets a setting value with a default fallback.
    /// </summary>
    public async Task<T?> GetSettingAsync<T>(string key, T defaultValue)
    {
        try
        {
            await EnsureCacheInitializedAsync();

            if (_cache.TryGetValue(key, out var value))
            {
                if (SecretProtection.IsSecretKey(key))
                {
                    value = await RevealSecretAsync(key, value);
                }

                return DeserializeValue<T>(value);
            }

            _logger.LogWarning("Setting '{Key}' not found, returning default value", key);
            return defaultValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting setting '{Key}'", key);
            return defaultValue;
        }
    }

    /// <summary>
    /// Sets a setting value.
    /// </summary>
    public async Task SetSettingAsync<T>(string key, T value)
    {
        try
        {
            var serializedValue = SerializeValue(value);
            if (SecretProtection.IsSecretKey(key))
            {
                serializedValue = ProtectForStorage(serializedValue);
            }

            await WriteRowAsync(key, serializedValue);

            _logger.LogInformation("Setting '{Key}' updated successfully", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting '{Key}'", key);
            throw;
        }
    }

    /// <summary>
    /// Gets all settings as a dictionary.
    /// </summary>
    public async Task<Dictionary<string, string>> GetAllSettingsAsync()
    {
        try
        {
            await EnsureCacheInitializedAsync();
            var snapshot = new Dictionary<string, string>(_cache);

            // Callers of this method see the same plaintext GetSettingAsync
            // returns; the stored form is an implementation detail of the row.
            foreach (var key in SecretProtection.SecretKeys)
            {
                if (snapshot.TryGetValue(key, out var stored))
                {
                    snapshot[key] = _secretProtector.Unprotect(stored);
                }
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all settings");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Checks if a setting exists.
    /// </summary>
    public async Task<bool> SettingExistsAsync(string key)
    {
        await EnsureCacheInitializedAsync();
        return _cache.ContainsKey(key);
    }

    /// <summary>
    /// Deletes a setting.
    /// </summary>
    public async Task DeleteSettingAsync(string key)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();

            var setting = await context.AppSettings.FindAsync(key);
            if (setting != null)
            {
                context.AppSettings.Remove(setting);
                await context.SaveChangesAsync();

                await _cacheLock.WaitAsync();
                try
                {
                    _cache.Remove(key);
                }
                finally
                {
                    _cacheLock.Release();
                }

                _logger.LogInformation("Setting '{Key}' deleted successfully", key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting setting '{Key}'", key);
            throw;
        }
    }

    // Typed getters for common settings

    public async Task<string> GetThemeAsync()
    {
        return await GetSettingAsync<string>(SettingKeys.Theme, "dark") ?? "dark";
    }

    public async Task SetThemeAsync(string theme)
    {
        await SetSettingAsync(SettingKeys.Theme, theme);
    }

    public async Task<string> GetDefaultZoomLevelAsync()
    {
        return await GetSettingAsync<string>(SettingKeys.DefaultZoomLevel, "month") ?? "month";
    }

    public async Task<string> GetLlmProviderAsync()
    {
        return await GetSettingAsync<string>(SettingKeys.LlmProvider, "anthropic") ?? "anthropic";
    }

    public async Task<string> GetLlmModelAsync()
    {
        return await GetSettingAsync<string>(SettingKeys.LlmModel, "claude-sonnet-4-20250514") ?? "claude-sonnet-4-20250514";
    }

    // Private helper methods

    /// <summary>
    /// Upserts one row and mirrors it into the cache. Shared by the public
    /// setter and the secret migration so both write a row the same way.
    /// </summary>
    private async Task WriteRowAsync(string key, string storedValue)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var setting = await context.AppSettings.FindAsync(key);

        if (setting != null)
        {
            setting.SettingValue = storedValue;
            setting.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            setting = new AppSetting
            {
                SettingKey = key,
                SettingValue = storedValue,
                UpdatedAt = DateTime.UtcNow
            };
            await context.AppSettings.AddAsync(setting);
        }

        await context.SaveChangesAsync();

        await _cacheLock.WaitAsync();
        try
        {
            _cache[key] = storedValue;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// The stored form of a secret. Empty stays empty (an absent key is not a
    /// secret worth encrypting, and an empty ciphertext would read back as a
    /// non-empty key), and a value already in the protected form is kept as-is
    /// so a caller that round-trips a stored value cannot double-wrap it.
    /// </summary>
    private string ProtectForStorage(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || SecretProtection.IsProtected(plaintext))
        {
            return plaintext;
        }

        return _secretProtector.Protect(plaintext);
    }

    /// <summary>
    /// The plaintext of a stored secret. A legacy plaintext row — written
    /// before protection existed, or by a build that could not protect — is
    /// returned unchanged, and while the protector is protecting it is
    /// re-written in protected form right here, so the one-time migration
    /// needs no startup step and no user action. The re-write is best-effort:
    /// the read already has its answer, and a row that would not update is a
    /// warning for the log, not a missing API key for the user.
    /// </summary>
    private async Task<string> RevealSecretAsync(string key, string stored)
    {
        if (SecretProtection.IsProtected(stored))
        {
            return _secretProtector.Unprotect(stored);
        }

        if (_secretProtector.IsProtecting && !string.IsNullOrEmpty(stored))
        {
            try
            {
                await WriteRowAsync(key, _secretProtector.Protect(stored));
                _logger.LogInformation("Setting '{Key}' migrated to protected storage", key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Setting '{Key}' could not be migrated to protected storage; it stays readable", key);
            }
        }

        return stored;
    }

    private async Task EnsureCacheInitializedAsync()
    {
        if (_cacheInitialized)
            return;

        await _cacheLock.WaitAsync();
        try
        {
            if (_cacheInitialized)
                return;

            await using var context = await _contextFactory.CreateDbContextAsync();
            var settings = await context.AppSettings.ToListAsync();
            foreach (var setting in settings)
            {
                _cache[setting.SettingKey] = setting.SettingValue;
            }

            _cacheInitialized = true;
            _logger.LogInformation("Settings cache initialized with {Count} settings", _cache.Count);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private string SerializeValue<T>(T value)
    {
        if (value == null)
            return string.Empty;

        if (value is string str)
            return str;

        return JsonSerializer.Serialize(value);
    }

    private T? DeserializeValue<T>(string value)
    {
        if (string.IsNullOrEmpty(value))
            return default;

        if (typeof(T) == typeof(string))
            return (T)(object)value;

        if (typeof(T) == typeof(int) && int.TryParse(value, out var intVal))
            return (T)(object)intVal;

        if (typeof(T) == typeof(double) && double.TryParse(value, out var doubleVal))
            return (T)(object)doubleVal;

        if (typeof(T) == typeof(bool) && bool.TryParse(value, out var boolVal))
            return (T)(object)boolVal;

        try
        {
            return JsonSerializer.Deserialize<T>(value);
        }
        catch
        {
            return default;
        }
    }
}
