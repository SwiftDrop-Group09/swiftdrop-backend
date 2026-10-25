using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Tests.Authentication;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.ClientHarness;

public sealed class BookingClientHarnessTests
{
    [Fact]
    public async Task RealHttpHostUsesLiveTimeAndCommitsOneServerOwnedBookingWithExactReplay()
    {
        await using var harness = await BookingClientHarness.StartAsync();
        using var client = harness.Host.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", DeliveryTestAuthentication.OtherCustomerId.ToString("D"));
        client.DefaultRequestHeaders.Add("X-User-Role", "Admin");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fictional-unverified-input");
        Assert.True(harness.Address.IsLoopback);
        Assert.InRange(harness.Address.Port, 1, 65535);
        var context = await client.GetFromJsonAsync<BookingHarnessContext>("/__test/booking-context");
        Assert.NotNull(context);
        Assert.Equal(DeliveryTestAuthentication.CustomerId, context.ActorId);
        Assert.Equal(["Customer"], context.Roles);
        Assert.Equal(2, context.Zones.Count);
        Assert.All(context.Zones, zone => Assert.StartsWith("Test ", zone.Name));
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var before = DateTimeOffset.UtcNow;
        var quote = await QuoteAsync(client, context);
        Assert.InRange(quote.ExpiresAt, before.AddMinutes(10).AddSeconds(-1),
            DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(1));
        Assert.Equal(540m, quote.Total);
        await using (var verification = harness.OpenContext())
        {
            Assert.Equal(1, await verification.Quotes.CountAsync());
            Assert.Equal(0, await verification.Parcels.CountAsync());
            Assert.Equal(0, await verification.OutboxMessages.CountAsync());
        }

        var key = Guid.NewGuid().ToString("D");
        using var original = await ConfirmAsync(client, quote, key);
        using var replay = await ConfirmAsync(client, quote, key);
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var body = await original.Content.ReadAsStringAsync();
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(original.Headers.Location, replay.Headers.Location);
        using var response = JsonDocument.Parse(body);
        var parcelId = response.RootElement.GetProperty("id").GetGuid();
        Assert.False(string.IsNullOrWhiteSpace(response.RootElement.GetProperty("trackingNumber").GetString()));
        await using (var verification = harness.OpenContext())
        {
            Assert.Equal(1, await verification.Parcels.CountAsync());
            Assert.Equal(1, await verification.PriceSnapshots.CountAsync());
            Assert.Equal(1, await verification.BookingHistory.CountAsync());
            Assert.Equal(1, await verification.IdempotencyRecords.CountAsync());
            var message = Assert.Single(await verification.OutboxMessages.ToArrayAsync());
            Assert.Null(message.PublishedAt);
            Assert.Equal(DeliveryTestAuthentication.CustomerId, (await verification.Parcels.SingleAsync()).CustomerId);
            Assert.Equal(DeliveryTestAuthentication.CustomerId, (await verification.BookingHistory.SingleAsync()).ChangedBy);
            Assert.Equal(DeliveryTestAuthentication.CustomerId.ToString("D"),
                (await verification.IdempotencyRecords.SingleAsync()).PrincipalId);
            Assert.Equal(parcelId, (await verification.Quotes.SingleAsync()).ConsumedByParcelId);
        }
    }

    [Fact]
    public async Task SocketActorsShareTheOwnedFixtureButCannotBorrowAnotherCustomersQuoteOrRole()
    {
        await using var harness = await BookingClientHarness.StartAsync();
        var otherHost = harness.StartActor(BookingHarnessActor.CustomerB);
        var courierHost = harness.StartActor(BookingHarnessActor.Courier);
        using var owner = harness.Host.CreateClient();
        using var other = otherHost.CreateClient();
        using var courier = courierHost.CreateClient();
        other.DefaultRequestHeaders.Add("X-User-Id", DeliveryTestAuthentication.CustomerId.ToString("D"));
        other.DefaultRequestHeaders.Add("X-User-Role", "Customer");
        courier.DefaultRequestHeaders.Add("X-User-Role", "Customer");
        Assert.Equal(DeliveryTestAuthentication.OtherCustomerId,
            (await other.GetFromJsonAsync<BookingHarnessContext>("/__test/booking-context"))!.ActorId);
        Assert.Equal(["Courier"],
            (await courier.GetFromJsonAsync<BookingHarnessContext>("/__test/booking-context"))!.Roles);
        var quote = await QuoteAsync(owner, harness.Context);
        var key = Guid.NewGuid().ToString("D");
        using (var denied = await ConfirmAsync(other, quote, key))
        {
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        using (var denied = await ConfirmAsync(courier, quote, key))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var deniedQuote = await courier.PostAsJsonAsync("/api/v1/quotes", Input(harness.Context)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedQuote.StatusCode);
        }
        await using (var verification = harness.OpenContext())
        {
            Assert.Equal(0, await verification.Parcels.CountAsync());
            Assert.Equal(0, await verification.IdempotencyRecords.CountAsync());
        }
        using var created = await ConfirmAsync(owner, quote, key);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var foreignReplay = await ConfirmAsync(other, quote, key);
        Assert.Equal(HttpStatusCode.NotFound, foreignReplay.StatusCode);
        await using (var verification = harness.OpenContext())
        {
            Assert.Equal(1, await verification.Parcels.CountAsync());
            Assert.Equal(1, await verification.IdempotencyRecords.CountAsync());
            Assert.Equal(1, await verification.OutboxMessages.CountAsync());
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task NormalHostsNeverExposeClientFixtureContextOrRegisterItsIdentity(string environment)
    {
        using var factory = new QuoteApiFactory(null, replaceAuthentication: false, environmentName: environment);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/__test/booking-context");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.DoesNotContain(await schemes.GetAllSchemesAsync(), scheme => scheme.Name == DeliveryTestAuthentication.SchemeName);
    }

    private static async Task<QuoteResponse> QuoteAsync(HttpClient client, BookingHarnessContext context)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/quotes", Input(context));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<QuoteResponse>())!;
    }

    private static QuoteInput Input(BookingHarnessContext context) => new(
        new("Fictional pickup", "Test city", "00000", context.Zones[0].Id.ToString("D")),
        new("Fictional delivery", "Test city", "00000", context.Zones[1].Id.ToString("D")),
        new(2.5m, "Small"), new("Fictional Recipient", "0000000000"));

    private static async Task<HttpResponseMessage> ConfirmAsync(HttpClient client, QuoteResponse quote, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/parcels")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { quoteId = quote.QuoteId, acceptTotal = quote.Total }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
}
