using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

public class AccountController(
    ISqlAuthService authService,
    IWebHostEnvironment environment,
    ILogger<AccountController> logger) : Controller
{
    private const string LoginAsUserAuditAction = "login_as_user";
    private const string LoginViewPath = "~/Views/Account/Login.cshtml";
    private const string UserDetailViewPath = "~/Views/Account/UserDetail.cshtml";
    private const long MaxLogoSizeBytes = 3 * 1024 * 1024;
    private const string UserDetailSaveSuccessMessage = "Cập nhật thông tin người dùng thành công.";
    private const string UserDetailSaveErrorMessage = "Không thể lưu thông tin người dùng. Vui lòng thử lại.";
    private const string UserDetailPasswordSuccessMessage = "Đổi mật khẩu thành công.";
    private const string UserDetailPasswordErrorMessage = "Không thể đổi mật khẩu. Vui lòng thử lại.";
    private const string DuplicateEmailErrorMessage = "Email đã được sử dụng bởi tài khoản khác.";
    private static readonly HashSet<string> AllowedLogoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif"
    };

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View(LoginViewPath, new LoginViewModel());
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;

        model.Username = model.Username.Trim();

        if (!ModelState.IsValid)
        {
            return View(LoginViewPath, model);
        }

        var user = await authService.GetUserByUsernameAsync(model.Username, HttpContext.RequestAborted);
        var verification = user is null
            ? new PasswordVerificationResult()
            : authService.VerifyPassword(model.Password, user.Password);

        if (user is null || !verification.IsValid)
        {
            ModelState.AddModelError(string.Empty, "Sai tên đăng nhập hoặc mật khẩu");
            return View(LoginViewPath, model);
        }

        var status = user.Status.Trim().ToLowerInvariant();
        if (status == "inactive")
        {
            ModelState.AddModelError(string.Empty, "Tài khoản hiện đang bị vô hiệu hóa");
            return View(LoginViewPath, model);
        }

        if (status == "locked")
        {
            ModelState.AddModelError(string.Empty, "Tài khoản hiện đang bị khóa");
            return View(LoginViewPath, model);
        }

        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("DisplayName", displayName),
            new Claim("UserType", ManagedUserType.NormalizeGroup(user.UserType)),
            new Claim("IsViewOnly", user.IsViewOnly ? "true" : "false")
        };

        if (!string.IsNullOrWhiteSpace(user.Avatar))
        {
            claims.Add(new Claim("Avatar", user.Avatar));
        }

        if (user.TenantId.HasValue && user.TenantId.Value > 0)
        {
            claims.Add(new Claim("TenantID", user.TenantId.Value.ToString()));
        }

        if (user.DeviceId.HasValue && user.DeviceId.Value > 0)
        {
            claims.Add(new Claim("DeviceID", user.DeviceId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var issuedUtc = DateTimeOffset.UtcNow;
        await SignInUserAsync(
            user,
            new AuthenticationProperties
            {
                IsPersistent = model.RememberMe,
                IssuedUtc = issuedUtc,
                ExpiresUtc = issuedUtc.AddHours(24)
            });

        var ipAddress = GetClientIpAddress();
        await authService.UpdateLoginAuditAsync(user.Username, ipAddress, HttpContext.RequestAborted);

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction("Index", "Dashboard");
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LoginAsUser(int id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        if (currentUser.IsCrew)
        {
            TempData["UserManagementError"] = "Tài khoản thuyền viên không có quyền đăng nhập bằng tài khoản khác.";
            return RedirectToAction("Index", "Dashboard");
        }

        var allowedTenantId = currentUser.IsTenantUser || currentUser.IsShipAdmin ? currentUser.TenantId ?? -1 : (int?)null;
        var allowedDeviceId = currentUser.IsShipAdmin ? currentUser.DeviceId ?? -1 : (int?)null;
        var targetSummary = await authService.GetManagedUserByIdAsync(id, allowedTenantId, allowedDeviceId, HttpContext.RequestAborted);
        if (targetSummary is null)
        {
            TempData["UserManagementError"] = "Không tìm thấy tài khoản hoặc bạn không có quyền đăng nhập bằng tài khoản này.";
            return RedirectToAction("Index", "User");
        }

        var targetUser = await authService.GetUserByIdAsync(id, HttpContext.RequestAborted);
        if (targetUser is null)
        {
            TempData["UserManagementError"] = "Không tìm thấy tài khoản cần đăng nhập.";
            return RedirectToAction("Index", "User");
        }

        var status = targetUser.Status.Trim().ToLowerInvariant();
        if (status == "inactive")
        {
            TempData["UserManagementError"] = "Không thể đăng nhập bằng tài khoản đang bị vô hiệu hóa.";
            return RedirectToAction("Index", "User");
        }

        if (status == "locked")
        {
            TempData["UserManagementError"] = "Không thể đăng nhập bằng tài khoản đang bị khóa.";
            return RedirectToAction("Index", "User");
        }

        var loginProperties = new AuthenticationProperties
        {
            IsPersistent = false,
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(24)
        };

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await SignInUserAsync(targetUser, loginProperties);

        var ipAddress = GetClientIpAddress();
        await authService.UpdateLoginAuditAsync(targetUser.Username, ipAddress, HttpContext.RequestAborted);
        await authService.InsertUserAuditAsync(
            currentUser.Id,
            LoginAsUserAuditAction,
            $"User '{currentUser.Username}' signed in as '{targetUser.Username}' (ID: {targetUser.Id}).",
            HttpContext.RequestAborted);

        return RedirectToAction("Index", "Dashboard");
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> UserDetail()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return NotFound();
        }

        return View(UserDetailViewPath, MapUserDetailViewModel(user));
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UserDetail(UserDetailViewModel model)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return NotFound();
        }

        model.Id = user.Id;
        model.Username = user.Username;
        model.ExistingLogoPath = user.Avatar;

        ValidateLogoFile(model.LogoFile);
        if (!ModelState.IsValid)
        {
            return View(UserDetailViewPath, model);
        }

        var displayName = model.DisplayName.Trim();
        var phone = NormalizeOptionalValue(model.Phone);
        var email = NormalizeOptionalValue(model.Email);
        var identificationNumber = NormalizeOptionalValue(model.IdentificationNumber);

        if (!string.IsNullOrWhiteSpace(email) &&
            await authService.IsEmailInUseAsync(email, user.Id, HttpContext.RequestAborted))
        {
            ModelState.AddModelError(nameof(UserDetailViewModel.Email), DuplicateEmailErrorMessage);
            return View(UserDetailViewPath, model);
        }

        try
        {
            var avatarPath = user.Avatar;
            if (model.LogoFile is not null)
            {
                avatarPath = await SaveLogoAsync(user.Id, model.LogoFile, HttpContext.RequestAborted);
            }

            await authService.UpdateUserProfileAsync(
                user.Id,
                displayName,
                phone,
                email,
                identificationNumber,
                avatarPath,
                BuildUserProfileAuditDetail(user, displayName, phone, email, identificationNumber, model.LogoFile is not null),
                HttpContext.RequestAborted);

            user.DisplayName = displayName;
            user.Phone = phone;
            user.Email = email;
            user.IdentificationNumber = identificationNumber;
            user.Avatar = avatarPath;

            await RefreshPrincipalAsync(user);

            TempData["UserDetailSuccess"] = UserDetailSaveSuccessMessage;
            return RedirectToAction(nameof(UserDetail));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update user detail for user id {UserId}.", user.Id);
            ModelState.AddModelError(string.Empty, UserDetailSaveErrorMessage);
            return View(UserDetailViewPath, model);
        }
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(UserDetailViewModel model)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return NotFound();
        }

        var viewModel = MapUserDetailViewModel(user);
        ValidatePasswordChange(model, user);
        if (!ModelState.IsValid)
        {
            return View(UserDetailViewPath, viewModel);
        }

        try
        {
            var encodedPassword = authService.EncodePassword(model.NewPassword!);
            await authService.UpdateUserPasswordAsync(
                user.Id,
                encodedPassword,
                BuildPasswordChangeAuditDetail(user),
                HttpContext.RequestAborted);

            var redirectUrl = Url.Action(nameof(UserDetail)) ?? "/Account/UserDetail";
            TempData["UserDetailSuccess"] = UserDetailPasswordSuccessMessage;
            return LocalRedirect($"{redirectUrl}#password-section");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to change password for user id {UserId}.", user.Id);
            ModelState.AddModelError(string.Empty, UserDetailPasswordErrorMessage);
            return View(UserDetailViewPath, viewModel);
        }
    }

    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    private string? GetClientIpAddress()
    {
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            return forwardedFor.Split(',')[0].Trim();
        }

        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp is null)
        {
            return null;
        }

        if (remoteIp.IsIPv4MappedToIPv6)
        {
            return remoteIp.MapToIPv4().ToString();
        }

        if (IPAddress.IsLoopback(remoteIp))
        {
            return remoteIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? "::1"
                : "127.0.0.1";
        }

        return remoteIp.ToString();
    }

    private async Task<AuthUserRecord?> GetCurrentUserAsync()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, out var userId))
        {
            return null;
        }

        return await authService.GetUserByIdAsync(userId, HttpContext.RequestAborted);
    }

    private UserDetailViewModel MapUserDetailViewModel(AuthUserRecord user)
    {
        return new UserDetailViewModel
        {
            Id = user.Id,
            Username = user.Username,
            DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
            Phone = user.Phone,
            Email = user.Email,
            IdentificationNumber = user.IdentificationNumber,
            ExistingLogoPath = user.Avatar
        };
    }

    private void ValidatePasswordChange(UserDetailViewModel model, AuthUserRecord user)
    {
        if (string.IsNullOrWhiteSpace(model.CurrentPassword))
        {
            ModelState.AddModelError(nameof(UserDetailViewModel.CurrentPassword), "Vui lòng nhập mật khẩu hiện tại.");
        }

        if (string.IsNullOrWhiteSpace(model.NewPassword))
        {
            ModelState.AddModelError(nameof(UserDetailViewModel.NewPassword), "Vui lòng nhập mật khẩu mới.");
        }

        if (string.IsNullOrWhiteSpace(model.ConfirmNewPassword))
        {
            ModelState.AddModelError(nameof(UserDetailViewModel.ConfirmNewPassword), "Vui lòng xác nhận mật khẩu mới.");
        }

        if (!ModelState.IsValid)
        {
            return;
        }

        if (!authService.VerifyPassword(model.CurrentPassword!, user.Password).IsValid)
        {
            ModelState.AddModelError(nameof(UserDetailViewModel.CurrentPassword), "Mật khẩu hiện tại không chính xác.");
        }

        if (string.Equals(model.CurrentPassword, model.NewPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError(nameof(UserDetailViewModel.NewPassword), "Mật khẩu mới phải khác mật khẩu hiện tại.");
        }
    }

    private static string BuildUserProfileAuditDetail(
        AuthUserRecord user,
        string displayName,
        string? phone,
        string? email,
        string? identificationNumber,
        bool hasNewLogo)
    {
        var changedFields = new List<string>();
        if (!string.Equals(NormalizeOptionalValue(user.DisplayName), displayName, StringComparison.Ordinal))
        {
            changedFields.Add("DisplayName");
        }

        if (!string.Equals(NormalizeOptionalValue(user.Phone), phone, StringComparison.Ordinal))
        {
            changedFields.Add("Phone");
        }

        if (!string.Equals(NormalizeOptionalValue(user.Email), email, StringComparison.OrdinalIgnoreCase))
        {
            changedFields.Add("Email");
        }

        if (!string.Equals(NormalizeOptionalValue(user.IdentificationNumber), identificationNumber, StringComparison.Ordinal))
        {
            changedFields.Add("IdentificationNumber");
        }

        if (hasNewLogo)
        {
            changedFields.Add("Avatar");
        }

        var detailBuilder = new StringBuilder();
        detailBuilder.Append("Updated profile for user '")
            .Append(user.Username)
            .Append("'. Changed fields: ");

        detailBuilder.Append(changedFields.Count == 0
            ? "none"
            : string.Join(", ", changedFields));

        return detailBuilder.ToString();
    }

    private static string BuildPasswordChangeAuditDetail(AuthUserRecord user)
    {
        return $"Changed password for user '{user.Username}'.";
    }

    private void ValidateLogoFile(IFormFile? logoFile)
    {
        if (logoFile is null || logoFile.Length == 0)
        {
            return;
        }

        var extension = Path.GetExtension(logoFile.FileName);
        if (!AllowedLogoExtensions.Contains(extension))
        {
            ModelState.AddModelError(nameof(UserDetailViewModel.LogoFile), "Logo chỉ hỗ trợ các file JPG, PNG, WEBP hoặc GIF.");
        }

        if (logoFile.Length > MaxLogoSizeBytes)
        {
            ModelState.AddModelError(nameof(UserDetailViewModel.LogoFile), "Logo tối đa 3MB.");
        }
    }

    private async Task<string> SaveLogoAsync(int userId, IFormFile logoFile, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(logoFile.FileName).ToLowerInvariant();
        var uploadsRoot = Path.Combine(environment.WebRootPath, "uploads", "user-logos");
        Directory.CreateDirectory(uploadsRoot);

        var fileName = $"{userId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}";
        var filePath = Path.Combine(uploadsRoot, fileName);

        await using var stream = System.IO.File.Create(filePath);
        await logoFile.CopyToAsync(stream, cancellationToken);

        return $"/uploads/user-logos/{fileName}";
    }

    private async Task RefreshPrincipalAsync(AuthUserRecord user)
    {
        var authResult = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = authResult.Properties ?? new AuthenticationProperties();
        await SignInUserAsync(user, properties);
    }

    private async Task SignInUserAsync(AuthUserRecord user, AuthenticationProperties properties)
    {
        var principal = BuildPrincipal(user);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            properties);
    }

    private static ClaimsPrincipal BuildPrincipal(AuthUserRecord user)
    {
        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("DisplayName", displayName),
            new Claim("UserType", ManagedUserType.NormalizeGroup(user.UserType)),
            new Claim("IsViewOnly", user.IsViewOnly ? "true" : "false")
        };

        if (!string.IsNullOrWhiteSpace(user.Avatar))
        {
            claims.Add(new Claim("Avatar", user.Avatar));
        }

        if (user.TenantId.HasValue && user.TenantId.Value > 0)
        {
            claims.Add(new Claim("TenantID", user.TenantId.Value.ToString()));
        }

        if (user.DeviceId.HasValue && user.DeviceId.Value > 0)
        {
            claims.Add(new Claim("DeviceID", user.DeviceId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
