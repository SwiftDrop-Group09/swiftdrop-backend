using System.Text.Json;
using System.Text.Json.Serialization;

using SwiftDrop.Merchant.Api.Application.Merchants;

namespace SwiftDrop.Merchant.Api.Presentation;

public static class MerchantEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void MapMerchantEndpoints(this WebApplication app)
    {
        // POST /api/v1/merchants/register — Human JWT; self applicant
        app.MapPost("/api/v1/merchants/register", async (HttpContext context,
            MerchantHandler handler, CancellationToken cancellationToken) =>
        {
            RegisterMerchantInput? input;
            try
            {
                if (!context.Request.HasJsonContentType())
                    return InvalidJson(context);

                input = await context.Request.ReadFromJsonAsync<RegisterMerchantInput>(InputJson, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or NotSupportedException)
            {
                return InvalidJson(context);
            }

            var (response, failure, errors) = await handler.RegisterAsync(input, cancellationToken);
            if (response is not null) return Results.Created($"/api/v1/merchants/me", response);
            return MerchantProblems.Failure(context, failure!.Value, errors);
        }).RequireAuthorization().WithName("RegisterMerchant")
          .Produces<BusinessResponse>(201)
          .ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);

        // GET /api/v1/merchants/me — Human JWT; own merchant
        app.MapGet("/api/v1/merchants/me", async (HttpContext context,
            MerchantHandler handler, CancellationToken cancellationToken) =>
        {
            var (response, failure, errors) = await handler.GetMeAsync(cancellationToken);
            if (response is not null) return Results.Ok(response);
            return MerchantProblems.Failure(context, failure!.Value, errors);
        }).RequireAuthorization().WithName("GetMerchant")
          .Produces<BusinessResponse>(200)
          .ProducesProblem(401).ProducesProblem(404);

        // GET /api/v1/merchants — Human JWT; Admin only
        app.MapGet("/api/v1/merchants", async (HttpContext context,
            MerchantHandler handler, CancellationToken cancellationToken) =>
        {
            var (response, failure, errors) = await handler.ListAllAsync(cancellationToken);
            if (response is not null) return Results.Ok(response);
            return MerchantProblems.Failure(context, failure!.Value, errors);
        }).RequireAuthorization().WithName("ListMerchants")
          .Produces<IReadOnlyList<BusinessResponse>>(200)
          .ProducesProblem(401).ProducesProblem(403);

        // PUT /api/v1/merchants/{id}/status — Human JWT; Admin only
        app.MapPut("/api/v1/merchants/{id:guid}/status", async (Guid id, HttpContext context,
            MerchantHandler handler, CancellationToken cancellationToken) =>
        {
            SetMerchantStatusInput? input;
            try
            {
                if (!context.Request.HasJsonContentType())
                    return InvalidJson(context);

                input = await context.Request.ReadFromJsonAsync<SetMerchantStatusInput>(InputJson, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or NotSupportedException)
            {
                return InvalidJson(context);
            }

            var (response, failure, errors) = await handler.SetStatusAsync(id, input, cancellationToken);
            if (response is not null) return Results.Ok(response);
            return MerchantProblems.Failure(context, failure!.Value, errors);
        }).RequireAuthorization().WithName("SetMerchantStatus")
          .Produces<BusinessResponse>(200)
          .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
          .ProducesProblem(404).ProducesProblem(409);

        // POST /api/v1/merchants/api-keys — Human JWT; approved Merchant owner
        app.MapPost("/api/v1/merchants/api-keys", async (HttpContext context,
            MerchantHandler handler, CancellationToken cancellationToken) =>
        {
            CreateApiKeyInput? input;
            try
            {
                if (!context.Request.HasJsonContentType())
                    return InvalidJson(context);

                input = await context.Request.ReadFromJsonAsync<CreateApiKeyInput>(InputJson, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or NotSupportedException)
            {
                return InvalidJson(context);
            }

            var (response, failure, errors) = await handler.CreateApiKeyAsync(input, cancellationToken);
            if (response is not null) return Results.Created($"/api/v1/merchants/api-keys/{response.KeyId}", response);
            return MerchantProblems.Failure(context, failure!.Value, errors);
        }).RequireAuthorization().WithName("CreateApiKey")
          .Produces<OneTimeApiKeyResponse>(201)
          .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        // GET /api/v1/merchants/api-keys — Human JWT; approved Merchant owner
        app.MapGet("/api/v1/merchants/api-keys", async (HttpContext context,
            MerchantHandler handler, CancellationToken cancellationToken) =>
        {
            var (response, failure, errors) = await handler.ListApiKeysAsync(cancellationToken);
            if (response is not null) return Results.Ok(response);
            return MerchantProblems.Failure(context, failure!.Value, errors);
        }).RequireAuthorization().WithName("ListApiKeys")
          .Produces<IReadOnlyList<ApiKeyResponse>>(200)
          .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        // DELETE /api/v1/merchants/api-keys/{keyId} — Human JWT; own approved Merchant
        app.MapDelete("/api/v1/merchants/api-keys/{keyId:guid}", async (Guid keyId, HttpContext context,
            MerchantHandler handler, CancellationToken cancellationToken) =>
        {
            var (success, failure, errors) = await handler.RevokeApiKeyAsync(keyId, cancellationToken);
            if (success) return Results.NoContent();
            return MerchantProblems.Failure(context, failure!.Value, errors);
        }).RequireAuthorization().WithName("RevokeApiKey")
          .Produces(204)
          .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
    }

    private static IResult InvalidJson(HttpContext context) =>
        MerchantProblems.Failure(context, MerchantFailure.InvalidInput,
            new Dictionary<string, string[]>
            {
                ["body"] = ["Supply a valid JSON request body."]
            });
}
