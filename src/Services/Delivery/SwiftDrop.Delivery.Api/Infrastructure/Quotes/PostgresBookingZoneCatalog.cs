using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.Quotes;

public sealed class PostgresBookingZoneCatalog(Func<DeliveryDbContext> database) : IBookingZoneCatalog
{
    public async Task<IReadOnlyList<BookableZone>> ReadActiveAsync(CancellationToken cancellationToken) =>
        await database().Zones.AsNoTracking().Where(zone => zone.IsActive)
            .OrderBy(zone => zone.Name).ThenBy(zone => zone.Id)
            .Select(zone => new BookableZone(zone.Id, zone.Name)).ToArrayAsync(cancellationToken);
}
