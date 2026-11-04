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
        Assert.NotNull(apiKey.KeyHash);
        Assert.NotNull(apiKey.Salt);
        Assert.Null(apiKey.RevokedAt);
        Assert.True(apiKey.HasScope(ApiKeyScopes.OrdersWrite));
        Assert.True(apiKey.HasScope(ApiKeyScopes.OrdersRead));
        Assert.False(apiKey.HasScope(ApiKeyScopes.WebhooksManage));
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

