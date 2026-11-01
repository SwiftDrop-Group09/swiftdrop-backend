using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Npgsql;

using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Bookings;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Bookings.Reliability;

[Collection("Postgres")]
public sealed class BookingContentionTests(PostgresFixture fixture)
{
    private static readonly TimeSpan CompletionLimit = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task TwentyIdenticalRequestsReturnOneOriginalBookingAndOneLogicalEvent()
    {
        await using var scenario = await BookingReliabilityScenario.CreateAsync(fixture);
        var before = await scenario.CountsAsync();
        var key = NewKey();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Enumerable.Range(0, 20).Select(async _ =>
        {
            await gate.Task;
            return await scenario.ConfirmAsync(key);
        }).ToArray();
        gate.SetResult();
        var responses = await Task.WhenAll(pending).WaitAsync(CompletionLimit);
        try
        {
            Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);
            for (var index = 0; index < responses.Length; index++)
            {
                responses[index] = await ResolveContentionAsync(scenario, responses[index], key);
                Assert.Equal(HttpStatusCode.Created, responses[index].StatusCode);
            }

            var original = await responses[0].Content.ReadAsStringAsync();
            foreach (var response in responses)
            {
                Assert.Equal(original, await response.Content.ReadAsStringAsync());
                Assert.Equal(responses[0].Headers.Location, response.Headers.Location);
            }

            Assert.Equal(before.Booked(), await scenario.CountsAsync());
            var graph = await scenario.GraphAsync(key);
            Assert.Equal(original, graph.Response.Body);
            Assert.Equal(graph.Parcel.Id, graph.Quote.ConsumedByParcelId);
            Assert.Null(graph.Outbox.PublishedAt);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task TwentyDifferentKeysCannotConsumeTheSameQuoteTwice()
    {
        await using var scenario = await BookingReliabilityScenario.CreateAsync(fixture);
        var before = await scenario.CountsAsync();
        var keys = Enumerable.Range(0, 20).Select(_ => NewKey()).ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = keys.Select(async key =>
        {
            await gate.Task;
            return await scenario.ConfirmAsync(key);
        }).ToArray();
        gate.SetResult();
        var responses = await Task.WhenAll(pending).WaitAsync(CompletionLimit);
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            for (var index = 0; index < responses.Length; index++)
            {
                if (responses[index].StatusCode == HttpStatusCode.Created) continue;
                responses[index] = await ResolveContentionAsync(scenario, responses[index], keys[index]);
                await AssertProblemAsync(responses[index], "quote_consumed");
            }

            Assert.Equal(before.Booked(), await scenario.CountsAsync());
            var winner = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.Created);
            var graph = await scenario.GraphAsync(keys[winner]);
            Assert.Equal(graph.Parcel.Id, graph.Quote.ConsumedByParcelId);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentDifferentValidQuotesWithTheSameKeyHaveOneWinnerAndOneConflict()
    {
        await using var scenario = await BookingReliabilityScenario.CreateAsync(fixture);
        var secondQuote = await CreateAnotherQuoteAsync(scenario);
        var before = await scenario.CountsAsync();
        var key = NewKey();
        var quotes = new[] { scenario.Quote, secondQuote };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = quotes.Select(async quote =>
        {
            await gate.Task;
            return await scenario.ConfirmAsync(key, quote.QuoteId, quote.Total);
        }).ToArray();
        gate.SetResult();
        var responses = await Task.WhenAll(pending).WaitAsync(CompletionLimit);
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            var loser = Array.FindIndex(responses, response => response.StatusCode != HttpStatusCode.Created);
            responses[loser] = await ResolveContentionAsync(scenario, responses[loser], key,
                quotes[loser].QuoteId, quotes[loser].Total);
            await AssertProblemAsync(responses[loser], "idempotency_conflict");
            Assert.Equal(before.Booked(), await scenario.CountsAsync());
            await using var context = scenario.OpenContext();
            Assert.Null((await context.Quotes.SingleAsync(quote => quote.Id == quotes[loser].QuoteId)).ConsumedByParcelId);
            var winner = 1 - loser;
            using var replay = await scenario.ConfirmAsync(key, quotes[winner].QuoteId, quotes[winner].Total);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(await responses[winner].Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
            Assert.Equal(responses[winner].Headers.Location, replay.Headers.Location);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task QuoteLockTimeoutReturnsRetryableConflictAndRollsBackTheNewReservation()
    {
        await using var scenario = await BookingReliabilityScenario.CreateAsync(fixture);
        var before = await scenario.CountsAsync();
        var key = NewKey();
        await using (var connection = await scenario.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await LockQuoteAsync(connection, transaction, scenario.Quote.QuoteId);
            var pending = scenario.ConfirmAsync(key);
            await scenario.WaitForBlockedRequestAsync();
            using var response = await pending.WaitAsync(CompletionLimit);
            await AssertProblemAsync(response, "operation_in_progress");
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
            Assert.Equal(before, await scenario.CountsAsync());
            await transaction.RollbackAsync();
        }

        using var recovered = await scenario.ConfirmAsync(key);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        Assert.Equal(before.Booked(), await scenario.CountsAsync());
    }

    [Fact]
    public async Task UncommittedIdenticalKeyTimesOutWithoutWritesAndRecoversAfterRollback()
    {
        await using var scenario = await BookingReliabilityScenario.CreateAsync(fixture);
        var before = await scenario.CountsAsync();
        var key = NewKey();
        var confirmation = new ConfirmInput(scenario.Quote.QuoteId.ToString("D"), scenario.Quote.Total);
        var normalized = BookingInputValidator.Validate(confirmation, key).Input!;
        var reservation = new IdempotencyRecord(Guid.NewGuid(), scenario.OwnerId.ToString("D"),
            "CreateParcel", key, normalized.Fingerprint, scenario.Clock.UtcNow);
        await using (var connection = await scenario.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO idempotency_keys
                    (id, principal_id, operation, key, request_fingerprint, created_at, expires_at)
                VALUES (@id, @principal, @operation, @key, @fingerprint, @created, @expires)
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", reservation.Id);
            insert.Parameters.AddWithValue("principal", reservation.PrincipalId);
            insert.Parameters.AddWithValue("operation", reservation.Operation);
            insert.Parameters.AddWithValue("key", reservation.Key);
            insert.Parameters.AddWithValue("fingerprint", reservation.RequestFingerprint);
            insert.Parameters.AddWithValue("created", reservation.CreatedAt);
            insert.Parameters.AddWithValue("expires", reservation.ExpiresAt);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            var pending = scenario.ConfirmAsync(key);
            await scenario.WaitForBlockedRequestAsync();
            using var response = await pending.WaitAsync(CompletionLimit);
            await AssertProblemAsync(response, "operation_in_progress");
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
            Assert.Equal(before, await scenario.CountsAsync());
            await transaction.RollbackAsync();
        }

        using var recovered = await scenario.ConfirmAsync(key);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        Assert.Equal(before.Booked(), await scenario.CountsAsync());
    }

    [Fact]
    public async Task CancellationDuringTheActualQuoteLockWaitDisposesAndRollsBackBeforeRetry()
    {
        await using var scenario = await BookingReliabilityScenario.CreateAsync(fixture);
        var before = await scenario.CountsAsync();
        var key = NewKey();
        await using var context = scenario.OpenContext();
        var handler = new CustomerBookingHandler(new CurrentCustomer(scenario.OwnerId),
            new PostgresBookingStore(() => context), new CryptographicTrackingNumberGenerator(), scenario.Clock);
        using var cancellation = new CancellationTokenSource();
        await using (var connection = await scenario.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await LockQuoteAsync(connection, transaction, scenario.Quote.QuoteId);
            var pending = handler.ExecuteAsync(new(scenario.Quote.QuoteId.ToString("D"), scenario.Quote.Total),
                key, "cancelled-contention", cancellation.Token);
            await scenario.WaitForBlockedRequestAsync();
            cancellation.Cancel();
            // Await the handler, including its asynchronous transaction disposal;
            // HttpClient cancellation alone would not prove server rollback finished.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(CompletionLimit));
            Assert.Empty(context.ChangeTracker.Entries());
            Assert.Equal(before, await scenario.CountsAsync());
            await transaction.RollbackAsync();
        }

        using var recovered = await scenario.ConfirmAsync(key);
        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        Assert.Equal(before.Booked(), await scenario.CountsAsync());
    }

    [Fact]
    public async Task QuoteExpiryIsCheckedAfterAnObservedDatabaseLockWait()
    {
        await using var scenario = await BookingReliabilityScenario.CreateAsync(fixture);
        var before = await scenario.CountsAsync();
        var key = NewKey();
        await using (var connection = await scenario.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await LockQuoteAsync(connection, transaction, scenario.Quote.QuoteId);
            var pending = scenario.ConfirmAsync(key);
            await scenario.WaitForBlockedRequestAsync();
            scenario.Clock.UtcNow = scenario.Quote.ExpiresAt;
            await transaction.RollbackAsync();
            using var response = await pending.WaitAsync(CompletionLimit);
            await AssertProblemAsync(response, "quote_expired");
        }

        Assert.Equal(before, await scenario.CountsAsync());
        await using var context = scenario.OpenContext();
        Assert.Null((await context.Quotes.SingleAsync(quote => quote.Id == scenario.Quote.QuoteId)).ConsumedByParcelId);
    }

    private static async Task<HttpResponseMessage> ResolveContentionAsync(BookingReliabilityScenario scenario,
        HttpResponseMessage response, string key, Guid? quoteId = null, decimal? total = null)
    {
        for (var attempt = 0; attempt < 3 && response.StatusCode == HttpStatusCode.Conflict; attempt++)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (body.RootElement.GetProperty("code").GetString() != "operation_in_progress") return response;
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
            response.Dispose();
            // All parallel responses have completed before recovery begins, so the
            // winning transaction has released its locks without a guessed delay.
            response = await scenario.ConfirmAsync(key, quoteId, total).WaitAsync(CompletionLimit);
        }

        return response;
    }

    private static async Task<QuoteResponse> CreateAnotherQuoteAsync(BookingReliabilityScenario scenario)
    {
        var input = new QuoteInput(
            new("Fictional pickup", "Fictional city", "00000", scenario.Pickup.Id.ToString("D")),
            new("Fictional delivery", "Fictional city", "00000", scenario.Delivery.Id.ToString("D")),
            new(2.5m, "Small"), new("Fictional recipient", "0000000000"));
        using var response = await scenario.Client.PostAsJsonAsync("/api/v1/quotes", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<QuoteResponse>())!;
    }

    private static async Task LockQuoteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid quoteId)
    {
        await using var command = new NpgsqlCommand("SELECT id FROM quotes WHERE id = @id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", quoteId);
        Assert.Equal(quoteId, await command.ExecuteScalarAsync());
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(409, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-Correlation-ID")),
            body.RootElement.GetProperty("correlationId").GetString());
    }

    private static string NewKey() => Guid.NewGuid().ToString("D");

    private sealed record CurrentCustomer(Guid? CustomerId) : ICurrentCustomer
    {
        public bool IsAuthenticated => true;
        public bool IsCustomer => true;
    }
}
