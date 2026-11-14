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

public sealed class PostgresDeliveryReadStore(Func<DeliveryDbContext> contextFactory)
    : IDeliveryReadStore, ICourierHistoryStore, ICustomerDeliveryReadStore
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

    public async Task<CourierHistoryPage> GetHistoryAsync(Guid courierId, DeliveryPageQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var database = contextFactory();
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
            // Historical terminal ownership is distinct from the current Active slot.
            // A Reassigned row cannot disclose its replacement's subsequent outcome.
            // EXISTS also avoids duplicates when a Courier was assigned more than once.
            var authorized = database.Parcels.AsNoTracking().Where(parcel => database.Assignments.Any(assignment =>
                assignment.ParcelId == parcel.Id && assignment.CourierId == courierId
                && ((assignment.Status == AssignmentStatus.Delivered && parcel.Status == ParcelStatus.Delivered)
                    || (assignment.Status == AssignmentStatus.Failed && parcel.Status == ParcelStatus.Failed))));
            var count = await authorized.CountAsync(cancellationToken);
            var page = from parcel in authorized
                       join snapshot in database.PriceSnapshots.AsNoTracking() on parcel.Id equals snapshot.ParcelId
                       orderby parcel.CreatedAt descending, parcel.Id descending
                       select new
                       {
                           parcel.Id, parcel.TrackingNumber, parcel.Status, parcel.Version, parcel.CreatedAt,
                           snapshot.Total, snapshot.Currency, snapshot.PricingVersion
                       };
            var rows = await page.Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize).ToArrayAsync(cancellationToken);
            var items = rows.Select(row => new ParcelBookingResponse(row.Id, row.TrackingNumber, row.Status.ToString(), row.Version,
                new BookingPriceSnapshotResponse(row.Total, row.Currency, row.PricingVersion), row.CreatedAt)).ToArray();
            await transaction.CommitAsync(cancellationToken);
            return new(items, count, query.Page, query.PageSize);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        { throw new DeliveryReadUnavailableException("Courier history is unavailable.", exception); }
    }

    public async Task<CustomerParcelPage> ListParcelsAsync(ParcelReadScope scope, CustomerParcelQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var database = contextFactory();
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

            var authorized = database.Parcels.AsNoTracking().Where(parcel =>
                scope.IsOperations || (scope.IsCustomer && parcel.CustomerId == scope.UserId));

            if (query.Status is { } status)
                authorized = authorized.Where(parcel => parcel.Status == status);
            if (query.ZoneId is { } zoneId)
                authorized = authorized.Where(parcel => parcel.ZoneId == zoneId);
            if (query.From is { } from)
                authorized = authorized.Where(parcel => parcel.CreatedAt >= from);
            if (query.To is { } to)
                authorized = authorized.Where(parcel => parcel.CreatedAt < to);
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var term = query.Search.ToLowerInvariant();
                authorized = authorized.Where(parcel =>
                    parcel.TrackingNumber.ToLower().Contains(term)
                    || parcel.DropoffContact.ToLower().Contains(term));
            }

            var count = await authorized.CountAsync(cancellationToken);
            var page = from parcel in authorized
                       join snapshot in database.PriceSnapshots.AsNoTracking() on parcel.Id equals snapshot.ParcelId
                       orderby parcel.CreatedAt descending, parcel.Id descending
                       select new
                       {
                           parcel.Id,
                           parcel.TrackingNumber,
                           parcel.Status,
                           parcel.Version,
                           parcel.Size,
                           parcel.WeightKg,
                           parcel.PickupAddress,
                           parcel.DropoffAddress,
                           parcel.DropoffContact,
                           parcel.CreatedAt,
                           parcel.UpdatedAt,
                           snapshot.Total,
                           snapshot.Currency,
                           snapshot.PricingVersion
                       };
            var rows = await page.Skip(checked((query.Page - 1) * query.PageSize)).Take(query.PageSize)
                .ToArrayAsync(cancellationToken);

            var items = rows.Select(row =>
            {
                var pickup = Address(row.PickupAddress);
                var delivery = Address(row.DropoffAddress);
                var recipient = Recipient(row.DropoffContact);
                return new CustomerParcelListItem(
                    row.Id,
                    row.TrackingNumber,
                    row.Status.ToString(),
                    row.Version,
                    new BookingPriceSnapshotResponse(row.Total, row.Currency, row.PricingVersion),
                    row.CreatedAt,
                    row.UpdatedAt,
                    pickup.City,
                    delivery.City,
                    recipient.Name,
                    new ReadPackageDetails(row.WeightKg, row.Size.ToString()),
                    row.Status == ParcelStatus.Delivered);
            }).ToArray();

            await transaction.CommitAsync(cancellationToken);
            return new CustomerParcelPage(items, count, query.Page, query.PageSize);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new DeliveryReadUnavailableException("Customer parcel history is unavailable.", exception);
        }
    }

    public async Task<PublicTrackingSummary?> GetPublicTrackingAsync(string trackingNumber, CancellationToken cancellationToken)
    {
        try
        {
            var database = contextFactory();
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

            var parcel = await database.Parcels.AsNoTracking()
                .Where(item => item.TrackingNumber == trackingNumber)
                .Select(item => new
                {
                    item.Id,
                    item.TrackingNumber,
                    item.Status,
                    item.PickupAddress,
                    item.DropoffAddress,
                    item.UpdatedAt
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (parcel is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            var history = await database.BookingHistory.AsNoTracking()
                .Where(item => item.ParcelId == parcel.Id)
                .OrderBy(item => item.Timestamp)
                .ThenBy(item => item.Id)
                .Select(item => new { item.ToStatus, item.Timestamp })
                .ToArrayAsync(cancellationToken);

            var origin = Address(parcel.PickupAddress);
            var destination = Address(parcel.DropoffAddress);
            var timeline = history
                .Select(item => new PublicTimelineEntry(item.ToStatus.ToString(), item.Timestamp, PublicStatusLabel(item.ToStatus)))
                .ToArray();

            await transaction.CommitAsync(cancellationToken);
            return new PublicTrackingSummary(
                parcel.TrackingNumber,
                parcel.Status.ToString(),
                origin.City,
                destination.City,
                parcel.UpdatedAt,
                timeline);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new DeliveryReadUnavailableException("Public tracking is unavailable.", exception);
        }
    }

    public async Task<ParcelEventsResponse?> GetParcelEventsAsync(ParcelReadScope scope, Guid parcelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var database = contextFactory();
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

            var parcel = await AuthorizedParcelQuery(database, scope, parcelId)
                .Select(item => new { item.Id, item.TrackingNumber, item.Status, item.Version, item.UpdatedAt })
                .SingleOrDefaultAsync(cancellationToken);

            if (parcel is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            var history = await database.BookingHistory.AsNoTracking()
                .Where(item => item.ParcelId == parcelId)
                .OrderBy(item => item.Timestamp)
                .ThenBy(item => item.Id)
                .Select(item => new HistoryRow(item.FromStatus, item.ToStatus, item.ChangeReason, item.DeliveryNote, item.Timestamp))
                .ToArrayAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new ParcelEventsResponse(
                parcel.Id,
                parcel.TrackingNumber,
                parcel.Status.ToString(),
                parcel.Version,
                parcel.UpdatedAt,
                history.Select(item => new PrivateHistoryResponse(
                    item.FromStatus?.ToString(),
                    item.ToStatus.ToString(),
                    item.ChangeReason,
                    scope.IsCustomer && !scope.IsOperations && !scope.IsCourier ? null : item.DeliveryNote,
                    item.Timestamp)).ToArray());
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new DeliveryReadUnavailableException("Parcel events are unavailable.", exception);
        }
    }

    public async Task<ParcelEventSnapshot?> GetParcelSnapshotAsync(ParcelReadScope scope, Guid parcelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var database = contextFactory();
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

            var parcel = await AuthorizedParcelQuery(database, scope, parcelId)
                .Select(item => new { item.Id, item.Status, item.Version, item.UpdatedAt })
                .SingleOrDefaultAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return parcel is null
                ? null
                : new ParcelEventSnapshot(parcel.Id, parcel.Status.ToString(), parcel.Version, parcel.UpdatedAt);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new DeliveryReadUnavailableException("Parcel snapshot is unavailable.", exception);
        }
    }

    public async Task<ParcelReceiptLookup?> GetReceiptAsync(ParcelReadScope scope, Guid parcelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var database = contextFactory();
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

            var row = await (from parcel in database.Parcels.AsNoTracking()
                             where parcel.Id == parcelId && (scope.IsOperations || (scope.IsCustomer && parcel.CustomerId == scope.UserId))
                             join snapshot in database.PriceSnapshots.AsNoTracking() on parcel.Id equals snapshot.ParcelId
                             select new
                             {
                                 parcel.Id,
                                 parcel.TrackingNumber,
                                 parcel.Status,
                                 parcel.Size,
                                 parcel.WeightKg,
                                 parcel.PickupAddress,
                                 parcel.DropoffAddress,
                                 parcel.DropoffContact,
                                 parcel.CreatedAt,
                                 parcel.UpdatedAt,
                                 snapshot.BaseFee,
                                 snapshot.WeightFee,
                                 snapshot.Total,
                                 snapshot.Currency,
                                 snapshot.PricingVersion,
                                 snapshot.CalculatedAt
                             }).SingleOrDefaultAsync(cancellationToken);

            if (row is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            if (row.Status != ParcelStatus.Delivered)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ParcelReceiptLookup(row.Status, null);
            }

            var deliveredAt = await database.BookingHistory.AsNoTracking()
                .Where(item => item.ParcelId == parcelId && item.ToStatus == ParcelStatus.Delivered)
                .OrderByDescending(item => item.Timestamp)
                .Select(item => (DateTimeOffset?)item.Timestamp)
                .FirstOrDefaultAsync(cancellationToken) ?? row.UpdatedAt;

            var receipt = new ParcelReceiptResponse(
                $"RCP-{row.TrackingNumber}",
                row.Id,
                row.TrackingNumber,
                row.Status.ToString(),
                row.CreatedAt,
                deliveredAt,
                Address(row.PickupAddress),
                Address(row.DropoffAddress),
                Recipient(row.DropoffContact),
                new ReadPackageDetails(row.WeightKg, row.Size.ToString()),
                row.BaseFee,
                row.WeightFee,
                row.Total,
                row.Currency,
                row.PricingVersion,
                row.CalculatedAt);

            await transaction.CommitAsync(cancellationToken);
            return new ParcelReceiptLookup(row.Status, receipt);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new DeliveryReadUnavailableException("Parcel receipt is unavailable.", exception);
        }
    }

    public async Task<ParcelProofLookup?> GetProofAsync(ParcelReadScope scope, Guid parcelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var database = contextFactory();
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);

            var parcel = await database.Parcels.AsNoTracking()
                .Where(item => item.Id == parcelId && (scope.IsOperations || (scope.IsCustomer && item.CustomerId == scope.UserId)))
                .Select(item => new { item.Id, item.Status, item.TrackingNumber })
                .SingleOrDefaultAsync(cancellationToken);

            if (parcel is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            if (parcel.Status != ParcelStatus.Delivered)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ParcelProofLookup(parcel.Status, parcel.TrackingNumber, []);
            }

            var proofs = await database.ProofMetadata.AsNoTracking()
                .Where(item => item.ParcelId == parcelId)
                .OrderBy(item => item.UploadedAt)
                .ThenBy(item => item.Id)
                .Select(item => new ProofOfDeliveryItem(
                    item.Id,
                    item.Type.ToString(),
                    item.RecipientName,
                    item.UploadedAt,
                    item.StorageRef))
                .ToArrayAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new ParcelProofLookup(parcel.Status, parcel.TrackingNumber, proofs);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            throw new DeliveryReadUnavailableException("Proof of delivery is unavailable.", exception);
        }
    }

    private static IQueryable<Parcel> AuthorizedParcelQuery(DeliveryDbContext database, ParcelReadScope scope, Guid parcelId) =>
        database.Parcels.AsNoTracking().Where(parcel => parcel.Id == parcelId
            && (scope.IsOperations || (scope.IsCustomer && parcel.CustomerId == scope.UserId)
                || (scope.IsCourier && ActiveStatuses.Contains(parcel.Status)
                    && database.Assignments.Any(assignment => assignment.ParcelId == parcel.Id
                        && assignment.CourierId == scope.UserId && assignment.Status == AssignmentStatus.Active))));

    private static string PublicStatusLabel(ParcelStatus status) => status switch
    {
        ParcelStatus.Created => "Booked",
        ParcelStatus.Assigned => "Assigned",
        ParcelStatus.PickedUp => "Collected",
        ParcelStatus.InTransit => "In transit",
        ParcelStatus.OutForDelivery => "Out for delivery",
        ParcelStatus.Delivered => "Delivered",
        ParcelStatus.Failed => "Delivery unsuccessful",
        ParcelStatus.Cancelled => "Cancelled",
        _ => status.ToString()
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
