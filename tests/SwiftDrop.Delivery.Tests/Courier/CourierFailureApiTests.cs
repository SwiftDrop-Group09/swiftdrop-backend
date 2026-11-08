using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Bookings.Reliability;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierFailureApiTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Unknown")]
    [InlineData("0")]
    [InlineData("recipientunavailable")]
    public async Task MissingOrInvalidReasonLeavesAssignmentHistoryVersionAndReplayUntouched(string? reason)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery);
        var before = await scenario.GraphAsync();
        var key = Guid.NewGuid().ToString("N");
        using var rejected = await scenario.UpdateAsync("Failed", 5, key, notes: null, reason: reason);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(rejected, HttpStatusCode.BadRequest, "validation");
        Assert.Equal(before.Signature(), (await scenario.GraphAsync()).Signature());
        // A corrected request can use the same key: validation did not consume it.
        using var corrected = await scenario.UpdateAsync("Failed", 5, key, notes: null, reason: "Other");
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        var after = await scenario.GraphAsync();
        Assert.Equal(AssignmentStatus.Failed, Assert.Single(after.Assignments).Status);
        Assert.Equal("Other", Assert.Single(after.History, x => x.ToStatus == ParcelStatus.Failed).ChangeReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalNotesStayInAuthorizedPrivateHistoryAndFailureNeedsNoProof(bool includeNotes)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery);
        var before = await scenario.GraphAsync();
        var notes = includeNotes ? new string('n', 500) : null;
        using var response = await scenario.UpdateAsync("Failed", 5, notes: notes, reason: "Other");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        using var receipt = JsonDocument.Parse(body);
        Assert.Equal(6, receipt.RootElement.EnumerateObject().Count());
        Assert.Equal("Failed", receipt.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("Other", body);
        if (notes is not null) Assert.DoesNotContain(notes, body);
        var after = await scenario.GraphAsync();
        Assert.Equal(before.Parcel.Version + 1, after.Parcel.Version);
        Assert.Equal(before.Snapshot.Total, after.Snapshot.Total);
        Assert.Equal(before.Parcel.TrackingNumber, after.Parcel.TrackingNumber);
        var assignment = Assert.Single(after.Assignments);
        Assert.Equal(AssignmentStatus.Failed, assignment.Status);
        Assert.Equal(before.Assignments.Single().PickedUpAt, assignment.PickedUpAt);
        Assert.Null(assignment.DeliveredAt);
        Assert.Equal(before.History.Length + 1, after.History.Length);
        var failure = Assert.Single(after.History, x => x.ToStatus == ParcelStatus.Failed);
        Assert.Equal(ParcelStatus.OutForDelivery, failure.FromStatus);
        Assert.Equal(scenario.CourierId, failure.ChangedBy);
        Assert.Equal("Other", failure.ChangeReason);
        Assert.Equal(notes, failure.DeliveryNote);
        Assert.Equal(after.Parcel.UpdatedAt, failure.Timestamp);
        var emitted = after.Events[^1];
        BookingOutboxContractTests.AssertSchema(emitted.Payload);
        Assert.Equal(before.Events.Length + 1, after.Events.Length);
        Assert.DoesNotContain("Other", emitted.Payload);
        if (notes is not null) Assert.DoesNotContain(notes, emitted.Payload);
        await using var context = scenario.Context();
        Assert.Empty(await context.ProofMetadata.Where(x => x.ParcelId == scenario.ParcelId).ToArrayAsync());
        Assert.False(await context.Assignments.AnyAsync(x => x.ParcelId == scenario.ParcelId && x.Status == AssignmentStatus.Active));

        using var ownerFactory = scenario.CreateFactory(before.Parcel.CustomerId, "Customer");
        using var owner = ownerFactory.CreateClient();
        using var details = await owner.GetAsync($"/api/v1/parcels/{scenario.ParcelId}");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        var privateBody = await details.Content.ReadAsStringAsync();
        Assert.Contains("\"changeReason\":\"Other\"", privateBody);
        if (notes is not null) Assert.Contains(notes, privateBody);
        using var closedCourierDetail = await scenario.Client.GetAsync($"/api/v1/parcels/{scenario.ParcelId}");
        Assert.Equal(HttpStatusCode.NotFound, closedCourierDetail.StatusCode);
        using var foreignFactory = scenario.CreateFactory(Guid.NewGuid(), "Customer");
        using var foreign = foreignFactory.CreateClient();
        using var denied = await foreign.GetAsync($"/api/v1/parcels/{scenario.ParcelId}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.DoesNotContain("Other", await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StaleFailureCannotCloseTheAssignmentOrConsumeItsKey()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.PickedUp);
        var before = await scenario.GraphAsync();
        var key = Guid.NewGuid().ToString("N");
        using var stale = await scenario.UpdateAsync("Failed", 2, key, notes: null, reason: "Damaged");
        await DeliveryReadAuthenticationTests.AssertProblemAsync(stale, HttpStatusCode.Conflict, "stale_version");
        using var error = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal(3, error.RootElement.GetProperty("currentVersion").GetInt64());
        Assert.Equal(before.Signature(), (await scenario.GraphAsync()).Signature());
        using var recovered = await scenario.UpdateAsync("Failed", 3, key, notes: null, reason: "Damaged");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(4, (await scenario.GraphAsync()).Parcel.Version);
    }

    [Fact]
    public async Task ForeignAndReassignedCouriersCannotFailTheParcelButItsReplacementCan()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var key = Guid.NewGuid().ToString("N");
        using var otherFactory = scenario.CreateFactory(scenario.OtherCourierId);
        using var other = otherFactory.CreateClient();
        var before = await scenario.GraphAsync();
        using var foreign = await scenario.UpdateAsync("Failed", 2, key, notes: null, reason: "Other", client: other);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.DoesNotContain("currentVersion", await foreign.Content.ReadAsStringAsync());
        Assert.Equal(before.Signature(), (await scenario.GraphAsync()).Signature());
        await using (var database = scenario.Context()) Assert.False(await database.IdempotencyRecords.AnyAsync(x => x.Key == key));
        Assert.True(await scenario.ReassignAsync(2));
        var reassigned = await scenario.GraphAsync();
        using var former = await scenario.UpdateAsync("Failed", 2, key, notes: null, reason: "Other");
        Assert.Equal(HttpStatusCode.NotFound, former.StatusCode);
        Assert.DoesNotContain("currentVersion", await former.Content.ReadAsStringAsync());
        Assert.Equal(reassigned.Signature(), (await scenario.GraphAsync()).Signature());
        using var replacement = await scenario.UpdateAsync("Failed", 3, key, notes: null, reason: "Other", client: other);
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        var after = await scenario.GraphAsync();
        Assert.Equal(2, after.Assignments.Length); // historical row retained; no automatic third assignment
        Assert.Single(after.Assignments, x => x.Status == AssignmentStatus.Reassigned);
        var failed = Assert.Single(after.Assignments, x => x.Status == AssignmentStatus.Failed);
        Assert.Equal(scenario.OtherCourierId, failed.CourierId);
        Assert.Equal(scenario.OtherCourierId, Assert.Single(after.History, x => x.ToStatus == ParcelStatus.Failed).ChangedBy);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Merchant")]
    [InlineData("Dispatcher")]
    [InlineData("Admin")]
    public async Task OtherRolesCannotSubmitAValidFailureForAnAssignedCourierAccount(string role)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var before = await scenario.GraphAsync();
        using var factory = scenario.CreateFactory(role: role);
        using var client = factory.CreateClient();
        using var denied = await scenario.UpdateAsync("Failed", 2, notes: null, reason: "Other", client: client);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(before.Signature(), (await scenario.GraphAsync()).Signature());
    }

    [Theory]
    [InlineData("Created")]
    [InlineData("Assigned")]
    [InlineData("PickedUp")]
    [InlineData("InTransit")]
    [InlineData("OutForDelivery")]
    [InlineData("Delivered")]
    [InlineData("Cancelled")]
    [InlineData("Failed")]
    public async Task FailedCannotBeRepeatedResumedReassignedCancelledOrDelivered(string target)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        using var failed = await scenario.UpdateAsync("Failed", 2, notes: null, reason: "RecipientUnavailable");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        var terminal = await scenario.GraphAsync();
        using var retry = await scenario.UpdateAsync(target, 3, notes: "No undocumented redelivery",
            reason: target == "Failed" ? "Other" : null);
        await DeliveryReadAuthenticationTests.AssertProblemAsync(retry, HttpStatusCode.Conflict, "invalid_transition");
        Assert.Equal(terminal.Signature(), (await scenario.GraphAsync()).Signature());
    }

    [Fact]
    public async Task UnassignedFailureIsNotAuthorizedAndFailedProofCompletionCannotResumeTheParcel()
    {
        await using (var unassigned = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created))
        {
            var before = await unassigned.GraphAsync();
            using var response = await unassigned.UpdateAsync("Failed", 1, notes: null, reason: "Other");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(before.Signature(), (await unassigned.GraphAsync()).Signature());
        }
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery);
        using var failure = await scenario.UpdateAsync("Failed", 5, notes: null, reason: "Damaged");
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        var terminal = await scenario.GraphAsync();
        using var proof = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form(6));
        Assert.Equal(HttpStatusCode.NotFound, proof.StatusCode);
        Assert.Equal(terminal.Signature(), (await scenario.GraphAsync()).Signature());
        await using var database = scenario.Context();
        Assert.Empty(await database.ProofMetadata.Where(x => x.ParcelId == scenario.ParcelId).ToArrayAsync());
        Assert.False(await database.IdempotencyRecords.AnyAsync(x => x.ParcelId == scenario.ParcelId && x.Operation == "CompleteDeliveryWithProof"));
    }

    [Fact]
    public async Task ACommittedDeliveryCannotBeOverwrittenByAFailure()
    {
        var storage = new MemoryProofStorage();
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.OutForDelivery,
            configure: services => ProofTestSupport.UseStorage(services, storage));
        using var completed = await ProofTestSupport.Send(scenario.Client, scenario.ParcelId, ProofTestSupport.Form());
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var before = await ProofTestSupport.Signature(scenario);
        using var failure = await scenario.UpdateAsync("Failed", 6, notes: null, reason: "Other");
        await DeliveryReadAuthenticationTests.AssertProblemAsync(failure, HttpStatusCode.Conflict, "invalid_transition");
        Assert.Equal(before, await ProofTestSupport.Signature(scenario));
        Assert.Equal(1, storage.Stages);
    }

    [Fact]
    public async Task MerchantFailureKeepsProvenanceAndUsesOnlyTheCanonicalRedactedStatusEvent()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.InTransit, merchant: true);
        var before = await scenario.GraphAsync();
        using var response = await scenario.UpdateAsync("Failed", 4, notes: "Private merchant failure note", reason: "AddressUnreachable");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await scenario.GraphAsync();
        Assert.Equal(before.Parcel.MerchantId, after.Parcel.MerchantId);
        Assert.Equal(before.Parcel.ExternalOrderId, after.Parcel.ExternalOrderId);
        Assert.Equal(before.Events.Length + 1, after.Events.Length);
        var emitted = after.Events[^1];
        Assert.Equal("StatusChanged", emitted.EventType);
        BookingOutboxContractTests.AssertSchema(emitted.Payload);
        using var payload = JsonDocument.Parse(emitted.Payload);
        Assert.Equal(before.Parcel.MerchantId!.Value.ToString(), payload.RootElement.GetProperty("merchantId").GetString());
        Assert.Equal("InTransit", payload.RootElement.GetProperty("previousStatus").GetString());
        Assert.Equal("Failed", payload.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("AddressUnreachable", emitted.Payload);
        Assert.DoesNotContain("Private merchant failure note", emitted.Payload);
        Assert.Null(emitted.PublishedAt);
    }
}
