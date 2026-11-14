using SwiftDrop.Contracts.Http.V1;

namespace SwiftDrop.Delivery.Api.Application.Notifications;

public sealed record NotificationContextResolution(bool IsVerifiedEvent, Guid? CustomerUserId);

public interface INotificationContextStore
{
    Task<NotificationContextResolution> ResolveAsync(NotificationContextRequest reference, CancellationToken cancellationToken);
}
