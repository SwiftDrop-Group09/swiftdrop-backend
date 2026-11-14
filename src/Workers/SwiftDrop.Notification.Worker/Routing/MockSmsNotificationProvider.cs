namespace SwiftDrop.Notification.Worker.Routing;

/// <summary>
/// Demonstration-only provider. It creates no external SMS and receives no phone number.
/// Its durable, observable result is recorded by the Worker in NotificationProviderAttempts.
/// </summary>
public sealed class MockSmsNotificationProvider : INotificationProvider
{
    public Task<NotificationProviderResult> SendMockSmsAsync(
        long notificationJobId,
        Guid eventId,
        Guid recipientId,
        string status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (notificationJobId < 1 || eventId == Guid.Empty || recipientId == Guid.Empty ||
            string.IsNullOrWhiteSpace(status))
        {
            return Task.FromResult(new NotificationProviderResult(false, "invalid_mock_sms_request"));
        }

        return Task.FromResult(new NotificationProviderResult(true, null));
    }
}
