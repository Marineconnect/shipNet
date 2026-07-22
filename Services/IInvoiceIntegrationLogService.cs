using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IInvoiceIntegrationLogService
{
    Task<long> WriteAsync(InvoiceIntegrationLogEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InvoiceIntegrationLogListItem>> GetLogsAsync(string invoiceCode, int page, int pageSize, string eventType = "", int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<InvoiceIntegrationLogEntry?> GetLogDetailAsync(string invoiceCode, long logId, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);
}
