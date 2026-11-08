using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Courier;

namespace SwiftDrop.Delivery.Tests.ClientHarness;

public sealed class CourierClientVerificationTests
{
    [Fact]
    public async Task PostClientGateVerifiesCompleteWorkflowAndDatabaseRejectsDuplicateEvents()
    {
        await using var harness = await BookingClientHarness.StartAsync(BookingHarnessActor.Courier, courierWorkflow: true);
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => harness.VerifyCompletedCourierWorkflowAsync());
        using var client = harness.Host.CreateClient();
        var manifest = (await client.GetFromJsonAsync<AssignedParcelPage>("/api/v1/couriers/me/assignments"))!;
        var signature = false;
        foreach (var parcel in manifest.Items.Where(x => x.Status == "Assigned"))
        {
            foreach (var (status, version) in new[] { ("PickedUp", 2L), ("InTransit", 3L), ("OutForDelivery", 4L) })
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/deliveries/{parcel.Id:D}/status")
                { Content = JsonContent.Create(new CourierStatusInput(status, version, "TESTDATA stage", null)) };
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            using var completed = await ProofTestSupport.Send(client, parcel.Id,
                ProofTestSupport.Form(photo: !signature, signature: signature), Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            signature = true;
        }
        foreach (var parcel in manifest.Items.Where(x => x.Status == "OutForDelivery"))
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/deliveries/{parcel.Id:D}/status")
            { Content = JsonContent.Create(new CourierStatusInput("Failed", 5, "TESTDATA failure", "Other")) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var verified = await harness.VerifyCompletedCourierWorkflowAsync();
        Assert.Equal(verified, await harness.VerifyCompletedCourierWorkflowAsync());
        Assert.Equal(27, verified.OutboxRows);
        await using var database = harness.OpenContext();
        var deliveredId = manifest.Items.First(x => x.Status == "Assigned").Id;
        var delivered = await database.Parcels.SingleAsync(x => x.Id == deliveredId);
        database.OutboxMessages.Add(OutboxMessage.ProofCompleted(Guid.NewGuid(), delivered, "duplicate-fixture-event"));
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        var rejected = Assert.IsType<Npgsql.PostgresException>(duplicate.InnerException);
        Assert.Equal("23505", rejected.SqlState);
        Assert.Equal("ux_outbox_parcel_version_event", rejected.ConstraintName);
        Assert.Equal(verified, await harness.VerifyCompletedCourierWorkflowAsync());
    }
}
