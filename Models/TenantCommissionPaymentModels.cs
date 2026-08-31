using System.ComponentModel.DataAnnotations;

namespace StarlinkDeviceManager.Models;

public sealed class TenantCommissionPaymentIndexViewModel
{
    public List<DeviceTenantOptionViewModel> Tenants { get; set; } = [];
    public List<TenantCommissionPaymentListItemViewModel> Payments { get; set; } = [];
    public TenantCommissionBalanceViewModel Balance { get; set; } = new();
    public int? TenantId { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Search { get; set; }
    public bool IsTenantScoped { get; set; }
    public bool CanCreate { get; set; }
}

public sealed class TenantCommissionBalanceViewModel
{
    public decimal GrossCommission { get; set; }
    public decimal PaidCommission { get; set; }
    public decimal RemainingCommission { get; set; }
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
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class TenantCommissionManualPaymentInput
{
    [Range(1, int.MaxValue)]
    public int TenantId { get; set; }

    [Required]
    public DateTime PaymentDate { get; set; }

    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }

    [Range(typeof(decimal), "0.01", "9999999999999999")]
    public decimal Amount { get; set; }

    [StringLength(1000)]
    public string? Note { get; set; }
}

public sealed class TenantCommissionCyclePaymentInput
{
    [Range(1, int.MaxValue)]
    public int TenantId { get; set; }

    [Required]
    public DateTime PaymentDate { get; set; }

    [MinLength(1)]
    public List<int> SubscriptionIds { get; set; } = [];

    [StringLength(1000)]
    public string? Note { get; set; }
}

public sealed class EligibleCommissionBillingCycleViewModel
{
    public int SubscriptionId { get; set; }
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string VesselName { get; set; } = string.Empty;
    public string KitId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public DateTime UsageMonth { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal CommissionAmount { get; set; }
    public int PaidInvoiceCount { get; set; }
}
