using System.Globalization;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.DeliveryReads;

public sealed record ReceiptPdfPayload(byte[] Content, string FileName);
public sealed record ProofImagePayload(byte[] Content, string ContentType);

public sealed class CustomerTrackingHandler(
    ICurrentDeliveryUser currentUser,
    ICustomerDeliveryReadStore store,
    IReceiptPdfRenderer pdfRenderer,
    IPrivateProofReader proofReader)
{
    public async Task<DeliveryReadResult<CustomerParcelPage>> ListParcelsAsync(
        CustomerParcelQueryInput input, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
            return new(null, DeliveryReadFailure.Unauthenticated);
        if (!currentUser.IsCustomer && !currentUser.IsOperations)
            return new(null, DeliveryReadFailure.Forbidden);

        var errors = new Dictionary<string, string[]>();
        if (input.InvalidParameters)
            errors["query"] = ["Supply only one value for each documented query parameter."];

        ParcelStatus? status = null;
        if (!string.IsNullOrWhiteSpace(input.Status))
        {
            if (Enum.TryParse<ParcelStatus>(input.Status.Trim(), ignoreCase: true, out var parsedStatus)
                && Enum.IsDefined(parsedStatus))
            {
                status = parsedStatus;
            }
            else
            {
                errors["status"] = ["Supply a valid parcel status."];
            }
        }

        DateTimeOffset? from = null;
        if (!string.IsNullOrWhiteSpace(input.From))
        {
            if (DateTimeOffset.TryParse(input.From, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedFrom))
            {
                from = parsedFrom;
            }
            else
            {
                errors["from"] = ["Supply a valid UTC ISO 8601 timestamp."];
            }
        }

        DateTimeOffset? to = null;
        if (!string.IsNullOrWhiteSpace(input.To))
        {
            if (DateTimeOffset.TryParse(input.To, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTo))
            {
                to = parsedTo;
            }
            else
            {
                errors["to"] = ["Supply a valid UTC ISO 8601 timestamp."];
            }
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
            errors["from"] = ["The start timestamp must not be after the end timestamp."];

        var search = string.IsNullOrWhiteSpace(input.Search) ? null : input.Search.Trim();
        if (search is { Length: > 128 })
            errors["search"] = ["Search text must not exceed 128 characters."];

        Guid? zoneId = null;
        if (input.ZoneId is not null)
        {
            if (Guid.TryParse(input.ZoneId, out var parsedZoneId) && parsedZoneId != Guid.Empty)
                zoneId = parsedZoneId;
            else
                errors["zoneId"] = ["Supply a non-empty zone UUID."];
        }

        var pagination = DeliveryPagination.Parse(input.Page, input.PageSize, errors);
        if (errors.Count != 0)
            return new(null, DeliveryReadFailure.InvalidInput, errors);

        try
        {
            var scope = new ParcelReadScope(userId, currentUser.IsCourier, currentUser.IsCustomer, currentUser.IsOperations);
            var page = await store.ListParcelsAsync(scope,
                new CustomerParcelQuery(status, from, to, search, pagination.Page, pagination.PageSize, zoneId),
                cancellationToken);
            return new(page, null);
        }
        catch (DeliveryReadUnavailableException)
        {
            return new(null, DeliveryReadFailure.Unavailable);
        }
    }

    public async Task<DeliveryReadResult<PublicTrackingSummary>> GetPublicTrackingAsync(
        string trackingNumber, bool invalidParameters = false, CancellationToken cancellationToken = default)
    {
        if (invalidParameters)
            return new(null, DeliveryReadFailure.InvalidInput,
                new Dictionary<string, string[]> { ["query"] = ["This operation has no query parameters."] });

        var trimmed = trackingNumber?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 128)
            return new(null, DeliveryReadFailure.NotFound);

        try
        {
            var summary = await store.GetPublicTrackingAsync(trimmed, cancellationToken);
            return summary is null ? new(null, DeliveryReadFailure.NotFound) : new(summary, null);
        }
        catch (DeliveryReadUnavailableException)
        {
            return new(null, DeliveryReadFailure.Unavailable);
        }
    }

    public async Task<DeliveryReadResult<ParcelEventsResponse>> GetParcelEventsAsync(
        string parcelId, bool invalidParameters = false, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
            return new(null, DeliveryReadFailure.Unauthenticated);
        if (!currentUser.IsCourier && !currentUser.IsCustomer && !currentUser.IsOperations)
            return new(null, DeliveryReadFailure.Forbidden);
        if (invalidParameters)
            return new(null, DeliveryReadFailure.InvalidInput,
                new Dictionary<string, string[]> { ["query"] = ["This operation has no query parameters."] });
        if (!Guid.TryParse(parcelId, out var id) || id == Guid.Empty)
            return new(null, DeliveryReadFailure.NotFound);

        try
        {
            var scope = new ParcelReadScope(userId, currentUser.IsCourier, currentUser.IsCustomer, currentUser.IsOperations);
            var events = await store.GetParcelEventsAsync(scope, id, cancellationToken);
            return events is null ? new(null, DeliveryReadFailure.NotFound) : new(events, null);
        }
        catch (DeliveryReadUnavailableException)
        {
            return new(null, DeliveryReadFailure.Unavailable);
        }
    }

    public async Task<DeliveryReadResult<ParcelEventSnapshot>> GetParcelSnapshotAsync(
        Guid parcelId, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
            return new(null, DeliveryReadFailure.Unauthenticated);
        if (!currentUser.IsCourier && !currentUser.IsCustomer && !currentUser.IsOperations)
            return new(null, DeliveryReadFailure.Forbidden);

        try
        {
            var scope = new ParcelReadScope(userId, currentUser.IsCourier, currentUser.IsCustomer, currentUser.IsOperations);
            var snapshot = await store.GetParcelSnapshotAsync(scope, parcelId, cancellationToken);
            return snapshot is null ? new(null, DeliveryReadFailure.NotFound) : new(snapshot, null);
        }
        catch (DeliveryReadUnavailableException)
        {
            return new(null, DeliveryReadFailure.Unavailable);
        }
    }

    public async Task<DeliveryReadResult<ParcelReceiptResponse>> GetReceiptAsync(
        string parcelId, bool invalidParameters = false, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
            return new(null, DeliveryReadFailure.Unauthenticated);
        if (!currentUser.IsCustomer && !currentUser.IsAdmin)
            return new(null, DeliveryReadFailure.Forbidden);
        if (invalidParameters)
            return new(null, DeliveryReadFailure.InvalidInput,
                new Dictionary<string, string[]> { ["query"] = ["This operation has no query parameters."] });
        if (!Guid.TryParse(parcelId, out var id) || id == Guid.Empty)
            return new(null, DeliveryReadFailure.NotFound);

        try
        {
            var scope = new ParcelReadScope(userId, false, currentUser.IsCustomer, currentUser.IsAdmin);
            var lookup = await store.GetReceiptAsync(scope, id, cancellationToken);
            if (lookup is null)
                return new(null, DeliveryReadFailure.NotFound);
            if (lookup.Status != ParcelStatus.Delivered || lookup.Receipt is null)
                return new(null, DeliveryReadFailure.Conflict);
            return new(lookup.Receipt, null);
        }
        catch (DeliveryReadUnavailableException)
        {
            return new(null, DeliveryReadFailure.Unavailable);
        }
    }

    public async Task<DeliveryReadResult<ReceiptPdfPayload>> GetReceiptPdfAsync(
        string parcelId, bool invalidParameters = false, CancellationToken cancellationToken = default)
    {
        var receiptResult = await GetReceiptAsync(parcelId, invalidParameters, cancellationToken);
        if (receiptResult.Response is not { } receipt)
            return new(null, receiptResult.Failure, receiptResult.Errors);

        var pdfBytes = pdfRenderer.Render(receipt);
        return new(new ReceiptPdfPayload(pdfBytes, $"swiftdrop-receipt-{receipt.TrackingNumber}.pdf"), null);
    }

    public async Task<DeliveryReadResult<ProofOfDeliveryResponse>> GetProofAsync(
        string parcelId, bool invalidParameters = false, CancellationToken cancellationToken = default)
    {
        var lookupResult = await LookupProofAsync(parcelId, invalidParameters, cancellationToken);
        if (lookupResult.Response is not { } lookup)
            return new(null, lookupResult.Failure, lookupResult.Errors);

        var primary = lookup.Proofs[0];
        return new(new ProofOfDeliveryResponse(
            Guid.Parse(parcelId),
            lookup.TrackingNumber,
            primary.Type,
            primary.RecipientName,
            primary.UploadedAt,
            HasImage: true), null);
    }

    public async Task<DeliveryReadResult<ProofImagePayload>> GetProofImageAsync(
        string parcelId, bool invalidParameters = false, CancellationToken cancellationToken = default)
    {
        var lookupResult = await LookupProofAsync(parcelId, invalidParameters, cancellationToken);
        if (lookupResult.Response is not { } lookup)
            return new(null, lookupResult.Failure, lookupResult.Errors);

        try
        {
            var objectData = await proofReader.ReadObjectAsync(lookup.Proofs[0].StorageRef, cancellationToken);
            if (objectData is null)
                return new(null, DeliveryReadFailure.NotFound);
            return new(new ProofImagePayload(objectData.Value.Content, objectData.Value.ContentType), null);
        }
        catch (Exception ex) when (ex is ProofStorageException or DeliveryReadUnavailableException)
        {
            return new(null, DeliveryReadFailure.Unavailable);
        }
    }

    private async Task<DeliveryReadResult<ParcelProofLookup>> LookupProofAsync(
        string parcelId, bool invalidParameters, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
            return new(null, DeliveryReadFailure.Unauthenticated);
        if (!currentUser.IsCustomer && !currentUser.IsAdmin)
            return new(null, DeliveryReadFailure.Forbidden);
        if (invalidParameters)
            return new(null, DeliveryReadFailure.InvalidInput,
                new Dictionary<string, string[]> { ["query"] = ["This operation has no query parameters."] });
        if (!Guid.TryParse(parcelId, out var id) || id == Guid.Empty)
            return new(null, DeliveryReadFailure.NotFound);

        try
        {
            var scope = new ParcelReadScope(userId, false, currentUser.IsCustomer, currentUser.IsAdmin);
            var lookup = await store.GetProofAsync(scope, id, cancellationToken);
            if (lookup is null)
                return new(null, DeliveryReadFailure.NotFound);
            if (lookup.Status != ParcelStatus.Delivered)
                return new(null, DeliveryReadFailure.Conflict);
            if (lookup.Proofs.Count == 0)
                return new(null, DeliveryReadFailure.NotFound);
            return new(lookup, null);
        }
        catch (DeliveryReadUnavailableException)
        {
            return new(null, DeliveryReadFailure.Unavailable);
        }
    }
}

