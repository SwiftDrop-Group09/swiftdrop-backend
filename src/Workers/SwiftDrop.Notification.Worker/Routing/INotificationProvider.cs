namespace SwiftDrop.Notification.Worker.Routing;

public interface INotificationProvider
{
    Task<NotificationProviderResult> SendMockSmsAsync(
        long notificationJobId,
        Guid eventId,
        Guid recipientId,
        string status,
        CancellationToken cancellationToken);
}

public sealed record NotificationProviderResult(bool Succeeded, string? FailureCode);
