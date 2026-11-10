using System.Text.Json;
using System.Text.Json.Serialization;
using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;

namespace SwiftDrop.Merchant.Api.Presentation;

public static class WebhookEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false
    };

    public static void MapWebhookEndpoints(this WebApplication app)
    {
        // 1. POST /api/v1/merchant/webhooks — Approved Merchant owner
        app.MapPost("/api/v1/merchant/webhooks", async (
            HttpContext context,
            CreateWebhookSubscriptionHandler handler,
            CancellationToken ct) =>
        {
            CreateWebhookRequest? request;
            try
            {
                if (!context.Request.HasJsonContentType())
                {
                    return InvalidJson(context);
                }

                request = await context.Request.ReadFromJsonAsync<CreateWebhookRequest>(InputJson, ct);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or NotSupportedException)
            {
                return InvalidJson(context);
            }

            if (request is null)
            {
                return InvalidJson(context);
            }

            var (response, failure) = await handler.HandleAsync(request, ct);
            if (response is not null)
            {
                return Results.Created($"/api/v1/merchant/webhooks/{response.Id}", response);
            }

            return WebhookProblems.Failure(context, failure!);
        }).RequireAuthorization().WithName("CreateWebhookSubscription")
          .Produces<OneTimeWebhookResponse>(201)
          .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409);

        // 2. GET /api/v1/merchant/webhooks — Approved Merchant owner
        app.MapGet("/api/v1/merchant/webhooks", async (
            HttpContext context,
            ListWebhookSubscriptionsHandler handler,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
        {
            var p = page ?? 1;
            var ps = pageSize ?? 20;

            var (response, failure) = await handler.HandleAsync(p, ps, ct);
            if (response is not null)
            {
                return Results.Ok(response);
            }

            return WebhookProblems.Failure(context, failure!);
        }).RequireAuthorization().WithName("ListWebhookSubscriptions")
          .Produces<WebhookPageResponse>(200)
          .ProducesProblem(401).ProducesProblem(403);

        // 3. DELETE /api/v1/merchant/webhooks/{id:guid} — Approved Merchant owner
        app.MapDelete("/api/v1/merchant/webhooks/{id:guid}", async (
            Guid id,
            HttpContext context,
            DeleteWebhookSubscriptionHandler handler,
            CancellationToken ct) =>
        {
            var failure = await handler.HandleAsync(id, ct);
            if (failure is null)
            {
                return Results.NoContent();
            }

            return WebhookProblems.Failure(context, failure);
        }).RequireAuthorization().WithName("DeleteWebhookSubscription")
          .Produces(204)
          .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        // 4. GET /api/v1/merchant/webhooks/{id:guid}/attempts — Approved Merchant owner
        app.MapGet("/api/v1/merchant/webhooks/{id:guid}/attempts", async (
            Guid id,
            HttpContext context,
            ListWebhookAttemptsHandler handler,
            Guid? jobId,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
        {
            var p = page ?? 1;
            var ps = pageSize ?? 20;

            var (response, failure) = await handler.HandleAsync(id, jobId, p, ps, ct);
            if (response is not null)
            {
                return Results.Ok(response);
            }

            return WebhookProblems.Failure(context, failure!);
        }).RequireAuthorization().WithName("ListWebhookAttempts")
          .Produces<WebhookAttemptPageResponse>(200)
          .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        // 5. POST /api/v1/merchant/webhooks/{id:guid}/replays — Approved Merchant owner
        app.MapPost("/api/v1/merchant/webhooks/{id:guid}/replays", async (
            Guid id,
            HttpContext context,
            ReplayWebhookJobHandler handler,
            CancellationToken ct) =>
        {
            ReplayWebhookRequest? request;
            try
            {
                if (!context.Request.HasJsonContentType())
                {
                    return InvalidJson(context);
                }

                request = await context.Request.ReadFromJsonAsync<ReplayWebhookRequest>(InputJson, ct);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or NotSupportedException)
            {
                return InvalidJson(context);
            }

            if (request is null)
            {
                return InvalidJson(context);
            }

            var (response, failure) = await handler.HandleAsync(id, request, ct);
            if (response is not null)
            {
                return Results.Accepted($"/api/v1/merchant/webhooks/{id}", response);
            }

            return WebhookProblems.Failure(context, failure!);
        }).RequireAuthorization().WithName("ReplayWebhookJob")
          .Produces<WebhookJobResponse>(202)
          .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        // 6. POST /internal/v1/webhook-dispatches — Service JWT (aud=merchant-internal, scope=webhooks:dispatch)
        app.MapPost("/internal/v1/webhook-dispatches", async (
            HttpContext context,
            AcceptStatusChangedDispatchHandler handler,
            CancellationToken ct) =>
        {
            var identity = context.User.Identities.FirstOrDefault(i => i.IsAuthenticated);
            if (identity is null)
            {
                return WebhookProblems.Failure(context, WebhookFailure.Unauthenticated("A verified service identity is required."));
            }

            var audClaims = identity.FindAll("aud").Select(c => c.Value).ToList();
            var hasAudience = audClaims.Any(a => string.Equals(a, "merchant-internal", StringComparison.OrdinalIgnoreCase));

            var scopeClaims = identity.FindAll("scope").Concat(identity.FindAll("scopes")).Select(c => c.Value).ToList();
            var hasScope = scopeClaims.Any(s => s.Split(' ').Contains("webhooks:dispatch", StringComparer.OrdinalIgnoreCase));

            if (!hasAudience || !hasScope)
            {
                return WebhookProblems.Failure(context, WebhookFailure.Forbidden("A service token with aud=merchant-internal and scope=webhooks:dispatch is required."));
            }

            InternalDispatchPayload? payload;
            try
            {
                if (!context.Request.HasJsonContentType())
                {
                    return InvalidJson(context);
                }

                payload = await context.Request.ReadFromJsonAsync<InternalDispatchPayload>(InputJson, ct);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or NotSupportedException)
            {
                return InvalidJson(context);
            }

            if (payload is null)
            {
                return InvalidJson(context);
            }

            var (response, failure) = await handler.HandleAsync(payload, ct);
            if (response is not null)
            {
                return Results.Accepted(null, response);
            }

            return WebhookProblems.Failure(context, failure!);
        }).RequireAuthorization().WithName("DispatchWebhook")
          .Produces<InternalDispatchResponse>(202)
          .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
    }

    private static IResult InvalidJson(HttpContext context) =>
        WebhookProblems.Failure(context, WebhookFailure.InvalidInput("Supply a valid JSON request body."));
}
