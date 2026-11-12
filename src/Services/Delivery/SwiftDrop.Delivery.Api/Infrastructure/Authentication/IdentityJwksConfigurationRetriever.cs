using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace SwiftDrop.Delivery.Api.Infrastructure.Authentication;

internal sealed class IdentityJwksConfigurationRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array
            || keys.GetArrayLength() is < 1 or > 16) throw new InvalidOperationException("Identity public keys are unavailable.");
        var result = new OpenIdConnectConfiguration { Issuer = issuer };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in keys.EnumerateArray())
        {
            var key = new JsonWebKey(entry.GetRawText());
            if (key.Kty != "RSA" || key.Use != "sig" || key.Alg != "RS256" || string.IsNullOrWhiteSpace(key.Kid)
                || key.Kid.Length > 128 || !ids.Add(key.Kid) || !string.IsNullOrEmpty(key.D)
                || !string.IsNullOrEmpty(key.P) || !string.IsNullOrEmpty(key.Q)
                || string.IsNullOrEmpty(key.N) || Base64UrlEncoder.DecodeBytes(key.N).Length is < 256 or > 1024
                || string.IsNullOrEmpty(key.E) || Base64UrlEncoder.DecodeBytes(key.E).Length is < 1 or > 8)
                throw new InvalidOperationException("Identity public keys are unavailable.");
            result.SigningKeys.Add(key);
        }
        return result;
    }
}

internal sealed class BoundedJwksDocumentRetriever(HttpClient client, Uri approvedAddress) : IDocumentRetriever
{
    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var target) || target != approvedAddress)
            throw new InvalidOperationException("Identity public key address is unavailable.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 65536) throw new InvalidOperationException("Identity public keys are unavailable.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, deadline.Token)) > 0)
        {
            if (buffer.Length + count > 65536) throw new InvalidOperationException("Identity public keys are unavailable.");
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
