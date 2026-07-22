using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IKvhCommandService
{
    Task<KvhCommandSubmitResult> SubmitDataOptInAsync(UpdateDeviceDataOptInRequest request, int? userId, string requestedBy, int? tenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task<KvhCommandSubmitResult> SubmitWifiUpdateAsync(UpdateDeviceWifiRequest request, int? userId, string requestedBy, int? tenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task<KvhCommandSubmitResult> SubmitRebootAsync(int id, int? userId, string requestedBy, int? tenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task<KvhCommandStatusDto?> GetCommandStatusAsync(long commandId, int? tenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KvhCommandStatusDto>> GetRecentCommandsAsync(int deviceId, int? tenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
}
