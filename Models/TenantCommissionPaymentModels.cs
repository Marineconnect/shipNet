using System.ComponentModel.DataAnnotations;

namespace StarlinkDeviceManager.Models;

public static class TenantCommissionPaymentSourceModes
{
    public const string Manual = "manual";
    public const string BillingCycles = "billing_cycles";
}

public sealed class TenantCommissionPaymentIndexViewModel
{
    public TenantCommissionBalanceViewModel Balance { get; set; } = new();
    public TenantCommissionPaymentFilterViewModel Filter { get; set; } = new();
    public List<TenantCommissionPaymentListItemViewModel> Payments { get; set; } = [];
    public List<DeviceTenantOptionViewModel> Tenants { get; set; } = [];
    public bool IsTenantScoped { get; set; }
    public bool CanCreatePayment { get; set; }
    public bool IsTransactionReupAdmin { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalItems { get; set; }
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalItems / (double)PageSize));
    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public int StartItem => TotalItems == 0 ? 0 : ((CurrentPage - 1) * PageSize) + 1;
    public int EndItem => Math.Min(TotalItems, CurrentPage * PageSize);
}

public sealed class TenantCommissionPaymentFilterViewModel
{
    public int? TenantId { get; set; }
    public DateTime? PaymentDateFrom { get; set; }
    public DateTime? PaymentDateTo { get; set; }
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public string? SourceMode { get; set; }
    public string? Keyword { get; set; }
    public string SortBy { get; set; } = "paymentDate";
    public string SortDirection { get; set; } = "desc";
    public int? TenantIdScope { get; set; }
}

public sealed class TenantCommissionPaymentPageResult
{
    public List<TenantCommissionPaymentListItemViewModel> Payments { get; set; } = [];
    public int CurrentPage { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
}

public sealed class TenantCommissionBalanceViewModel
{
    public decimal GrossCommission { get; set; }
    public decimal PaidCommission { get; set; }
    public decimal RemainingCommission => Math.Max(0, GrossCommission - PaidCommission);
    public int PaymentCount { get; set; }
}

public sealed class TenantCommissionPaymentListItemViewModel
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public DateTime PaymentDate { get; set; }
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public decimal Amount { get; set; }
    public string SourceMode { get; set; } = string.Empty;
    public int BillingCycleCount { get; set; }
    public string Note { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public sealed class TenantCommissionPaymentDetailViewModel
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public DateTime PaymentDate { get; set; }
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public decimal Amount { get; set; }
    public string SourceMode { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public int? CreatedByUserId { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<TenantCommissionPaymentItemViewModel> Items { get; set; } = [];
}

public sealed class TenantCommissionPaymentItemViewModel
{
    public long Id { get; set; }
    public int SubscriptionId { get; set; }
    public string BillingCycle { get; set; } = string.Empty;
    public DateTime UsageMonth { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string VesselName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string KitId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string InvoiceNumbers { get; set; } = string.Empty;
    public string TransactionReferences { get; set; } = string.Empty;
    public decimal CommissionAmount { get; set; }
}

public sealed class TenantCommissionManualPaymentInput
{
    [Required]
    public int TenantId { get; set; }
    [Required]
    public DateTime? PaymentDate { get; set; }
    [Required]
    public DateTime? PeriodFrom { get; set; }
    [Required]
    public DateTime? PeriodTo { get; set; }
    [Range(typeof(decimal), "0.01", "999999999999999")]
    public decimal Amount { get; set; }
    [StringLength(1000)]
    public string? Note { get; set; }
}

public sealed class TenantCommissionCyclePaymentInput
{
    [Required]
    public int TenantId { get; set; }
    [Required]
    public DateTime? PaymentDate { get; set; }
    [StringLength(1000)]
    public string? Note { get; set; }
    public List<int> SubscriptionIds { get; set; } = [];
}

public sealed class EligibleCommissionBillingCycleViewModel
{
    public int SubscriptionId { get; set; }
    public string BillingCycle { get; set; } = string.Empty;
    public DateTime UsageMonth { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string VesselName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string KitId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public int InvoiceCount { get; set; }
    public decimal CommissionAmount { get; set; }
}
