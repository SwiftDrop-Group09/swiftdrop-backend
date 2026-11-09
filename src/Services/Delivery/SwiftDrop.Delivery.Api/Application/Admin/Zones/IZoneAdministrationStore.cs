namespace SwiftDrop.Delivery.Api.Application.Admin.Zones;

public interface IZoneAdministrationStore
{
    Task<ZonePage> ListZonesAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<ZoneDto?> GetZoneByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<ZoneResult<ZoneDto>> CreateZoneAsync(ZoneInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<ZoneResult<ZoneDto>> UpdateZoneAsync(Guid id, ZoneInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<ZoneSummaryResponse> GetZoneSummaryAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken);
}

