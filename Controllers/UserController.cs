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
    public async Task<IActionResult> Index(string group = ManagedUserType.Admin, int page = 1, int pageSize = 10)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser?.IsCrew == true)
        {
            return Forbid();
        }

        return View(UserIndexViewPath, await BuildIndexViewModelAsync(currentUser, page: page, pageSize: pageSize, activeGroup: group));
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
        if (currentUser?.IsCrew == true)
        {
            return Forbid();
        }

        ApplyCreationScope(requestModel.CreateForm, currentUser);
        if (currentUser?.IsTenantUser == true)
        {
            requestModel.CreateForm.TenantId = currentUser.TenantId;
        }

        var model = requestModel.CreateForm;
        NormalizeUserModel(model);
        RemoveModelStateForPrefix(nameof(UserManagementIndexViewModel.EditForm));
        ValidateUserForm(model, nameof(UserManagementIndexViewModel.CreateForm), requirePassword: true);
        await ValidateUserScopeAsync(model, currentUser, nameof(UserManagementIndexViewModel.CreateForm));

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
                    pageSize: model.PageSize,
                    activeGroup: model.UserGroup));
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
            return RedirectToAction(nameof(Index), new { group = ManagedUserType.NormalizeGroup(model.UserGroup), page = 1, pageSize = model.PageSize });
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
                    pageSize: model.PageSize,
                    activeGroup: model.UserGroup));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, string group = ManagedUserType.Admin, int page = 1, int pageSize = 10)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser?.IsCrew == true)
        {
            return Forbid();
        }

        var user = await authService.GetManagedUserByIdAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
        if (user is null)
        {
            return NotFound();
        }

        if (!CanManageTargetGroup(currentUser, user.UserGroup))
        {
            return Forbid();
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
                pageSize: pageSize,
                activeGroup: group));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(UserManagementIndexViewModel requestModel)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser?.IsCrew == true)
        {
            return Forbid();
        }

        ApplyCreationScope(requestModel.EditForm, currentUser);

        var model = requestModel.EditForm;
        NormalizeUserModel(model);
        RemoveModelStateForPrefix(nameof(UserManagementIndexViewModel.CreateForm));
        ValidateUserForm(model, nameof(UserManagementIndexViewModel.EditForm), requirePassword: false);
        await ValidateUserScopeAsync(model, currentUser, nameof(UserManagementIndexViewModel.EditForm));
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
                    pageSize: model.PageSize,
                    activeGroup: model.UserGroup));
        }

        var (userId, username) = GetCurrentAuditContext();

        try
        {
            var existingUser = await authService.GetManagedUserByIdAsync(model.Id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            if (existingUser is null)
            {
                return NotFound();
            }

            if (!CanManageTargetGroup(currentUser, existingUser.UserGroup) || !CanManageTargetGroup(currentUser, model.UserGroup))
            {
                return Forbid();
            }

            model.ExistingLogoPath = existingUser.ExistingLogoPath;
            model.Status = existingUser.Status;

            if (model.LogoFile is not null)
            {
                model.ExistingLogoPath = await SaveLogoAsync(model.LogoFile, HttpContext.RequestAborted);
            }

            await authService.UpdateManagedUserAsync(model, userId, username, HttpContext.RequestAborted);
            TempData["UserManagementSuccess"] = "Cập nhật người dùng thành công.";
            return RedirectToAction(nameof(Index), new { group = ManagedUserType.NormalizeGroup(model.UserGroup), page = model.CurrentPage, pageSize = model.PageSize });
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
                    pageSize: model.PageSize,
                    activeGroup: model.UserGroup));
        }
    }

    private async Task<UserManagementIndexViewModel> BuildIndexViewModelAsync(
        AuthUserRecord? currentUser,
        UserManagementFormViewModel? createForm = null,
        bool openCreateModal = false,
        UserManagementFormViewModel? editForm = null,
        bool openEditModal = false,
        int page = 1,
        int pageSize = 10,
        string activeGroup = ManagedUserType.Admin)
    {
        var allowedTenantId = GetAllowedTenantId(currentUser);
        var allowedDeviceId = GetAllowedDeviceId(currentUser);
        var visibleGroups = GetVisibleUserGroups(currentUser);
        var normalizedActiveGroup = ManagedUserType.NormalizeGroup(activeGroup);
        if (!visibleGroups.Contains(normalizedActiveGroup))
        {
            normalizedActiveGroup = visibleGroups.FirstOrDefault() ?? ManagedUserType.Crew;
        }

        var userPage = await authService.GetManagedUsersAsync(page, pageSize, allowedTenantId, allowedDeviceId, normalizedActiveGroup, HttpContext.RequestAborted);
        var tenants = await tenantService.GetTenantOptionsAsync(allowedTenantId, HttpContext.RequestAborted);
        var vessels = await authService.GetVesselOptionsAsync(allowedTenantId, allowedDeviceId, HttpContext.RequestAborted);
        var resolvedPage = userPage.CurrentPage;
        var resolvedPageSize = userPage.PageSize;
        var creatableGroups = GetCreatableUserGroups(currentUser);

        createForm ??= new UserManagementFormViewModel();
        createForm.CurrentPage = resolvedPage;
        createForm.PageSize = resolvedPageSize;
        ApplyCreationScope(createForm, currentUser);

        editForm ??= new UserManagementFormViewModel();
        editForm.CurrentPage = resolvedPage;
        editForm.PageSize = resolvedPageSize;
        ApplyCreationScope(editForm, currentUser);

        return new UserManagementIndexViewModel
        {
            Users = userPage.Users,
            CreateForm = createForm,
            EditForm = editForm,
            Tenants = tenants,
            Vessels = vessels,
            CreatableUserGroups = creatableGroups,
            OpenCreateModal = openCreateModal,
            OpenEditModal = openEditModal,
            CurrentPage = resolvedPage,
            PageSize = resolvedPageSize,
            ActiveUserGroup = normalizedActiveGroup,
            TotalUsers = userPage.TotalUsers,
            IsTenantScoped = currentUser?.IsTenantUser == true,
            CurrentTenantId = currentUser?.TenantId,
            CurrentTenantName = tenants.FirstOrDefault(tenant => tenant.Id == currentUser?.TenantId)?.TenantName,
            CanManageUsers = currentUser?.IsCrew != true
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
        return user?.IsTenantUser == true || user?.IsShipAdmin == true
            ? user.TenantId ?? -1
            : null;
    }

    private static int? GetAllowedDeviceId(AuthUserRecord? user)
    {
        return user?.IsShipAdmin == true ? user.DeviceId ?? -1 : null;
    }

    private static List<string> GetVisibleUserGroups(AuthUserRecord? user)
    {
        if (user?.IsCrew == true)
        {
            return [];
        }

        if (user?.IsShipAdmin == true)
        {
            return [ManagedUserType.Crew];
        }

        if (user?.IsTenantUser == true)
        {
            return [ManagedUserType.ShipAdmin, ManagedUserType.Crew];
        }

        return [ManagedUserType.Admin, ManagedUserType.Tenant, ManagedUserType.ShipAdmin, ManagedUserType.Crew];
    }

    private static List<string> GetCreatableUserGroups(AuthUserRecord? user)
    {
        return GetVisibleUserGroups(user);
    }

    private static bool CanManageTargetGroup(AuthUserRecord? user, string? targetGroup)
    {
        return GetVisibleUserGroups(user).Contains(ManagedUserType.NormalizeGroup(targetGroup));
    }

    private static void ApplyCreationScope(UserManagementFormViewModel model, AuthUserRecord? currentUser)
    {
        var creatableGroups = GetCreatableUserGroups(currentUser);
        var normalizedGroup = ManagedUserType.NormalizeGroup(model.UserGroup);
        model.UserGroup = creatableGroups.Contains(normalizedGroup)
            ? normalizedGroup
            : creatableGroups.FirstOrDefault() ?? ManagedUserType.Crew;

        if (currentUser?.IsTenantUser == true)
        {
            model.TenantId = currentUser.TenantId;
        }

        if (currentUser?.IsShipAdmin == true)
        {
            model.UserGroup = ManagedUserType.Crew;
            model.TenantId = currentUser.TenantId;
            model.DeviceId = currentUser.DeviceId;
        }
    }

    private void ValidateUserForm(UserManagementFormViewModel model, string prefix, bool requirePassword)
    {
        ValidateLogoFile(model.LogoFile, $"{prefix}.{nameof(UserManagementFormViewModel.LogoFile)}");

        if (requirePassword && string.IsNullOrWhiteSpace(model.Password))
        {
            ModelState.AddModelError($"{prefix}.{nameof(UserManagementFormViewModel.Password)}", "Vui lòng nhập mật khẩu.");
        }

        if (ManagedUserType.RequiresTenant(model.UserGroup))
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

        if (ManagedUserType.RequiresVessel(model.UserGroup))
        {
            if (!model.DeviceId.HasValue || model.DeviceId.Value <= 0)
            {
                ModelState.AddModelError($"{prefix}.{nameof(UserManagementFormViewModel.DeviceId)}", "Vui lòng chọn tàu.");
            }
        }
        else
        {
            model.DeviceId = null;
        }
    }

    private async Task ValidateUserScopeAsync(UserManagementFormViewModel model, AuthUserRecord? currentUser, string prefix)
    {
        if (!CanManageTargetGroup(currentUser, model.UserGroup))
        {
            ModelState.AddModelError($"{prefix}.{nameof(UserManagementFormViewModel.UserGroup)}", "Bạn không có quyền tạo nhóm tài khoản này.");
            return;
        }

        if (currentUser?.IsTenantUser == true && model.TenantId != currentUser.TenantId)
        {
            ModelState.AddModelError($"{prefix}.{nameof(UserManagementFormViewModel.TenantId)}", "Bạn chỉ được tạo tài khoản tàu trong tenant mình quản lý.");
        }

        if (currentUser?.IsShipAdmin == true && model.DeviceId != currentUser.DeviceId)
        {
            ModelState.AddModelError($"{prefix}.{nameof(UserManagementFormViewModel.DeviceId)}", "Admin tàu chỉ được tạo tài khoản cho thuyền viên của tàu mình.");
        }

        if (!ManagedUserType.RequiresVessel(model.UserGroup) || !model.DeviceId.HasValue)
        {
            return;
        }

        var matchingVessel = (await authService.GetVesselOptionsAsync(model.TenantId, model.DeviceId, HttpContext.RequestAborted)).FirstOrDefault();
        if (matchingVessel is null)
        {
            ModelState.AddModelError($"{prefix}.{nameof(UserManagementFormViewModel.DeviceId)}", "Tàu không tồn tại hoặc không thuộc phạm vi quản lý.");
        }
        else if (model.TenantId != matchingVessel.TenantId)
        {
            ModelState.AddModelError($"{prefix}.{nameof(UserManagementFormViewModel.DeviceId)}", "Tàu không thuộc tenant đã chọn.");
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
