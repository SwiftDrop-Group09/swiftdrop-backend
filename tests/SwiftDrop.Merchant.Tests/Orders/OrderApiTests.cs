using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SwiftDrop.Merchant.Api.Application.Orders;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Tests.Authentication;

namespace SwiftDrop.Merchant.Tests.Orders;

/// <summary>
/// End-to-end over HTTP: real X-API-Key authentication, order policies,
/// endpoints and handlers against an in-memory Merchant database.
/// Covers TC-M04, TC-M07 and the isolation part of TC-SEC-03.
/// </summary>
public sealed class OrderApiTests
{
    private const string OrdersPath = "/api/v1/merchant/orders";
    private static readonly string ZoneId = "11111111-1111-4111-8111-111111111111";
    private static readonly string[] ReadWrite = [ApiKeyScopes.OrdersWrite, ApiKeyScopes.OrdersRead];

    // ── POST /api/v1/merchant/orders ───────────────────────────────────────

    [Fact]
    public async Task Submit_ValidOrder_Returns202WithLocationAndAcceptedJob()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();

        using var response = await SubmitAsync(client, key, "idem-1", Order("ORD-1001"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await ReadJobAsync(response);
        Assert.Equal($"{OrdersPath}/{job.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("ORD-1001", job.ExternalOrderId);
        Assert.Equal(nameof(InboxJobState.Accepted), job.State);
        Assert.Equal(0, job.Attempts);
        Assert.Null(job.ParcelId);
        Assert.Null(job.TrackingNumber);
        Assert.Null(job.ParcelStatus);
    }

    [Fact]
    public async Task Submit_StoresExactValidatedPayloadAndItsFingerprint()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();

        using var response = await SubmitAsync(client, key, "idem-1", Order("ORD-1001", weightKg: 2.50m));
        var stored = await host.FindJobAsync((await ReadJobAsync(response)).Id);

        Assert.NotNull(stored);
        Assert.Contains("\"weightKg\":2.50", stored.OrderPayload, StringComparison.Ordinal);
        Assert.Equal(OrderFingerprint.Compute(stored.OrderPayload), stored.RequestFingerprint);
        Assert.Equal("idem-1", stored.IdempotencyKey);
    }

    [Fact]
    public async Task Submit_SameOrderAgain_ReturnsTheSameJob()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();

        using var first = await SubmitAsync(client, key, "idem-1", Order("ORD-1001"));
        using var sameKey = await SubmitAsync(client, key, "idem-1", Order("ORD-1001"));
        using var newKey = await SubmitAsync(client, key, "idem-2", Order("ORD-1001"));

        var id = (await ReadJobAsync(first)).Id;
        Assert.Equal(HttpStatusCode.Accepted, sameKey.StatusCode);
        Assert.Equal(id, (await ReadJobAsync(sameKey)).Id);
        Assert.Equal(id, (await ReadJobAsync(newKey)).Id);
    }

    [Fact]
    public async Task Submit_SameExternalIdWithChangedDetails_Returns409OrderConflict()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();
        using var _ = await SubmitAsync(client, key, "idem-1", Order("ORD-1001", recipientName: "Fictional A"));

        using var response = await SubmitAsync(client, key, "idem-2", Order("ORD-1001", recipientName: "Fictional B"));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "order_conflict");
    }

    [Fact]
    public async Task Submit_IdempotencyKeyReusedForAnotherOrder_Returns409()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();
        using var _ = await SubmitAsync(client, key, "idem-1", Order("ORD-1001"));

        using var response = await SubmitAsync(client, key, "idem-1", Order("ORD-1002"));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "idempotency_conflict");
    }

    [Fact]
    public async Task Submit_WithoutIdempotencyKey_Returns400()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();

        using var response = await SubmitAsync(client, key, idempotencyKey: null, Order("ORD-1001"));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
    }

    [Fact]
    public async Task Submit_InvalidFields_Returns400WithFieldErrors()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();

        using var response = await SubmitAsync(client, key, "idem-1",
            Order(" ORD-1001", weightKg: 31m, zoneId: "not-a-zone", recipientName: "  "));

        var errors = (await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation")).GetProperty("errors");
        Assert.True(errors.TryGetProperty("externalOrderId", out _));
        Assert.True(errors.TryGetProperty("packageDetails.weightKg", out _));
        Assert.True(errors.TryGetProperty("pickupAddress.zoneId", out _));
        Assert.True(errors.TryGetProperty("recipient.name", out _));
    }

    [Theory]
    [InlineData("merchantId")]
    [InlineData("userId")]
    [InlineData("role")]
    public async Task Submit_CallerSuppliedIdentityFields_AreRejected(string field)
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();
        var body = JsonSerializer.SerializeToNode(Order("ORD-1001"), JsonSerializerOptions.Web)!.AsObject();
        body[field] = Guid.NewGuid().ToString();

        using var response = await SubmitAsync(client, key, "idem-1", body);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task Submit_WeightAsString_IsRejectedByStrictNumbers()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();
        var body = JsonSerializer.SerializeToNode(Order("ORD-1001"), JsonSerializerOptions.Web)!.AsObject();
        body["packageDetails"]!["weightKg"] = "2.5";

        using var response = await SubmitAsync(client, key, "idem-1", body);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task Submit_MalformedJson_Returns400()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();
        using var request = Request(HttpMethod.Post, OrdersPath, key, "idem-1");
        request.Content = new StringContent("{not json", Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task Submit_KeyWithoutWriteScope_Returns403()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, [ApiKeyScopes.OrdersRead]);
        using var client = host.CreateClient();

        using var response = await SubmitAsync(client, key, "idem-1", Order("ORD-1001"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Submit_OwnerJwtWithoutKey_Returns401()
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        await host.SeedBusinessAsync(BusinessStatus.Approved, MerchantApiFactory.MerchantId);
        using var client = host.CreateClient();

        using var response = await SubmitAsync(client, apiKey: null, "idem-1", Order("ORD-1001"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Submit_SuspendedBusiness_Returns403()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Suspended);
        var key = await host.SeedKeyAsync(business.Id, scopes: ReadWrite);
        using var client = host.CreateClient();

        using var response = await SubmitAsync(client, key, "idem-1", Order("ORD-1001"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── GET /api/v1/merchant/orders/{id} ───────────────────────────────────

    [Fact]
    public async Task Get_OwnOrder_WithReadKey_Returns200()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();
        using var submitted = await SubmitAsync(client, key, "idem-1", Order("ORD-1001"));
        var id = (await ReadJobAsync(submitted)).Id;

        using var response = await client.SendAsync(Request(HttpMethod.Get, $"{OrdersPath}/{id}", key));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(id, (await ReadJobAsync(response)).Id);
    }

    [Fact]
    public async Task Get_OwnOrder_AsOwnerWithJwt_Returns200()
    {
        using var host = new OrderAuthTestHost(MerchantApiFactory.Merchant());
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved, MerchantApiFactory.MerchantId);
        var key = await host.SeedKeyAsync(business.Id, scopes: ReadWrite);
        using var client = host.CreateClient();
        using var submitted = await SubmitAsync(client, key, "idem-1", Order("ORD-1001"));
        var id = (await ReadJobAsync(submitted)).Id;

        using var response = await client.SendAsync(Request(HttpMethod.Get, $"{OrdersPath}/{id}", apiKey: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_AnotherBusinessesOrder_Returns404()
    {
        using var host = new OrderAuthTestHost();
        var keyA = await ApprovedKeyAsync(host, ReadWrite);
        var keyB = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();
        using var submitted = await SubmitAsync(client, keyA, "idem-1", Order("ORD-1001"));
        var id = (await ReadJobAsync(submitted)).Id;

        using var response = await client.SendAsync(Request(HttpMethod.Get, $"{OrdersPath}/{id}", keyB));

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "order_not_found");
    }

    [Fact]
    public async Task Get_UnknownOrder_Returns404()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Get, $"{OrdersPath}/{Guid.NewGuid()}", key));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── GET /api/v1/merchant/orders ────────────────────────────────────────

    [Fact]
    public async Task List_PagesNewestFirstAndOnlyShowsOwnOrders()
    {
        using var host = new OrderAuthTestHost();
        var keyA = await ApprovedKeyAsync(host, ReadWrite);
        var keyB = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();
        foreach (var number in new[] { 1, 2, 3 })
        {
            using var _ = await SubmitAsync(client, keyA, $"idem-a{number}", Order($"ORD-A{number}"));
        }
        using var other = await SubmitAsync(client, keyB, "idem-b1", Order("ORD-B1"));

        var firstPage = await ListAsync(client, keyA, "?page=1&pageSize=2");
        var secondPage = await ListAsync(client, keyA, "?page=2&pageSize=2");

        Assert.Equal(3, firstPage.TotalCount);
        Assert.Equal(new[] { "ORD-A3", "ORD-A2" }, firstPage.Items.Select(item => item.ExternalOrderId));
        Assert.Equal(new[] { "ORD-A1" }, secondPage.Items.Select(item => item.ExternalOrderId));
        Assert.DoesNotContain(firstPage.Items.Concat(secondPage.Items), item => item.ExternalOrderId == "ORD-B1");
    }

    [Fact]
    public async Task List_DefaultsAndStateFilter()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();
        using var _ = await SubmitAsync(client, key, "idem-1", Order("ORD-1001"));

        var all = await ListAsync(client, key, "");
        var accepted = await ListAsync(client, key, "?state=Accepted");
        var created = await ListAsync(client, key, "?state=Created");

        Assert.Equal((1, 20), (all.Page, all.PageSize));
        Assert.Single(accepted.Items);
        Assert.Empty(created.Items);
        Assert.Equal(0, created.TotalCount);
    }

    [Fact]
    public async Task List_PageBeyondData_IsEmpty()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();

        var page = await ListAsync(client, key, "?page=2147483647&pageSize=100");

        Assert.Empty(page.Items);
    }

    [Theory]
    [InlineData("?page=0", "page")]
    [InlineData("?page=abc", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=101", "pageSize")]
    [InlineData("?state=Pending", "state")]
    [InlineData("?state=accepted", "state")]
    [InlineData("?state=1", "state")]
    public async Task List_InvalidQuery_Returns400(string query, string field)
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, ReadWrite);
        using var client = host.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Get, OrdersPath + query, key));

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task List_KeyWithOnlyWriteScope_Returns403()
    {
        using var host = new OrderAuthTestHost();
        var key = await ApprovedKeyAsync(host, [ApiKeyScopes.OrdersWrite]);
        using var client = host.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Get, OrdersPath, key));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task<string> ApprovedKeyAsync(OrderAuthTestHost host, string[] scopes)
    {
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        return await host.SeedKeyAsync(business.Id, scopes: scopes);
    }

    private static object Order(
        string externalOrderId,
        decimal weightKg = 2.5m,
        string zoneId = "11111111-1111-4111-8111-111111111111",
        string recipientName = "Fictional Recipient") => new
    {
        externalOrderId,
        pickupAddress = new { street = "1 Fictional Road", city = "Colombo", postalCode = "00300", zoneId },
        deliveryAddress = new { street = "2 Example Lane", city = "Kandy", postalCode = "20000", zoneId = ZoneId },
        packageDetails = new { weightKg, size = "Small" },
        recipient = new { name = recipientName, phoneNumber = "+94770000001" },
        acceptedPriceVersion = "2026-11-v1"
    };

    private static HttpRequestMessage Request(HttpMethod method, string path, string? apiKey, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (apiKey is not null)
        {
            request.Headers.Add("X-API-Key", apiKey);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient client, string? apiKey, string? idempotencyKey, object body)
    {
        using var request = Request(HttpMethod.Post, OrdersPath, apiKey, idempotencyKey);
        request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        return await client.SendAsync(request);
    }

    private static async Task<OrderJobPage> ListAsync(HttpClient client, string apiKey, string query)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, OrdersPath + query, apiKey));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderJobPage>(JsonSerializerOptions.Web))!;
    }

    private static async Task<OrderJobResponse> ReadJobAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<OrderJobResponse>(JsonSerializerOptions.Web))!;

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }
}
