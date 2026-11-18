using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftDrop.Merchant.Api.Application.Orders;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Tests.Authentication;

namespace SwiftDrop.Merchant.Tests.Orders.Processing;

/// <summary>
/// The whole Merchant pipeline in one host: POST with an API key (202, Accepted)
/// → background processor → fake Delivery → GET shows Created with a tracking number.
/// </summary>
public sealed class OrderPipelineEndToEndTests
{
    [Fact]
    public async Task SubmittedOrder_IsProcessedIntoACreatedParcel()
    {
        using var host = new OrderAuthTestHost(runOrderProcessor: true);
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: [ApiKeyScopes.OrdersWrite, ApiKeyScopes.OrdersRead]);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);

        using var submit = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/merchant/orders", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                externalOrderId = "ORD-E2E-1",
                pickupAddress = new { street = "1 Fictional Road", city = "Colombo", postalCode = "00300", zoneId = "11111111-1111-4111-8111-111111111111" },
                deliveryAddress = new { street = "2 Example Lane", city = "Kandy", postalCode = "20000", zoneId = "22222222-2222-4222-8222-222222222222" },
                packageDetails = new { weightKg = 2.5m, size = "Small" },
                recipient = new { name = "Fictional Recipient", phoneNumber = "+94770000001" },
                acceptedPriceVersion = "2026-11-v1"
            }, options: JsonSerializerOptions.Web)
        };
        submit.Headers.Add("Idempotency-Key", "idem-e2e-1");
        using var accepted = await client.SendAsync(submit);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var order = await WaitForStateAsync(client, accepted.Headers.Location!, nameof(InboxJobState.Created));

        Assert.NotNull(order.ParcelId);
        Assert.StartsWith("SD-DEV-", order.TrackingNumber, StringComparison.Ordinal);
        Assert.Equal("Created", order.ParcelStatus);
        Assert.Equal(1, order.ParcelVersion);
        Assert.Null(order.ErrorCode);
    }

    private static async Task<OrderJobResponse> WaitForStateAsync(HttpClient client, Uri location, string state)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var order = await client.GetFromJsonAsync<OrderJobResponse>(location, JsonSerializerOptions.Web);
            if (order!.State == state)
            {
                return order;
            }

            if (timer.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail($"Order stayed '{order.State}' (error '{order.ErrorCode}') instead of reaching '{state}'.");
            }

            await Task.Delay(25);
        }
    }
}
