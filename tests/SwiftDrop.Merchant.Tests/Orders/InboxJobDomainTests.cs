using SwiftDrop.Merchant.Api.Domain.Orders;
using static SwiftDrop.Merchant.Tests.Orders.InboxJobTestData;

namespace SwiftDrop.Merchant.Tests.Orders;

public sealed class InboxJobDomainTests
{
    // ── Accept ─────────────────────────────────────────────────────────────

    [Fact]
    public void Accept_CreatesJobThatIsDueImmediately()
    {
        var businessId = Guid.NewGuid();

        var job = NewJob(businessId);

        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.Equal(businessId, job.BusinessId);
        Assert.Equal("ORD-1001", job.ExternalOrderId);
        Assert.Equal(InboxJobState.Accepted, job.State);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(Now, job.NextRetryAt);
        Assert.Equal(Now, job.CreatedAt);
        Assert.Equal(Now, job.UpdatedAt);
        Assert.Null(job.ParcelId);
        Assert.Null(job.TrackingNumber);
        Assert.Null(job.ErrorCode);
    }

    [Fact]
    public void Accept_NormalisesTimestampsToUtc()
    {
        var local = new DateTimeOffset(2026, 11, 13, 14, 30, 0, TimeSpan.FromHours(5.5));

        var job = InboxJob.Accept(Guid.NewGuid(), "ORD-1", "idem-1", Fingerprint, Payload, local);

        Assert.Equal(TimeSpan.Zero, job.CreatedAt.Offset);
        Assert.Equal(local.UtcDateTime, job.CreatedAt.UtcDateTime);
    }

    [Fact]
    public void Accept_RejectsEmptyBusinessId() =>
        Assert.Throws<ArgumentException>(() => NewJob(Guid.Empty));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" ORD-1")]
    [InlineData("ORD-1 ")]
    [InlineData("ORD\n1")]
    public void Accept_RejectsInvalidExternalOrderId(string externalOrderId) =>
        Assert.ThrowsAny<ArgumentException>(() => NewJob(externalOrderId: externalOrderId));

    [Fact]
    public void Accept_EnforcesExternalOrderIdLength()
    {
        _ = NewJob(externalOrderId: new string('x', InboxJobLimits.ExternalOrderIdMaxLength));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NewJob(externalOrderId: new string('x', InboxJobLimits.ExternalOrderIdMaxLength + 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("g000000000000000000000000000000000000000000000000000000000000000")]
    public void Accept_RejectsMalformedFingerprint(string fingerprint) =>
        Assert.Throws<ArgumentException>(() =>
            InboxJob.Accept(Guid.NewGuid(), "ORD-1", "idem-1", fingerprint, Payload, Now));

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("""{"a":1,"a":2}""")]
    public void Accept_RequiresPayloadToBeAJsonObject(string payload) =>
        Assert.Throws<ArgumentException>(() =>
            InboxJob.Accept(Guid.NewGuid(), "ORD-1", "idem-1", Fingerprint, payload, Now));

    [Fact]
    public void HasSameContentAs_ComparesFingerprintsExactly()
    {
        var job = NewJob();

        Assert.True(job.HasSameContentAs(Fingerprint));
        Assert.False(job.HasSameContentAs(OrderFingerprint.Compute("""{"externalOrderId":"ORD-1002"}""")));
    }

    // ── MarkCreated ────────────────────────────────────────────────────────

    [Fact]
    public void MarkCreated_FromAccepted_StoresBookingAndSettlesJob()
    {
        var job = NewJob();
        var booking = Booking();
        var createdAt = Now.AddSeconds(5);

        job.MarkCreated(booking, createdAt);

        Assert.Equal(InboxJobState.Created, job.State);
        Assert.Equal(booking.ParcelId, job.ParcelId);
        Assert.Equal("SD-FICTIONAL-0001", job.TrackingNumber);
        Assert.Equal(ParcelStatuses.Created, job.ParcelStatus);
        Assert.Equal(1, job.ParcelVersion);
        Assert.Equal(750.00m, job.Total);
        Assert.Equal("LKR", job.Currency);
        Assert.Equal("2026-11-v1", job.AcceptedPriceVersion);
        Assert.Null(job.NextRetryAt);
        Assert.Equal(createdAt, job.LastRefreshedAt);
        Assert.Equal(createdAt, job.UpdatedAt);
    }

    [Fact]
    public void MarkCreated_AfterRetry_ClearsPreviousError()
    {
        var job = NewJob();
        job.ScheduleRetry("delivery_unavailable", Now);

        job.MarkCreated(Booking(), Now.AddMinutes(1));

        Assert.Equal(InboxJobState.Created, job.State);
        Assert.Null(job.ErrorCode);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public void MarkCreated_OnSettledJob_Throws()
    {
        var created = CreatedJob();
        var failed = NewJob();
        failed.MarkFailed("invalid_order", Now);

        Assert.Throws<InvalidOperationException>(() => created.MarkCreated(Booking(), Now));
        Assert.Throws<InvalidOperationException>(() => failed.MarkCreated(Booking(), Now));
    }

    // ── ScheduleRetry ──────────────────────────────────────────────────────

    [Fact]
    public void ScheduleRetry_FollowsBackoffSchedule()
    {
        var job = NewJob();

        job.ScheduleRetry("delivery_timeout", Now);
        Assert.Equal(InboxJobState.Retrying, job.State);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(Now.AddSeconds(5), job.NextRetryAt);
        Assert.Equal("delivery_timeout", job.ErrorCode);

        job.ScheduleRetry("delivery_timeout", Now);
        Assert.Equal(Now.AddSeconds(30), job.NextRetryAt);
    }

    [Fact]
    public void ScheduleRetry_OnSettledJob_Throws() =>
        Assert.Throws<InvalidOperationException>(() => CreatedJob().ScheduleRetry("delivery_timeout", Now));

    [Theory]
    [InlineData("")]
    [InlineData("Timeout")]
    [InlineData("delivery timeout")]
    [InlineData("1_starts_with_digit")]
    [InlineData("System.TimeoutException: The operation has timed out")]
    public void ScheduleRetry_RejectsUnsafeErrorCodes(string errorCode)
    {
        var job = NewJob();

        Assert.Throws<ArgumentException>(() => job.ScheduleRetry(errorCode, Now));
        Assert.Equal(InboxJobState.Accepted, job.State);
        Assert.Equal(0, job.Attempts);
    }

    [Fact]
    public void ScheduleRetry_RejectsOverlongErrorCode() =>
        Assert.Throws<ArgumentException>(() =>
            NewJob().ScheduleRetry(new string('a', InboxJobLimits.ErrorCodeMaxLength + 1), Now));

    // ── MarkFailed ─────────────────────────────────────────────────────────

    [Fact]
    public void MarkFailed_StopsProcessingWithSafeCode()
    {
        var job = NewJob();
        job.ScheduleRetry("delivery_timeout", Now);

        job.MarkFailed("pricing_changed", Now.AddMinutes(1));

        Assert.Equal(InboxJobState.Failed, job.State);
        Assert.Equal("pricing_changed", job.ErrorCode);
        Assert.Equal(2, job.Attempts);
        Assert.Null(job.NextRetryAt);
        Assert.Null(job.ParcelId);
    }

    [Fact]
    public void MarkFailed_OnSettledJob_Throws() =>
        Assert.Throws<InvalidOperationException>(() => CreatedJob().MarkFailed("invalid_order", Now));

    // ── Claim (processor lease) ────────────────────────────────────────────

    [Fact]
    public void Claim_PushesNextRetryAtToTheEndOfTheLease()
    {
        var job = NewJob();

        job.Claim(Now.AddMinutes(2), Now);

        Assert.Equal(Now.AddMinutes(2), job.NextRetryAt);
        Assert.Equal(InboxJobState.Accepted, job.State);
        Assert.Equal(0, job.Attempts);
    }

    [Fact]
    public void Claim_RejectsPastLeasesAndSettledJobs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewJob().Claim(Now, Now));
        Assert.Throws<InvalidOperationException>(() => CreatedJob().Claim(Now.AddMinutes(2), Now));
    }

    // ── ApplyStatus ────────────────────────────────────────────────────────

    [Fact]
    public void ApplyStatus_WithNewerVersion_UpdatesParcelState()
    {
        var job = CreatedJob();
        var refreshedAt = Now.AddMinutes(10);

        var applied = job.ApplyStatus(Status(ParcelStatuses.Assigned, version: 2), refreshedAt);

        Assert.True(applied);
        Assert.Equal(ParcelStatuses.Assigned, job.ParcelStatus);
        Assert.Equal(2, job.ParcelVersion);
        Assert.Equal(Now.AddMinutes(2), job.LastStatusChangedAt);
        Assert.Equal(refreshedAt, job.LastRefreshedAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ApplyStatus_WithStaleOrDuplicateVersion_IsIgnoredButRecordsRefresh(long staleVersion)
    {
        var job = CreatedJob();
        job.ApplyStatus(Status(ParcelStatuses.InTransit, version: 2), Now.AddMinutes(5));
        var refreshedAt = Now.AddMinutes(20);

        var applied = job.ApplyStatus(Status(ParcelStatuses.Assigned, staleVersion), refreshedAt);

        Assert.False(applied);
        Assert.Equal(ParcelStatuses.InTransit, job.ParcelStatus);
        Assert.Equal(2, job.ParcelVersion);
        Assert.Equal(refreshedAt, job.LastRefreshedAt);
    }

    [Fact]
    public void ApplyStatus_TerminalStatus_RecordsTerminalTime()
    {
        var job = CreatedJob();
        var deliveredAt = Now.AddHours(3);

        job.ApplyStatus(Status(ParcelStatuses.Delivered, version: 6, terminalAt: deliveredAt), Now.AddHours(3));

        Assert.Equal(ParcelStatuses.Delivered, job.ParcelStatus);
        Assert.Equal(deliveredAt, job.TerminalAt);
    }

    [Fact]
    public void ApplyStatus_BeforeParcelExists_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            NewJob().ApplyStatus(Status(ParcelStatuses.Assigned, version: 2), Now));
}
