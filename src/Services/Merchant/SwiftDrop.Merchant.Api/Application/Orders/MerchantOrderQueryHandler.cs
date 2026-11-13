using Microsoft.EntityFrameworkCore;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Api.Application.Orders;

/// <summary>
/// FR-M07: a business reads its own orders. Every query is forced to the
/// caller's business; another business's order is indistinguishable from a
/// missing one (404).
/// </summary>
public sealed class MerchantOrderQueryHandler(MerchantDbContext database, ICurrentOrderCaller currentCaller)
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

        var (page, pageSize, state) = paging.Value!;
        var jobs = database.InboxJobs.AsNoTracking().Where(job => job.BusinessId == businessId);
        if (state is { } wanted)
        {
            jobs = jobs.Where(job => job.State == wanted);
        }

        var totalCount = await jobs.CountAsync(cancellationToken);

        // Pages beyond the data (including absurdly large page numbers) are simply empty.
        var skip = (long)(page - 1) * pageSize;
        var items = skip >= totalCount
            ? []
            : await jobs
                .OrderByDescending(job => job.CreatedAt)
                .ThenByDescending(job => job.Id)
                .Skip((int)skip)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

        return OrderResult<OrderJobPage>.Ok(new OrderJobPage(
            items.Select(OrderJobMapper.ToResponse).ToList(), totalCount, page, pageSize));
    }

    public async Task<OrderResult<OrderJobResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var caller = await currentCaller.ResolveAsync(cancellationToken);
        if (!caller.CanReadOrders || caller.BusinessId is not { } businessId)
        {
            return OrderResult<OrderJobResponse>.Fail(OrderFailure.Forbidden);
        }

        var job = await database.InboxJobs.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.BusinessId == businessId, cancellationToken);

        return job is null
            ? OrderResult<OrderJobResponse>.Fail(OrderFailure.NotFound)
            : OrderResult<OrderJobResponse>.Ok(OrderJobMapper.ToResponse(job));
    }
}
