using System.Net;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Network;

namespace SwiftDrop.Merchant.Tests.Webhooks.Network;

public sealed class SsrfDestinationValidatorTests
{
    private sealed class MockDnsResolver(params IPAddress[] addresses) : IDnsResolver
    {
        public Task<IPAddress[]> GetHostAddressesAsync(string hostName, CancellationToken ct) =>
            Task.FromResult(addresses);
    }

    [Theory]
    [InlineData("http://example.com/webhook")] // Not HTTPS
    [InlineData("ftp://example.com/webhook")] // Not HTTPS
    [InlineData("https://example.com:8443/webhook")] // Not port 443
    [InlineData("https://user:pass@example.com/webhook")] // Has userinfo
    [InlineData("https://example.com/webhook#anchor")] // Has fragment
    [InlineData("https://localhost/webhook")] // Localhost
    [InlineData("https://127.0.0.1/webhook")] // Loopback IP literal
    [InlineData("https://10.0.0.1/webhook")] // RFC 1918 Private IP literal
    [InlineData("https://172.16.0.1/webhook")] // RFC 1918 Private IP literal
    [InlineData("https://192.168.1.1/webhook")] // RFC 1918 Private IP literal
    [InlineData("https://169.254.169.254/webhook")] // Cloud metadata literal
    [InlineData("not-a-url")]
    public async Task ValidateAsync_WithBlockedUrlOrIpLiteral_ReturnsFalse(string url)
    {
        var validator = new SsrfDestinationValidator();
        var isValid = await validator.ValidateAsync(url, CancellationToken.None);
        Assert.False(isValid);
    }

    [Fact]
    public async Task ValidateAsync_WhenDnsResolvesToLoopback_ReturnsFalse()
    {
        var dns = new MockDnsResolver(IPAddress.Parse("127.0.0.1"));
        var validator = new SsrfDestinationValidator(dns);

        var isValid = await validator.ValidateAsync("https://merchant.example.com/webhook", CancellationToken.None);
        Assert.False(isValid);
    }

    [Fact]
    public async Task ValidateAsync_WhenDnsResolvesToPrivateRfc1918_ReturnsFalse()
    {
        var dns = new MockDnsResolver(IPAddress.Parse("10.50.1.100"));
        var validator = new SsrfDestinationValidator(dns);

        var isValid = await validator.ValidateAsync("https://merchant.example.com/webhook", CancellationToken.None);
        Assert.False(isValid);
    }

    [Fact]
    public async Task ValidateAsync_WhenDnsResolvesToCloudMetadata_ReturnsFalse()
    {
        var dns = new MockDnsResolver(IPAddress.Parse("169.254.169.254"));
        var validator = new SsrfDestinationValidator(dns);

        var isValid = await validator.ValidateAsync("https://merchant.example.com/webhook", CancellationToken.None);
        Assert.False(isValid);
    }

    [Fact]
    public async Task ValidateAsync_WhenDnsResolvesToIpv4MappedLoopback_ReturnsFalse()
    {
        var mappedLoopback = IPAddress.Parse("::ffff:127.0.0.1");
        var dns = new MockDnsResolver(mappedLoopback);
        var validator = new SsrfDestinationValidator(dns);

        var isValid = await validator.ValidateAsync("https://merchant.example.com/webhook", CancellationToken.None);
        Assert.False(isValid);
    }

    [Fact]
    public async Task ValidateAsync_WhenDnsResolvesToValidPublicIp_ReturnsTrue()
    {
        var publicIp = IPAddress.Parse("93.184.216.34"); // example.com public IP
        var dns = new MockDnsResolver(publicIp);
        var validator = new SsrfDestinationValidator(dns);

        var isValid = await validator.ValidateAsync("https://merchant.example.com/webhook", CancellationToken.None);
        Assert.True(isValid);
    }

    [Fact]
    public async Task ValidateAsync_WhenOneOfMultipleIpsIsPrivate_ReturnsFalse()
    {
        // Rebinding / mixed A record protection: reject if ANY resolved address is private
        var dns = new MockDnsResolver(IPAddress.Parse("93.184.216.34"), IPAddress.Parse("192.168.1.5"));
        var validator = new SsrfDestinationValidator(dns);

        var isValid = await validator.ValidateAsync("https://merchant.example.com/webhook", CancellationToken.None);
        Assert.False(isValid);
    }
}
