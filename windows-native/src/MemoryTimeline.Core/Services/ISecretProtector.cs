namespace MemoryTimeline.Core.Services;

/// <summary>
/// Protects secret setting values at rest (API keys, sync tokens).
///
/// <para>This is a platform port in the <c>IThumbnailGenerator</c> /
/// <c>ICaptureStatusPublisher</c> shape: the interface lives in Core so
/// <see cref="SettingsService"/> can apply it without knowing how, and the
/// implementation lives in the head. Windows registers
/// <c>DpapiSecretProtector</c> (user-scoped DPAPI); Core, Data and Sync default
/// to <see cref="PassthroughSecretProtector"/>, which keeps them buildable and
/// testable off Windows and keeps the storage format a plain string.</para>
///
/// <para><b>Storage format.</b> A protected value is stored as
/// <c>dpapi:v1:</c> followed by the base64 ciphertext
/// (<see cref="SecretProtection.Prefix"/>). A value without the prefix is a
/// legacy plaintext row written before protection existed; implementations
/// must return it unchanged from <see cref="Unprotect"/> so existing user rows
/// keep working, and the settings service re-writes it in protected form the
/// first time it reads it while <see cref="IsProtecting"/> is true.</para>
///
/// <para>Implementations never log the plaintext or the ciphertext.</para>
/// </summary>
public interface ISecretProtector
{
    /// <summary>
    /// True when <see cref="Protect"/> actually encrypts. False for the
    /// passthrough used off Windows, where nothing is re-written on read.
    /// </summary>
    bool IsProtecting { get; }

    /// <summary>Returns the storage form of <paramref name="plaintext"/>.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Returns the plaintext for a stored value. A value that carries no
    /// <see cref="SecretProtection.Prefix"/> is legacy plaintext and comes back
    /// unchanged.
    /// </summary>
    string Unprotect(string stored);
}

/// <summary>
/// The portable default: identity in both directions, never protecting. The
/// archive stack (Core/Data/Sync) and the test projects use this; a head that
/// can encrypt registers its own implementation instead.
/// </summary>
public sealed class PassthroughSecretProtector : ISecretProtector
{
    /// <inheritdoc />
    public bool IsProtecting => false;

    /// <inheritdoc />
    public string Protect(string plaintext) => plaintext;

    /// <inheritdoc />
    public string Unprotect(string stored) => stored;
}

/// <summary>
/// The storage-format rules every <see cref="ISecretProtector"/> and the
/// settings service agree on, in one place so the prefix and the set of keys
/// that are secrets cannot drift between the head and Core.
/// </summary>
public static class SecretProtection
{
    /// <summary>Marks a protected value; everything after it is base64 ciphertext.</summary>
    public const string Prefix = "dpapi:v1:";

    /// <summary>
    /// The settings whose values are secrets and therefore pass through the
    /// protector: the two provider API keys and the two sync tokens. Adding a
    /// key here is all it takes to protect a new secret.
    /// </summary>
    public static readonly IReadOnlySet<string> SecretKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        SettingKeys.AnthropicApiKey,
        SettingKeys.EmbeddingApiKey,
        SettingKeys.SyncAccessToken,
        SettingKeys.SyncRefreshToken,
    };

    /// <summary>Whether <paramref name="key"/> names a secret setting.</summary>
    public static bool IsSecretKey(string key) => SecretKeys.Contains(key);

    /// <summary>Whether a stored value is in the protected form (carries the prefix).</summary>
    public static bool IsProtected(string? stored) =>
        stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Encodes ciphertext into the stored form.</summary>
    public static string Encode(byte[] ciphertext) => Prefix + Convert.ToBase64String(ciphertext);

    /// <summary>
    /// Decodes a stored value into its ciphertext. False when the value is not
    /// in the protected form, including when it is legacy plaintext or the
    /// base64 after the prefix is malformed.
    /// </summary>
    public static bool TryDecode(string? stored, out byte[] ciphertext)
    {
        ciphertext = Array.Empty<byte>();
        if (!IsProtected(stored))
        {
            return false;
        }

        var payload = stored!.AsSpan(Prefix.Length);
        var buffer = new byte[payload.Length]; // base64 never expands when decoded
        if (!Convert.TryFromBase64Chars(payload, buffer, out var written))
        {
            return false;
        }

        ciphertext = buffer[..written];
        return true;
    }
}
