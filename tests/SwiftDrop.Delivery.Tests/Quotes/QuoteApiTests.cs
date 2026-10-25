using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Quotes;

[Collection("Postgres")]
public sealed class QuoteApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var context = Context();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CustomerGetsPersistedQuoteUsingDeliveryTariffWithoutBookingSideEffects()
    {
        var seed = await SeedAsync();
        var owner = Guid.NewGuid();
        var before = await CountsAsync();
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Customer(owner));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "quote-test-correlation");
        var input = Input(seed);
        input["recipient"]!["name"] = "  Fictional Recipient  ";
        input["pickupAddress"]!["street"] = "  Fictional pickup street  ";

        using var response = await PostAsync(client, input);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("quote-test-correlation", Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = document.RootElement;
        Assert.Equal(new[] { "currency", "expiresAt", "fingerprint", "pricingVersion", "quoteId", "total" },
            result.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var quoteId = result.GetProperty("quoteId").GetGuid();
        Assert.NotEqual(Guid.Empty, quoteId);
        Assert.Equal(540m, result.GetProperty("total").GetDecimal());
        Assert.Equal("LKR", result.GetProperty("currency").GetString());
        Assert.Equal(seed.Tier.PricingVersion, result.GetProperty("pricingVersion").GetString());
        Assert.Equal(QuoteApiFactory.Now.AddMinutes(10), result.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("fingerprint").GetString()));

        await using var verification = Context();
        var quote = await verification.Quotes.SingleAsync(item => item.Id == quoteId);
        Assert.Equal(owner, quote.OwnerId);
        Assert.Equal(seed.Tier.Id, quote.PricingTierId);
        Assert.Equal(seed.Delivery.Id, seed.Tier.ZoneId);
        Assert.Equal(seed.Tier.PricingVersion, quote.PricingVersion);
        Assert.Equal("LKR", quote.Currency);
        Assert.Equal(540m, quote.Total);
        Assert.Equal(QuoteApiFactory.Now, quote.CreatedAt);
        Assert.Equal(QuoteApiFactory.Now.AddMinutes(10), quote.ExpiresAt);
        Assert.Equal(TimeSpan.Zero, quote.ExpiresAt.Offset);
        Assert.Null(quote.ConsumedByParcelId);
        Assert.Equal(result.GetProperty("fingerprint").GetString(), quote.RequestFingerprint);
        using var retained = JsonDocument.Parse(quote.DeliveryInput);
        Assert.Equal("Fictional Recipient", retained.RootElement.GetProperty("recipient").GetProperty("name").GetString());
        Assert.Equal("0000000000", retained.RootElement.GetProperty("recipient").GetProperty("phoneNumber").GetString());
        AssertAddress(retained.RootElement.GetProperty("pickupAddress"), seed.Pickup.Id, "Fictional pickup street");
        AssertAddress(retained.RootElement.GetProperty("deliveryAddress"), seed.Delivery.Id, "Fictional delivery street");
        Assert.Equal(2.5m, retained.RootElement.GetProperty("packageDetails").GetProperty("weightKg").GetDecimal());
        Assert.Equal("Small", retained.RootElement.GetProperty("packageDetails").GetProperty("size").GetString());

        // Declining the displayed quote is represented by taking no further action.
        var after = await CountsAsync();
        Assert.Equal(before with { Quotes = before.Quotes + 1 }, after);
    }

    [Theory]
    [InlineData(ParcelSize.Small, false, 540)]
    [InlineData(ParcelSize.Medium, false, 720)]
    [InlineData(ParcelSize.Large, false, 1050)]
    [InlineData(ParcelSize.Small, true, 540)]
    [InlineData(ParcelSize.Medium, true, 720)]
    [InlineData(ParcelSize.Large, true, 1050)]
    public async Task QuoteSelectsTheRequestedDeliveryZoneAndSizeWithNoBookingSideEffects(
        ParcelSize size, bool sameZone, int expectedTotal)
    {
        var delivery = new DeliveryZone(Guid.NewGuid(), "Size delivery " + Guid.NewGuid().ToString("N"));
        var pickup = sameZone ? delivery
            : new DeliveryZone(Guid.NewGuid(), "Size pickup " + Guid.NewGuid().ToString("N"));
        var tiers = new[]
        {
            new PricingTier(Guid.NewGuid(), delivery.Id, ParcelSize.Small, "size-small-v1", 450m, 60m),
            new PricingTier(Guid.NewGuid(), delivery.Id, ParcelSize.Medium, "size-medium-v2", 600m, 80m),
            new PricingTier(Guid.NewGuid(), delivery.Id, ParcelSize.Large, "size-large-v3", 900m, 100m)
        };
        var expectedTier = tiers.Single(tier => tier.Size == size);
        await using (var setup = Context())
        {
            setup.Zones.Add(delivery);
            setup.PricingTiers.AddRange(tiers);
            if (!sameZone)
            {
                setup.Zones.Add(pickup);
                setup.PricingTiers.Add(new PricingTier(Guid.NewGuid(), pickup.Id, size,
                    "pickup-competing-v1", 1200m, 120m));
            }

            await setup.SaveChangesAsync();
        }

        var owner = Guid.NewGuid();
        var before = await CountsAsync();
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Customer(owner));
        using var client = factory.CreateClient();
        var input = Input(pickup.Id, delivery.Id);
        input["packageDetails"]!["size"] = size.ToString();

        using var response = await PostAsync(client, input);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var quoteId = result.RootElement.GetProperty("quoteId").GetGuid();
        Assert.Equal((decimal)expectedTotal, result.RootElement.GetProperty("total").GetDecimal());
        Assert.Equal(expectedTier.PricingVersion, result.RootElement.GetProperty("pricingVersion").GetString());
        var fingerprint = result.RootElement.GetProperty("fingerprint").GetString();
        Assert.Matches("^[a-f0-9]{64}$", fingerprint);

        await using var verification = Context();
        var quote = await verification.Quotes.SingleAsync(item => item.Id == quoteId);
        Assert.Equal(owner, quote.OwnerId);
        Assert.Equal(expectedTier.Id, quote.PricingTierId);
        Assert.Equal(expectedTier.PricingVersion, quote.PricingVersion);
        Assert.Equal((decimal)expectedTotal, quote.Total);
        Assert.Equal(fingerprint, quote.RequestFingerprint);
        Assert.Null(quote.ConsumedByParcelId);
        using var retained = JsonDocument.Parse(quote.DeliveryInput);
        Assert.Equal(size.ToString(), retained.RootElement.GetProperty("packageDetails").GetProperty("size").GetString());
        Assert.Equal(pickup.Id, retained.RootElement.GetProperty("pickupAddress").GetProperty("zoneId").GetGuid());
        Assert.Equal(delivery.Id, retained.RootElement.GetProperty("deliveryAddress").GetProperty("zoneId").GetGuid());
        Assert.Equal(before with { Quotes = before.Quotes + 1 }, await CountsAsync());
    }

    [Theory]
    [InlineData("0.0001", "450")]
    [InlineData("0.5", "450")]
    [InlineData("1", "450")]
    [InlineData("2.5", "540")]
    [InlineData("30", "2190")]
    public async Task QuoteUsesBaseFeeThroughOneKilogramAndDocumentedExtraWeightFormula(string weight, string expected)
    {
        var seed = await SeedAsync();
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var client = factory.CreateClient();
        var input = Input(seed);
        input["packageDetails"]!["weightKg"] = decimal.Parse(weight, System.Globalization.CultureInfo.InvariantCulture);

        using var response = await PostAsync(client, input);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            result.RootElement.GetProperty("total").GetDecimal());
    }

    [Theory]
    [InlineData("2.5000833333333333333333333333", "540.00")]
    [InlineData("2.5000833333333333333333333334", "540.01")]
    public async Task RepresentableWeightKeepsExactHalfCentBoundaryThroughPersistence(string weight, string expected)
    {
        var seed = await SeedAsync();
        var exactWeight = decimal.Parse(weight, System.Globalization.CultureInfo.InvariantCulture);
        var expectedTotal = decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var client = factory.CreateClient();
        var input = Input(seed);
        input["packageDetails"]!["weightKg"] = exactWeight;

        using var response = await PostAsync(client, input);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedTotal, result.RootElement.GetProperty("total").GetDecimal());
        var quoteId = result.RootElement.GetProperty("quoteId").GetGuid();
        await using var verification = Context();
        var quote = await verification.Quotes.SingleAsync(item => item.Id == quoteId);
        Assert.Equal(expectedTotal, quote.Total);
        using var retained = JsonDocument.Parse(quote.DeliveryInput);
        Assert.Equal(exactWeight, retained.RootElement.GetProperty("packageDetails").GetProperty("weightKg").GetDecimal());
    }

    [Fact]
    public async Task QuoteRoundsMidpointHalfUpToTwoDecimalPlaces()
    {
        var seed = await SeedAsync(perKgRate: 0.05m);
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var client = factory.CreateClient();
        var input = Input(seed);
        input["packageDetails"]!["weightKg"] = 1.1m;

        using var response = await PostAsync(client, input);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(450.01m, result.RootElement.GetProperty("total").GetDecimal());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.1")]
    [InlineData("30.0001")]
    public async Task InvalidWeightFailsBeforePersistenceIsResolved(string weight)
    {
        var input = Input(Guid.NewGuid(), Guid.NewGuid());
        input["packageDetails"]!["weightKg"] = decimal.Parse(weight, System.Globalization.CultureInfo.InvariantCulture);

        await AssertInvalidWithoutDatabaseAsync(input.ToJsonString());
    }

    [Theory]
    [InlineData("30.00000000000000000000000000001")]
    [InlineData("1.00499999999999999999999999999")]
    public async Task UnrepresentableDecimalWeightCannotBeRoundedIntoAnAcceptedRequest(string weight)
    {
        var body = Input(Guid.NewGuid(), Guid.NewGuid()).ToJsonString()
            .Replace("\"weightKg\":2.5", "\"weightKg\":" + weight, StringComparison.Ordinal);

        await AssertInvalidWithoutDatabaseAsync(body);
    }

    [Theory]
    [InlineData("small")]
    [InlineData("ExtraLarge")]
    [InlineData("1")]
    [InlineData("")]
    public async Task SizeMustExactlyMatchTheDocumentedEnum(string size)
    {
        var input = Input(Guid.NewGuid(), Guid.NewGuid());
        input["packageDetails"]!["size"] = size;

        await AssertInvalidWithoutDatabaseAsync(input.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task RequiredMalformedAndUnknownFieldsAreRejectedBeforeDatabaseAccess(string body)
    {
        await AssertInvalidWithoutDatabaseAsync(body);
    }

    public static IEnumerable<object[]> InvalidBodies()
    {
        yield return ["{"];
        yield return ["null"];
        yield return ["[]"];
        foreach (var field in new[] { "pickupAddress", "deliveryAddress", "packageDetails", "recipient" })
        {
            var input = Input(Guid.NewGuid(), Guid.NewGuid());
            input.Remove(field);
            yield return [input.ToJsonString()];
        }

        foreach (var path in new[]
        {
            "packageDetails.weightKg", "packageDetails.size", "recipient.phoneNumber", "deliveryAddress.zoneId"
        })
        {
            var input = Input(Guid.NewGuid(), Guid.NewGuid());
            var parts = path.Split('.');
            input[parts[0]]!.AsObject().Remove(parts[1]);
            yield return [input.ToJsonString()];
        }

        foreach (var path in new[]
        {
            "pickupAddress.street", "deliveryAddress.city", "deliveryAddress.postalCode",
            "recipient.name", "recipient.phoneNumber"
        })
        {
            var input = Input(Guid.NewGuid(), Guid.NewGuid());
            var parts = path.Split('.');
            input[parts[0]]![parts[1]] = " ";
            yield return [input.ToJsonString()];
        }

        var malformedZone = Input(Guid.NewGuid(), Guid.NewGuid());
        malformedZone["pickupAddress"]!["zoneId"] = "not-a-zone-id";
        yield return [malformedZone.ToJsonString()];
        var zeroZone = Input(Guid.NewGuid(), Guid.NewGuid());
        zeroZone["deliveryAddress"]!["zoneId"] = Guid.Empty.ToString();
        yield return [zeroZone.ToJsonString()];
        var stringWeight = Input(Guid.NewGuid(), Guid.NewGuid());
        stringWeight["packageDetails"]!["weightKg"] = "2.5";
        yield return [stringWeight.ToJsonString()];
        var numericSize = Input(Guid.NewGuid(), Guid.NewGuid());
        numericSize["packageDetails"]!["size"] = 0;
        yield return [numericSize.ToJsonString()];
        var duplicateWeight = Input(Guid.NewGuid(), Guid.NewGuid()).ToJsonString()
            .Replace("\"weightKg\":2.5", "\"weightKg\":2.5,\"weightKg\":2.6", StringComparison.Ordinal);
        yield return [duplicateWeight];
        foreach (var path in new[] { "pickupAddress.street", "deliveryAddress.city", "recipient.name", "recipient.phoneNumber" })
        {
            var input = Input(Guid.NewGuid(), Guid.NewGuid());
            var parts = path.Split('.');
            input[parts[0]]![parts[1]] = "Invalid\0value";
            yield return [input.ToJsonString()];
        }

        foreach (var field in new[] { "userId", "ownerId", "merchantId", "role", "total", "pricingVersion" })
        {
            var input = Input(Guid.NewGuid(), Guid.NewGuid());
            input[field] = "caller-controlled";
            yield return [input.ToJsonString()];
        }

        foreach (var field in new[] { "pickupAddress", "deliveryAddress", "packageDetails", "recipient" })
        {
            var input = Input(Guid.NewGuid(), Guid.NewGuid());
            input[field]!["ownerId"] = "caller-controlled";
            yield return [input.ToJsonString()];
            var unknown = Input(Guid.NewGuid(), Guid.NewGuid());
            unknown[field]!["unexpected"] = true;
            yield return [unknown.ToJsonString()];
        }
    }

    [Theory]
    [InlineData("pickup", false)]
    [InlineData("delivery", false)]
    [InlineData("pickup", true)]
    [InlineData("delivery", true)]
    public async Task MissingOrInactivePickupAndDeliveryZonesAreUnsupported(string address, bool inactive)
    {
        var seed = await SeedAsync();
        var input = Input(seed);
        if (inactive)
        {
            await using var context = Context();
            var zoneId = address == "pickup" ? seed.Pickup.Id : seed.Delivery.Id;
            var zone = await context.Zones.SingleAsync(item => item.Id == zoneId);
            context.Entry(zone).Property(item => item.IsActive).CurrentValue = false;
            await context.SaveChangesAsync();
        }
        else
        {
            input[address + "Address"]!["zoneId"] = Guid.NewGuid().ToString();
        }

        var before = await CountsAsync();
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var client = factory.CreateClient();
        using var response = await PostAsync(client, input);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(before, await CountsAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    [InlineData("ambiguous")]
    public async Task UnavailableOrAmbiguousActiveTariffFailsWithoutCreatingAQuote(string condition)
    {
        var seed = await SeedAsync();
        await using (var context = Context())
        {
            if (condition == "missing")
            {
                var tier = await context.PricingTiers.SingleAsync(item => item.Id == seed.Tier.Id);
                context.PricingTiers.Remove(tier);
            }
            else if (condition == "inactive")
            {
                var tier = await context.PricingTiers.SingleAsync(item => item.Id == seed.Tier.Id);
                context.Entry(tier).Property(item => item.IsActive).CurrentValue = false;
            }
            else
            {
                context.PricingTiers.Add(new PricingTier(Guid.NewGuid(), seed.Delivery.Id,
                    ParcelSize.Small, "alternative-active-version", 500m, 80m));
            }

            await context.SaveChangesAsync();
        }

        var before = await CountsAsync();
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var client = factory.CreateClient();
        using var response = await PostAsync(client, Input(seed));

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task FingerprintIsCanonicalAndBindsOwnerRecipientAddressAndPricingVersion()
    {
        var seed = await SeedAsync();
        var owner = Guid.NewGuid();
        using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Customer(owner));
        using var client = factory.CreateClient();
        var original = Input(seed);
        var first = await QuoteResultAsync(client, original.ToJsonString());
        var reordered = JsonNode.Parse(original.ToJsonString())!.AsObject();
        var differentlyOrdered = new JsonObject();
        foreach (var property in reordered.Reverse())
        {
            differentlyOrdered[property.Key] = property.Value?.DeepClone();
        }

        var equivalentBody = differentlyOrdered.ToJsonString().Replace("2.5", "2.5000", StringComparison.Ordinal);
        var equivalent = await QuoteResultAsync(client, equivalentBody);
        Assert.Equal(first.Fingerprint, equivalent.Fingerprint);
        Assert.NotEqual(first.Id, equivalent.Id);

        var changedRecipient = Input(seed);
        changedRecipient["recipient"]!["name"] = "Another Fictional Recipient";
        Assert.NotEqual(first.Fingerprint, (await QuoteResultAsync(client, changedRecipient.ToJsonString())).Fingerprint);
        var changedAddress = Input(seed);
        changedAddress["deliveryAddress"]!["street"] = "Another fictional street";
        Assert.NotEqual(first.Fingerprint, (await QuoteResultAsync(client, changedAddress.ToJsonString())).Fingerprint);
        using var otherFactory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var otherClient = otherFactory.CreateClient();
        Assert.NotEqual(first.Fingerprint, (await QuoteResultAsync(otherClient, original.ToJsonString())).Fingerprint);

        await using (var context = Context())
        {
            var oldTier = await context.PricingTiers.SingleAsync(item => item.Id == seed.Tier.Id);
            context.Entry(oldTier).Property(item => item.IsActive).CurrentValue = false;
            context.PricingTiers.Add(new PricingTier(Guid.NewGuid(), seed.Delivery.Id,
                ParcelSize.Small, "replacement-v2", 450m, 60m));
            await context.SaveChangesAsync();
        }

        var updated = await QuoteResultAsync(client, original.ToJsonString());
        Assert.NotEqual(first.Fingerprint, updated.Fingerprint);
        await using var verification = Context();
        var retained = await verification.Quotes.SingleAsync(item => item.Id == first.Id);
        Assert.Equal(first.Fingerprint, retained.RequestFingerprint);
        Assert.Equal(seed.Tier.Id, retained.PricingTierId);
        Assert.Equal(seed.Tier.PricingVersion, retained.PricingVersion);
        Assert.Equal(540m, retained.Total);
        Assert.Null(retained.ConsumedByParcelId);
        var replacement = await verification.Quotes.SingleAsync(item => item.Id == updated.Id);
        Assert.Equal("replacement-v2", replacement.PricingVersion);
        Assert.NotEqual(retained.PricingTierId, replacement.PricingTierId);
        Assert.Equal(540m, replacement.Total);
    }

    [Theory]
    [InlineData("Courier")]
    [InlineData("Admin")]
    [InlineData("Merchant")]
    public async Task ValidPrincipalWithoutCustomerRoleIsForbiddenBeforeDatabaseResolution(string role)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        using var response = await PostAsync(client, Input(Guid.NewGuid(), Guid.NewGuid()));

        await AssertProblemAsync(response, HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-user-id")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task CustomerWithMissingOrInvalidVerifiedSubjectIsUnauthorized(string? subject)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Principal(subject, "Customer"));
        using var client = factory.CreateClient();
        using var response = await PostAsync(client, Input(Guid.NewGuid(), Guid.NewGuid()));

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionAuthenticationRejectsAnonymousAndCallerSuppliedIdentityWithoutDatabase(bool fakeCredentials)
    {
        using var factory = new QuoteApiFactory(null, replaceAuthentication: false);
        using var client = factory.CreateClient();
        if (fakeCredentials)
        {
            client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
            client.DefaultRequestHeaders.Add("X-User-Role", "Customer");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "caller-controlled-token");
        }

        using var response = await PostAsync(client, Input(Guid.NewGuid(), Guid.NewGuid()));

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnonymousMalformedRequestIsRejectedBeforeBodyParsing()
    {
        using var factory = new QuoteApiFactory(null, replaceAuthentication: false);
        using var client = factory.CreateClient();
        using var content = new StringContent("{", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/v1/quotes", content);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task QuoteDependencyFailureIsSafeAndMalformedConfirmationIsRejected()
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var client = factory.CreateClient();
        using var quote = await PostAsync(client, Input(Guid.NewGuid(), Guid.NewGuid()));
        await AssertProblemAsync(quote, HttpStatusCode.ServiceUnavailable);
        using var parcel = await client.PostAsync("/api/v1/parcels", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, parcel.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PricingReadOrQuoteSaveFailureReturnsSafeUnavailableWithoutPersistence(bool failSave)
    {
        var seed = await SeedAsync();
        var before = await CountsAsync();
        using var factory = new QuoteApiFactory(fixture.ConnectionString,
            QuoteApiFactory.Customer(Guid.NewGuid()), configureServices: services =>
            {
                services.RemoveAll<IQuoteStore>();
                services.AddSingleton<IQuoteStore>(new UnavailableQuoteStore(seed.Tier, failSave));
            });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "safe-unavailable-correlation");

        using var response = await PostAsync(client, Input(seed));

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("safe-unavailable-correlation", problem.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("safe-unavailable-correlation", Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task PostgreSqlInsertFailureReturnsUnavailableAndLeavesNoQuoteOrBookingRows()
    {
        var seed = await SeedAsync();
        var owner = Guid.NewGuid();
        var before = await CountsAsync();
        // Both identifiers contain only a constant prefix and a generated UUID.
        // The fixture connects only to its private cluster or owned test schema.
        var suffix = Guid.NewGuid().ToString("N");
        var trigger = "quote_test_reject_" + suffix;
        var function = "quote_test_reject_owner_" + suffix;
        var createSql = $"""
            CREATE FUNCTION {function}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'Fictional private PostgreSQL insert failure' USING ERRCODE = '23514';
            END;
            $$;
            CREATE TRIGGER {trigger} BEFORE INSERT ON quotes
            FOR EACH ROW WHEN (NEW.owner_id = '{owner:D}'::uuid)
            EXECUTE FUNCTION {function}();
            """;
        var cleanupSql = $"DROP TRIGGER IF EXISTS {trigger} ON quotes; DROP FUNCTION IF EXISTS {function}();";
        await using var setup = Context();
        try
        {
            await setup.Database.ExecuteSqlRawAsync(createSql);
            using var factory = new QuoteApiFactory(fixture.ConnectionString, QuoteApiFactory.Customer(owner));
            using var client = factory.CreateClient();

            using var response = await PostAsync(client, Input(seed));

            await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
            Assert.Equal(before, await CountsAsync());
        }
        finally
        {
            await setup.Database.ExecuteSqlRawAsync(cleanupSql);
        }
    }

    private static async Task AssertInvalidWithoutDatabaseAsync(string body)
    {
        using var factory = new QuoteApiFactory(null, QuoteApiFactory.Customer(Guid.NewGuid()));
        using var client = factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/v1/quotes", content);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.StartsWith("https://example.invalid/problems/", problem.RootElement.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("code").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("correlationId").GetString()));
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionStrings", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fictional", body, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertAddress(JsonElement address, Guid zoneId, string street)
    {
        Assert.Equal(street, address.GetProperty("street").GetString());
        Assert.Equal("Fictional city", address.GetProperty("city").GetString());
        Assert.Equal("00000", address.GetProperty("postalCode").GetString());
        Assert.Equal(zoneId, address.GetProperty("zoneId").GetGuid());
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject input) =>
        client.PostAsync("/api/v1/quotes", new StringContent(input.ToJsonString(), Encoding.UTF8, "application/json"));

    private static async Task<(Guid Id, string Fingerprint)> QuoteResultAsync(HttpClient client, string body)
    {
        using var response = await client.PostAsync("/api/v1/quotes", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (result.RootElement.GetProperty("quoteId").GetGuid(), result.RootElement.GetProperty("fingerprint").GetString()!);
    }

    private static JsonObject Input(Seed seed) => Input(seed.Pickup.Id, seed.Delivery.Id);

    private static JsonObject Input(Guid pickupZone, Guid deliveryZone) => new()
    {
        ["pickupAddress"] = Address(pickupZone, "Fictional pickup street"),
        ["deliveryAddress"] = Address(deliveryZone, "Fictional delivery street"),
        ["packageDetails"] = new JsonObject { ["weightKg"] = 2.5m, ["size"] = "Small" },
        ["recipient"] = new JsonObject { ["name"] = "Fictional Recipient", ["phoneNumber"] = "0000000000" }
    };

    private static JsonObject Address(Guid zone, string street) => new()
    {
        ["street"] = street,
        ["city"] = "Fictional city",
        ["postalCode"] = "00000",
        ["zoneId"] = zone.ToString()
    };

    private DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>()
        .UseNpgsql(fixture.ConnectionString).Options);

    private async Task<Seed> SeedAsync(decimal perKgRate = 60m)
    {
        var pickup = new DeliveryZone(Guid.NewGuid(), "Quote pickup " + Guid.NewGuid().ToString("N"));
        var delivery = new DeliveryZone(Guid.NewGuid(), "Quote delivery " + Guid.NewGuid().ToString("N"));
        var tier = new PricingTier(Guid.NewGuid(), delivery.Id, ParcelSize.Small, "quote-test-v1", 450m, perKgRate);
        await using var context = Context();
        context.Zones.AddRange(pickup, delivery);
        context.PricingTiers.AddRange(tier,
            new PricingTier(Guid.NewGuid(), pickup.Id, ParcelSize.Small, "pickup-only-v1", 900m, 90m));
        await context.SaveChangesAsync();
        return new Seed(pickup, delivery, tier);
    }

    private async Task<Counts> CountsAsync()
    {
        await using var context = Context();
        return new Counts(await context.Quotes.CountAsync(), await context.Parcels.CountAsync(),
            await context.PriceSnapshots.CountAsync(), await context.BookingHistory.CountAsync(),
            await context.IdempotencyRecords.CountAsync(), await context.OutboxMessages.CountAsync());
    }

    private sealed record Seed(DeliveryZone Pickup, DeliveryZone Delivery, PricingTier Tier);
    private sealed record Counts(int Quotes, int Parcels, int Snapshots, int History, int Idempotency, int Outbox);

    private sealed class UnavailableQuoteStore(PricingTier tier, bool failSave) : IQuoteStore
    {
        public Task<QuotePricingData> ReadPricingAsync(Guid pickupZoneId, Guid deliveryZoneId,
            ParcelSize size, CancellationToken cancellationToken)
        {
            if (!failSave)
            {
                throw new QuoteStoreUnavailableException("Private fictional provider failure must not be returned.");
            }

            return Task.FromResult(new QuotePricingData(true, true, [tier]));
        }

        public Task SaveAsync(Quote quote, CancellationToken cancellationToken) =>
            throw new QuoteStoreUnavailableException("Private fictional provider failure must not be returned.");
    }
}
