using System.Text.Json;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface IPaymentTransactionService
{
    Task<PaymentTransactionIndexViewModel> GetTransactionsAsync(
        PaymentTransactionFilterViewModel filter,
        int page,
        int pageSize,
        int? allowedTenantId = null,
        int? allowedDeviceId = null,
        bool canManage = false,
        CancellationToken cancellationToken = default);

    Task<PaymentTransactionDetailViewModel?> GetTransactionDetailAsync(
        int invoiceId,
        int? allowedTenantId = null,
        int? allowedDeviceId = null,
        CancellationToken cancellationToken = default);

    Task<NinePayQrInfoViewModel> CreateNinePayQrInfoAsync(int invoiceId, string clientIp = "", string createdBy = "", CancellationToken cancellationToken = default);

    Task<NinePayBankTransferInfoViewModel> CreateNinePayBankTransferInfoAsync(int invoiceId, string clientIp = "", string createdBy = "", CancellationToken cancellationToken = default);

    Task<NinePayBankTransferInfoViewModel> CreateNinePayBankTransferForSubscriptionsAsync(IReadOnlyList<int> subscriptionIds, string clientIp = "", string createdBy = "", CancellationToken cancellationToken = default);

    Task<NinePayInvoicePaymentStatusViewModel?> GetNinePayInvoicePaymentStatusAsync(int invoiceId, CancellationToken cancellationToken = default);

    Task<NinePayQrSessionIpnDetailViewModel?> GetNinePayQrSessionIpnDetailAsync(int qrSessionId, CancellationToken cancellationToken = default);

    Task<NinePaySampleTestResult> RunNinePaySampleCreateBankTransferAsync(CancellationToken cancellationToken = default);

    Task<InvoiceRabbitMqPublishResult> SendInvoiceToRabbitMqAsync(int invoiceId, string transactionCode = "", DateTime? paymentTime = null, string operatorName = "", CancellationToken cancellationToken = default);

    Task RecordNinePayIpnAttemptAsync(
        string resultBase64,
        string checksum,
        JsonElement decodedResult,
        string processStatus,
        string processMessage,
        CancellationToken cancellationToken = default);

    Task RecordNinePayIpnRequestLogAsync(
        string method,
        string path,
        string source,
        string resultBase64,
        string checksum,
        string rawPayload,
        string providerInvoiceNo,
        string paymentNo,
        string providerStatus,
        string processStatus,
        string processMessage,
        CancellationToken cancellationToken = default);

    Task<NinePayIpnProcessResult> ProcessNinePayIpnAsync(
        string resultBase64,
        string checksum,
        JsonElement decodedResult,
        CancellationToken cancellationToken = default);
}
