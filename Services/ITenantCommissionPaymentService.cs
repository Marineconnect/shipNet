using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface ITenantCommissionPaymentService
{
    Task<TenantCommissionPaymentIndexViewModel> GetIndexAsync(TenantCommissionPaymentFilterViewModel filter, int page, int pageSize, int? allowedTenantId = null, bool canCreatePayment = false, CancellationToken cancellationToken = default);
    Task<TenantCommissionBalanceViewModel> GetBalanceAsync(int? tenantId, int? allowedTenantId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EligibleCommissionBillingCycleViewModel>> SearchEligibleCyclesAsync(int tenantId, DateTime? dateFrom, DateTime? dateTo, string? search, int? allowedTenantId = null, CancellationToken cancellationToken = default);
    Task<long> CreateManualPaymentAsync(TenantCommissionManualPaymentInput input, int? createdByUserId, string createdBy, int? allowedTenantId = null, CancellationToken cancellationToken = default);
    Task<long> CreateBillingCyclePaymentAsync(TenantCommissionCyclePaymentInput input, int? createdByUserId, string createdBy, int? allowedTenantId = null, CancellationToken cancellationToken = default);
    Task<TenantCommissionPaymentDetailViewModel?> GetDetailAsync(long id, int? allowedTenantId = null, CancellationToken cancellationToken = default);
}
