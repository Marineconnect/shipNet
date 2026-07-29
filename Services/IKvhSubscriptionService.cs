using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IKvhSubscriptionService
{
    Task<KvhSubscriptionSyncResult> SyncForDeviceAsync(int deviceId, string terminalId, string accessToken, string? trafficId = null, CancellationToken cancellationToken = default);
    Task<KvhSolutionPageResult> GetSolutionsAsync(KvhSolutionFilter filter, int page, int pageSize, int? allowedTenantId = null, int? allowedDeviceId = null, bool canManage = false, CancellationToken cancellationToken = default);
    Task<KvhSolutionDetailViewModel?> GetSolutionDetailAsync(int deviceId, int? allowedTenantId = null, int? allowedDeviceId = null, bool canManage = false, CancellationToken cancellationToken = default);
    Task<KvhCommandSubmitResult> PauseAsync(KvhSolutionCommandRequest request, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task<KvhCommandSubmitResult> ResumeAsync(KvhSolutionCommandRequest request, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task<KvhCommandSubmitResult> CancelScheduleAsync(KvhSolutionCommandRequest request, int? userId, string requestedBy, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
}
