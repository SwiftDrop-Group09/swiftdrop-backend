using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SwiftDrop.Delivery.Tests.Quotes;

public sealed class QuoteRequestEncodingTests
{
    [Fact]
    public async Task UnsupportedCharsetReturnsSafeValidationProblemBeforeDatabaseAccess()
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var client = factory.CreateClient();
        using var content = new StringContent("""
            {
              "pickupAddress": {"street":"Fictional pickup","city":"Fictional city","postalCode":"00000","zoneId":"f31f3748-d84e-42d6-82a6-b1011ac9f774"},
              "deliveryAddress": {"street":"Fictional destination","city":"Fictional city","postalCode":"00000","zoneId":"38d54465-4fd0-40f5-81a7-d3c338ed19b9"},
              "packageDetails": {"weightKg":2.5,"size":"Small"},
              "recipient": {"name":"Fictional Recipient","phoneNumber":"0000000000"}
            }
            """, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            "application/json; charset=unknown-swiftdrop-charset");

        using var response = await client.PostAsync("/api/v1/quotes", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("validation", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("https://example.invalid/problems/validation", problem.RootElement.GetProperty("type").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("body", out _));
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("correlationId").GetString()));
        Assert.DoesNotContain("unknown-swiftdrop-charset", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fictional", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.OrdinalIgnoreCase);
    }
}
