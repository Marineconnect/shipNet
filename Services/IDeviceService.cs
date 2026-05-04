using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IDeviceService
{
    Task<DevicePageResult> GetDevicesAsync(int page, int pageSize, string? searchTerm = null, int? tenantId = null, CancellationToken cancellationToken = default);
    Task<DeviceDetailViewModel?> GetDeviceByIdAsync(int id, int? tenantId = null, CancellationToken cancellationToken = default);
    Task<DeviceDetailViewModel?> GetDeviceDetailAsync(int id, int? userId = null, int? tenantId = null, CancellationToken cancellationToken = default);
    Task<DeviceWifiResult> GetDeviceWifiAsync(int id, int? tenantId = null, CancellationToken cancellationToken = default);
    Task<TelemetryTimelineResult> GetTelemetryTimelineAsync(int id, long start, long end, string metric, CancellationToken cancellationToken = default);
    Task<CreateDeviceResult> CreateDeviceAsync(CreateDeviceRequest request, int? userId, CancellationToken cancellationToken = default);
    Task<UpdateDeviceResult> UpdateDeviceAsync(UpdateDeviceRequest request, int? userId, CancellationToken cancellationToken = default);
    Task<DeleteDeviceResult> DeleteDeviceAsync(int id, int? userId, CancellationToken cancellationToken = default);
    Task<RefreshDeviceResult> RefreshExpiredDeviceAsync(int id, CancellationToken cancellationToken = default);
}
