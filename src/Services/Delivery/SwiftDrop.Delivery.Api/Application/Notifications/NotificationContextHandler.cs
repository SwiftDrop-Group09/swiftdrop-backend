using SwiftDrop.Contracts.Http.V1;

namespace SwiftDrop.Delivery.Api.Application.Notifications;

public sealed class NotificationContextHandler(INotificationContextStore store)
{
    public async Task<NotificationContextResponse?> HandleAsync(
        NotificationContextRequest reference,
        CancellationToken cancellationToken)
    {
        if (reference.EventId == Guid.Empty || reference.ParcelId == Guid.Empty || reference.ParcelVersion < 1)
        {
            return null;
        }

        var resolution = await store.ResolveAsync(reference, cancellationToken);
        return resolution.IsVerifiedEvent
            ? new NotificationContextResponse(resolution.CustomerUserId)
            : null;
    }
}
