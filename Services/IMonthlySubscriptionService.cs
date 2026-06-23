using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IMonthlySubscriptionService
{
    Task<MonthlySubscriptionPageResult> GetSubscriptionsAsync(MonthlySubscriptionFilterViewModel filter, int page, int pageSize, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<MonthlySubscriptionDetailViewModel?> GetSubscriptionDetailAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<List<SubscriptionDeviceOptionViewModel>> GetDeviceOptionsAsync(int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<List<SubscriptionPlanOptionViewModel>> GetPlanOptionsAsync(int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<int> CreateSubscriptionAsync(CreateMonthlySubscriptionViewModel model, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<int> CreateInvoiceAsync(CreateSubscriptionInvoiceViewModel model, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task UpdateInvoiceAsync(UpdateSubscriptionInvoiceViewModel model, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task UpdateSubscriptionBillingAsync(UpdateMonthlySubscriptionBillingViewModel model, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task UpdateSubscriptionStatusAsync(UpdateMonthlySubscriptionStatusViewModel model, int? userId, string username, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
}
