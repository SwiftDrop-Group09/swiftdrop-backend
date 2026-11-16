using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.CourierDuty;

namespace SwiftDrop.Delivery.Api.Presentation;

public static class CourierDutyEndpoints
{
    public static void MapCourierDuty(this WebApplication app)
    {
        app.MapGet("/api/v1/couriers/me/duty", async (
            HttpContext context,
            ICurrentDeliveryUser actor,
            CourierDutyHandler handler,
            CancellationToken cancellationToken) =>
        {
            if (!actor.IsAuthenticated || actor.UserId is null)
            {
                return Results.Unauthorized();
            }

            if (!actor.IsCourier)
            {
                return Results.Forbid();
            }

            var response = await handler.GetDutyStatusAsync(cancellationToken);
            return response is not null ? Results.Ok(response) : Results.NotFound();
        }).RequireAuthorization().WithName("GetCourierDutyStatus");

        app.MapPut("/api/v1/couriers/me/duty", async (
            SetCourierDutyInput input,
            HttpContext context,
            ICurrentDeliveryUser actor,
            CourierDutyHandler handler,
            CancellationToken cancellationToken) =>
        {
            if (!actor.IsAuthenticated || actor.UserId is null)
            {
                return Results.Unauthorized();
            }

            if (!actor.IsCourier)
            {
                return Results.Forbid();
            }

            var response = await handler.SetDutyAsync(input.OnDuty, cancellationToken);
            return response is not null ? Results.Ok(response) : Results.NotFound();
        }).RequireAuthorization().WithName("SetCourierDutyStatus");
    }
}

