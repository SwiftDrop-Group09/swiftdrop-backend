using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Application.Orders;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Application.Orders.Processing;
using SwiftDrop.Merchant.Api.Domain.Orders;

namespace SwiftDrop.Merchant.Tests.Orders.Processing;

/// <summary>
/// Delivery double with a script per RPC. Each call takes the next scripted step;
/// when the script runs out, lookups answer NotFound and creates succeed.
/// </summary>
internal sealed class ScriptedDeliveryBookingClient : IDeliveryBookingClient
{
    private readonly Queue<Func<CancellationToken, Task<BookingCallResult>>> _lookups = new();
    private readonly Queue<Func<CancellationToken, Task<BookingCallResult>>> _creates = new();

    public List<CreateBookingCommand> CreateCalls { get; } = [];
    public int LookupCalls { get; private set; }
    public BookingConfirmation Booking { get; } = InboxJobTestData.Booking();

    public ScriptedDeliveryBookingClient LookupReturns(BookingCallResult result) => Enqueue(_lookups, result);
    public ScriptedDeliveryBookingClient CreateReturns(BookingCallResult result) => Enqueue(_creates, result);
    public ScriptedDeliveryBookingClient LookupFinds() => LookupReturns(new BookingCallResult.Found(Booking));

    public ScriptedDeliveryBookingClient CreateHangs()
    {
        _creates.Enqueue(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable.");
        });
        return this;
    }

    public ScriptedDeliveryBookingClient CreateThrows()
    {
        _creates.Enqueue(_ => throw new HttpRequestException("Fictional network failure."));
        return this;
    }

    public Task<BookingCallResult> CreateBookingAsync(CreateBookingCommand command, CancellationToken cancellationToken)
    {
        CreateCalls.Add(command);
        return _creates.TryDequeue(out var step)
            ? step(cancellationToken)
            : Task.FromResult<BookingCallResult>(new BookingCallResult.Found(Booking));
    }

    public Task<BookingCallResult> GetByExternalOrderAsync(Guid merchantId, string externalOrderId, CancellationToken cancellationToken)
    {
        LookupCalls++;
        return _lookups.TryDequeue(out var step)
            ? step(cancellationToken)
            : Task.FromResult<BookingCallResult>(new BookingCallResult.NotFound());
    }

    private ScriptedDeliveryBookingClient Enqueue(Queue<Func<CancellationToken, Task<BookingCallResult>>> queue, BookingCallResult result)
    {
        queue.Enqueue(_ => Task.FromResult(result));
        return this;
    }
}

/// <summary>A clock that only moves when a test moves it.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class ProcessingTestData
{
    public static readonly DateTimeOffset Now = InboxJobTestData.Now;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>A job whose stored payload is a real validated order (weight 2.50 kg).</summary>
    public static InboxJob Job(Guid? businessId = null, string externalOrderId = "ORD-1001", DateTimeOffset? acceptedAt = null)
    {
        var order = new MerchantOrderPayload(
            externalOrderId,
            new OrderAddress("1 Fictional Road", "Colombo", "00300", Guid.Parse("11111111-1111-4111-8111-111111111111")),
            new OrderAddress("2 Example Lane", "Kandy", "20000", Guid.Parse("22222222-2222-4222-8222-222222222222")),
            new OrderPackage(2.50m, "Small"),
            new OrderRecipient("Fictional Recipient", "+94770000001"),
            "2026-11-v1");
        var payload = JsonSerializer.Serialize(order, PayloadJson);

        return InboxJob.Accept(businessId ?? Guid.NewGuid(), externalOrderId, $"idem-{externalOrderId}",
            OrderFingerprint.Compute(payload), payload, acceptedAt ?? Now);
    }

    public static InboxJobProcessor Processor(IDeliveryBookingClient client, TimeProvider clock, TimeSpan? timeout = null) =>
        new(client, clock, Options.Create(Settings(timeout)), NullLogger<InboxJobProcessor>.Instance);

    public static OrderProcessingOptions Settings(TimeSpan? timeout = null) => new()
    {
        BatchSize = 10,
        DeliveryTimeout = timeout ?? TimeSpan.FromSeconds(5),
        LeaseDuration = TimeSpan.FromMinutes(2)
    };
}
