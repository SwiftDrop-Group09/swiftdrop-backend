namespace SwiftDrop.Delivery.Api.Application.DeliveryReads;

public sealed class OperationsClock(IConfiguration configuration, TimeProvider timeProvider)
{
    public string TimeZoneId => configuration["Operations:TimeZone"] ?? "Asia/Colombo";

    public DateOnly Today()
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new DeliveryReadUnavailableException("The operations service day is unavailable.", exception);
        }
    }
}
