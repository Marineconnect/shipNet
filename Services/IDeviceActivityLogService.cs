using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IDeviceActivityLogService
{
    Task WriteAsync(DeviceActivityLogEntry entry, CancellationToken cancellationToken = default);

    Task<DeviceActivityPageResult> GetDeviceActivityAsync(
        int deviceId,
        DeviceActivityFilter filter,
        int page,
        int pageSize,
        int? allowedTenantId = null,
        int? allowedDeviceId = null,
        CancellationToken cancellationToken = default);
}
