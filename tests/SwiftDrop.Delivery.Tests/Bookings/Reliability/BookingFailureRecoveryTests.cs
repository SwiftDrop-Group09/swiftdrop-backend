using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Bookings;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Bookings.Reliability;

[Collection("Postgres")]
public sealed class BookingFailureRecoveryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task LostHttpResponseReplaysCommittedBytesFromAnotherHostWithoutAnotherEvent()
    {
        await using var scenario = await BookingReliabilityScenario.CreateAsync(fixture);
        var key = NewKey();
        var correlation = "lost-response-" + Guid.NewGuid().ToString("N");
        var before = await scenario.CountsAsync();
        using var transport = new LoseCommittedResponseHandler();
        using (var client = scenario.Factory.CreateDefaultClient(transport))
        using (var request = Confirmation(scenario, key))
        {
            request.Headers.Add("X-Correlation-ID", correlation);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        }

        Assert.True(transport.ResponseWasLost);
        Assert.Equal(before.Booked(), await scenario.CountsAsync());
        var committed = await scenario.GraphAsync(key);
        var originalEventId = committed.Outbox.Id;
        var originalPayload = committed.Outbox.Payload;
        Assert.Null(committed.Outbox.PublishedAt);
        using (var payload = JsonDocument.Parse(originalPayload))
        {
            Assert.Equal(correlation, payload.RootElement.GetProperty("correlationId").GetString());
        }

        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddMinutes(11);
        using var freshFactory = scenario.CreateFactory();
        using var retryClient = freshFactory.CreateClient();
        using var retryRequest = Confirmation(scenario, key);
        retryRequest.Headers.Add("X-Correlation-ID", "retry-" + Guid.NewGuid().ToString("N"));
        using var replay = await retryClient.SendAsync(retryRequest);

        await AssertOriginalResponseAsync(committed.Response, replay);
        Assert.Equal(before.Booked(), await scenario.CountsAsync());
        var after = await scenario.GraphAsync(key);
        Assert.Equal(originalEventId, after.Outbox.Id);
        Assert.Equal(originalPayload, after.Outbox.Payload);
        Assert.Equal(committed.Record.Id, after.Record.Id);
        Assert.Equal(committed.Quote.ConsumedByParcelId, after.Quote.ConsumedByParcelId);
    }

    [Fact]
    public async Task LostCommitAcknowledgementReturnsUnavailableThenReplaysCommittedResult()
    {
        var observation = new TransactionObservation { LoseCommitAcknowledgement = true };
        await using var scenario = await BookingReliabilityScenario.CreateAsync(fixture,
            services => ObserveTransactions(services, observation));
        var key = NewKey();
        var before = await scenario.CountsAsync();

        using (var uncertain = await scenario.ConfirmAsync(key))
        {
            await AssertUnavailableAsync(uncertain);
        }

        Assert.Equal(1, observation.SaveCount);
        Assert.Equal(1, observation.CommitAttempts);
        Assert.Equal(1, observation.CommitCompletions);
        Assert.Equal(before.Booked(), await scenario.CountsAsync());
        var committed = await scenario.GraphAsync(key);
        var quoteLoads = observation.QuoteLoads;
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddMinutes(11);
        using var freshFactory = scenario.CreateFactory(services => ObserveTransactions(services, observation));
        using var client = freshFactory.CreateClient();
        using var request = Confirmation(scenario, key);
        using var replay = await client.SendAsync(request);

        await AssertOriginalResponseAsync(committed.Response, replay);
        Assert.Equal(quoteLoads, observation.QuoteLoads);
        Assert.Equal(1, observation.SaveCount);
        Assert.Equal(1, observation.CommitAttempts);
        Assert.Equal(before.Booked(), await scenario.CountsAsync());
        var after = await scenario.GraphAsync(key);
        Assert.Equal(committed.Outbox.Id, after.Outbox.Id);
        Assert.Equal(committed.Outbox.Payload, after.Outbox.Payload);
    }

    [Theory]
    [InlineData("reservation")]
    [InlineData("parcel")]
    [InlineData("snapshot")]
    [InlineData("history")]
    [InlineData("outbox")]
    [InlineData("quote-consumption")]
    [InlineData("response-completion")]
    [InlineData("commit")]
    public async Task PostgreSqlFailureAtEveryAtomicStageRollsBackAndAllowsSameKeyRecovery(string stage)
    {
        var observation = new TransactionObservation();
        await using var scenario = await BookingReliabilityScenario.CreateAsync(fixture,
            services => ObserveTransactions(services, observation));
        var key = NewKey();
        var before = await scenario.CountsAsync();
        await using (var fault = await PostgreSqlFault.InstallAsync(scenario, key, stage))
        {
            using var failed = await scenario.ConfirmAsync(key);

            await AssertUnavailableAsync(failed);
            Assert.Equal(before, await scenario.CountsAsync());
            await using var verification = scenario.OpenContext();
            var quote = await verification.Quotes.FindAsync(scenario.Quote.QuoteId);
            Assert.NotNull(quote);
            Assert.Null(quote.ConsumedByParcelId);
            if (stage == "commit")
            {
                // The real deferred PostgreSQL constraint fires after every
                // SaveChanges write succeeded, when COMMIT is attempted.
                Assert.Equal(1, observation.SaveCount);
                Assert.Equal(1, observation.CommitAttempts);
                Assert.Equal(0, observation.CommitCompletions);
            }
        }

        using var recovered = await scenario.ConfirmAsync(key);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        Assert.Equal(before.Booked(), await scenario.CountsAsync());
        var committed = await scenario.GraphAsync(key);
        await AssertOriginalResponseAsync(committed.Response, recovered);
        Assert.Equal(committed.Parcel.Id, committed.Quote.ConsumedByParcelId);
        Assert.Null(committed.Outbox.PublishedAt);
        using var replay = await scenario.ConfirmAsync(key);
        await AssertOriginalResponseAsync(committed.Response, replay);
        Assert.Equal(before.Booked(), await scenario.CountsAsync());
        var after = await scenario.GraphAsync(key);
        Assert.Equal(committed.Outbox.Id, after.Outbox.Id);
        Assert.Equal(committed.Outbox.Payload, after.Outbox.Payload);
    }

    private static void ObserveTransactions(IServiceCollection services, TransactionObservation observation)
    {
        services.RemoveAll<IBookingStore>();
        services.AddScoped<IBookingStore>(provider => new ObservedBookingStore(
            new PostgresBookingStore(() => provider.GetRequiredService<DeliveryDbContext>()), observation));
    }

    private static HttpRequestMessage Confirmation(BookingReliabilityScenario scenario, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/parcels")
        {
            Content = new StringContent(BookingReliabilityScenario.Body(scenario.Quote.QuoteId, scenario.Quote.Total),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static async Task AssertOriginalResponseAsync(StoredBookingResponse original, HttpResponseMessage response)
    {
        Assert.Equal((HttpStatusCode)original.StatusCode, response.StatusCode);
        Assert.Equal(original.Body, await response.Content.ReadAsStringAsync());
        Assert.Equal(original.Location, response.Headers.Location?.OriginalString);
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("delivery_unavailable", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(503, document.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("correlationId").GetString()));
        Assert.DoesNotContain("Fictional private booking", body);
        Assert.DoesNotContain("23514", body);
        Assert.DoesNotContain("Postgres", body);
        Assert.DoesNotContain("stackTrace", body);
    }

    private static string NewKey() => "recovery-" + Guid.NewGuid().ToString("N");

    private sealed class LoseCommittedResponseHandler : DelegatingHandler
    {
        public bool ResponseWasLost { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            response.Dispose();
            ResponseWasLost = true;
            throw new HttpRequestException("The fictional test transport lost the committed response.");
        }
    }

    private sealed class TransactionObservation
    {
        private int _saveCount;
        private int _commitAttempts;
        private int _commitCompletions;
        private int _quoteLoads;
        private int _acknowledgementLost;

        public bool LoseCommitAcknowledgement { get; init; }
        public int SaveCount => Volatile.Read(ref _saveCount);
        public int CommitAttempts => Volatile.Read(ref _commitAttempts);
        public int CommitCompletions => Volatile.Read(ref _commitCompletions);
        public int QuoteLoads => Volatile.Read(ref _quoteLoads);
        public void Saved() => Interlocked.Increment(ref _saveCount);
        public void CommitAttempted() => Interlocked.Increment(ref _commitAttempts);
        public void Committed() => Interlocked.Increment(ref _commitCompletions);
        public void QuoteLoaded() => Interlocked.Increment(ref _quoteLoads);
        public bool ShouldLoseAcknowledgement() => LoseCommitAcknowledgement
            && Interlocked.Exchange(ref _acknowledgementLost, 1) == 0;
    }

    private sealed class ObservedBookingStore(IBookingStore inner, TransactionObservation observation) : IBookingStore
    {
        public async Task<IBookingTransaction> BeginAsync(CancellationToken cancellationToken) =>
            new ObservedTransaction(await inner.BeginAsync(cancellationToken), observation);
    }

    private sealed class ObservedTransaction(IBookingTransaction inner, TransactionObservation observation)
        : IBookingTransaction
    {
        public Task<BookingReservation> ReserveAsync(Guid ownerId, string key, string fingerprint,
            DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.ReserveAsync(ownerId, key, fingerprint, now, cancellationToken);

        public Task<BookingQuote?> LoadOwnedQuoteForUpdateAsync(Guid ownerId, Guid quoteId,
            CancellationToken cancellationToken)
        {
            observation.QuoteLoaded();
            return inner.LoadOwnedQuoteForUpdateAsync(ownerId, quoteId, cancellationToken);
        }

        public async Task SaveAsync(Quote quote, BookingWriteSet write, IdempotencyRecord reservation,
            CancellationToken cancellationToken)
        {
            await inner.SaveAsync(quote, write, reservation, cancellationToken);
            observation.Saved();
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            observation.CommitAttempted();
            await inner.CommitAsync(cancellationToken);
            observation.Committed();
            if (observation.ShouldLoseAcknowledgement())
            {
                throw new BookingStoreException(BookingStorageFailure.Unavailable);
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class PostgreSqlFault(BookingReliabilityScenario scenario, string trigger, string table,
        string function) : IAsyncDisposable
    {
        public static async Task<PostgreSqlFault> InstallAsync(BookingReliabilityScenario scenario,
            string key, string stage)
        {
            var owner = scenario.OwnerId.ToString("D");
            var (table, operation, predicate) = stage switch
            {
                "reservation" => ("idempotency_keys", "INSERT", $"NEW.principal_id = '{owner}' AND NEW.key = '{key}'"),
                "parcel" => ("parcels", "INSERT", $"NEW.customer_id = '{owner}'::uuid"),
                "snapshot" => ("price_snapshots", "INSERT", OwnedParcel(owner)),
                "history" => ("booking_history", "INSERT", OwnedParcel(owner)),
                "outbox" => ("outbox_messages", "INSERT",
                    $"NEW.payload->>'correlationId' = '{scenario.ApplicationName}'"),
                "quote-consumption" => ("quotes", "UPDATE", $"NEW.owner_id = '{owner}'::uuid AND NEW.consumed_by_parcel IS NOT NULL"),
                "response-completion" or "commit" => ("idempotency_keys", "UPDATE",
                    $"NEW.principal_id = '{owner}' AND NEW.key = '{key}' AND NEW.stored_response IS NOT NULL"),
                _ => throw new ArgumentOutOfRangeException(nameof(stage))
            };
            var suffix = Guid.NewGuid().ToString("N");
            var trigger = "recovery_fail_" + suffix;
            var function = "recovery_fail_" + suffix;
            // Only generated UUIDs, keys and application names are interpolated;
            // these identifiers belong exclusively to this fixture's schema.
            var triggerSql = stage == "commit"
                ? $"CREATE CONSTRAINT TRIGGER {trigger} AFTER UPDATE ON {table} DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION {function}();"
                : $"CREATE TRIGGER {trigger} BEFORE {operation} ON {table} FOR EACH ROW EXECUTE FUNCTION {function}();";
            var sql = $$"""
                CREATE FUNCTION {{function}}() RETURNS trigger LANGUAGE plpgsql AS $failure$
                BEGIN
                    IF {{predicate}} THEN
                        RAISE EXCEPTION 'Fictional private booking write failure' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $failure$;
                {{triggerSql}}
                """;
            await ExecuteAsync(scenario, sql);
            return new(scenario, trigger, table, function);
        }

        public async ValueTask DisposeAsync() => await ExecuteAsync(scenario,
            $"DROP TRIGGER IF EXISTS {trigger} ON {table}; DROP FUNCTION IF EXISTS {function}();");

        private static string OwnedParcel(string owner) =>
            $"EXISTS (SELECT 1 FROM parcels WHERE id = NEW.parcel_id AND customer_id = '{owner}'::uuid)";

        private static async Task ExecuteAsync(BookingReliabilityScenario scenario, string sql)
        {
            await using var connection = await scenario.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
    }
}
