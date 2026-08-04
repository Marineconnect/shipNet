namespace StarlinkDeviceManager.Models;

public sealed class PaymentTransactionFilterViewModel
{
    public string? Search { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? PaymentStatus { get; set; }
    public string? PaymentMethod { get; set; }
    public string? QrState { get; set; }
    public int? TenantId { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}

public sealed class PaymentTransactionIndexViewModel
{
    public List<PaymentTransactionListItemViewModel> Items { get; set; } = [];
    public List<DeviceTenantOptionViewModel> Tenants { get; set; } = [];
    public PaymentTransactionFilterViewModel Filter { get; set; } = new();
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalItems { get; set; }
    public bool IsTenantScoped { get; set; }
    public bool CanManageTransactions { get; set; }
    public bool IsTransactionReupAdmin { get; set; }
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalItems / (double)PageSize));
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public int StartItem => TotalItems == 0 ? 0 : ((CurrentPage - 1) * PageSize) + 1;
    public int EndItem => TotalItems == 0 ? 0 : Math.Min(CurrentPage * PageSize, TotalItems);
}

public class PaymentTransactionListItemViewModel
{
    public int InvoiceId { get; set; }
    public int SubscriptionId { get; set; }
    public int? TransactionId { get; set; }
    public int? QrSessionId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string KitNumber { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string InvoiceStatus { get; set; } = string.Empty;
    public decimal InvoiceAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ProviderPaymentNo { get; set; } = string.Empty;
    public string ProviderInvoiceNo { get; set; } = string.Empty;
    public string ProviderStatus { get; set; } = string.Empty;
    public string TransactionStatus { get; set; } = string.Empty;
    public decimal AmountVnd { get; set; }
    public string Currency { get; set; } = "VND";
    public string Method { get; set; } = string.Empty;
    public DateTime? TransactionAt { get; set; }
    public DateTime? InvoiceCreatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public string QrStatus { get; set; } = string.Empty;
    public DateTime? QrStartedAt { get; set; }
    public DateTime? QrExpiresAt { get; set; }
    public string QrCodeUrl { get; set; } = string.Empty;
    public string BankAccountNo { get; set; } = string.Empty;
    public string TransferContent { get; set; } = string.Empty;
    public int IpnLogCount { get; set; }
    public int IntegrationLogCount { get; set; }

    public bool HasTransaction => TransactionId.HasValue;
    public bool HasQr => QrSessionId.HasValue;
    public bool IsQrPaid =>
        string.Equals(InvoiceStatus, "paid", StringComparison.OrdinalIgnoreCase)
        || string.Equals(QrStatus, "Paid", StringComparison.OrdinalIgnoreCase);
    public bool IsQrActive => HasQr && !IsQrPaid && QrExpiresAt.HasValue && QrExpiresAt.Value > DateTime.UtcNow;
    public string QrStateDisplay => IsQrPaid ? "Paid" : IsQrActive ? "Còn hiệu lực" : HasQr ? "Hết hạn" : "Chưa có QR";
    public string InvoiceCreatedAtDisplay => Format(InvoiceCreatedAt);
    public string PaidAtDisplay => Format(PaidAt);
    public string TransactionAtDisplay => Format(TransactionAt);
    public string QrExpiresDisplay => Format(QrExpiresAt);
    public string InvoiceAmountDisplay => $"{InvoiceAmount:#,##0.##}";
    public string PaidAmountDisplay => $"{PaidAmount:#,##0.##}";
    public string AmountVndDisplay => AmountVnd <= 0 ? "-" : $"{AmountVnd:#,##0} {Currency}";

    private static string Format(DateTime? value) => value?.ToString("dd/MM/yyyy HH:mm:ss") ?? "-";
}

public sealed class PaymentTransactionDetailViewModel : PaymentTransactionListItemViewModel
{
    public string PaymentUrl { get; set; } = string.Empty;
    public string FailureReason { get; set; } = string.Empty;
    public string RawResultJson { get; set; } = string.Empty;
    public string RawResultBase64 { get; set; } = string.Empty;
    public string RawChecksum { get; set; } = string.Empty;
    public string ProviderResponseJson { get; set; } = string.Empty;
    public string DebugLog { get; set; } = string.Empty;
    public List<PaymentTransactionIpnLogViewModel> IpnLogs { get; set; } = [];
    public List<PaymentTransactionIntegrationLogViewModel> IntegrationLogs { get; set; } = [];
}

public sealed class PaymentTransactionIpnLogViewModel
{
    public int Id { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public string PaymentNo { get; set; } = string.Empty;
    public string ProviderInvoiceNo { get; set; } = string.Empty;
    public string ProviderStatus { get; set; } = string.Empty;
    public string ProcessStatus { get; set; } = string.Empty;
    public string ProcessMessage { get; set; } = string.Empty;
    public string ResultBase64 { get; set; } = string.Empty;
    public string RawPayload { get; set; } = string.Empty;
    public string ReceivedAtDisplay => ReceivedAt?.ToString("dd/MM/yyyy HH:mm:ss") ?? "-";
}

public sealed class PaymentTransactionIntegrationLogViewModel
{
    public long Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string TargetSystem { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime? CreatedAtUtc { get; set; }
    public string CreatedAtDisplay => CreatedAtUtc?.ToString("dd/MM/yyyy HH:mm:ss") ?? "-";
}

public class NinePayIpnProcessResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string PaymentNo { get; set; } = string.Empty;
}

public class NinePayQrSessionIpnDetailViewModel
{
    public int QrSessionId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string ProviderInvoiceNo { get; set; } = string.Empty;
    public string QrStatus { get; set; } = string.Empty;
    public string ProviderStatus { get; set; } = string.Empty;
    public string ProviderPaymentNo { get; set; } = string.Empty;
    public DateTime? IpnReceivedAt { get; set; }
    public string IpnReceivedAtDisplay => IpnReceivedAt?.ToString("dd/MM/yyyy HH:mm:ss") ?? "-";
    public string IpnProcessStatus { get; set; } = string.Empty;
    public string IpnProcessMessage { get; set; } = string.Empty;
    public string IpnChecksum { get; set; } = string.Empty;
    public string IpnResultBase64 { get; set; } = string.Empty;
    public string IpnRawJson { get; set; } = string.Empty;
    public string LatestTransactionStatus { get; set; } = string.Empty;
    public string LatestTransactionProviderStatus { get; set; } = string.Empty;
    public string LatestTransactionFailureReason { get; set; } = string.Empty;
    public DateTime? LatestTransactionAt { get; set; }
    public string LatestTransactionAtDisplay => LatestTransactionAt?.ToString("dd/MM/yyyy HH:mm:ss") ?? "-";
}

public class NinePayQrInfoViewModel
{
    public int InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public decimal OrderTotalUsd { get; set; }
    public decimal ExchangeRateVndPerUsd { get; set; }
    public decimal AmountVnd { get; set; }
    public decimal TransactionFeeVnd { get; set; }
    public decimal TotalToPayVnd { get; set; }
    public string Currency { get; set; } = "VND";
    public string BankName { get; set; } = string.Empty;
    public string PaymentUrl { get; set; } = string.Empty;
    public string QrImageUrl { get; set; } = string.Empty;
    public DateTime PaymentDate { get; set; }
}

public class NinePayBankTransferInfoViewModel
{
    public int InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public decimal OrderTotalUsd { get; set; }
    public decimal ExchangeRateVndPerUsd { get; set; }
    public decimal AmountVnd { get; set; }
    public decimal TransactionFeeVnd { get; set; }
    public decimal TotalToPayVnd { get; set; }
    public string Currency { get; set; } = "VND";
    public string PaymentMethod { get; set; } = "bank_transfer";
    public string PaymentUrl { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public string ProviderPaymentId { get; set; } = string.Empty;
    public string ProviderOrderRef { get; set; } = string.Empty;
    public DateTime? QrStartedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int ExpiresInSeconds => ExpiresAt.HasValue
        ? Math.Max(0, (int)Math.Ceiling((ExpiresAt.Value - DateTime.UtcNow).TotalSeconds))
        : 0;
    public string QrStatus { get; set; } = string.Empty;
    public bool ReusedQr { get; set; }
    public List<NinePayBankTransferBankViewModel> Banks { get; set; } = [];
    public NinePayBankTransferBankViewModel? SelectedBank => Banks.FirstOrDefault();
    public DateTime PaymentDate { get; set; }
    public string DebugLog { get; set; } = string.Empty;
}

public class NinePayBankTransferBankViewModel
{
    public string Logo { get; set; } = string.Empty;
    public string IsVa { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Keyword { get; set; } = string.Empty;
    public string BankCode { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string Plaintext { get; set; } = string.Empty;
    public string QrCodeUrl { get; set; } = string.Empty;
    public string BankAccountNo { get; set; } = string.Empty;
    public string BankAccountName { get; set; } = string.Empty;
    public string DisplayName => string.IsNullOrWhiteSpace(BankCode) ? BankName : $"{BankCode} - {BankName}";
}

public class NinePayInvoicePaymentStatusViewModel
{
    public int InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string InvoiceStatus { get; set; } = string.Empty;
    public decimal PaidAmount { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public DateTime? CompletedAt { get; set; }
    public string CompletedAtDisplay => CompletedAt?.ToString("dd/MM/yyyy HH:mm") ?? "-";
    public string ProviderPaymentNo { get; set; } = string.Empty;
    public string ProviderStatus { get; set; } = string.Empty;
    public string TransactionStatus { get; set; } = string.Empty;
    public decimal AmountVnd { get; set; }
    public string Currency { get; set; } = "VND";
    public string Method { get; set; } = string.Empty;
    public bool IsPaid => string.Equals(InvoiceStatus, "paid", StringComparison.OrdinalIgnoreCase);
}

public class NinePaySampleTestResult
{
    public string Message { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string RequestPayload { get; set; } = string.Empty;
    public string ResponsePayload { get; set; } = string.Empty;
}

public class NinePaySubscriptionQrRequest
{
    public List<int> SubscriptionIds { get; set; } = [];
}
