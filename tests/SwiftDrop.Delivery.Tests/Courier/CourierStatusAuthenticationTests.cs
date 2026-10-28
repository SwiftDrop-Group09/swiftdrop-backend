using System.Net;
using System.Text;
using System.Text.Json;
using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Tests.Authentication;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Courier;

public sealed class CourierStatusAuthenticationTests
{
    private const string Path = "/api/v1/deliveries/11111111-1111-4111-8111-111111111111/status";
    private const string Valid = "{\"status\":\"PickedUp\",\"expectedVersion\":2,\"notes\":\"private note\"}";

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task UnverifiedIdentityCannotWriteEvenWithForgedCourierHeaders(string environment)
    {
        using var factory = new QuoteApiFactory(null, replaceAuthentication: false, environmentName: environment);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-User-Role", "Courier");
        client.DefaultRequestHeaders.Add("Authorization", "Bearer unverified-status-token");
        using var response = await Send(client, Valid);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Admin")]
    [InlineData("Dispatcher")]
    [InlineData("Merchant")]
    public async Task NonCourierRolesCannotImpersonateCourierOrReachBodyValidation(string role)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Role", "Courier");
        using var response = await Send(client, "malformed");
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bad")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task MissingOrInvalidCourierSubjectFailsBeforeStorage(string? subject)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(subject, "Courier"));
        using var client = factory.CreateClient();
        using var response = await Send(client, Valid);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    public static IEnumerable<object[]> InvalidBodies()
    {
        yield return ["null"];
        yield return ["{}"];
        yield return ["{bad"];
        yield return ["{\"status\":\"Collected\",\"expectedVersion\":2,\"notes\":\"note\"}"];
        yield return ["{\"status\":\"1\",\"expectedVersion\":2,\"notes\":\"note\"}"];
        yield return ["{\"status\":\"PickedUp\",\"expectedVersion\":\"2\",\"notes\":\"note\"}"];
        yield return ["{\"status\":\"PickedUp\",\"expectedVersion\":2.5,\"notes\":\"note\"}"];
        yield return ["{\"status\":\"PickedUp\",\"expectedVersion\":0,\"notes\":\"note\"}"];
        yield return ["{\"status\":\"PickedUp\",\"expectedVersion\":2}"];
        yield return ["{\"status\":\"PickedUp\",\"expectedVersion\":2,\"notes\":\" \"}"];
        yield return [JsonSerializer.Serialize(new CourierStatusInput("PickedUp", 2, new string('x', 501)))];
        yield return ["{\"status\":\"Failed\",\"expectedVersion\":2}"];
        yield return ["{\"status\":\"Failed\",\"expectedVersion\":2,\"failureReason\":\"Unknown\"}"];
        yield return ["{\"status\":\"Failed\",\"expectedVersion\":2,\"failureReason\":\"0\"}"];
        yield return ["{\"status\":\"PickedUp\",\"expectedVersion\":2,\"notes\":\"note\",\"failureReason\":\"Damaged\"}"];
        yield return ["{\"status\":\"PickedUp\",\"status\":\"InTransit\",\"expectedVersion\":2,\"notes\":\"note\"}"];
        yield return ["{\"status\":\"PickedUp\",\"expectedVersion\":2,\"notes\":\"note\",\"courierId\":\"forged\"}"];
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task InvalidJsonOrTransitionMetadataHasNoDatabaseDependency(string json)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), "Courier"));
        using var client = factory.CreateClient();
        using var response = await Send(client, json);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingOrBlankIdempotencyKeyIsRejectedBeforeStorage(string? key)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), "Courier"));
        using var client = factory.CreateClient();
        using var response = await Send(client, Valid, key);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task ValidAuthenticatedRequestFailsSafelyWhenDatabaseIsUnconfigured()
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), "Courier"));
        using var client = factory.CreateClient();
        using var response = await Send(client, Valid);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "delivery_unavailable");
    }

    internal static async Task<HttpResponseMessage> Send(HttpClient client, string json, string? key = "test-status-key", string? path = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path ?? Path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
}
