using SwiftDrop.Delivery.Api.Application.Admin.Couriers;

namespace SwiftDrop.Delivery.Api.Presentation;

public static class AdminCourierEndpoints
{
    public static void MapAdminCouriers(this WebApplication app)
    {
        app.MapGet("/api/v1/admin/couriers", async (
            AdminCourierHandler handler,
            CancellationToken cancellationToken) =>
        {
            var couriers = await handler.ListCouriersAsync(cancellationToken);
            return Results.Ok(couriers);
        }).RequireAuthorization(policy => policy.RequireRole("Admin", "Dispatcher")).WithName("ListAdminCouriers");

        app.MapGet("/api/v1/admin/couriers/{id:guid}", async (
            Guid id,
            AdminCourierHandler handler,
            CancellationToken cancellationToken) =>
        {
            var courier = await handler.GetCourierAsync(id, cancellationToken);
            return courier is not null ? Results.Ok(courier) : Results.NotFound();
        }).RequireAuthorization(policy => policy.RequireRole("Admin", "Dispatcher")).WithName("GetAdminCourier");

        app.MapPut("/api/v1/admin/couriers/{id:guid}", async (
            Guid id,
            UpdateCourierProfileInput input,
            AdminCourierHandler handler,
            CancellationToken cancellationToken) =>
        {
            var courier = await handler.UpdateCourierAsync(id, input, cancellationToken);
            return courier is not null ? Results.Ok(courier) : Results.BadRequest();
        }).RequireAuthorization(policy => policy.RequireRole("Admin")).WithName("UpdateAdminCourier");
    }
}

