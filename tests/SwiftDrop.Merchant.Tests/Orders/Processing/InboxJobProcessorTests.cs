using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Application.Orders.Processing;
using SwiftDrop.Merchant.Api.Domain.Orders;
using static SwiftDrop.Merchant.Tests.Orders.Processing.ProcessingTestData;

namespace SwiftDrop.Merchant.Tests.Orders.Processing;

/// <summary>One job, one attempt: reconcile → create → map the result (TC-M04 processing, TC-REC-02).</summary>
public sealed class InboxJobProcessorTests
{
    private readonly ManualTimeProvider _clock = new(Now);

    [Fact]
    public async Task NewOrder_LooksUpThenCreates_AndBecomesCreated()
    {
        var delivery = new ScriptedDeliveryBookingClient();
        var job = Job();

        var outcome = await Processor(delivery, _clock).ProcessAsync(job, CancellationToken.None);

        Assert.Equal(JobOutcome.Created, outcome);
        Assert.Equal(InboxJobState.Created, job.State);
        Assert.Equal(delivery.Booking.TrackingNumber, job.TrackingNumber);
        Assert.Equal(1, delivery.LookupCalls);
        Assert.Single(delivery.CreateCalls);
    }

    [Fact]
    public async Task CreateCommand_CarriesStableIdentityAndExactWeight()
    {
        var delivery = new ScriptedDeliveryBookingClient();
        var job = Job(externalOrderId: "ORD-7");

        await Processor(delivery, _clock).ProcessAsync(job, CancellationToken.None);

        var command = Assert.Single(delivery.CreateCalls);
        Assert.Equal(job.BusinessId, command.MerchantId);
        Assert.Equal("ORD-7", command.ExternalOrderId);
        Assert.Equal(job.IdempotencyKey, command.IdempotencyKey);
        Assert.Equal(job.RequestFingerprint, command.RequestFingerprint);
        Assert.Equal("2.50", command.WeightKg);
        Assert.Equal("Small", command.Size);
        Assert.Equal("2026-11-v1", command.AcceptedPriceVersion);
        Assert.Equal(job.Id.ToString("N"), command.CorrelationId);
    }

    [Fact]
    public async Task ExistingParcel_IsAdoptedWithoutCreatingAnother()
    {
        var delivery = new ScriptedDeliveryBookingClient().LookupFinds();
        var job = Job();

        var outcome = await Processor(delivery, _clock).ProcessAsync(job, CancellationToken.None);

        Assert.Equal(JobOutcome.Created, outcome);
        Assert.Empty(delivery.CreateCalls);
    }

    [Fact]
    public async Task LostCreateResponse_IsReconciledOnRetry_WithoutASecondCreate()
    {
        // TC-REC-02: the create call times out (outcome unknown) but Delivery did commit the parcel.
        var delivery = new ScriptedDeliveryBookingClient()
            .CreateReturns(new BookingCallResult.Transient(DeliveryErrorCodes.Timeout))
            .LookupReturns(new BookingCallResult.NotFound())
            .LookupFinds();
        var processor = Processor(delivery, _clock);
        var job = Job();

        Assert.Equal(JobOutcome.Retrying, await processor.ProcessAsync(job, CancellationToken.None));
        Assert.Equal(InboxJobState.Retrying, job.State);

        _clock.Now = job.NextRetryAt!.Value;
        Assert.Equal(JobOutcome.Created, await processor.ProcessAsync(job, CancellationToken.None));

        Assert.Equal(InboxJobState.Created, job.State);
        Assert.Single(delivery.CreateCalls);
    }

    [Fact]
    public async Task TransientFailures_RetryWithEscalatingBackoff()
    {
        var delivery = new ScriptedDeliveryBookingClient()
            .CreateReturns(new BookingCallResult.Transient(DeliveryErrorCodes.Unavailable))
            .CreateReturns(new BookingCallResult.Transient(DeliveryErrorCodes.Unavailable));
        var processor = Processor(delivery, _clock);
        var job = Job();

        await processor.ProcessAsync(job, CancellationToken.None);
        Assert.Equal(Now.AddSeconds(5), job.NextRetryAt);

        _clock.Now = job.NextRetryAt!.Value;
        await processor.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(2, job.Attempts);
        Assert.Equal(_clock.Now.AddSeconds(30), job.NextRetryAt);
        Assert.Equal(DeliveryErrorCodes.Unavailable, job.ErrorCode);
    }

    [Theory]
    [InlineData(DeliveryErrorCodes.PricingChanged)]
    [InlineData(DeliveryErrorCodes.InvalidOrder)]
    [InlineData(DeliveryErrorCodes.OrderConflict)]
    public async Task PermanentRejection_FailsTheJobWithItsSafeCode(string errorCode)
    {
        var delivery = new ScriptedDeliveryBookingClient().CreateReturns(new BookingCallResult.Rejected(errorCode));
        var job = Job();

        var outcome = await Processor(delivery, _clock).ProcessAsync(job, CancellationToken.None);

        Assert.Equal(JobOutcome.Failed, outcome);
        Assert.Equal(InboxJobState.Failed, job.State);
        Assert.Equal(errorCode, job.ErrorCode);
        Assert.Null(job.NextRetryAt);
    }

    [Fact]
    public async Task RefusedCredential_KeepsTheJobAndSignalsStop()
    {
        var delivery = new ScriptedDeliveryBookingClient()
            .LookupReturns(new BookingCallResult.Unauthorized(DeliveryErrorCodes.Unauthorized));
        var job = Job();

        var outcome = await Processor(delivery, _clock).ProcessAsync(job, CancellationToken.None);

        Assert.Equal(JobOutcome.DeliveryUnauthorized, outcome);
        Assert.Equal(InboxJobState.Retrying, job.State);
        Assert.Empty(delivery.CreateCalls);
    }

    [Fact]
    public async Task CallPastTheDeadline_IsATransientTimeout()
    {
        var delivery = new ScriptedDeliveryBookingClient().CreateHangs();
        var job = Job();

        var outcome = await Processor(delivery, _clock, timeout: TimeSpan.FromMilliseconds(50))
            .ProcessAsync(job, CancellationToken.None);

        Assert.Equal(JobOutcome.Retrying, outcome);
        Assert.Equal(DeliveryErrorCodes.Timeout, job.ErrorCode);
    }

    [Fact]
    public async Task UnexpectedClientException_IsTransient()
    {
        var delivery = new ScriptedDeliveryBookingClient().CreateThrows();
        var job = Job();

        var outcome = await Processor(delivery, _clock).ProcessAsync(job, CancellationToken.None);

        Assert.Equal(JobOutcome.Retrying, outcome);
        Assert.Equal(DeliveryErrorCodes.UnexpectedError, job.ErrorCode);
    }

    [Fact]
    public async Task UnreadableStoredPayload_FailsWithoutCallingDelivery()
    {
        var delivery = new ScriptedDeliveryBookingClient();
        var job = InboxJobTestData.NewJob(); // its payload stores weightKg as a string, which is not a valid order

        var outcome = await Processor(delivery, _clock).ProcessAsync(job, CancellationToken.None);

        Assert.Equal(JobOutcome.Failed, outcome);
        Assert.Equal(DeliveryErrorCodes.InvalidStoredOrder, job.ErrorCode);
        Assert.Equal(0, delivery.LookupCalls);
    }

    [Fact]
    public async Task StoredPayloadMissingAField_FailsInsteadOfSendingNulls()
    {
        const string incomplete = """
            {"externalOrderId":"ORD-1","deliveryAddress":{"street":"s","city":"c","postalCode":"p","zoneId":"22222222-2222-4222-8222-222222222222"},
             "packageDetails":{"weightKg":2.5,"size":"Small"},"recipient":{"name":"n","phoneNumber":"p"},"acceptedPriceVersion":"v1"}
            """;
        var job = InboxJob.Accept(Guid.NewGuid(), "ORD-1", "idem-1", OrderFingerprint.Compute(incomplete), incomplete, Now);
        var delivery = new ScriptedDeliveryBookingClient();

        var outcome = await Processor(delivery, _clock).ProcessAsync(job, CancellationToken.None);

        Assert.Equal(JobOutcome.Failed, outcome);
        Assert.Equal(DeliveryErrorCodes.InvalidStoredOrder, job.ErrorCode);
        Assert.Empty(delivery.CreateCalls);
    }

    [Fact]
    public async Task ShutdownCancellation_IsNotSwallowed()
    {
        var delivery = new ScriptedDeliveryBookingClient().CreateHangs();
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Processor(delivery, _clock).ProcessAsync(Job(), shutdown.Token));
    }
}
