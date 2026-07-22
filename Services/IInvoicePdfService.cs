using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IInvoicePdfService
{
    string BuildUploadUrl(string invoiceCode);
    Task<InvoicePdfUploadResult> UploadAsync(InvoicePdfUploadRequest request, CancellationToken cancellationToken = default);
    Task<InvoicePdfOpenResult?> OpenReadAsync(string invoiceCode, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<InvoicePdfFileViewModel> GetCurrentFileViewModelAsync(string invoiceCode, bool canReplace, bool canDelete, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<Dictionary<int, InvoicePdfFileViewModel>> GetCurrentFilesForSubscriptionAsync(int subscriptionId, bool canReplace, bool canDelete, CancellationToken cancellationToken = default);
    Task DeleteAsync(string invoiceCode, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
}
