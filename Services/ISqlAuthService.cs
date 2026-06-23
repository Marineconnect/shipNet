using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public interface ISqlAuthService
{
    Task<AuthUserRecord?> GetUserByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<AuthUserRecord?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default);
    Task<UserManagementPageResult> GetManagedUsersAsync(int page, int pageSize, int? tenantId = null, int? deviceId = null, string? userGroup = null, CancellationToken cancellationToken = default);
    Task<UserManagementFormViewModel?> GetManagedUserByIdAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    Task<List<UserVesselOptionViewModel>> GetVesselOptionsAsync(int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default);
    PasswordVerificationResult VerifyPassword(string rawPassword, string storedPassword);
    string EncodePassword(string rawPassword);
    Task UpdateLoginAuditAsync(string username, string? ipAddress, CancellationToken cancellationToken = default);
    Task UpdateUserProfileAsync(
        int id,
        string displayName,
        string? phone,
        string? email,
        string? identificationNumber,
        string? avatar,
        string auditDetail,
        CancellationToken cancellationToken = default);
    Task<bool> IsUsernameInUseAsync(string username, int? excludeUserId, CancellationToken cancellationToken = default);
    Task<bool> IsEmailInUseAsync(string email, int excludeUserId, CancellationToken cancellationToken = default);
    Task<int> CreateManagedUserAsync(
        UserManagementFormViewModel model,
        string encodedPassword,
        int? auditUserId,
        string auditUsername,
        CancellationToken cancellationToken = default);
    Task UpdateManagedUserAsync(
        UserManagementFormViewModel model,
        int? auditUserId,
        string auditUsername,
        CancellationToken cancellationToken = default);
    Task UpdateUserPasswordAsync(
        int id,
        string encodedPassword,
        string auditDetail,
        CancellationToken cancellationToken = default);
    Task InsertUserAuditAsync(
        int? userId,
        string logAction,
        string logDetail,
        CancellationToken cancellationToken = default);
}
