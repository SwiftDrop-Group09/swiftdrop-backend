using System.Text.Json;
using SwiftDrop.Contracts.Events.V1;
using SwiftDrop.Delivery.OutboxRelay;

namespace SwiftDrop.Delivery.OutboxRelay.Tests;

public sealed class OutboxEventParserTests
{
    [Fact]
    public void ParsesValidStatusChangedAndPreservesOpaqueCorrelation()
    {
        var message = ValidMessage() with { CorrelationId = "trace/from-upstream:opaque" };
        var payload = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var parsed = OutboxEventParser.Parse(message.EventId, StatusChangedV1.CanonicalEventType, payload);

        Assert.Equal(message, parsed);
        Assert.Equal("trace/from-upstream:opaque", parsed.CorrelationId);
    }

    [Fact]
    public void RejectsUnsupportedOutboxEventType()
    {
        var message = ValidMessage();
        var payload = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Throws<InvalidDataException>(() =>
            OutboxEventParser.Parse(message.EventId, "UnrelatedEvent", payload));
    }

    [Fact]
    public void RejectsPayloadWhoseEventIdDiffersFromOutboxIdentity()
    {
        var message = ValidMessage();
        var payload = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Throws<InvalidDataException>(() =>
            OutboxEventParser.Parse(Guid.NewGuid(), StatusChangedV1.CanonicalEventType, payload));
    }

    [Fact]
    public void RejectsInvalidContractVersion()
    {
        var message = ValidMessage() with { SchemaVersion = StatusChangedV1.CurrentSchemaVersion + 1 };
        var payload = JsonSerializer.Serialize(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Throws<InvalidDataException>(() =>
            OutboxEventParser.Parse(message.EventId, StatusChangedV1.CanonicalEventType, payload));
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        Assert.Throws<JsonException>(() =>
            OutboxEventParser.Parse(Guid.NewGuid(), StatusChangedV1.CanonicalEventType, "{"));
    }

    private static StatusChangedV1 ValidMessage() => new(
        Guid.NewGuid(),
        StatusChangedV1.CanonicalEventType,
        StatusChangedV1.CurrentSchemaVersion,
        DateTimeOffset.Parse("2026-11-17T09:00:00Z"),
        Guid.NewGuid().ToString("D"),
        2,
        null,
        "InTransit",
        "PickedUp",
        "opaque-correlation-value");
}
