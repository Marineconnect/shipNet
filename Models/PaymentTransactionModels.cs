namespace StarlinkDeviceManager.Models;

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
