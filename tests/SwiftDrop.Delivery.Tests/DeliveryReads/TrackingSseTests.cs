using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Courier;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;
using Xunit.Abstractions;

namespace SwiftDrop.Delivery.Tests.DeliveryReads;

[Collection("Postgres")]
public sealed class TrackingSseTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task AuthenticatedStreamReportsCommittedStatusToItsOwner()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.PickedUp);
        var parcel = (await scenario.GraphAsync()).Parcel;
        using var ownerHost = scenario.CreateFactory(parcel.CustomerId!.Value, "Customer");
        using var owner = ownerHost.CreateClient();
        using var foreignHost = scenario.CreateFactory(Guid.NewGuid(), "Customer");
        using var foreign = foreignHost.CreateClient();
        var path = $"/api/v1/parcels/{parcel.Id:D}/events";
        using var denied = await foreign.GetAsync(path, ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        using var stream = await owner.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(ct));
        using var first = await ReadSnapshot(reader, ct);
        Assert.Equal("PickedUp", first.RootElement.GetProperty("status").GetString());

        var started = Stopwatch.GetTimestamp();
        using var update = await scenario.UpdateAsync("InTransit", parcel.Version);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var changed = await ReadSnapshot(reader, ct);
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.Equal("InTransit", changed.RootElement.GetProperty("status").GetString());
        Assert.Equal(parcel.Version + 1, changed.RootElement.GetProperty("version").GetInt64());
        output.WriteLine($"Authenticated tracking write-to-frame latency: {elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public async Task PublicStreamRedactsFieldsAndReleasesItsPermitOnDisconnectAndTerminalStatus()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery);
        var parcel = (await scenario.GraphAsync()).Parcel;
        using var host = new QuoteApiFactory(fixture.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Tracking:PublicStreamLimit"] = "1" })));
        using var client = host.CreateClient();
        var path = $"/api/v1/parcels/track/{parcel.TrackingNumber}/events";
        using var missing = await client.GetAsync("/api/v1/parcels/track/unknown-tracking-number/events", ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain(parcel.Id.ToString(), await missing.Content.ReadAsStringAsync(ct));

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using (var stream = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, lifetime.Token))
        using (var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(lifetime.Token)))
        {
            Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
            Assert.True(stream.Headers.CacheControl?.NoStore);
            using var initial = await ReadSnapshot(reader, lifetime.Token);
            AssertPublicSnapshot(initial, parcel.TrackingNumber, "OutForDelivery");
            using var busy = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
            await lifetime.CancelAsync();
        }

        using (var stream = await ReconnectAsync(client, path, ct))
        using (var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(ct)))
        {
            using var initial = await ReadSnapshot(reader, ct);
            AssertPublicSnapshot(initial, parcel.TrackingNumber, "OutForDelivery");

            var started = Stopwatch.GetTimestamp();
            using var completed = await ProofTestSupport.Send(scenario.Client, parcel.Id, ProofTestSupport.Form(parcel.Version));
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            using var latest = await ReadSnapshot(reader, ct);
            var elapsed = Stopwatch.GetElapsedTime(started);
            AssertPublicSnapshot(latest, parcel.TrackingNumber, "Delivered");
            Assert.Equal("", await reader.ReadLineAsync(ct));
            Assert.Null(await reader.ReadLineAsync(ct));
            output.WriteLine($"Public tracking write-to-frame latency: {elapsed.TotalMilliseconds:F0} ms.");
        }

        using var terminal = await ReconnectAsync(client, path, ct);
        using var terminalReader = new StreamReader(await terminal.Content.ReadAsStreamAsync(ct));
        using var snapshot = await ReadSnapshot(terminalReader, ct);
        AssertPublicSnapshot(snapshot, parcel.TrackingNumber, "Delivered");
        Assert.Equal("", await terminalReader.ReadLineAsync(ct));
        Assert.Null(await terminalReader.ReadLineAsync(ct));
    }

    private static async Task<HttpResponseMessage> ReconnectAsync(HttpClient client, string path, CancellationToken ct)
    {
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Last-Event-ID", "previous");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return response;
            }

            response.Dispose();
            await Task.Delay(30, ct);
        }
    }

    private static void AssertPublicSnapshot(JsonDocument document, string trackingNumber, string status)
    {
        var body = document.RootElement;
        Assert.Equal(new[] { "lastUpdatedAt", "status", "trackingNumber" },
            body.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(trackingNumber, body.GetProperty("trackingNumber").GetString());
        Assert.Equal(status, body.GetProperty("status").GetString());
    }

    private static async Task<JsonDocument> ReadSnapshot(StreamReader reader, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return JsonDocument.Parse(line[6..]);
            }
        }

        throw new EndOfStreamException("The tracking stream closed before the next snapshot.");
    }
}
