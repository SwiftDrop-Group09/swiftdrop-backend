using SwiftDrop.Delivery.Api.Application.Admin.Couriers;

namespace SwiftDrop.Delivery.Api.Presentation;

public static class AdminCourierEndpoints
{
    public static void MapAdminCouriers(this WebApplication app)
    {
        static async Task<IResult> ListCouriers(AdminCourierHandler handler, CancellationToken ct)
        {
            var couriers = await handler.ListCouriersAsync(ct);
            return Results.Ok(couriers);
        }

        static async Task<IResult> GetCourier(Guid id, AdminCourierHandler handler, CancellationToken ct)
        {
            var courier = await handler.GetCourierAsync(id, ct);
            return courier is not null ? Results.Ok(courier) : Results.NotFound();
        }

        static async Task<IResult> UpdateCourier(Guid id, UpdateCourierProfileInput input, AdminCourierHandler handler, CancellationToken ct)
        {
            var courier = await handler.UpdateCourierAsync(id, input, ct);
            return courier is not null ? Results.Ok(courier) : Results.BadRequest();
        }

        app.MapGet("/api/v1/admin/couriers", ListCouriers)
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Dispatcher"))
            .WithName("ListAdminCouriers");

        app.MapGet("/api/v1/admin/couriers/{id:guid}", GetCourier)
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Dispatcher"))
            .WithName("GetAdminCourier");

        app.MapPut("/api/v1/admin/couriers/{id:guid}", UpdateCourier)
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Dispatcher"))
            .WithName("UpdateAdminCourier");

        // Alias under /api/v1/couriers/... for seamless gateway proxying
        app.MapGet("/api/v1/couriers/ops", ListCouriers)
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Dispatcher"))
            .WithName("ListCouriersOps");

        app.MapGet("/api/v1/couriers/{id:guid}/ops", GetCourier)
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Dispatcher"))
            .WithName("GetCourierOps");

        app.MapPut("/api/v1/couriers/{id:guid}/ops", UpdateCourier)
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Dispatcher"))
            .WithName("UpdateCourierOps");
    }
}

