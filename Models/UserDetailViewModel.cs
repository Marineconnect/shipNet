using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace StarlinkDeviceManager.Models;

public class UserDetailViewModel
{
    public int Id { get; set; }

    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Tên không được để trống")]
    [StringLength(250, ErrorMessage = "Tên tối đa 250 ký tự")]
    [Display(Name = "Tên")]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(50, ErrorMessage = "Phone tối đa 50 ký tự")]
    [Display(Name = "Phone")]
    public string? Phone { get; set; }

    [StringLength(50, ErrorMessage = "Email tối đa 50 ký tự")]
    [EmailAddress(ErrorMessage = "Email không đúng định dạng")]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [StringLength(50, ErrorMessage = "Identification Number tối đa 50 ký tự")]
    [Display(Name = "Identification Number")]
    public string? IdentificationNumber { get; set; }

    public string? ExistingLogoPath { get; set; }

    [Display(Name = "Logo")]
    public IFormFile? LogoFile { get; set; }

    [StringLength(100, ErrorMessage = "Mật khẩu hiện tại tối đa 100 ký tự")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu hiện tại")]
    public string? CurrentPassword { get; set; }

    [StringLength(100, ErrorMessage = "Mật khẩu mới tối đa 100 ký tự")]
    [DataType(DataType.Password)]
    [Display(Name = "Mật khẩu mới")]
    public string? NewPassword { get; set; }

    [StringLength(100, ErrorMessage = "Xác nhận mật khẩu tối đa 100 ký tự")]
    [DataType(DataType.Password)]
    [Display(Name = "Xác nhận mật khẩu mới")]
    [Compare(nameof(NewPassword), ErrorMessage = "Xác nhận mật khẩu mới không khớp")]
    public string? ConfirmNewPassword { get; set; }
}
