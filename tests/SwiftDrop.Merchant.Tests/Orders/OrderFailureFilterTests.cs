using System.Net;
using System.Text.Json;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Tests.Authentication;
using SwiftDrop.Merchant.Tests.Orders.Processing;

namespace SwiftDrop.Merchant.Tests.Orders;

public sealed class OrderFailureFilterTests
{
    [Fact]
    public async Task DeliveryFailureFilterRunsBeforePagingAndDoesNotLeakForeignBusinessOrProcessingFailures()
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var foreign = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: [ApiKeyScopes.OrdersRead]);
        var at = InboxJobTestData.Now;
        var own = ProcessingTestData.CreatedJob(business.Id, "OWN-FAILED", at, "Failed", 4, at);
        own.ApplyStatus(new("Failed", 4, at, at, "Damaged"), at.AddMinutes(1));
        await host.SeedJobAsync(own);
        await host.SeedJobAsync(ProcessingTestData.CreatedJob(business.Id, "OWN-ACTIVE", at, "Assigned", 2));
        await host.SeedJobAsync(ProcessingTestData.CreatedJob(foreign.Id, "FOREIGN-FAILED", at, "Failed", 4, at));
        var processingFailure = ProcessingTestData.Job(business.Id, "REJECTED-ORDER");
        processingFailure.MarkFailed("pricing_changed", at);
        await host.SeedJobAsync(processingFailure);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);
        using var response = await client.GetAsync("/api/v1/merchant/orders?parcelStatus=Failed&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("totalCount").GetInt32());
        var row = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(own.Id, row.GetProperty("id").GetGuid());
        Assert.Equal("Created", row.GetProperty("state").GetString());
        Assert.Equal("Damaged", row.GetProperty("failureReason").GetString());
        Assert.Equal("Failed", row.GetProperty("parcelStatus").GetString());
        Assert.DoesNotContain("deliveryNote", json.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        using var processing = await client.GetAsync("/api/v1/merchant/orders?state=Failed");
        using var processingJson = JsonDocument.Parse(await processing.Content.ReadAsStringAsync());
        Assert.Equal(processingFailure.Id, Assert.Single(processingJson.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("parcelStatus=Unknown")]
    [InlineData("parcelStatus=3")]
    [InlineData("parcelStatus=Failed&parcelStatus=Delivered")]
    [InlineData("businessId=forged")]
    public async Task InvalidFiltersAreRejectedForAnAuthorizedBusiness(string query)
    {
        using var host = new OrderAuthTestHost();
        var business = await host.SeedBusinessAsync(BusinessStatus.Approved);
        var key = await host.SeedKeyAsync(business.Id, scopes: [ApiKeyScopes.OrdersRead]);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", key);
        using var response = await client.GetAsync("/api/v1/merchant/orders?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
