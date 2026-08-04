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
    public int ValidCount { get; set; }
    public int PublishedCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public int DuplicateCount { get; set; }
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
    public string TransactionCode { get; set; } = string.Empty;
    public string RequestInvoiceCode { get; set; } = string.Empty;
    public string InvoiceCode { get; set; } = string.Empty;
    public decimal TotalAmountVnd { get; set; }
    public string ValidationStatus { get; set; } = string.Empty;
    public string RabbitMqStatus { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string PublishMessage { get; set; } = string.Empty;
    public string PublishLogs { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public string PublishedAtDisplay => PublishedAtUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "-";
    public bool CanRetry => string.Equals(RabbitMqStatus, TransactionReupStatuses.PublishFailed, StringComparison.OrdinalIgnoreCase);
}

public sealed record TransactionReupSourceRow(
    int RowNumber,
    string CreatedAtText,
    string UpdatedAtText,
    string TransactionCode,
    string RequestInvoiceCode,
    string OriginalRequestCode,
    string InvoiceCreatorName,
    string TransactionType,
    string PaymentMethod,
    string BankOrCard,
    decimal TotalAmountVnd,
    decimal ProcessingFee,
    string TransferContent,
    string FeeBearer,
    decimal ReceivedAmount,
    string SourceStatus,
    IReadOnlyDictionary<string, string> Values);

public sealed record TransactionReupImportResult(
    int BatchId,
    string Message,
    int FirstInvoiceNumber,
    int LastInvoiceNumber,
    int NextInvoiceNumber);

