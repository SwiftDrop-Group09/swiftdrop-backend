namespace SwiftDrop.Delivery.Api.Application.Admin.Reports;

public interface IDeliveryReportStore
{
    Task<DeliveryReportResponse> GenerateReportAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

