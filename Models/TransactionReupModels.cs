using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace StarlinkDeviceManager.Models;

public static class TransactionReupStatuses
{
    public const string Pending = "Pending";
    public const string Published = "Published";
    public const string PublishFailed = "PublishFailed";
    public const string Invalid = "Invalid";
    public const string Duplicate = "Duplicate";
    public const string Skipped = "Skipped";
}

public sealed class TransactionReupImportViewModel
{
    [Required]
    public IFormFile? File { get; set; }

    [Range(1, int.MaxValue)]
    public int StartInvoiceNumber { get; set; }
}

public sealed class TransactionReupIndexViewModel
{
    public List<TransactionReupBatchViewModel> Batches { get; set; } = [];
    public TransactionReupImportViewModel Import { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
}

public sealed class TransactionReupBatchViewModel
{
    public int Id { get; set; }
    public string BatchCode { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string ImportedByUsername { get; set; } = string.Empty;
    public DateTime ImportedAtUtc { get; set; }
    public int InvoiceStartNumber { get; set; }
    public int InvoiceEndNumber { get; set; }
    public int NextInvoiceNumber { get; set; }
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int PublishedRows { get; set; }
    public int FailedRows { get; set; }
    public int SkippedRows { get; set; }
    public int DuplicateRows { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ImportedAtDisplay => ImportedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
}

public sealed class TransactionReupDetailsViewModel
{
    public TransactionReupBatchViewModel Batch { get; set; } = new();
    public List<TransactionReupItemViewModel> Items { get; set; } = [];
}

public sealed class TransactionReupItemViewModel
{
    public int Id { get; set; }
    public int RowNumber { get; set; }
    public string SourceTransactionCode { get; set; } = string.Empty;
    public string SourceRequestCode { get; set; } = string.Empty;
    public string InvoiceCode { get; set; } = string.Empty;
    public decimal GrossAmountVnd { get; set; }
    public string ValidationStatus { get; set; } = string.Empty;
    public string PublishStatus { get; set; } = string.Empty;
    public int PublishAttemptCount { get; set; }
    public string RabbitMessageId { get; set; } = string.Empty;
    public string RabbitCorrelationId { get; set; } = string.Empty;
    public string PublishMessage { get; set; } = string.Empty;
    public string PublishLogs { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string TransactionType { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public decimal ProcessingFeeVnd { get; set; }
    public decimal NetAmountVnd { get; set; }
    public string SourceStatus { get; set; } = string.Empty;
    public DateTime? PublishedAtUtc { get; set; }
    public string PublishedAtDisplay => PublishedAtUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "-";
    public bool CanRetry => string.Equals(PublishStatus, TransactionReupStatuses.PublishFailed, StringComparison.OrdinalIgnoreCase);
}

public sealed record TransactionReupSourceRow(
    int RowNumber,
    string CreatedAtText,
    string UpdatedAtText,
    string TransactionCode,
    string RequestInvoiceCode,
    string SourceOriginalRequestCode,
    string SourceCreatedBy,
    string TransactionType,
    string PaymentMethod,
    string BankName,
    decimal TotalAmountVnd,
    decimal ProcessingFee,
    string TransferContent,
    string FeeBearer,
    decimal NetAmountVnd,
    string SourceStatus,
    IReadOnlyDictionary<string, string> Values);

public sealed record TransactionReupImportResult(
    int BatchId,
    string Message,
    int FirstInvoiceNumber,
    int LastInvoiceNumber,
    int NextInvoiceNumber);
