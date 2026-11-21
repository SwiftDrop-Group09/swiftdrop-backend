namespace SwiftDrop.Contracts.Http.V1;

public sealed record NotificationContextRequest(Guid EventId, Guid ParcelId, long ParcelVersion);

public sealed record NotificationContextResponse(Guid? CustomerUserId);
