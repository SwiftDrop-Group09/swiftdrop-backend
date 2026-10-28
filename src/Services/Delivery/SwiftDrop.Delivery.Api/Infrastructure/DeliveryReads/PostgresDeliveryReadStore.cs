using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;

namespace SwiftDrop.Delivery.Api.Infrastructure.DeliveryReads;

public sealed class PostgresDeliveryReadStore(Func<DeliveryDbContext> contextFactory) : IDeliveryReadStore
{
    private static readonly ParcelStatus[] ActiveStatuses =
        [ParcelStatus.Assigned, ParcelStatus.PickedUp, ParcelStatus.InTransit, ParcelStatus.OutForDelivery];
    private static readonly JsonSerializerOptions PrivateJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false
    };

    public async Task<AssignedParcelPage> GetAssignmentsAsync(Guid courierId, AssignmentQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var database = contextFactory();
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var authorized = database.Parcels.AsNoTracking().Where(parcel => ActiveStatuses.Contains(parcel.Status)
                && database.Assignments.Any(assignment => assignment.ParcelId == parcel.Id
                    && assignment.CourierId == courierId && assignment.Status == AssignmentStatus.Active
                    && (assignment.ServiceDate == query.Date || (query.IncludeOverdue && assignment.ServiceDate < query.Date))));
            var count = await authorized.CountAsync(cancellationToken);
            var rows = await Project(database, authorized).OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Id)
                .Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArrayAsync(cancellationToken);
            var items = rows.Select(row => new AssignedParcelResponse(row.Id, row.TrackingNumber, row.Status.ToString(), row.Version,
                Price(row), row.CreatedAt, Address(row.PickupAddress), Address(row.DeliveryAddress),
                new ReadPackageDetails(row.WeightKg, row.Size.ToString()), Recipient(row.Recipient),
                row.ServiceDate ?? throw new InvalidOperationException("A current assignment is required."), row.ServiceDate < query.Date)).ToArray();
            await transaction.CommitAsync(cancellationToken);
            return new AssignedParcelPage(items, count, query.Page, query.PageSize);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new DeliveryReadUnavailableException("Delivery reads are unavailable.", exception);
        }
    }

    public async Task<ParcelDetailResponse?> GetParcelAsync(ParcelReadScope scope, Guid parcelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var database = contextFactory();
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            // Resource authorization is part of the read predicate, not a separate lookup.
            var authorized = database.Parcels.AsNoTracking().Where(parcel => parcel.Id == parcelId
                && (scope.IsOperations || (scope.IsCustomer && parcel.CustomerId == scope.UserId)
                    || (scope.IsCourier && ActiveStatuses.Contains(parcel.Status)
                        && database.Assignments.Any(assignment => assignment.ParcelId == parcel.Id
                            && assignment.CourierId == scope.UserId && assignment.Status == AssignmentStatus.Active))));
            var row = await Project(database, authorized).SingleOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            var history = await database.BookingHistory.AsNoTracking().Where(item => item.ParcelId == parcelId)
                .OrderBy(item => item.Timestamp).ThenBy(item => item.Id)
                .Select(item => new HistoryRow(item.FromStatus, item.ToStatus, item.ChangeReason, item.DeliveryNote, item.Timestamp))
                .ToArrayAsync(cancellationToken);
            var result = new ParcelDetailResponse(row.Id, row.TrackingNumber, row.Status.ToString(), row.Version,
                Price(row), row.CreatedAt, Address(row.PickupAddress), Address(row.DeliveryAddress),
                new ReadPackageDetails(row.WeightKg, row.Size.ToString()), Recipient(row.Recipient),
                row.ServiceDate is { } date && row.AssignmentStatus is { } status && row.AssignedAt is { } assigned
                    ? new CurrentAssignmentResponse(status.ToString(), date, assigned, row.PickedUpAt, row.DeliveredAt) : null,
                history.Select(item => new PrivateHistoryResponse(item.FromStatus?.ToString(), item.ToStatus.ToString(),
                    item.ChangeReason, item.DeliveryNote, item.Timestamp)).ToArray());
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new DeliveryReadUnavailableException("Delivery reads are unavailable.", exception);
        }
    }

    private static IQueryable<ParcelRow> Project(DeliveryDbContext database, IQueryable<Parcel> authorized) =>
        from parcel in authorized
        join snapshot in database.PriceSnapshots.AsNoTracking() on parcel.Id equals snapshot.ParcelId
        from assignment in database.Assignments.AsNoTracking()
            .Where(item => item.ParcelId == parcel.Id && item.Status == AssignmentStatus.Active).DefaultIfEmpty()
        select new ParcelRow
        {
            Id = parcel.Id,
            TrackingNumber = parcel.TrackingNumber,
            Status = parcel.Status,
            Version = parcel.Version,
            Size = parcel.Size,
            WeightKg = parcel.WeightKg,
            PickupAddress = parcel.PickupAddress,
            DeliveryAddress = parcel.DropoffAddress,
            Recipient = parcel.DropoffContact,
            Total = snapshot.Total,
            Currency = snapshot.Currency,
            PricingVersion = snapshot.PricingVersion,
            CreatedAt = parcel.CreatedAt,
            ServiceDate = (DateOnly?)assignment.ServiceDate,
            AssignmentStatus = (AssignmentStatus?)assignment.Status,
            AssignedAt = (DateTimeOffset?)assignment.AssignedAt,
            PickedUpAt = assignment.PickedUpAt,
            DeliveredAt = assignment.DeliveredAt
        };

    private static BookingPriceSnapshotResponse Price(ParcelRow row) => new(row.Total, row.Currency, row.PricingVersion);

    private static NormalizedAddress Address(string json)
    {
        var value = JsonSerializer.Deserialize<NormalizedAddress>(json, PrivateJson);
        return value is not null && value.ZoneId != Guid.Empty && !string.IsNullOrWhiteSpace(value.Street)
            && !string.IsNullOrWhiteSpace(value.City) && !string.IsNullOrWhiteSpace(value.PostalCode)
            ? value : throw new InvalidOperationException("Stored address data is invalid.");
    }

    private static NormalizedRecipient Recipient(string json)
    {
        var value = JsonSerializer.Deserialize<NormalizedRecipient>(json, PrivateJson);
        return value is not null && !string.IsNullOrWhiteSpace(value.Name) && !string.IsNullOrWhiteSpace(value.PhoneNumber)
            ? value : throw new InvalidOperationException("Stored recipient data is invalid.");
    }

    private static bool IsReadFailure(Exception exception) => exception is
        NpgsqlException or DbUpdateException or TimeoutException or InvalidOperationException or ArgumentException or JsonException;

    // Member bindings allow EF to order/page this projection entirely in SQL.
    private sealed class ParcelRow
    {
        public Guid Id { get; init; }
        public string TrackingNumber { get; init; } = null!;
        public ParcelStatus Status { get; init; }
        public long Version { get; init; }
        public ParcelSize Size { get; init; }
        public decimal WeightKg { get; init; }
        public string PickupAddress { get; init; } = null!;
        public string DeliveryAddress { get; init; } = null!;
        public string Recipient { get; init; } = null!;
        public decimal Total { get; init; }
        public string Currency { get; init; } = null!;
        public string PricingVersion { get; init; } = null!;
        public DateTimeOffset CreatedAt { get; init; }
        public DateOnly? ServiceDate { get; init; }
        public AssignmentStatus? AssignmentStatus { get; init; }
        public DateTimeOffset? AssignedAt { get; init; }
        public DateTimeOffset? PickedUpAt { get; init; }
        public DateTimeOffset? DeliveredAt { get; init; }
    }
    private sealed record HistoryRow(ParcelStatus? FromStatus, ParcelStatus ToStatus, string? ChangeReason,
        string? DeliveryNote, DateTimeOffset Timestamp);
}
