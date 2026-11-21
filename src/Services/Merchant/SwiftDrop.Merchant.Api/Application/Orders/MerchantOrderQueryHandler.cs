using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Contracts.Http.V1;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Application.Orders.Processing;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Api.Application.Orders;

/// <summary>
/// FR-M07: a business reads its own orders. Every query is forced to the
/// caller's business; another business's order is indistinguishable from a
/// missing one (404).
/// </summary>
public sealed class MerchantOrderQueryHandler(
    MerchantDbContext database,
    ICurrentOrderCaller currentCaller,
    RefreshOrderStatusesHandler? refreshHandler = null,
    TimeProvider? clock = null)
{
    public async Task<OrderResult<OrderJobPage>> ListAsync(OrderListQuery query, CancellationToken cancellationToken)
    {
        var caller = await currentCaller.ResolveAsync(cancellationToken);
        if (!caller.CanReadOrders || caller.BusinessId is not { } businessId)
        {
            return OrderResult<OrderJobPage>.Fail(OrderFailure.Forbidden);
        }

        var paging = OrderInputValidator.ValidatePage(query);
        if (!paging.IsValid)
        {
            return OrderResult<OrderJobPage>.Fail(OrderFailure.InvalidInput, paging.Errors);
        }

        var (page, pageSize, state, parcelStatus) = paging.Value!;
        var jobs = database.InboxJobs.AsNoTracking().Where(job => job.BusinessId == businessId);
        if (state is { } wanted)
        {
            jobs = jobs.Where(job => job.State == wanted);
        }

        if (parcelStatus is not null) jobs = jobs.Where(job => job.ParcelStatus == parcelStatus);
        var (totalCount, items) = await ReadPageAsync(jobs, page, pageSize, cancellationToken);
        return OrderResult<OrderJobPage>.Ok(new OrderJobPage(
            items.Select(OrderJobMapper.ToResponse).ToList(), totalCount, page, pageSize));
    }

    public async Task<OrderResult<MerchantAssistantOrderPage>> ListForAssistantAsync(CancellationToken cancellationToken)
    {
        var caller = await currentCaller.ResolveAsync(cancellationToken);
        if (!caller.CanViewDashboard || caller.BusinessId is not { } businessId)
        {
            return OrderResult<MerchantAssistantOrderPage>.Fail(OrderFailure.Forbidden);
        }

        var jobs = database.InboxJobs.AsNoTracking().Where(job => job.BusinessId == businessId);
        var (totalCount, items) = await ReadPageAsync(jobs, 1, MerchantOrderApi.AssistantPageSize, cancellationToken);
        var summary = items.Select(job =>
        {
            var payload = JsonSerializer.Deserialize<MerchantOrderPayload>(job.OrderPayload, SubmitMerchantOrderHandler.PayloadJson)
                ?? throw new JsonException("Merchant order payload is missing.");
            return new MerchantAssistantOrder(job.ExternalOrderId, job.State.ToString(), job.TrackingNumber, job.ParcelStatus,
                payload.DeliveryAddress.City, payload.PackageDetails.WeightKg, job.Currency == "LKR" ? job.Total : null, job.CreatedAt);
        }).ToArray();
        return OrderResult<MerchantAssistantOrderPage>.Ok(new(totalCount, summary));
    }

    private static async Task<(int TotalCount, List<InboxJob> Items)> ReadPageAsync(
        IQueryable<InboxJob> jobs, int page, int pageSize, CancellationToken cancellationToken)
    {
        var totalCount = await jobs.CountAsync(cancellationToken);
        // Pages beyond the data (including absurdly large page numbers) are simply empty.
        var skip = (long)(page - 1) * pageSize;
        var items = skip >= totalCount
            ? []
            : await jobs.OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id)
                .Skip((int)skip).Take(pageSize).ToListAsync(cancellationToken);
        return (totalCount, items);
    }

    public async Task<OrderResult<OrderJobResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var caller = await currentCaller.ResolveAsync(cancellationToken);
        if (!caller.CanReadOrders || caller.BusinessId is not { } businessId)
        {
            return OrderResult<OrderJobResponse>.Fail(OrderFailure.Forbidden);
        }

        var job = await database.InboxJobs
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.BusinessId == businessId, cancellationToken);

        if (job is null)
        {
            return OrderResult<OrderJobResponse>.Fail(OrderFailure.NotFound);
        }

        if (refreshHandler is not null &&
            job.State == InboxJobState.Created &&
            job.ParcelStatus is not null &&
            !ParcelStatuses.Terminal.Contains(job.ParcelStatus))
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            if (job.LastRefreshedAt is null || now - job.LastRefreshedAt > TimeSpan.FromSeconds(2))
            {
                await refreshHandler.RefreshJobAsync(job, cancellationToken);
            }
        }

        return OrderResult<OrderJobResponse>.Ok(OrderJobMapper.ToResponse(job));
    }
}
