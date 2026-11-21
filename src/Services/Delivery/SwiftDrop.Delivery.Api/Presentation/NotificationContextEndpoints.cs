using System.Text.Json;
using SwiftDrop.Contracts.Http.V1;
using SwiftDrop.Delivery.Api.Application.Notifications;
using SwiftDrop.Delivery.Api.Infrastructure.Authentication;

namespace SwiftDrop.Delivery.Api.Presentation;

public static class NotificationContextEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowDuplicateProperties = false
    };

    public static void MapNotificationContext(this WebApplication app)
    {
        app.MapPost("/internal/v1/notification-context", async (
            HttpContext context,
            NotificationContextHandler handler,
            CancellationToken cancellationToken) =>
        {
            var scopes = context.User.FindAll("scope").SelectMany(claim => claim.Value.Split(' '));
            if (!scopes.Contains("notifications:resolve", StringComparer.Ordinal))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "The required service scope is missing.");
            }

            if (!context.Request.HasJsonContentType())
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "A JSON event reference is required.");
            }

            NotificationContextRequest? reference;
            try
            {
                reference = await context.Request.ReadFromJsonAsync<NotificationContextRequest>(InputJson, cancellationToken);
            }
            catch (JsonException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "The event reference is invalid.");
            }

            if (reference is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "The event reference is invalid.");
            }

            var response = await handler.HandleAsync(reference, cancellationToken);
            return response is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound,
                    title: "The status event was not found for this parcel version.")
                : Results.Ok(response);
        })
        .RequireAuthorization(DeliveryIdentityAuthentication.WorkerServicePolicy)
        .WithName("NotificationContext")
        .Produces<NotificationContextResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
