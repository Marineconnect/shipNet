using System.ComponentModel.DataAnnotations;

namespace StarlinkDeviceManager.Models;

public class SystemSettingsIndexViewModel
{
    public List<SystemSettingViewModel> Settings { get; set; } = [];
    public SystemSettingFormViewModel EditForm { get; set; } = new();
    public bool OpenEditModal { get; set; }
}

public class SystemSettingViewModel
{
    public int Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string SettingCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? SettingValue { get; set; }
    public bool IsSecret { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime? UpdatedDate { get; set; }
    public string? UpdatedBy { get; set; }
    public bool CanEdit { get; set; } = true;
    public string DisplayValue => IsSecret && !string.IsNullOrWhiteSpace(SettingValue) ? "********" : SettingValue ?? string.Empty;
    public string UpdatedDateDisplay => UpdatedDate.HasValue ? UpdatedDate.Value.ToString("dd/MM/yyyy HH:mm") : "-";
}

public class SystemSettingFormViewModel
{
    public int Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string SettingCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsSecret { get; set; }

    [StringLength(2000, ErrorMessage = "Setting value can contain up to 2000 characters.")]
    public string? SettingValue { get; set; }
}
