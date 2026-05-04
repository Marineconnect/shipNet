using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface ITenantService
{
    Task<TenantPageResult> GetTenantsAsync(int page, int pageSize, int? tenantId = null, CancellationToken cancellationToken = default);
    Task<List<DeviceTenantOptionViewModel>> GetTenantOptionsAsync(int? tenantId = null, CancellationToken cancellationToken = default);
    Task<TenantFormViewModel?> GetTenantByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<int> CreateTenantAsync(TenantFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default);
    Task UpdateTenantAsync(TenantFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default);
    Task DeleteTenantAsync(int id, int? userId, string username, CancellationToken cancellationToken = default);
}
