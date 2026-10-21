using System.Security.Cryptography;
using System.Text;
using MemoryTimeline.Core.Services;
using Microsoft.Extensions.Logging;

namespace MemoryTimeline.Services;

/// <summary>
/// Windows <see cref="ISecretProtector"/> over DPAPI
/// (<see cref="ProtectedData"/>, <see cref="DataProtectionScope.CurrentUser"/>)
/// with a fixed application entropy. Protected values are stored as
/// <c>dpapi:v1:</c> + base64 (<see cref="SecretProtection.Encode"/>).
///
/// <para><b>What the user scope buys and costs.</b> Only the Windows account
/// that wrote a secret can read it back, and no key material is kept by the
/// app — Windows holds it. The cost is that a backup restored under another
/// account or on another machine carries ciphertext that account cannot open:
/// such a value reads back as empty and the user re-enters the key in
/// Settings. That is the intended failure mode; silently falling back to
/// plaintext would defeat the protection, and throwing would turn every
/// settings read into an error.</para>
///
/// <para>Legacy plaintext rows (no prefix) are returned unchanged by
/// <see cref="Unprotect"/>; <see cref="SettingsService"/> re-writes them in
/// protected form on first read. Nothing here logs a plaintext or a
/// ciphertext.</para>
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>
    /// Fixed entropy so a ciphertext produced for this app cannot be unprotected
    /// by another DPAPI client running as the same user without also knowing
    /// this value. Changing it invalidates every stored secret — treat it as
    /// part of the storage format version in the prefix.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MemoryTimeline/app_settings/dpapi:v1");

    private readonly ILogger<DpapiSecretProtector> _logger;

    public DpapiSecretProtector(ILogger<DpapiSecretProtector> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsProtecting => true;

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || SecretProtection.IsProtected(plaintext))
        {
            return plaintext;
        }

        var ciphertext = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return SecretProtection.Encode(ciphertext);
    }

    /// <inheritdoc />
    public string Unprotect(string stored)
    {
        if (!SecretProtection.TryDecode(stored, out var ciphertext))
        {
            // Legacy plaintext, or a prefixed value whose base64 is damaged; in
            // the second case the value was never going to decrypt either.
            return SecretProtection.IsProtected(stored) ? string.Empty : stored;
        }

        try
        {
            var plaintext = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            // Written by another Windows account or machine (typically a
            // restored backup). The secret is unrecoverable by design.
            _logger.LogWarning(ex,
                "A stored secret could not be unprotected under the current Windows account; it must be re-entered in Settings");
            return string.Empty;
        }
    }
}
