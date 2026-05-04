using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public class UserController(
    ISqlAuthService authService,
    ITenantService tenantService,
    IWebHostEnvironment environment,
    ILogger<UserController> logger) : Controller
{
    private const string UserIndexViewPath = "~/Views/User/Index.cshtml";
    private const long MaxLogoSizeBytes = 3 * 1024 * 1024;
    private const string DuplicateUsernameErrorMessage = "Username đã tồn tại.";
    private const string DuplicateEmailErrorMessage = "Email đã được sử dụng bởi tài khoản khác.";
    private const string UserSaveErrorMessage = "Không thể lưu người dùng. Vui lòng thử lại.";
    private static readonly HashSet<string> AllowedLogoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif"
    };

    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, int pageSize = 10)
    {
        var currentUser = await GetCurrentUserAsync();
        return View(UserIndexViewPath, await BuildIndexViewModelAsync(currentUser, page: page, pageSize: pageSize));
    }

    [HttpGet]
    public IActionResult Create()
    {
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(UserManagementIndexViewModel requestModel)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser?.IsTenantUser == true)
        {
            requestModel.CreateForm.UserGroup = ManagedUserType.Tenant;
            requestModel.CreateForm.TenantId = currentUser.TenantId;
        }

        var model = requestModel.CreateForm;
        NormalizeUserModel(model);
        RemoveModelStateForPrefix(nameof(UserManagementIndexViewModel.EditForm));
        ValidateUserForm(model, nameof(UserManagementIndexViewModel.CreateForm), requirePassword: true);

        if (await ValidateDuplicateFieldsAsync(model, nameof(UserManagementIndexViewModel.CreateForm), excludeUserId: null))
        {
            ModelState.AddModelError(
                $"{nameof(UserManagementIndexViewModel.CreateForm)}.{nameof(UserManagementFormViewModel.Password)}",
                "Vui lòng nhập mật khẩu.");
        }

        if (!ModelState.IsValid)
        {
            return View(
                UserIndexViewPath,
                await BuildIndexViewModelAsync(
                    currentUser,
                    createForm: model,
                    openCreateModal: true,
                    page: model.CurrentPage,
                    pageSize: model.PageSize));
        }

        var (userId, username) = GetCurrentAuditContext();

        try
        {
            if (model.LogoFile is not null)
            {
                model.ExistingLogoPath = await SaveLogoAsync(model.LogoFile, HttpContext.RequestAborted);
            }

            var encodedPassword = authService.EncodePassword(model.Password!);
            await authService.CreateManagedUserAsync(model, encodedPassword, userId, username, HttpContext.RequestAborted);
            TempData["UserManagementSuccess"] = "Thêm người dùng thành công.";
            return RedirectToAction(nameof(Index), new { page = 1, pageSize = model.PageSize });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to create user.");
            ModelState.AddModelError(string.Empty, BuildUserSaveErrorMessage(exception));
            return View(
                UserIndexViewPath,
                await BuildIndexViewModelAsync(
                    currentUser,
                    createForm: model,
                    openCreateModal: true,
                    page: model.CurrentPage,
                    pageSize: model.PageSize));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, int page = 1, int pageSize = 10)
    {
        var currentUser = await GetCurrentUserAsync();
        var user = await authService.GetManagedUserByIdAsync(id, GetAllowedTenantId(currentUser), HttpContext.RequestAborted);
        if (user is null)
        {
            return NotFound();
        }

        user.CurrentPage = page;
        user.PageSize = pageSize;
        return View(
            UserIndexViewPath,
            await BuildIndexViewModelAsync(
                currentUser,
                editForm: user,
                openEditModal: true,
                page: page,
                pageSize: pageSize));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(UserManagementIndexViewModel requestModel)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser?.IsTenantUser == true)
        {
            requestModel.EditForm.UserGroup = ManagedUserType.Tenant;
            requestModel.EditForm.TenantId = currentUser.TenantId;
        }

        var model = requestModel.EditForm;
        NormalizeUserModel(model);
        RemoveModelStateForPrefix(nameof(UserManagementIndexViewModel.CreateForm));
        ValidateUserForm(model, nameof(UserManagementIndexViewModel.EditForm), requirePassword: false);
        await ValidateDuplicateFieldsAsync(model, nameof(UserManagementIndexViewModel.EditForm), model.Id);

        if (!ModelState.IsValid)
        {
            return View(
                UserIndexViewPath,
                await BuildIndexViewModelAsync(
                    currentUser,
                    editForm: model,
                    openEditModal: true,
                    page: model.CurrentPage,
                    pageSize: model.PageSize));
        }

        var (userId, username) = GetCurrentAuditContext();

        try
        {
            var existingUser = await authService.GetManagedUserByIdAsync(model.Id, GetAllowedTenantId(currentUser), HttpContext.RequestAborted);
            if (existingUser is null)
            {
                return NotFound();
            }

            model.ExistingLogoPath = existingUser.ExistingLogoPath;
            model.Status = existingUser.Status;

            if (model.LogoFile is not null)
            {
                model.ExistingLogoPath = await SaveLogoAsync(model.LogoFile, HttpContext.RequestAborted);
            }

            await authService.UpdateManagedUserAsync(model, userId, username, HttpContext.RequestAborted);
            TempData["UserManagementSuccess"] = "Cập nhật người dùng thành công.";
            return RedirectToAction(nameof(Index), new { page = model.CurrentPage, pageSize = model.PageSize });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update user id {UserId}.", model.Id);
            ModelState.AddModelError(string.Empty, BuildUserSaveErrorMessage(exception));
            return View(
                UserIndexViewPath,
                await BuildIndexViewModelAsync(
                    currentUser,
                    editForm: model,
                    openEditModal: true,
                    page: model.CurrentPage,
                    pageSize: model.PageSize));
        }
    }

    private async Task<UserManagementIndexViewModel> BuildIndexViewModelAsync(
        AuthUserRecord? currentUser,
        UserManagementFormViewModel? createForm = null,
        bool openCreateModal = false,
        UserManagementFormViewModel? editForm = null,
        bool openEditModal = false,
        int page = 1,
        int pageSize = 10)
    {
        var allowedTenantId = GetAllowedTenantId(currentUser);
        var userPage = await authService.GetManagedUsersAsync(page, pageSize, allowedTenantId, HttpContext.RequestAborted);
        var tenants = await tenantService.GetTenantOptionsAsync(allowedTenantId, HttpContext.RequestAborted);
        var resolvedPage = userPage.CurrentPage;
        var resolvedPageSize = userPage.PageSize;

        createForm ??= new UserManagementFormViewModel();
        createForm.CurrentPage = resolvedPage;
        createForm.PageSize = resolvedPageSize;
        if (currentUser?.IsTenantUser == true)
        {
            createForm.UserGroup = ManagedUserType.Tenant;
            createForm.TenantId = currentUser.TenantId;
        }

        editForm ??= new UserManagementFormViewModel();
        editForm.CurrentPage = resolvedPage;
        editForm.PageSize = resolvedPageSize;
        if (currentUser?.IsTenantUser == true)
        {
            editForm.UserGroup = ManagedUserType.Tenant;
            editForm.TenantId = currentUser.TenantId;
        }

        return new UserManagementIndexViewModel
        {
            Users = userPage.Users,
            CreateForm = createForm,
            EditForm = editForm,
            Tenants = tenants,
            OpenCreateModal = openCreateModal,
            OpenEditModal = openEditModal,
            CurrentPage = resolvedPage,
            PageSize = resolvedPageSize,
            TotalUsers = userPage.TotalUsers,
            IsTenantScoped = currentUser?.IsTenantUser == true,
            CurrentTenantId = currentUser?.TenantId,
            CurrentTenantName = tenants.FirstOrDefault(tenant => tenant.Id == currentUser?.TenantId)?.TenantName
        };
    }

    private async Task<bool> ValidateDuplicateFieldsAsync(UserManagementFormViewModel model, string prefix, int? excludeUserId)
    {
        var hasPasswordError = false;

        if (await authService.IsUsernameInUseAsync(model.Username, excludeUserId, HttpContext.RequestAborted))
        {
            ModelState.AddModelError($"{prefix}.{nameof(UserManagementFormViewModel.Username)}", DuplicateUsernameErrorMessage);
        }

        if (!string.IsNullOrWhiteSpace(model.Email) &&
            await authService.IsEmailInUseAsync(model.Email, excludeUserId ?? 0, HttpContext.RequestAborted))
        {
            ModelState.AddModelError($"{prefix}.{nameof(UserManagementFormViewModel.Email)}", DuplicateEmailErrorMessage);
        }

        if (excludeUserId is null && string.IsNullOrWhiteSpace(model.Password))
        {
            hasPasswordError = true;
        }

        return hasPasswordError;
    }

    private (int? UserId, string Username) GetCurrentAuditContext()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
        var username = User.Identity?.Name;

        return (userId, string.IsNullOrWhiteSpace(username) ? "system" : username);
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

    private static int? GetAllowedTenantId(AuthUserRecord? user)
    {
        return user?.IsTenantUser == true ? user.TenantId ?? -1 : null;
    }

    private void ValidateUserForm(UserManagementFormViewModel model, string prefix, bool requirePassword)
    {
        ValidateLogoFile(model.LogoFile, $"{prefix}.{nameof(UserManagementFormViewModel.LogoFile)}");

        if (requirePassword && string.IsNullOrWhiteSpace(model.Password))
        {
            ModelState.AddModelError($"{prefix}.{nameof(UserManagementFormViewModel.Password)}", "Vui lòng nhập mật khẩu.");
        }

        if (string.Equals(model.UserGroup, ManagedUserType.Tenant, StringComparison.OrdinalIgnoreCase))
        {
            if (!model.TenantId.HasValue || model.TenantId.Value <= 0)
            {
                ModelState.AddModelError($"{prefix}.{nameof(UserManagementFormViewModel.TenantId)}", "Vui lòng chọn tenant.");
            }
        }
        else
        {
            model.TenantId = null;
        }
    }

    private void ValidateLogoFile(IFormFile? logoFile, string modelKey)
    {
        if (logoFile is null || logoFile.Length == 0)
        {
            return;
        }

        var extension = Path.GetExtension(logoFile.FileName);
        if (!AllowedLogoExtensions.Contains(extension))
        {
            ModelState.AddModelError(modelKey, "Logo chỉ hỗ trợ các file JPG, PNG, WEBP hoặc GIF.");
        }

        if (logoFile.Length > MaxLogoSizeBytes)
        {
            ModelState.AddModelError(modelKey, "Logo tối đa 3MB.");
        }
    }

    private void RemoveModelStateForPrefix(string prefix)
    {
        var keys = ModelState.Keys
            .Where(key => key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith($"{prefix}.", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keys)
        {
            ModelState.Remove(key);
        }
    }

    private async Task<string> SaveLogoAsync(IFormFile logoFile, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(logoFile.FileName).ToLowerInvariant();
        var uploadsRoot = Path.Combine(environment.WebRootPath, "uploads", "user-logos");
        Directory.CreateDirectory(uploadsRoot);

        var fileName = $"managed-user-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}";
        var filePath = Path.Combine(uploadsRoot, fileName);

        await using var stream = System.IO.File.Create(filePath);
        await logoFile.CopyToAsync(stream, cancellationToken);

        return $"/uploads/user-logos/{fileName}";
    }

    private static void NormalizeUserModel(UserManagementFormViewModel model)
    {
        model.Username = (model.Username ?? string.Empty).Trim();
        model.DisplayName = (model.DisplayName ?? string.Empty).Trim();
        model.Phone = NormalizeOptionalValue(model.Phone);
        model.Email = NormalizeOptionalValue(model.Email);
        model.IdentificationNumber = NormalizeOptionalValue(model.IdentificationNumber);
        model.Password = NormalizeOptionalValue(model.Password);
        model.UserGroup = ManagedUserType.NormalizeGroup(model.UserGroup);
        model.Status = string.IsNullOrWhiteSpace(model.Status) ? "active" : model.Status.Trim();
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string BuildUserSaveErrorMessage(Exception exception)
    {
        var rootException = exception.GetBaseException();

        if (rootException is IOException)
        {
            return "Không thể lưu người dùng vì upload logo thất bại. Vui lòng thử lại với file khác.";
        }

        if (rootException is SqlException sqlException)
        {
            if (sqlException.Number is 2627 or 2601)
            {
                return "Không thể lưu người dùng vì username hoặc dữ liệu đang bị trùng.";
            }

            if (sqlException.Number == 8152 || sqlException.Message.Contains("truncated", StringComparison.OrdinalIgnoreCase))
            {
                return "Không thể lưu người dùng vì có trường vượt quá độ dài cho phép của cơ sở dữ liệu.";
            }

            if (sqlException.Number == -2)
            {
                return "Không thể lưu người dùng vì thao tác với cơ sở dữ liệu bị quá thời gian chờ.";
            }

            if (sqlException.Number is 53 or 4060 or 10060 or 233)
            {
                return "Không thể lưu người dùng vì không kết nối được tới cơ sở dữ liệu.";
            }

            return $"Không thể lưu người dùng do lỗi cơ sở dữ liệu: {sqlException.Message}";
        }

        if (rootException is InvalidOperationException invalidOperationException)
        {
            return $"Không thể lưu người dùng: {invalidOperationException.Message}";
        }

        return $"{UserSaveErrorMessage} Chi tiết: {rootException.Message}";
    }
}
