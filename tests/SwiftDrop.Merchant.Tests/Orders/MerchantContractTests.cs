using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwiftDrop.Contracts.Http.V1;
using SwiftDrop.Merchant.Api.Application.Orders;
using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Domain.Webhooks;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Network;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Security;
using SwiftDrop.Merchant.Api.Presentation;
using SwiftDrop.Merchant.Tests.Authentication;

namespace SwiftDrop.Merchant.Tests.Orders;

public sealed class MerchantContractTests
{
    [Theory]
    [InlineData(BusinessStatus.Approved, HttpStatusCode.OK)]
    [InlineData(BusinessStatus.Pending, HttpStatusCode.Forbidden)]
    public async Task AssistantOrdersResolveOwnerToTheCorrectBusiness(BusinessStatus status, HttpStatusCode expected)
    {
        var ownerId = Guid.NewGuid();
        using var host = new OrderAuthTestHost(MerchantApiFactory.Principal(ownerId, "Merchant"));
        var ownerBusiness = await host.SeedBusinessAsync(status, ownerId);
        var otherBusiness = await host.SeedBusinessAsync(BusinessStatus.Approved);
        Assert.NotEqual(ownerId, ownerBusiness.Id);
        Assert.NotEqual(ownerBusiness.Id, otherBusiness.Id);
        using var client = host.CreateClient();

        var example = Read("order.example.json");
        var otherOrder = JsonNode.Parse(example)!.AsObject();
        otherOrder["externalOrderId"] = "other-merchant-order";
        var otherKey = await host.SeedKeyAsync(otherBusiness.Id, scopes: [ApiKeyScopes.OrdersWrite]);
        using var other = await Submit(client, otherOrder.ToJsonString(), otherKey);
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
        if (status == BusinessStatus.Approved)
        {
            var ownKey = await host.SeedKeyAsync(ownerBusiness.Id, scopes: [ApiKeyScopes.OrdersWrite]);
            using var own = await Submit(client, example, ownKey);
            Assert.Equal(HttpStatusCode.Accepted, own.StatusCode);
        }

        using var response = await client.GetAsync(MerchantOrderApi.AssistantOrdersPath);
        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("other-merchant-order", body);
        if (status == BusinessStatus.Approved)
        {
            var page = JsonSerializer.Deserialize<MerchantAssistantOrderPage>(body, JsonSerializerOptions.Web);
            Assert.NotNull(page);
            Assert.Equal(1, page.TotalCount);
            var pending = Assert.Single(page.Items);
            Assert.Equal("example-order-001", pending.ExternalOrderId);
            Assert.Equal("Accepted", pending.State);
            Assert.Null(pending.TrackingNumber);
            Assert.Equal(2.5m, pending.WeightKg);
        }
    }

    [Fact]
    public async Task DocumentedOrderExampleAndResponseSchemasMatchTheApi()
    {
        var example = Read("order.example.json");
        using var openApi = JsonDocument.Parse(Read("merchant-orders.openapi.json"));
        var paths = openApi.RootElement.GetProperty("paths");
        var orderPath = paths.GetProperty(OrderEndpoints.OrdersPath);
        var submit = orderPath.GetProperty("post");
        Assert.Equal(OrderEndpoints.IdempotencyKeyHeader, submit.GetProperty("parameters")[0].GetProperty("name").GetString());
        Assert.Equal(ApiKeyScopes.OrdersWrite, submit.GetProperty("x-merchant-api-key-scope").GetString());
        Assert.Equal(ApiKeyScopes.OrdersRead, orderPath.GetProperty("get").GetProperty("x-merchant-api-key-scope").GetString());
        var components = openApi.RootElement.GetProperty("components");
        Assert.Equal("X-API-Key", components.GetProperty("securitySchemes").GetProperty("merchantApiKey").GetProperty("name").GetString());
        var schemas = components.GetProperty("schemas");
        using var order = JsonDocument.Parse(example);
        AssertShape(schemas, "MerchantOrderInput", order.RootElement);
        AssertShape(schemas, "OrderAddressInput", order.RootElement.GetProperty("pickupAddress"));
        AssertShape(schemas, "OrderAddressInput", order.RootElement.GetProperty("deliveryAddress"));
        AssertShape(schemas, "OrderPackageInput", order.RootElement.GetProperty("packageDetails"));
        AssertShape(schemas, "OrderRecipientInput", order.RootElement.GetProperty("recipient"));

        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: [ApiKeyScopes.OrdersWrite, ApiKeyScopes.OrdersRead]);
        using var client = host.CreateClient();
        using var anonymous = await Submit(client, example);
        AssertStatus(submit, anonymous, HttpStatusCode.Unauthorized);

        using var invalid = await Submit(client, """{"externalOrderId":"incomplete-example"}""", key);
        AssertStatus(submit, invalid, HttpStatusCode.BadRequest);

        using var accepted = await Submit(client, example, key);
        AssertStatus(submit, accepted, HttpStatusCode.Accepted);
        using var job = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        AssertShape(schemas, "OrderJobResponse", job.RootElement);
        var location = accepted.Headers.Location?.OriginalString;
        Assert.Equal($"{OrderEndpoints.OrdersPath}/{job.RootElement.GetProperty("id").GetGuid():D}", location);

        using var lookup = new HttpRequestMessage(HttpMethod.Get, location);
        lookup.Headers.Add("X-API-Key", key);
        using var retrieved = await client.SendAsync(lookup);
        AssertStatus(paths.GetProperty(OrderEndpoints.OrdersPath + "/{id}").GetProperty("get"), retrieved, HttpStatusCode.OK);
        using var retrievedJob = JsonDocument.Parse(await retrieved.Content.ReadAsStringAsync());
        Assert.Equal(job.RootElement.GetProperty("id").GetGuid(), retrievedJob.RootElement.GetProperty("id").GetGuid());

        using var duplicate = await Submit(client, example, key);
        AssertStatus(submit, duplicate, HttpStatusCode.Accepted);
        Assert.Equal(accepted.Headers.Location, duplicate.Headers.Location);
        var changed = JsonNode.Parse(example)!.AsObject();
        changed["packageDetails"]!["weightKg"] = 3.5m;
        using var conflicting = await Submit(client, changed.ToJsonString(), key);
        AssertStatus(submit, conflicting, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DocumentedWebhookMatchesTheActualSignedRequest()
    {
        using var example = JsonDocument.Parse(Read("webhook.example.json"));
        var body = example.RootElement;
        var payload = body.Deserialize<ExternalWebhookPayload>(WebhookJson.Options)!;
        var actual = WebhookJson.SerializeExternalEvent(payload.EventId, payload.EventType, payload.SchemaVersion,
            payload.OccurredAt, payload.ParcelId, payload.ParcelVersion, payload.TrackingNumber, payload.Status);
        using var actualJson = JsonDocument.Parse(actual);
        Assert.True(JsonElement.DeepEquals(body, actualJson.RootElement));

        using var openApi = JsonDocument.Parse(Read("merchant-orders.openapi.json"));
        var schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
        AssertShape(schemas, "ExternalWebhookPayload", actualJson.RootElement);
        Assert.Equal(WebhookEvents.All.Order(), schemas.GetProperty("ExternalWebhookPayload").GetProperty("properties")
            .GetProperty("eventType").GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).Order());

        var secret = RandomNumberGenerator.GetBytes(32);
        using var receiver = new HttpClient(new WebhookReceiver(actual, payload.EventId, secret));
        using var sender = new ResilientWebhookSender(receiver);
        var result = await sender.SendAsync("https://test-receiver.invalid/webhook", actual, secret, payload.EventId.ToString("D"),
            secretVersion: 1, default);
        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(204, result.StatusCode);
    }

    private static string Read(string filename) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MerchantContract", filename));

    private static void AssertShape(JsonElement schemas, string name, JsonElement payload)
    {
        var schema = schemas.GetProperty(name);
        var documented = schema.GetProperty("properties").EnumerateObject().Select(item => item.Name).Order();
        Assert.Equal(documented, payload.EnumerateObject().Select(item => item.Name).Order());
    }

    private static async Task<HttpResponseMessage> Submit(HttpClient client, string body, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, OrderEndpoints.OrdersPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(OrderEndpoints.IdempotencyKeyHeader, "merchant-example");
        if (key is not null) request.Headers.Add("X-API-Key", key);
        return await client.SendAsync(request);
    }

    private static void AssertStatus(JsonElement operation, HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.True(operation.GetProperty("responses").TryGetProperty(((int)expected).ToString(), out _));
    }

    private sealed class WebhookReceiver(byte[] expected, Guid eventId, byte[] secret) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Assert.Equal(expected, body);
            Assert.Equal(eventId.ToString("D"), request.Headers.GetValues("X-SwiftDrop-Event-Id").Single());
            Assert.True(HmacSignatureProvider.VerifySignature(secret, body,
                request.Headers.GetValues("X-SwiftDrop-Timestamp").Single(),
                request.Headers.GetValues("X-SwiftDrop-Signature").Single()));
            return new(HttpStatusCode.NoContent);
        }
    }
}
