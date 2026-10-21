using System.Text;
using FluentAssertions;
using MemoryTimeline.Core.Services;
using MemoryTimeline.Sync;
using MemoryTimeline.SyncContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// Secrets at rest: <see cref="SettingsService"/> stores the keys in
/// <see cref="SecretProtection.SecretKeys"/> through the registered
/// <see cref="ISecretProtector"/> and hands callers plaintext. Covered with a
/// fake protector over the REAL settings service and EF InMemory (unique
/// database per test instance): the round trip, the legacy plaintext rows an
/// existing installation already holds, the one-time migration of those rows on
/// first read, the passthrough default the portable stack runs with, and the
/// sync tokens that travel through <see cref="SyncSettingsStore"/>.
/// </summary>
public class SecretProtectionTests
{
    private readonly TestDbContextFactory _contextFactory = TestDbContextFactory.CreateInMemory();

    /// <summary>
    /// Reversible stand-in for DPAPI: base64 of the UTF-8 bytes behind the real
    /// prefix, so the stored form is distinguishable from the plaintext and from
    /// a legacy row, and so <see cref="SecretProtection.TryDecode"/> is exercised.
    /// </summary>
    private sealed class FakeSecretProtector : ISecretProtector
    {
        public int ProtectCalls { get; private set; }

        public bool IsProtecting => true;

        public string Protect(string plaintext)
        {
            ProtectCalls++;
            return SecretProtection.Encode(Encoding.UTF8.GetBytes(plaintext));
        }

        public string Unprotect(string stored) =>
            SecretProtection.TryDecode(stored, out var bytes) ? Encoding.UTF8.GetString(bytes) : stored;
    }

    private SettingsService CreateService(ISecretProtector? protector) =>
        new(_contextFactory, NullLogger<SettingsService>.Instance, protector);

    [Fact]
    public async Task SetAndGet_SecretKey_StoresProtectedFormAndReturnsPlaintext()
    {
        // Arrange
        var protector = new FakeSecretProtector();
        var service = CreateService(protector);

        // Act
        await service.SetSettingAsync(SettingKeys.AnthropicApiKey, "sk-ant-secret");
        var readBack = await service.GetSettingAsync<string>(SettingKeys.AnthropicApiKey, string.Empty);

        // Assert - the caller never sees the storage form...
        readBack.Should().Be("sk-ant-secret");

        // ...and the row never holds the plaintext.
        var stored = await ReadRowAsync(SettingKeys.AnthropicApiKey);
        stored.Should().StartWith(SecretProtection.Prefix);
        stored.Should().NotContain("sk-ant-secret");
        SecretProtection.IsProtected(stored).Should().BeTrue();
    }

    [Fact]
    public async Task Get_LegacyPlaintextRow_ReadsThroughUnchanged()
    {
        // Arrange - a row written by a build that predates protection
        await SeedRowAsync(SettingKeys.EmbeddingApiKey, "sk-legacy-plaintext");
        var service = CreateService(new FakeSecretProtector());

        // Act
        var value = await service.GetSettingAsync<string>(SettingKeys.EmbeddingApiKey, string.Empty);

        // Assert
        value.Should().Be("sk-legacy-plaintext", "existing user rows must keep working");
    }

    [Fact]
    public async Task Get_LegacyPlaintextRow_WhileProtecting_MigratesTheRowOnFirstRead()
    {
        // Arrange
        await SeedRowAsync(SettingKeys.AnthropicApiKey, "sk-legacy");
        var protector = new FakeSecretProtector();
        var service = CreateService(protector);

        // Act - first read migrates, second read must not re-protect
        var first = await service.GetSettingAsync<string>(SettingKeys.AnthropicApiKey, string.Empty);
        var second = await service.GetSettingAsync<string>(SettingKeys.AnthropicApiKey, string.Empty);

        // Assert - both reads answer plaintext; the row is now protected; the
        // protector ran exactly once
        first.Should().Be("sk-legacy");
        second.Should().Be("sk-legacy");
        var stored = await ReadRowAsync(SettingKeys.AnthropicApiKey);
        SecretProtection.IsProtected(stored).Should().BeTrue();
        protector.ProtectCalls.Should().Be(1);
    }

    [Fact]
    public async Task Get_LegacyPlaintextRow_WithPassthroughProtector_IsNotRewritten()
    {
        // Arrange - the portable stack: nothing can protect, so nothing is touched
        await SeedRowAsync(SettingKeys.AnthropicApiKey, "sk-legacy");
        var service = CreateService(null);

        // Act
        var value = await service.GetSettingAsync<string>(SettingKeys.AnthropicApiKey, string.Empty);

        // Assert
        value.Should().Be("sk-legacy");
        (await ReadRowAsync(SettingKeys.AnthropicApiKey)).Should().Be("sk-legacy");
    }

    [Fact]
    public async Task Set_WithPassthroughProtector_StoresPlaintext()
    {
        // Arrange
        var service = CreateService(new PassthroughSecretProtector());

        // Act
        await service.SetSettingAsync(SettingKeys.AnthropicApiKey, "sk-plain");

        // Assert - identity in both directions, no prefix invented
        (await ReadRowAsync(SettingKeys.AnthropicApiKey)).Should().Be("sk-plain");
        (await service.GetSettingAsync<string>(SettingKeys.AnthropicApiKey, string.Empty)).Should().Be("sk-plain");
    }

    [Fact]
    public async Task Set_NonSecretKey_IsNeverProtected()
    {
        // Arrange
        var protector = new FakeSecretProtector();
        var service = CreateService(protector);

        // Act
        await service.SetSettingAsync(SettingKeys.LlmModel, "claude-sonnet-4-20250514");
        await service.SetSettingAsync(SettingKeys.Theme, "dark");

        // Assert
        (await ReadRowAsync(SettingKeys.LlmModel)).Should().Be("claude-sonnet-4-20250514");
        (await ReadRowAsync(SettingKeys.Theme)).Should().Be("dark");
        protector.ProtectCalls.Should().Be(0);
    }

    [Fact]
    public async Task Set_EmptySecret_StaysEmptyAndClearsTheKey()
    {
        // Arrange - clearing a key in Settings writes an empty string
        var protector = new FakeSecretProtector();
        var service = CreateService(protector);
        await service.SetSettingAsync(SettingKeys.AnthropicApiKey, "sk-ant-secret");

        // Act
        await service.SetSettingAsync(SettingKeys.AnthropicApiKey, string.Empty);

        // Assert - an empty ciphertext would read back as a non-empty key
        (await ReadRowAsync(SettingKeys.AnthropicApiKey)).Should().BeEmpty();
        (await service.GetSettingAsync<string>(SettingKeys.AnthropicApiKey, string.Empty)).Should().BeNullOrEmpty(
            "an empty stored secret reads as no secret, exactly as before protection existed");
    }

    [Fact]
    public async Task Set_AlreadyProtectedValue_IsNotDoubleWrapped()
    {
        // Arrange - a caller that round-trips a stored value (e.g. a settings
        // export/import) hands back the protected form
        var protector = new FakeSecretProtector();
        var service = CreateService(protector);
        var protectedForm = protector.Protect("sk-ant-secret");

        // Act
        await service.SetSettingAsync(SettingKeys.AnthropicApiKey, protectedForm);

        // Assert
        (await ReadRowAsync(SettingKeys.AnthropicApiKey)).Should().Be(protectedForm);
        (await service.GetSettingAsync<string>(SettingKeys.AnthropicApiKey, string.Empty)).Should().Be("sk-ant-secret");
    }

    [Fact]
    public async Task GetAllSettings_RevealsSecretsLikeGetSetting()
    {
        // Arrange
        var service = CreateService(new FakeSecretProtector());
        await service.SetSettingAsync(SettingKeys.EmbeddingApiKey, "sk-embed");
        await service.SetSettingAsync(SettingKeys.Theme, "light");

        // Act
        var all = await service.GetAllSettingsAsync();

        // Assert
        all[SettingKeys.EmbeddingApiKey].Should().Be("sk-embed");
        all[SettingKeys.Theme].Should().Be("light");
    }

    [Fact]
    public async Task SyncSettingsStore_Tokens_AreProtectedAtRestAndPlaintextOnRead()
    {
        // Arrange - the sync layer never sees the protector; it goes through
        // the settings service like every other writer
        var service = CreateService(new FakeSecretProtector());
        var store = new SyncSettingsStore(service, NullLogger<SyncSettingsStore>.Instance);
        var registration = new DeviceRegisterResponse
        {
            DeviceId = "device-1",
            OwnerId = "owner-1",
            AccessToken = "access-token-value",
            RefreshToken = "refresh-token-value",
            AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
        };

        // Act
        await store.SaveRegistrationAsync("https://sync.example.test", registration);

        // Assert - rows protected, reads plaintext, the non-secret identity untouched
        SecretProtection.IsProtected(await ReadRowAsync(SettingKeys.SyncAccessToken)).Should().BeTrue();
        SecretProtection.IsProtected(await ReadRowAsync(SettingKeys.SyncRefreshToken)).Should().BeTrue();
        (await ReadRowAsync(SettingKeys.SyncAccessToken)).Should().NotContain("access-token-value");
        (await store.GetAccessTokenAsync()).Should().Be("access-token-value");
        (await store.GetRefreshTokenAsync()).Should().Be("refresh-token-value");
        (await ReadRowAsync(SettingKeys.SyncDeviceId)).Should().Be("device-1");
    }

    [Fact]
    public void SecretProtection_Decode_RejectsLegacyAndMalformedValues()
    {
        // A legacy plaintext row is not protected; a prefixed value whose
        // payload is not base64 decodes to nothing rather than to garbage.
        SecretProtection.IsProtected("sk-plain").Should().BeFalse();
        SecretProtection.TryDecode("sk-plain", out _).Should().BeFalse();
        SecretProtection.TryDecode(null, out _).Should().BeFalse();
        SecretProtection.TryDecode(SecretProtection.Prefix + "@@not-base64@@", out _).Should().BeFalse();

        var bytes = Encoding.UTF8.GetBytes("payload");
        SecretProtection.TryDecode(SecretProtection.Encode(bytes), out var decoded).Should().BeTrue();
        decoded.Should().Equal(bytes);
    }

    [Fact]
    public void SecretProtection_SecretKeys_NameExactlyTheKeysThatHoldSecrets()
    {
        SecretProtection.SecretKeys.Should().BeEquivalentTo(new[]
        {
            SettingKeys.AnthropicApiKey,
            SettingKeys.EmbeddingApiKey,
            SettingKeys.SyncAccessToken,
            SettingKeys.SyncRefreshToken,
        });
    }

    #region Helpers

    private async Task SeedRowAsync(string key, string value)
    {
        await using var context = _contextFactory.CreateDbContext();
        context.AppSettings.Add(new Data.Models.AppSetting
        {
            SettingKey = key,
            SettingValue = value,
            UpdatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private async Task<string> ReadRowAsync(string key)
    {
        await using var context = _contextFactory.CreateDbContext();
        var row = await context.AppSettings.AsNoTracking().SingleAsync(s => s.SettingKey == key);
        return row.SettingValue;
    }

    #endregion
}
