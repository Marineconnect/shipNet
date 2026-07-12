using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IPricingPlanService
{
    Task<PricingPlanPageResult> GetPlansAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<List<PricingPlanFormViewModel>> GetPlansForExportAsync(CancellationToken cancellationToken = default);
    Task<List<PricingPlanOptionViewModel>> GetPlanOptionsAsync(CancellationToken cancellationToken = default);
    Task<PricingPlanFormViewModel?> GetPlanByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> IsPlanCodeInUseAsync(string planCode, int? excludePlanId = null, CancellationToken cancellationToken = default);
    Task<int> CreatePlanAsync(PricingPlanFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default);
    Task UpdatePlanAsync(PricingPlanFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default);
    Task DeletePlanAsync(int id, int? userId, string username, CancellationToken cancellationToken = default);
    Task<PricingPlanImportResult> ImportPlansAsync(IReadOnlyList<PricingPlanFormViewModel> plans, int? userId, string username, CancellationToken cancellationToken = default);
    Task<TenantPricingPageResult> GetTenantPricesAsync(int page, int pageSize, int? tenantId = null, string? search = null, CancellationToken cancellationToken = default);
    Task<List<TenantPricingListItemViewModel>> GetTenantPricesForExportAsync(int? tenantId = null, string? search = null, CancellationToken cancellationToken = default);
    Task<TenantPricingFormViewModel?> GetTenantPriceByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> IsTenantPlanPriceInUseAsync(int tenantId, int pricingPlanId, int? excludeTenantPriceId = null, CancellationToken cancellationToken = default);
    Task<int> CreateTenantPriceAsync(TenantPricingFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default);
    Task UpdateTenantPriceAsync(TenantPricingFormViewModel model, int? userId, string username, CancellationToken cancellationToken = default);
    Task DeleteTenantPriceAsync(int id, int? userId, string username, CancellationToken cancellationToken = default);
    Task<TenantPricingDevicePreviewResult> GetTenantPricingDevicePreviewAsync(IReadOnlyList<TenantPricingImportRow> prices, CancellationToken cancellationToken = default);
    Task<TenantPricingImportResult> ImportTenantPricesAsync(IReadOnlyList<TenantPricingImportRow> prices, int? userId, string username, IReadOnlyCollection<int>? deviceIds = null, CancellationToken cancellationToken = default);
}
