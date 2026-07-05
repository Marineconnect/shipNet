using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IDeviceService
{
    Task<DevicePageResult> GetDevicesAsync(int page, int pageSize, string? searchTerm = null, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<DeviceDetailViewModel?> GetDeviceByIdAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<DeviceDetailViewModel?> GetDeviceDetailAsync(int id, int? userId = null, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<DeviceWifiResult> GetDeviceWifiAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> UpdateDeviceWifiAsync(UpdateDeviceWifiRequest request, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<DeviceCommandResult> RebootDeviceRouterAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<DeviceDataOptInManagementResult> GetDeviceDataOptInAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<DeviceDataOptInChangeResult> UpdateDeviceDataOptInAsync(UpdateDeviceDataOptInRequest request, int? userId, string performedBy, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<DevicePlanManagementResult> GetDevicePlanManagementAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<SaveDevicePlanResult> SaveDevicePlanAsync(SaveDevicePlanRequest request, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<DeleteDevicePlanResult> DeleteDevicePlanAsync(DeleteDevicePlanRequest request, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<TelemetryTimelineResult> GetTelemetryTimelineAsync(int id, long start, long end, string metric, CancellationToken cancellationToken = default);
    Task<CreateDeviceResult> CreateDeviceAsync(CreateDeviceRequest request, int? userId, CancellationToken cancellationToken = default);
    Task<UpdateDeviceResult> UpdateDeviceAsync(UpdateDeviceRequest request, int? userId, CancellationToken cancellationToken = default);
    Task<DeleteDeviceResult> DeleteDeviceAsync(int id, int? userId, CancellationToken cancellationToken = default);
    Task<RefreshDeviceResult> RefreshExpiredDeviceAsync(int id, CancellationToken cancellationToken = default);
}
