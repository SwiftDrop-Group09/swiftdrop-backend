using System.Net;
using System.Text.Json;

using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Bookings.Reliability;

[Collection("Postgres")]
public sealed class BookingOutboxContractTests(PostgresFixture fixture)
{
    [Fact]
    public async Task BookingWithoutBrokerAdapterRetainsOneSchemaValidPrivateDataFreePendingEvent()
    {
        // This host has PostgreSQL only: no publisher, relay or broker transport
        // is registered. This proves booking independence, not broker delivery.
        Assert.DoesNotContain(typeof(SwiftDrop.Delivery.Api.Program).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name is not null &&
                (assembly.Name.StartsWith("MassTransit", StringComparison.Ordinal)
                || assembly.Name.StartsWith("RabbitMQ", StringComparison.Ordinal)));
        await using var scenario = await BookingReliabilityScenario.CreateAsync(fixture);
        var before = await scenario.CountsAsync();
        var key = Guid.NewGuid().ToString("D");

        using var response = await scenario.ConfirmAsync(key);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var graph = await scenario.GraphAsync(key);
        Assert.Equal(before.Booked(), await scenario.CountsAsync());
        Assert.Null(graph.Outbox.PublishedAt);
        AssertSchema(graph.Outbox.Payload);
        using var payload = JsonDocument.Parse(graph.Outbox.Payload);
        Assert.Equal(graph.Outbox.Id, payload.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal(graph.Parcel.Id, payload.RootElement.GetProperty("parcelId").GetGuid());
        Assert.Equal(graph.Parcel.Version, payload.RootElement.GetProperty("parcelVersion").GetInt64());
        Assert.Equal(graph.Parcel.CreatedAt, payload.RootElement.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal("Created", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("previousStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("merchantId").ValueKind);
        Assert.Equal(scenario.ApplicationName, payload.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("Private", graph.Outbox.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0000000000", graph.Outbox.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.OwnerId.ToString("D"), graph.Outbox.Payload, StringComparison.Ordinal);

        scenario.Client.DefaultRequestHeaders.Remove("X-Correlation-ID");
        scenario.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "retry-with-another-correlation");
        using var replay = await scenario.ConfirmAsync(key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await response.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        var retained = await scenario.GraphAsync(key);
        Assert.Equal(graph.Outbox.Id, retained.Outbox.Id);
        Assert.Equal(graph.Outbox.Payload, retained.Outbox.Payload);
        Assert.Null(retained.Outbox.PublishedAt);
        Assert.Equal(before.Booked(), await scenario.CountsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialEventMatchesTheCheckedInSchemaForBothExistingParcelOrigins(bool merchant)
    {
        var details = new ParcelDetails(ParcelSize.Small, 2.5m, Guid.NewGuid(),
            "Private pickup street", null, "Private delivery street", "Private recipient and phone 0000000000");
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var originId = Guid.NewGuid();
        var parcel = merchant
            ? Parcel.ForMerchant(Guid.NewGuid(), originId, "private-external-order", "TRK-contract-fixture", details, now)
            : Parcel.ForCustomer(Guid.NewGuid(), originId, "TRK-contract-fixture", details, now);
        var eventId = Guid.NewGuid();

        var message = OutboxMessage.ParcelCreated(eventId, parcel, "contract-correlation");

        AssertSchema(message.Payload);
        using var payload = JsonDocument.Parse(message.Payload);
        Assert.Equal(eventId, payload.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal(now, message.CreatedAt);
        Assert.Null(message.PublishedAt);
        if (merchant)
        {
            Assert.Equal(originId, payload.RootElement.GetProperty("merchantId").GetGuid());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("merchantId").ValueKind);
            Assert.DoesNotContain(originId.ToString("D"), message.Payload, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Private", message.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0000000000", message.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("TRK-contract-fixture", message.Payload, StringComparison.Ordinal);
    }

    private static void AssertSchema(string json)
    {
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "status-changed-v1.schema.json")));
        using var payloadDocument = JsonDocument.Parse(json);
        var schema = schemaDocument.RootElement;
        var payload = payloadDocument.RootElement;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var properties = schema.GetProperty("properties");
        foreach (var required in schema.GetProperty("required").EnumerateArray())
        {
            Assert.True(payload.TryGetProperty(required.GetString()!, out _), "Missing required event field.");
        }

        // Assert every constraint used by the checked-in flat v1 schema. This
        // reads the contract fixture; it is not a general JSON Schema engine.
        foreach (var field in payload.EnumerateObject())
        {
            Assert.True(properties.TryGetProperty(field.Name, out var rule), "Unexpected event field.");
            if (rule.TryGetProperty("type", out var type))
            {
                var allowed = type.ValueKind == JsonValueKind.Array
                    ? type.EnumerateArray().Select(value => value.GetString()).ToArray() : [type.GetString()];
                var actual = field.Value.ValueKind switch
                {
                    JsonValueKind.Null => "null",
                    JsonValueKind.String => "string",
                    JsonValueKind.Number when field.Value.TryGetInt64(out _) => "integer",
                    _ => "unsupported"
                };
                Assert.Contains(actual, allowed);
            }

            if (rule.TryGetProperty("const", out var constant))
            {
                Assert.True(JsonElement.DeepEquals(constant, field.Value), "Unexpected event constant.");
            }

            if (rule.TryGetProperty("enum", out var choices))
            {
                Assert.Contains(choices.EnumerateArray(), choice => JsonElement.DeepEquals(choice, field.Value));
            }

            if (rule.TryGetProperty("minimum", out var minimum))
            {
                Assert.True(field.Value.GetInt64() >= minimum.GetInt64());
            }

            if (rule.TryGetProperty("minLength", out var minLength))
            {
                Assert.True(field.Value.GetString()!.Length >= minLength.GetInt32());
            }

            if (rule.TryGetProperty("format", out var format))
            {
                if (format.GetString() == "uuid")
                {
                    Assert.True(field.Value.TryGetGuid(out var id));
                    Assert.NotEqual(Guid.Empty, id);
                }
                else
                {
                    Assert.Equal("date-time", format.GetString());
                    Assert.True(field.Value.TryGetDateTimeOffset(out var occurredAt));
                    Assert.Equal(TimeSpan.Zero, occurredAt.Offset);
                }
            }
        }
    }
}
