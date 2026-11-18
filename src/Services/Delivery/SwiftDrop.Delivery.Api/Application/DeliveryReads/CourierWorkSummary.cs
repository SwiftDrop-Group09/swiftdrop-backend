using System.Globalization;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.DeliveryReads;

public sealed record CourierSummaryInput(string? Date = null, string? IncludeOverdue = null,
    string? OverdueOnly = null, string? Search = null, bool InvalidParameters = false);
public sealed record CourierStageCounts(int Assigned, int PickedUp, int InTransit, int OutForDelivery,
    int Delivered, int Failed)
{
    public int Active => Assigned + PickedUp + InTransit + OutForDelivery;
    public int Completed => Delivered + Failed;
    public int Total => Active + Completed;
}
public sealed record CourierWorkSummary(DateOnly ServiceDate, string TimeZone, CourierStageCounts Day,
    CourierStageCounts Queues, int OverdueCount);

public interface ICourierSummaryStore
{
    Task<CourierWorkSummary> GetSummaryAsync(Guid courierId, AssignmentQuery query, CancellationToken cancellationToken);
}

public sealed class CourierSummaryHandler(ICurrentDeliveryUser user, ICourierSummaryStore store, OperationsClock clock)
{
    public async Task<DeliveryReadResult<CourierWorkSummary>> ExecuteAsync(CourierSummaryInput input,
        CancellationToken cancellationToken = default)
    {
        if (!user.IsAuthenticated || user.UserId is not { } actor || actor == Guid.Empty)
            return new(null, DeliveryReadFailure.Unauthenticated);
        if (!user.IsCourier) return new(null, DeliveryReadFailure.Forbidden);
        var errors = new Dictionary<string, string[]>();
        if (input.InvalidParameters) errors["query"] = ["Supply only one value for each documented query parameter."];
        var date = CourierReadValidation.Date(input.Date, errors);
        var search = CourierReadValidation.Search(input.Search, errors);
        var includeOverdue = true;
        var overdueOnly = false;
        if (input.IncludeOverdue is not null && !bool.TryParse(input.IncludeOverdue, out includeOverdue))
            errors["includeOverdue"] = ["Use true or false."];
        if (input.OverdueOnly is not null && !bool.TryParse(input.OverdueOnly, out overdueOnly))
            errors["overdueOnly"] = ["Use true or false."];
        if (errors.Count != 0) return new(null, DeliveryReadFailure.InvalidInput, errors);
        try
        {
            var summary = await store.GetSummaryAsync(actor, new(date ?? clock.Today(), includeOverdue, 1, 20,
                Search: search, OverdueOnly: overdueOnly), cancellationToken);
            return new(summary with { TimeZone = clock.TimeZoneId }, null);
        }
        catch (DeliveryReadUnavailableException) { return new(null, DeliveryReadFailure.Unavailable); }
    }
}

internal static class CourierReadValidation
{
    public static DateOnly? Date(string? value, Dictionary<string, string[]> errors)
    {
        if (value is null) return null;
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        errors["date"] = ["Use a valid YYYY-MM-DD service date."];
        return null;
    }

    public static ParcelStatus? Status(string? value, bool terminal, Dictionary<string, string[]> errors)
    {
        if (value is null) return null;
        if (Enum.TryParse<ParcelStatus>(value, false, out var status) && Enum.GetName(status) == value
            && (terminal ? status is ParcelStatus.Delivered or ParcelStatus.Failed
                : status is ParcelStatus.Assigned or ParcelStatus.PickedUp or ParcelStatus.InTransit or ParcelStatus.OutForDelivery))
            return status;
        errors["status"] = [terminal ? "Use Delivered or Failed." : "Use Assigned, PickedUp, InTransit or OutForDelivery."];
        return null;
    }

    public static string? Search(string? value, Dictionary<string, string[]> errors)
    {
        if (value is null) return null;
        if (value.Length > 100 || value.Any(char.IsControl)) errors["search"] = ["Use at most 100 characters without control characters."];
        return value.Trim() is { Length: > 0 } text ? text : null;
    }
}
