namespace SwiftDrop.Delivery.Api.Application.Quotes;

public sealed record BookableZone(Guid Id, string Name);

public interface IBookingZoneCatalog
{
    Task<IReadOnlyList<BookableZone>> ReadActiveAsync(CancellationToken cancellationToken);
}
