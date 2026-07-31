using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IKvhSubscriptionOperationService
{
    Task<KvhSubscriptionOperationIndexViewModel> GetBatchesAsync(KvhSubscriptionOperationFilter filter, int page, int pageSize, int? allowedTenantId = null, int? allowedDeviceId = null, bool canManage = false, CancellationToken cancellationToken = default);
    Task<KvhSubscriptionOperationDetailViewModel?> GetBatchAsync(long id, int? allowedTenantId = null, int? allowedDeviceId = null, bool canManage = false, CancellationToken cancellationToken = default);
    Task<long> CreateBatchAsync(KvhSubscriptionOperationCreateRequest request, int? userId, string requestedBy, int? allowedTenantId = null, CancellationToken cancellationToken = default);
    Task<int> AddDevicesAsync(long batchId, IReadOnlyList<int> deviceIds, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task<KvhSubscriptionOperationImportPreview> PreviewImportAsync(long batchId, IFormFile file, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task<int> ConfirmImportAsync(long batchId, KvhSubscriptionOperationImportPreview preview, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task<int> ValidateBatchAsync(long batchId, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task<bool> StartBatchAsync(long batchId, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task CancelBatchAsync(long batchId, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task RetryFailedAsync(long batchId, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task RemoveItemAsync(long batchId, long itemId, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task<byte[]> ExportAsync(long batchId, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    byte[] BuildTemplate();
    Task<IReadOnlyList<long>> ClaimQueuedItemsAsync(int batchSize, CancellationToken cancellationToken = default);
    Task SubmitItemAsync(long itemId, int? userId, string requestedBy, CancellationToken cancellationToken = default);
    Task SyncCommandStatusesAsync(CancellationToken cancellationToken = default);
}
