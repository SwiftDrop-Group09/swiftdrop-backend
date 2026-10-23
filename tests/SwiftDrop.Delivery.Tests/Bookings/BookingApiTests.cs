using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Bookings;

[Collection("Postgres")]
public sealed class BookingApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var context = Context();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ConfirmedQuoteAtomicallyCreatesTheExactBookingAndPrivateDataFreeEvent()
    {
        var seed = await SeedAsync();
        var owner = Guid.NewGuid();
        var clock = new BookingClock();
        using var factory = Factory(owner, clock);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "booking-correlation");
        var quote = await QuoteAsync(client, seed);
        var before = await CountsAsync();
        var key = Guid.NewGuid().ToString();

        using var response = await ConfirmAsync(client, quote.Id, quote.Total, key);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("booking-correlation", Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var result = document.RootElement;
        AssertFields(result, "createdAt", "id", "priceSnapshot", "status", "trackingNumber", "version");
        var parcelId = result.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, parcelId);
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("trackingNumber").GetString()));
        Assert.Equal("Created", result.GetProperty("status").GetString());
        Assert.Equal(1, result.GetProperty("version").GetInt64());
        AssertFields(result.GetProperty("priceSnapshot"), "currency", "pricingVersion", "total");
        Assert.Equal(540m, result.GetProperty("priceSnapshot").GetProperty("total").GetDecimal());
        Assert.Equal("LKR", result.GetProperty("priceSnapshot").GetProperty("currency").GetString());
        Assert.Equal(seed.Tier.PricingVersion, result.GetProperty("priceSnapshot").GetProperty("pricingVersion").GetString());
        Assert.Equal($"/api/v1/parcels/{parcelId}", response.Headers.Location?.OriginalString);

        await using var verification = Context();
        var parcel = await verification.Parcels.SingleAsync(item => item.Id == parcelId);
        var retainedQuote = await verification.Quotes.SingleAsync(item => item.Id == quote.Id);
        var snapshot = await verification.PriceSnapshots.SingleAsync(item => item.ParcelId == parcelId);
        var history = await verification.BookingHistory.SingleAsync(item => item.ParcelId == parcelId);
        var replay = await verification.IdempotencyRecords.SingleAsync(item => item.Key == key);
        var events = await verification.OutboxMessages.ToArrayAsync();
        var outbox = Assert.Single(events, item => EventParcelId(item) == parcelId);
        Assert.Equal(owner, parcel.CustomerId);
        Assert.Null(parcel.MerchantId);
        Assert.Null(parcel.ExternalOrderId);
        Assert.Equal(ParcelStatus.Created, parcel.Status);
        Assert.Equal(1, parcel.Version);
        Assert.Equal(ParcelSize.Small, parcel.Size);
        Assert.Equal(2.5m, parcel.WeightKg);
        Assert.Equal(seed.Delivery.Id, parcel.ZoneId);
        Assert.Null(parcel.PickupContact);
        AssertAddress(parcel.PickupAddress, seed.Pickup.Id, "Fictional pickup street");
        AssertAddress(parcel.DropoffAddress, seed.Delivery.Id, "Fictional delivery street");
        using var recipient = JsonDocument.Parse(parcel.DropoffContact);
        Assert.Equal("Fictional Recipient", recipient.RootElement.GetProperty("name").GetString());
        Assert.Equal("0000000000", recipient.RootElement.GetProperty("phoneNumber").GetString());
        Assert.Equal(parcelId, retainedQuote.ConsumedByParcelId);
        Assert.Equal(seed.Tier.Id, snapshot.PricingTierId);
        Assert.Equal(seed.Delivery.Id, snapshot.ZoneId);
        Assert.Equal(450m, snapshot.BaseFee);
        Assert.Equal(90m, snapshot.WeightFee);
        Assert.Equal(540m, snapshot.Total);
        Assert.Equal(seed.Tier.PricingVersion, snapshot.PricingVersion);
        Assert.Equal("LKR", snapshot.Currency);
        Assert.Null(history.FromStatus);
        Assert.Equal(ParcelStatus.Created, history.ToStatus);
        Assert.Equal(owner, history.ChangedBy);
        Assert.Null(history.ChangeReason);
        Assert.Null(history.DeliveryNote);
        Assert.Equal(result.GetProperty("createdAt").GetDateTimeOffset(), parcel.CreatedAt);
        Assert.Equal(parcel.CreatedAt, parcel.UpdatedAt);
        Assert.Equal(parcel.CreatedAt, snapshot.CalculatedAt);
        Assert.Equal(parcel.CreatedAt, history.Timestamp);
        Assert.Equal(parcel.CreatedAt, outbox.CreatedAt);
        Assert.Equal(TimeSpan.Zero, parcel.CreatedAt.Offset);
        Assert.Null(outbox.PublishedAt);
        using var payload = JsonDocument.Parse(outbox.Payload);
        AssertFields(payload.RootElement, "correlationId", "eventId", "eventType", "merchantId", "occurredAt",
            "parcelId", "parcelVersion", "previousStatus", "schemaVersion", "status");
        Assert.Equal(outbox.Id, payload.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal("StatusChanged", payload.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(parcel.CreatedAt, payload.RootElement.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(1, payload.RootElement.GetProperty("parcelVersion").GetInt64());
        Assert.Equal("Created", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("previousStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("merchantId").ValueKind);
        Assert.Equal("booking-correlation", payload.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("Fictional", outbox.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("0000000000", outbox.Payload, StringComparison.Ordinal);
        Assert.Equal(owner.ToString(), replay.PrincipalId);
        Assert.Equal("CreateParcel", replay.Operation);
        Assert.Equal(parcelId, replay.ParcelId);
        using var stored = JsonDocument.Parse(replay.StoredResponse!);
        Assert.Equal(201, stored.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(body, stored.RootElement.GetProperty("body").GetString());
        Assert.Equal(response.Headers.Location!.OriginalString, stored.RootElement.GetProperty("location").GetString());
        Assert.Equal(before.Booked(), await CountsAsync());
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.NotFound, "not_found")]
    [InlineData("foreign", HttpStatusCode.NotFound, "not_found")]
    [InlineData("expired", HttpStatusCode.Conflict, "quote_expired")]
    [InlineData("wrong-price", HttpStatusCode.Conflict, "price_mismatch")]
    public async Task InvalidQuoteConfirmationLeavesEveryBookingTableUnchanged(string condition,
        HttpStatusCode expectedStatus, string expectedCode)
    {
        var seed = await SeedAsync();
        var owner = Guid.NewGuid();
        var clock = new BookingClock();
        using var quoteFactory = Factory(owner, clock);
        using var quoteClient = quoteFactory.CreateClient();
        var quote = await QuoteAsync(quoteClient, seed);
        var selectedId = condition == "missing" ? Guid.NewGuid() : quote.Id;
        if (condition == "expired")
        {
            await using var context = Context();
            clock.UtcNow = (await context.Quotes.SingleAsync(item => item.Id == quote.Id)).ExpiresAt;
        }

        using var factory = Factory(condition == "foreign" ? Guid.NewGuid() : owner, clock);
        using var client = factory.CreateClient();
        var before = await CountsAsync();

        using var response = await ConfirmAsync(client, selectedId, condition == "wrong-price" ? 541m : quote.Total, NewKey());

        await AssertProblemAsync(response, expectedStatus, expectedCode);
        Assert.Equal(before, await CountsAsync());
        await using var verification = Context();
        Assert.Null((await verification.Quotes.SingleAsync(item => item.Id == quote.Id)).ConsumedByParcelId);
    }

    [Fact]
    public async Task ConsumedQuoteCannotCreateAnotherParcelWithAnotherKey()
    {
        var seed = await SeedAsync();
        using var factory = Factory(Guid.NewGuid(), new BookingClock());
        using var client = factory.CreateClient();
        var quote = await QuoteAsync(client, seed);
        using var first = await ConfirmAsync(client, quote.Id, quote.Total, NewKey());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var before = await CountsAsync();

        using var second = await ConfirmAsync(client, quote.Id, quote.Total, NewKey());

        await AssertProblemAsync(second, HttpStatusCode.Conflict, "quote_consumed");
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task ConfirmationUsesTheRetainedInactiveTariffAfterRepricing()
    {
        var seed = await SeedAsync();
        using var factory = Factory(Guid.NewGuid(), new BookingClock());
        using var client = factory.CreateClient();
        var quote = await QuoteAsync(client, seed);
        await using (var context = Context())
        {
            var tier = await context.PricingTiers.SingleAsync(item => item.Id == seed.Tier.Id);
            context.Entry(tier).Property(item => item.IsActive).CurrentValue = false;
            context.PricingTiers.Add(new PricingTier(Guid.NewGuid(), seed.Delivery.Id, ParcelSize.Small,
                "replacement-v2", 999m, 100m));
            await context.SaveChangesAsync();
        }

        using var response = await ConfirmAsync(client, quote.Id, quote.Total, NewKey());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await using var verification = Context();
        var snapshot = await verification.PriceSnapshots.SingleAsync(item => item.ParcelId == result.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(seed.Tier.Id, snapshot.PricingTierId);
        Assert.Equal(seed.Tier.PricingVersion, snapshot.PricingVersion);
        Assert.Equal(540m, snapshot.Total);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("input")]
    [InlineData("total")]
    public async Task InconsistentPersistedQuoteIsRejectedWithoutAWrite(string corruption)
    {
        var seed = await SeedAsync();
        var owner = Guid.NewGuid();
        var clock = new BookingClock();
        using var factory = Factory(owner, clock);
        using var client = factory.CreateClient();
        var source = await QuoteAsync(client, seed);
        var corruptId = Guid.NewGuid();
        await using (var context = Context())
        {
            var original = await context.Quotes.SingleAsync(item => item.Id == source.Id);
            context.Quotes.Add(new Quote(corruptId, owner,
                corruption == "fingerprint" ? "inconsistent-fingerprint" : original.RequestFingerprint,
                corruption == "input" ? "{}" : original.DeliveryInput,
                seed.Tier, corruption == "total" ? 541m : original.Total, clock.UtcNow));
            await context.SaveChangesAsync();
        }

        var before = await CountsAsync();
        using var response = await ConfirmAsync(client, corruptId, corruption == "total" ? 541m : source.Total, NewKey());

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "quote_conflict");
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task ExactCommittedResponseReplaysAfterQuoteExpiryWithoutAnotherBooking()
    {
        var seed = await SeedAsync();
        var clock = new BookingClock();
        using var factory = Factory(Guid.NewGuid(), clock);
        using var client = factory.CreateClient();
        var quote = await QuoteAsync(client, seed);
        var key = NewKey();
        using var first = await ConfirmAsync(client, quote.Id, quote.Total, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var originalBody = await first.Content.ReadAsStringAsync();
        var originalLocation = first.Headers.Location;
        var before = await CountsAsync();
        clock.UtcNow = clock.UtcNow.AddMinutes(11);

        using var replay = await ConfirmAsync(client, quote.Id, quote.Total, key);

        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync());
        Assert.Equal(originalLocation, replay.Headers.Location);
        Assert.Equal(before, await CountsAsync());
        using var changed = await ConfirmAsync(client, quote.Id, quote.Total + 1m, key);
        await AssertProblemAsync(changed, HttpStatusCode.Conflict, "idempotency_conflict");
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task EquivalentConfirmationPropertyOrderDecimalZerosAndExponentReplayExactly()
    {
        var seed = await SeedAsync();
        using var factory = Factory(Guid.NewGuid(), new BookingClock());
        using var client = factory.CreateClient();
        var quote = await QuoteAsync(client, seed);
        var key = NewKey();
        using var original = await ConfirmAsync(client, quote.Id, quote.Total, key);
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        var originalBody = await original.Content.ReadAsStringAsync();
        var before = await CountsAsync();

        foreach (var amount in new[] { "540.00", "5.4000e2" })
        {
            var body = $"{{\"acceptTotal\":{amount},\"quoteId\":\"{quote.Id}\"}}";
            using var replay = await ConfirmRawAsync(client, body, key);

            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync());
            Assert.Equal(original.Headers.Location, replay.Headers.Location);
            Assert.Equal(before, await CountsAsync());
        }
    }

    [Fact]
    public async Task MaximumLengthOpaqueKeyIsScopedToEachAuthenticatedPrincipal()
    {
        var seed = await SeedAsync();
        var firstOwner = Guid.NewGuid();
        var secondOwner = Guid.NewGuid();
        var key = new string('k', 128);
        using var firstFactory = Factory(firstOwner, new BookingClock());
        using var firstClient = firstFactory.CreateClient();
        using var secondFactory = Factory(secondOwner, new BookingClock());
        using var secondClient = secondFactory.CreateClient();
        var firstQuote = await QuoteAsync(firstClient, seed);
        var secondQuote = await QuoteAsync(secondClient, seed);
        var before = await CountsAsync();

        using var first = await ConfirmAsync(firstClient, firstQuote.Id, firstQuote.Total, key);
        using var second = await ConfirmAsync(secondClient, secondQuote.Id, secondQuote.Total, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.NotEqual(firstBody.RootElement.GetProperty("id").GetGuid(), secondBody.RootElement.GetProperty("id").GetGuid());
        await using var verification = Context();
        var records = await verification.IdempotencyRecords.Where(item => item.Key == key).ToArrayAsync();
        Assert.Equal(new[] { firstOwner.ToString(), secondOwner.ToString() }.Order(StringComparer.Ordinal),
            records.Select(item => item.PrincipalId).Order(StringComparer.Ordinal));
        Assert.Equal(before.Booked().Booked(), await CountsAsync());
    }

    [Fact]
    public async Task ExpiredIdempotencyKeyRemainsATombstone()
    {
        var seed = await SeedAsync();
        var clock = new BookingClock();
        using var factory = Factory(Guid.NewGuid(), clock);
        using var client = factory.CreateClient();
        var quote = await QuoteAsync(client, seed);
        var key = NewKey();
        using var first = await ConfirmAsync(client, quote.Id, quote.Total, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await using (var context = Context())
        {
            clock.UtcNow = (await context.IdempotencyRecords.SingleAsync(item => item.Key == key)).ExpiresAt;
        }

        var before = await CountsAsync();
        using var replay = await ConfirmAsync(client, quote.Id, quote.Total, key);

        await AssertProblemAsync(replay, HttpStatusCode.Conflict, "key_expired");
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task PendingIdempotencyReservationReturnsRetryAfterBeforeConsumedQuoteCheck()
    {
        var seed = await SeedAsync();
        var owner = Guid.NewGuid();
        var clock = new BookingClock();
        using var factory = Factory(owner, clock);
        using var client = factory.CreateClient();
        var quote = await QuoteAsync(client, seed);
        var completedKey = NewKey();
        using var first = await ConfirmAsync(client, quote.Id, quote.Total, completedKey);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var pendingKey = NewKey();
        await using (var context = Context())
        {
            var completed = await context.IdempotencyRecords.SingleAsync(item => item.Key == completedKey);
            context.IdempotencyRecords.Add(new IdempotencyRecord(Guid.NewGuid(), owner.ToString(), "CreateParcel",
                pendingKey, completed.RequestFingerprint, clock.UtcNow));
            await context.SaveChangesAsync();
        }

        var before = await CountsAsync();
        using var response = await ConfirmAsync(client, quote.Id, quote.Total, pendingKey);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "operation_in_progress");
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task OutboxInsertFailureRollsBackConsumptionAndEveryBookingRow()
    {
        var seed = await SeedAsync();
        using var factory = Factory(Guid.NewGuid(), new BookingClock());
        using var client = factory.CreateClient();
        var quote = await QuoteAsync(client, seed);
        var before = await CountsAsync();
        var correlation = "fail-outbox-" + Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add("X-Correlation-ID", correlation);
        var suffix = Guid.NewGuid().ToString("N");
        var trigger = "booking_test_reject_" + suffix;
        var function = "booking_test_reject_" + suffix;
        var createSql = $"""
            CREATE FUNCTION {function}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'Fictional private outbox failure' USING ERRCODE = '23514';
            END;
            $$;
            CREATE TRIGGER {trigger} BEFORE INSERT ON outbox_messages
            FOR EACH ROW WHEN (NEW.payload->>'correlationId' = '{correlation}')
            EXECUTE FUNCTION {function}();
            """;
        var cleanupSql = $"DROP TRIGGER IF EXISTS {trigger} ON outbox_messages; DROP FUNCTION IF EXISTS {function}();";
        await using var setup = Context();
        try
        {
            await setup.Database.ExecuteSqlRawAsync(createSql);
            using var response = await ConfirmAsync(client, quote.Id, quote.Total, NewKey());

            await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "delivery_unavailable");
            Assert.Equal(before, await CountsAsync());
            await using var verification = Context();
            Assert.Null((await verification.Quotes.SingleAsync(item => item.Id == quote.Id)).ConsumedByParcelId);
        }
        finally
        {
            await setup.Database.ExecuteSqlRawAsync(cleanupSql);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentRequestsCreateExactlyOneBooking(bool sameKey)
    {
        var seed = await SeedAsync();
        using var factory = Factory(Guid.NewGuid(), new BookingClock());
        using var client = factory.CreateClient();
        var quote = await QuoteAsync(client, seed);
        var key = NewKey();
        var secondKey = sameKey ? key : NewKey();
        var before = await CountsAsync();

        var responses = await Task.WhenAll(ConfirmAsync(client, quote.Id, quote.Total, key),
            ConfirmAsync(client, quote.Id, quote.Total, secondKey));
        try
        {
            if (sameKey)
            {
                for (var index = 0; index < responses.Length; index++)
                {
                    if (responses[index].StatusCode == HttpStatusCode.Conflict)
                    {
                        await AssertProblemAsync(responses[index], HttpStatusCode.Conflict, "operation_in_progress");
                        Assert.Equal(TimeSpan.FromSeconds(1), responses[index].Headers.RetryAfter?.Delta);
                        responses[index].Dispose();
                        responses[index] = await ConfirmAsync(client, quote.Id, quote.Total, key);
                    }

                    Assert.Equal(HttpStatusCode.Created, responses[index].StatusCode);
                }

                Assert.Equal(await responses[0].Content.ReadAsStringAsync(), await responses[1].Content.ReadAsStringAsync());
                Assert.Equal(responses[0].Headers.Location, responses[1].Headers.Location);
            }
            else
            {
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
                await AssertProblemAsync(Assert.Single(responses, response => response.StatusCode != HttpStatusCode.Created),
                    HttpStatusCode.Conflict, "quote_consumed");
            }

            Assert.Equal(before.Booked(), await CountsAsync());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task TrackingCollisionRetriesWithoutLeavingPartialBookingRows()
    {
        var seed = await SeedAsync();
        var generator = new SequenceTrackingNumbers("collision-" + Guid.NewGuid().ToString("N"),
            "success-" + Guid.NewGuid().ToString("N"));
        using var factory = Factory(Guid.NewGuid(), new BookingClock(), services =>
        {
            services.RemoveAll<ITrackingNumberGenerator>();
            services.AddSingleton<ITrackingNumberGenerator>(generator);
        });
        using var client = factory.CreateClient();
        var firstQuote = await QuoteAsync(client, seed);
        using var first = await ConfirmAsync(client, firstQuote.Id, firstQuote.Total, NewKey());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var secondQuote = await QuoteAsync(client, seed);
        var before = await CountsAsync();

        using var second = await ConfirmAsync(client, secondQuote.Id, secondQuote.Total, NewKey());

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(3, generator.Calls);
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.NotEqual(firstBody.RootElement.GetProperty("trackingNumber").GetString(),
            secondBody.RootElement.GetProperty("trackingNumber").GetString());
        Assert.Equal(before.Booked(), await CountsAsync());
    }

    [Fact]
    public async Task ThreeTrackingCollisionsReturnUnavailableAndRollBackEveryAttempt()
    {
        var seed = await SeedAsync();
        var collision = "exhausted-" + Guid.NewGuid().ToString("N");
        var generator = new SequenceTrackingNumbers(collision, collision);
        using var factory = Factory(Guid.NewGuid(), new BookingClock(), services =>
        {
            services.RemoveAll<ITrackingNumberGenerator>();
            services.AddSingleton<ITrackingNumberGenerator>(generator);
        });
        using var client = factory.CreateClient();
        var originalQuote = await QuoteAsync(client, seed);
        using var original = await ConfirmAsync(client, originalQuote.Id, originalQuote.Total, NewKey());
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        var rejectedQuote = await QuoteAsync(client, seed);
        var before = await CountsAsync();

        using var response = await ConfirmAsync(client, rejectedQuote.Id, rejectedQuote.Total, NewKey());

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "delivery_unavailable");
        Assert.Equal(4, generator.Calls);
        Assert.Equal(before, await CountsAsync());
        await using var verification = Context();
        Assert.Null((await verification.Quotes.SingleAsync(item => item.Id == rejectedQuote.Id)).ConsumedByParcelId);
    }

    [Theory]
    [MemberData(nameof(InvalidConfirmBodies))]
    public async Task InvalidConfirmationFailsBeforeDatabaseResolution(string body)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var client = factory.CreateClient();
        using var response = await ConfirmRawAsync(client, body, NewKey());

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }

    public static IEnumerable<object[]> InvalidConfirmBodies()
    {
        yield return ["{"];
        yield return ["null"];
        yield return ["[]"];
        yield return ["{}"];
        yield return ["{\"quoteId\":\"not-an-id\",\"acceptTotal\":540}"];
        yield return ["{\"quoteId\":\"00000000-0000-0000-0000-000000000000\",\"acceptTotal\":540}"];
        var quote = Guid.NewGuid().ToString();
        yield return [$"{{\"quoteId\":\"{quote}\"}}"];
        yield return ["{\"acceptTotal\":540}"];
        yield return [$"{{\"quoteId\":\"{quote}\",\"acceptTotal\":null}}"];
        yield return [$"{{\"quoteId\":\"{quote}\",\"acceptTotal\":\"540\"}}"];
        yield return [$"{{\"quoteId\":\"{quote}\",\"acceptTotal\":-1}}"];
        yield return [$"{{\"quoteId\":\"{quote}\",\"acceptTotal\":540.001}}"];
        yield return [$"{{\"quoteId\":\"{quote}\",\"acceptTotal\":540.0000000000000000000000000001}}"];
        yield return [$"{{\"quoteId\":\"{quote}\",\"acceptTotal\":540,\"acceptTotal\":541}}"];
        foreach (var field in new[] { "userId", "ownerId", "merchantId", "role", "pricingVersion", "recipient", "deliveryAddress", "fingerprint" })
        {
            yield return [$"{{\"quoteId\":\"{quote}\",\"acceptTotal\":540,\"{field}\":\"caller-controlled\"}}"];
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("oversized")]
    [InlineData("multiple")]
    public async Task IdempotencyKeyMustBeOneNonblankValueOfAtMost128Characters(string condition)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var client = factory.CreateClient();
        var body = ConfirmBody(Guid.NewGuid(), 540m);
        var key = condition switch { "missing" => null, "blank" => " ", "oversized" => new string('k', 129), _ => NewKey() };
        using var response = await ConfirmRawAsync(client, body, key, condition == "multiple");

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionAuthenticationRejectsAnonymousAndFakeIdentityBeforeParsing(bool fakeIdentity)
    {
        using var factory = new QuoteApiFactory(null, replaceAuthentication: false);
        using var client = factory.CreateClient();
        if (fakeIdentity)
        {
            client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
            client.DefaultRequestHeaders.Add("X-User-Role", "Customer");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "unverified-token");
        }

        using var response = await ConfirmRawAsync(client, "{", null);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Theory]
    [InlineData("Courier")]
    [InlineData("Admin")]
    public async Task NonCustomerIsForbiddenBeforeValidationAndDatabaseResolution(string role)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        using var response = await ConfirmRawAsync(client, "{", null);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-user")]
    public async Task MissingOrInvalidTrustedSubjectIsUnauthorized(string? subject)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(subject, "Customer"));
        using var client = factory.CreateClient();
        using var response = await ConfirmRawAsync(client, "{}", NewKey());

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task MissingDatabaseConfigurationReturnsSafeUnavailableWithCorrelation()
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "booking-storage-unavailable");
        using var response = await ConfirmAsync(client, Guid.NewGuid(), 540m, NewKey());

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "delivery_unavailable");
        Assert.Equal("booking-storage-unavailable", Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("booking-storage-unavailable", problem.RootElement.GetProperty("correlationId").GetString());
    }

    private QuoteApiFactory Factory(Guid owner, BookingClock clock, Action<IServiceCollection>? configure = null) =>
        new(fixture.ConnectionString, QuoteApiFactory.Customer(owner), configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            configure?.Invoke(services);
        });

    private DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>()
        .UseNpgsql(fixture.ConnectionString).Options);

    private async Task<Seed> SeedAsync()
    {
        var pickup = new DeliveryZone(Guid.NewGuid(), "Booking pickup " + Guid.NewGuid().ToString("N"));
        var delivery = new DeliveryZone(Guid.NewGuid(), "Booking delivery " + Guid.NewGuid().ToString("N"));
        var tier = new PricingTier(Guid.NewGuid(), delivery.Id, ParcelSize.Small, "booking-test-v1", 450m, 60m);
        await using var context = Context();
        context.Zones.AddRange(pickup, delivery);
        context.PricingTiers.Add(tier);
        await context.SaveChangesAsync();
        return new Seed(pickup, delivery, tier);
    }

    private static async Task<Quoted> QuoteAsync(HttpClient client, Seed seed)
    {
        var input = new JsonObject
        {
            ["pickupAddress"] = Address(seed.Pickup.Id, "Fictional pickup street"),
            ["deliveryAddress"] = Address(seed.Delivery.Id, "Fictional delivery street"),
            ["packageDetails"] = new JsonObject { ["weightKg"] = 2.5m, ["size"] = "Small" },
            ["recipient"] = new JsonObject { ["name"] = "Fictional Recipient", ["phoneNumber"] = "0000000000" }
        };
        using var response = await client.PostAsync("/api/v1/quotes", new StringContent(input.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new Quoted(result.RootElement.GetProperty("quoteId").GetGuid(), result.RootElement.GetProperty("total").GetDecimal());
    }

    private static JsonObject Address(Guid zone, string street) => new()
    {
        ["street"] = street,
        ["city"] = "Fictional city",
        ["postalCode"] = "00000",
        ["zoneId"] = zone.ToString()
    };

    private static void AssertAddress(string json, Guid zoneId, string street)
    {
        using var address = JsonDocument.Parse(json);
        Assert.Equal(street, address.RootElement.GetProperty("street").GetString());
        Assert.Equal("Fictional city", address.RootElement.GetProperty("city").GetString());
        Assert.Equal("00000", address.RootElement.GetProperty("postalCode").GetString());
        Assert.Equal(zoneId, address.RootElement.GetProperty("zoneId").GetGuid());
    }

    private static void AssertFields(JsonElement value, params string[] fields) => Assert.Equal(
        fields.Order(StringComparer.Ordinal), value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private static Guid EventParcelId(OutboxMessage message)
    {
        using var document = JsonDocument.Parse(message.Payload);
        return document.RootElement.GetProperty("parcelId").GetGuid();
    }

    private static string NewKey() => Guid.NewGuid().ToString();

    private static string ConfirmBody(Guid quoteId, decimal total) =>
        new JsonObject { ["quoteId"] = quoteId.ToString(), ["acceptTotal"] = total }.ToJsonString();

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, Guid quoteId, decimal total, string key) =>
        ConfirmRawAsync(client, ConfirmBody(quoteId, total), key);

    private static async Task<HttpResponseMessage> ConfirmRawAsync(HttpClient client, string body, string? key, bool multipleKeys = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/parcels")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", multipleKeys ? new[] { key, NewKey() } : [key]);
        }

        return await client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("https://example.invalid/problems/" + code, problem.RootElement.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("correlationId").GetString()));
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionStrings", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fictional", body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Counts> CountsAsync()
    {
        await using var context = Context();
        return new Counts(await context.Quotes.CountAsync(), await context.Parcels.CountAsync(),
            await context.PriceSnapshots.CountAsync(), await context.BookingHistory.CountAsync(),
            await context.IdempotencyRecords.CountAsync(), await context.OutboxMessages.CountAsync());
    }

    private sealed record Seed(DeliveryZone Pickup, DeliveryZone Delivery, PricingTier Tier);
    private sealed record Quoted(Guid Id, decimal Total);
    private sealed record Counts(int Quotes, int Parcels, int Snapshots, int History, int Keys, int Outbox)
    {
        public Counts Booked() => this with { Parcels = Parcels + 1, Snapshots = Snapshots + 1, History = History + 1, Keys = Keys + 1, Outbox = Outbox + 1 };
    }

    private sealed class BookingClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = QuoteApiFactory.Now.AddTicks(9);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class SequenceTrackingNumbers(string collision, string success) : ITrackingNumberGenerator
    {
        public int Calls { get; private set; }
        public string Generate() => ++Calls <= 2 ? collision : success;
    }
}
