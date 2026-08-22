using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IDashboardKpiService
{
    Task<DashboardKpiViewModel> GetKpiAsync(int month, int year, int? allowedTenantId = null, int? allowedDeviceId = null, CancellationToken cancellationToken = default);
}
