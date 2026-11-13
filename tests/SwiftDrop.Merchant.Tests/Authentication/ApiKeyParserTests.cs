using SwiftDrop.Merchant.Api.Infrastructure.Authentication.ApiKeys;

namespace SwiftDrop.Merchant.Tests.Authentication;

public sealed class ApiKeyParserTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private const string Secret = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    [Fact]
    public void CurrentFormat_YieldsKeyIdAndPrefix()
    {
        Assert.True(ApiKeyParser.TryParse($"swd_test_{Id}_{Secret}", out var parsed));

        Assert.Equal($"swd_test_{Id}", parsed.Prefix);
        Assert.Equal(Guid.ParseExact(Id, "N"), parsed.KeyId);
        Assert.False(parsed.IsLegacy);
    }

    [Fact]
    public void LegacyFormat_YieldsPrefixOnly()
    {
        Assert.True(ApiKeyParser.TryParse($"swd_test_01234567_{Secret}", out var parsed));

        Assert.Equal("swd_test_01234567", parsed.Prefix);
        Assert.True(parsed.IsLegacy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("swd_live_0123456789abcdef0123456789abcdef_fedcba9876543210")]
    [InlineData("swd_test_0123456789ABCDEF0123456789ABCDEF_fedcba9876543210")]
    [InlineData("swd_test_0123456789abcdef0123456789abcdef_short")]
    [InlineData("swd_test_0123456789abcdef0123456789abcdef_has-dash-in-secret0")]
    [InlineData(" swd_test_0123456789abcdef0123456789abcdef_fedcba9876543210")]
    public void MalformedKeys_AreRejected(string? presented) =>
        Assert.False(ApiKeyParser.TryParse(presented, out _));

    [Fact]
    public void OversizedKey_IsRejected() =>
        Assert.False(ApiKeyParser.TryParse($"swd_test_{Id}_{new string('a', 300)}", out _));

    [Fact]
    public void KeysIssuedByTheDomain_AlwaysParse()
    {
        var (_, plaintext) = SwiftDrop.Merchant.Api.Domain.ApiKey.Create(Guid.NewGuid(), ["orders:write"]);

        Assert.True(ApiKeyParser.TryParse(plaintext, out var parsed));
        Assert.NotNull(parsed.KeyId);
    }
}
