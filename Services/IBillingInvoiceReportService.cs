using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IBillingInvoiceReportService
{
    Task<BillingInvoicePageResult> GetInvoicesAsync(
        BillingInvoiceFilterViewModel filter,
        int page,
        int pageSize,
        int? allowedTenantId = null,
        int? allowedDeviceId = null,
        CancellationToken cancellationToken = default);

    Task<BillingInvoiceIndexViewModel> GetIndexOptionsAsync(
        BillingInvoiceFilterViewModel filter,
        int? allowedTenantId = null,
        int? allowedDeviceId = null,
        CancellationToken cancellationToken = default);

    Task<byte[]> ExportCsvAsync(
        BillingInvoiceFilterViewModel filter,
        int? allowedTenantId = null,
        int? allowedDeviceId = null,
        CancellationToken cancellationToken = default);
}
