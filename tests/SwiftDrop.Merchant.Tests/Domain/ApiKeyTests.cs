using SwiftDrop.Merchant.Api.Domain;

namespace SwiftDrop.Merchant.Tests.Domain;

public sealed class ApiKeyTests
{
    private readonly Guid _businessId = Guid.NewGuid();

    [Fact]
    public void Create_WithValidScopes_GeneratesPlaintextKeyAndHashedEntity()
    {
        var scopes = new[] { ApiKeyScopes.OrdersWrite, ApiKeyScopes.OrdersRead };
        var (apiKey, plaintextKey) = ApiKey.Create(_businessId, scopes);

        Assert.NotEqual(Guid.Empty, apiKey.Id);
        Assert.Equal(_businessId, apiKey.BusinessId);
        Assert.True(apiKey.IsActive);
        Assert.StartsWith("swd_test_", apiKey.KeyPrefix, StringComparison.Ordinal);
        Assert.StartsWith(apiKey.KeyPrefix, plaintextKey, StringComparison.Ordinal);
        Assert.Matches("^swd_test_[0-9a-f]{32}_[0-9a-f]{64}$", plaintextKey);
        var expectedHash = System.Security.Cryptography.SHA256.HashData(
            Convert.FromBase64String(apiKey.Salt).Concat(System.Text.Encoding.UTF8.GetBytes(plaintextKey)).ToArray());
        Assert.Equal(Convert.ToHexStringLower(expectedHash), apiKey.KeyHash);
        Assert.NotNull(apiKey.KeyHash);
        Assert.NotNull(apiKey.Salt);
        Assert.Null(apiKey.RevokedAt);
        Assert.True(apiKey.HasScope(ApiKeyScopes.OrdersWrite));
        Assert.True(apiKey.HasScope(ApiKeyScopes.OrdersRead));
        Assert.False(apiKey.HasScope(ApiKeyScopes.WebhooksManage));
    }

    [Fact]
    public void ExistingShortPrefixKeysKeepTheirOriginalVerificationAndRevocation()
    {
        var (apiKey, _) = ApiKey.Create(_businessId, [ApiKeyScopes.OrdersRead]);
        var prefix = $"swd_test_{apiKey.Id.ToString("N")[..8]}";
        var raw = prefix + "_fictional-legacy-test-secret";
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw + ":" + apiKey.Salt)));
        typeof(ApiKey).GetProperty(nameof(ApiKey.KeyPrefix))!.SetValue(apiKey, prefix);
        typeof(ApiKey).GetProperty(nameof(ApiKey.KeyHash))!.SetValue(apiKey, hash);
        Assert.True(apiKey.Verify(raw));
        Assert.False(apiKey.Verify(raw + "wrong"));
        apiKey.Revoke();
        Assert.False(apiKey.Verify(raw));
    }

    [Fact]
    public void Create_WithInvalidScope_ThrowsArgumentException()
    {
        var invalidScopes = new[] { "admin:superuser", "orders:write" };

        Assert.Throws<ArgumentException>(() =>
            ApiKey.Create(_businessId, invalidScopes));
    }

    [Fact]
    public void Create_WithEmptyScopes_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            ApiKey.Create(_businessId, Array.Empty<string>()));
    }

    [Fact]
    public void Verify_WithCorrectPlaintextKey_ReturnsTrue()
    {
        var scopes = new[] { ApiKeyScopes.OrdersWrite };
        var (apiKey, plaintextKey) = ApiKey.Create(_businessId, scopes);

        var isValid = apiKey.Verify(plaintextKey);

        Assert.True(isValid);
    }

    [Fact]
    public void Verify_WithIncorrectPlaintextKey_ReturnsFalse()
    {
        var scopes = new[] { ApiKeyScopes.OrdersWrite };
        var (apiKey, _) = ApiKey.Create(_businessId, scopes);

        var isValid = apiKey.Verify("swd_test_wrong_secret_12345");

        Assert.False(isValid);
    }

    [Fact]
    public void Revoke_ActiveKey_DeactivatesAndFailsSubsequentVerifications()
    {
        var scopes = new[] { ApiKeyScopes.OrdersWrite };
        var (apiKey, plaintextKey) = ApiKey.Create(_businessId, scopes);

        apiKey.Revoke();

        Assert.False(apiKey.IsActive);
        Assert.NotNull(apiKey.RevokedAt);
        Assert.False(apiKey.Verify(plaintextKey));
    }
}
