using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IKvhPaymentResumeService
{
    Task<KvhPaymentResumeResult> HandlePaidSubscriptionAsync(
        int subscriptionId,
        string source,
        int? userId,
        string performedBy,
        string referenceType,
        string referenceId,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<KvhPaymentResumeResult> HandlePaidSubscriptionAsync(
        KvhPaymentResumeRequest request,
        CancellationToken cancellationToken = default);

    Task<KvhPaymentResumePrecheckResult> PrecheckAsync(
        int invoiceId,
        int subscriptionId,
        int? allowedTenantId = null,
        int? allowedDeviceId = null,
        CancellationToken cancellationToken = default);
}
