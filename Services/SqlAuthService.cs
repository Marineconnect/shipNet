using System.Security.Cryptography;
using System.Text;
using System.Data;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public class SqlAuthService(IConfiguration configuration) : ISqlAuthService
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int IterationCount = 100_000;
    private const string HashPrefix = "PBKDF2";
    private const string CreateManagedUserAuditAction = "created_user";
    private const string UpdateManagedUserAuditAction = "updated_user";
    private const string UpdateUserProfileAuditAction = "updated_user_profile";
    private const string ChangeUserPasswordAuditAction = "changed_user_password";

    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");

    private readonly string _tokenKey = configuration["Security:TokenKey"] ?? string.Empty;
    private bool _shipUserColumnsEnsured;

    public async Task<AuthUserRecord?> GetUserByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1
                [ID],
                [USName],
                [USPass],
                [DisplayName],
                [Status],
                [Lastonlinetime],
                [IPAccess],
                [LastUpdatePassword],
                [Avatar],
                [UserType],
                [TenantID],
                [DeviceID],
                [Phone],
                [Email],
                [IdentificationNumber],
                [IsViewOnly],
                [CanManageTransactions]
            FROM [TblMRUser]
            WHERE [ID] = @id
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureShipUserColumnsAsync(connection, null, cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapAuthUser(reader);
    }

    public async Task<AuthUserRecord?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1
                [ID],
                [USName],
                [USPass],
                [DisplayName],
                [Status],
                [Lastonlinetime],
                [IPAccess],
                [LastUpdatePassword],
                [Avatar],
                [UserType],
                [TenantID],
                [DeviceID],
                [Phone],
                [Email],
                [IdentificationNumber],
                [IsViewOnly],
                [CanManageTransactions]
            FROM [TblMRUser]
            WHERE [USName] = @username
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureShipUserColumnsAsync(connection, null, cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@username", username);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapAuthUser(reader);
    }

    public async Task<UserManagementPageResult> GetManagedUsersAsync(int page, int pageSize, int? tenantId = null, int? deviceId = null, string? userGroup = null, CancellationToken cancellationToken = default)
    {
        const string countQuery = """
            SELECT COUNT(1)
            FROM [TblMRUser]
            WHERE (@tenantId IS NULL OR [TenantID] = @tenantId)
              AND (@deviceId IS NULL OR [DeviceID] = @deviceId)
              AND (@userGroup IS NULL OR [UserType] = @userGroup)
            """;

        const string listQuery = """
            SELECT
                u.[ID],
                u.[USName],
                u.[DisplayName],
                u.[Status],
                u.[Avatar],
                u.[Phone],
                u.[Email],
                u.[IdentificationNumber],
                u.[IsViewOnly],
                u.[CanManageTransactions],
                u.[UserType],
                u.[TenantID],
                u.[DeviceID],
                u.[Lastonlinetime],
                u.[LastUpdatePassword],
                t.[TenantName],
                d.[VesselName],
                d.[DeviceCode]
            FROM [TblMRUser] u
            LEFT JOIN [TblTenant] t ON t.[ID] = u.[TenantID]
            LEFT JOIN [TblDevices] d ON d.[ID] = u.[DeviceID]
            WHERE (@tenantId IS NULL OR u.[TenantID] = @tenantId)
              AND (@deviceId IS NULL OR u.[DeviceID] = @deviceId)
              AND (@userGroup IS NULL OR u.[UserType] = @userGroup)
            ORDER BY u.[ID] DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        var normalizedPageSize = pageSize <= 0 ? 10 : pageSize;
        var normalizedPage = page <= 0 ? 1 : page;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureShipUserColumnsAsync(connection, null, cancellationToken);
        var normalizedUserGroup = string.IsNullOrWhiteSpace(userGroup) ? null : ManagedUserType.NormalizeGroup(userGroup);

        int totalUsers;
        await using (var countCommand = new SqlCommand(countQuery, connection))
        {
            countCommand.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
            countCommand.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
            countCommand.Parameters.Add("@userGroup", SqlDbType.NVarChar, 50).Value = (object?)normalizedUserGroup ?? DBNull.Value;
            totalUsers = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        var totalPages = totalUsers == 0 ? 1 : (int)Math.Ceiling(totalUsers / (double)normalizedPageSize);
        var clampedPage = Math.Min(normalizedPage, totalPages);
        var offset = (clampedPage - 1) * normalizedPageSize;

        var users = new List<UserListItemViewModel>();
        await using (var listCommand = new SqlCommand(listQuery, connection))
        {
            listCommand.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
            listCommand.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
            listCommand.Parameters.Add("@userGroup", SqlDbType.NVarChar, 50).Value = (object?)normalizedUserGroup ?? DBNull.Value;
            listCommand.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
            listCommand.Parameters.Add("@pageSize", SqlDbType.Int).Value = normalizedPageSize;

            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                users.Add(MapManagedUserListItem(reader));
            }
        }

        return new UserManagementPageResult
        {
            Users = users,
            CurrentPage = clampedPage,
            PageSize = normalizedPageSize,
            TotalUsers = totalUsers
        };
    }

    public async Task<UserManagementFormViewModel?> GetManagedUserByIdAsync(int id, int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1
                [ID],
                [USName],
                [DisplayName],
                [Status],
                [Avatar],
                [UserType],
                [TenantID],
                [DeviceID],
                [Phone],
                [Email],
                [IdentificationNumber],
                [IsViewOnly],
                [CanManageTransactions]
            FROM [TblMRUser]
            WHERE [ID] = @id
              AND (@tenantId IS NULL OR [TenantID] = @tenantId)
              AND (@deviceId IS NULL OR [DeviceID] = @deviceId)
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureShipUserColumnsAsync(connection, null, cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapManagedUserForm(reader);
    }

    public async Task<List<UserVesselOptionViewModel>> GetVesselOptionsAsync(int? tenantId = null, int? deviceId = null, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT
                d.[ID],
                d.[VesselName],
                d.[DeviceCode],
                d.[TenantID],
                t.[TenantName]
            FROM [TblDevices] d
            LEFT JOIN [TblTenant] t ON t.[ID] = d.[TenantID]
            WHERE (@tenantId IS NULL OR d.[TenantID] = @tenantId)
              AND (@deviceId IS NULL OR d.[ID] = @deviceId)
            ORDER BY d.[VesselName] ASC, d.[DeviceCode] ASC, d.[ID] ASC
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var vessels = new List<UserVesselOptionViewModel>();
        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            vessels.Add(new UserVesselOptionViewModel
            {
                Id = reader["ID"] is int id ? id : 0,
                VesselName = reader["VesselName"]?.ToString() ?? string.Empty,
                DeviceCode = reader["DeviceCode"]?.ToString() ?? string.Empty,
                TenantId = reader["TenantID"] as int?,
                TenantName = reader["TenantName"]?.ToString() ?? string.Empty
            });
        }

        return vessels;
    }

    public PasswordVerificationResult VerifyPassword(string rawPassword, string storedPassword)
    {
        if (string.IsNullOrWhiteSpace(storedPassword))
        {
            return new PasswordVerificationResult();
        }

        var normalizedStored = storedPassword.Trim();
        if (normalizedStored.StartsWith($"{HashPrefix}$", StringComparison.Ordinal))
        {
            return new PasswordVerificationResult
            {
                IsValid = VerifyPbkdf2Password(rawPassword, normalizedStored)
            };
        }

        return new PasswordVerificationResult();
    }

    public string EncodePassword(string rawPassword)
    {
        Span<byte> salt = stackalloc byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        var pepperedPassword = $"{_tokenKey}:{rawPassword}";
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            pepperedPassword,
            salt.ToArray(),
            IterationCount,
            HashAlgorithmName.SHA256,
            KeySize);

        return string.Join(
            "$",
            HashPrefix,
            IterationCount,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public async Task UpdateLoginAuditAsync(string username, string? ipAddress, CancellationToken cancellationToken = default)
    {
        const string query = """
            UPDATE [TblMRUser]
            SET
                [Lastonlinetime] = GETDATE(),
                [IPAccess] = @ipAddress
            WHERE [USName] = @username
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@username", username);
        command.Parameters.AddWithValue("@ipAddress", (object?)ipAddress ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateUserProfileAsync(
        int id,
        string displayName,
        string? phone,
        string? email,
        string? identificationNumber,
        string? avatar,
        string auditDetail,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            UPDATE [TblMRUser]
            SET
                [DisplayName] = @displayName,
                [Phone] = @phone,
                [Email] = @email,
                [IdentificationNumber] = @identificationNumber,
                [Avatar] = @avatar
            WHERE [ID] = @id
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@displayName", SqlDbType.NVarChar, 250).Value = displayName;
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 50).Value = (object?)phone ?? DBNull.Value;
        command.Parameters.Add("@email", SqlDbType.NVarChar, 50).Value = (object?)email ?? DBNull.Value;
        command.Parameters.Add("@identificationNumber", SqlDbType.NVarChar, 50).Value = (object?)identificationNumber ?? DBNull.Value;
        command.Parameters.Add("@avatar", SqlDbType.NVarChar, 550).Value = (object?)avatar ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await InsertUserAuditAsync(connection, transaction, id, UpdateUserProfileAuditAction, auditDetail, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> IsUsernameInUseAsync(string username, int? excludeUserId, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1 1
            FROM [TblMRUser]
            WHERE LOWER(LTRIM(RTRIM([USName]))) = LOWER(@username)
              AND (@excludeUserId IS NULL OR [ID] <> @excludeUserId)
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@username", SqlDbType.NVarChar, 50).Value = username;
        command.Parameters.Add("@excludeUserId", SqlDbType.Int).Value = (object?)excludeUserId ?? DBNull.Value;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value;
    }

    public async Task<bool> IsEmailInUseAsync(string email, int excludeUserId, CancellationToken cancellationToken = default)
    {
        const string query = """
            SELECT TOP 1 1
            FROM [TblMRUser]
            WHERE [Email] IS NOT NULL
              AND LOWER(LTRIM(RTRIM([Email]))) = LOWER(@email)
              AND [ID] <> @excludeUserId
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@email", SqlDbType.NVarChar, 50).Value = email;
        command.Parameters.Add("@excludeUserId", SqlDbType.Int).Value = excludeUserId;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value;
    }

    public async Task<int> CreateManagedUserAsync(
        UserManagementFormViewModel model,
        string encodedPassword,
        int? auditUserId,
        string auditUsername,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            INSERT INTO [TblMRUser]
                ([USName], [USPass], [DisplayName], [Status], [Avatar], [UserType], [TenantID], [DeviceID], [Phone], [Email], [IdentificationNumber], [IsViewOnly], [CanManageTransactions], [LastUpdatePassword])
            OUTPUT INSERTED.[ID]
            VALUES
                (@username, @password, @displayName, @status, @avatar, @userType, @tenantId, @deviceId, @phone, @email, @identificationNumber, @isViewOnly, @canManageTransactions, GETDATE())
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureShipUserColumnsAsync(connection, transaction, cancellationToken);

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@username", SqlDbType.NVarChar, 50).Value = model.Username;
        command.Parameters.Add("@password", SqlDbType.NVarChar, 150).Value = encodedPassword;
        command.Parameters.Add("@displayName", SqlDbType.NVarChar, 250).Value = model.DisplayName;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = model.Status;
        command.Parameters.Add("@avatar", SqlDbType.NVarChar, 550).Value = (object?)model.ExistingLogoPath ?? DBNull.Value;
        command.Parameters.Add("@userType", SqlDbType.NVarChar, 50).Value = ManagedUserType.NormalizeGroup(model.UserGroup);
        command.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)model.TenantId ?? DBNull.Value;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)model.DeviceId ?? DBNull.Value;
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 50).Value = (object?)model.Phone ?? DBNull.Value;
        command.Parameters.Add("@email", SqlDbType.NVarChar, 50).Value = (object?)model.Email ?? DBNull.Value;
        command.Parameters.Add("@identificationNumber", SqlDbType.NVarChar, 50).Value = (object?)model.IdentificationNumber ?? DBNull.Value;
        command.Parameters.Add("@isViewOnly", SqlDbType.Bit).Value = model.IsViewOnly;
        command.Parameters.Add("@canManageTransactions", SqlDbType.Bit).Value = model.CanManageTransactions;

        var userId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));

        var auditDetail = $"Created user '{model.Username}' (ID: {userId}) with group '{ManagedUserType.NormalizeGroup(model.UserGroup)}', tenant '{model.TenantId?.ToString() ?? "-"}', vessel '{model.DeviceId?.ToString() ?? "-"}' by '{auditUsername}'.";
        await InsertUserAuditAsync(connection, transaction, auditUserId, CreateManagedUserAuditAction, auditDetail, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return userId;
    }

    public async Task UpdateManagedUserAsync(
        UserManagementFormViewModel model,
        int? auditUserId,
        string auditUsername,
        CancellationToken cancellationToken = default)
    {
        const string selectQuery = """
            SELECT TOP 1
                [ID],
                [USName],
                [DisplayName],
                [Status],
                [Avatar],
                [UserType],
                [TenantID],
                [DeviceID],
                [Phone],
                [Email],
                [IdentificationNumber],
                [IsViewOnly],
                [CanManageTransactions]
            FROM [TblMRUser]
            WHERE [ID] = @id
            """;

        const string updateQuery = """
            UPDATE [TblMRUser]
            SET
                [USName] = @username,
                [DisplayName] = @displayName,
                [Status] = @status,
                [Avatar] = @avatar,
                [UserType] = @userType,
                [TenantID] = @tenantId,
                [DeviceID] = @deviceId,
                [Phone] = @phone,
                [Email] = @email,
                [IdentificationNumber] = @identificationNumber,
                [IsViewOnly] = @isViewOnly,
                [CanManageTransactions] = @canManageTransactions
            WHERE [ID] = @id
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureShipUserColumnsAsync(connection, transaction, cancellationToken);

        UserManagementFormViewModel? existingUser;
        await using (var selectCommand = new SqlCommand(selectQuery, connection, transaction))
        {
            selectCommand.Parameters.Add("@id", SqlDbType.Int).Value = model.Id;
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException($"User with id {model.Id} was not found.");
            }

            existingUser = MapManagedUserForm(reader);
        }

        await using (var updateCommand = new SqlCommand(updateQuery, connection, transaction))
        {
            updateCommand.Parameters.Add("@id", SqlDbType.Int).Value = model.Id;
            updateCommand.Parameters.Add("@username", SqlDbType.NVarChar, 50).Value = model.Username;
            updateCommand.Parameters.Add("@displayName", SqlDbType.NVarChar, 250).Value = model.DisplayName;
            updateCommand.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = model.Status;
            updateCommand.Parameters.Add("@avatar", SqlDbType.NVarChar, 550).Value = (object?)model.ExistingLogoPath ?? DBNull.Value;
            updateCommand.Parameters.Add("@userType", SqlDbType.NVarChar, 50).Value = ManagedUserType.NormalizeGroup(model.UserGroup);
            updateCommand.Parameters.Add("@tenantId", SqlDbType.Int).Value = (object?)model.TenantId ?? DBNull.Value;
            updateCommand.Parameters.Add("@deviceId", SqlDbType.Int).Value = (object?)model.DeviceId ?? DBNull.Value;
            updateCommand.Parameters.Add("@phone", SqlDbType.NVarChar, 50).Value = (object?)model.Phone ?? DBNull.Value;
            updateCommand.Parameters.Add("@email", SqlDbType.NVarChar, 50).Value = (object?)model.Email ?? DBNull.Value;
            updateCommand.Parameters.Add("@identificationNumber", SqlDbType.NVarChar, 50).Value = (object?)model.IdentificationNumber ?? DBNull.Value;
            updateCommand.Parameters.Add("@isViewOnly", SqlDbType.Bit).Value = model.IsViewOnly;
            updateCommand.Parameters.Add("@canManageTransactions", SqlDbType.Bit).Value = model.CanManageTransactions;
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var auditDetail = BuildManagedUserUpdateAuditDetail(existingUser!, model, auditUsername);
        await InsertUserAuditAsync(connection, transaction, auditUserId, UpdateManagedUserAuditAction, auditDetail, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateUserPasswordAsync(
        int id,
        string encodedPassword,
        string auditDetail,
        CancellationToken cancellationToken = default)
    {
        const string query = """
            UPDATE [TblMRUser]
            SET
                [USPass] = @password,
                [LastUpdatePassword] = GETDATE()
            WHERE [ID] = @id
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@password", SqlDbType.NVarChar, 512).Value = encodedPassword;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await InsertUserAuditAsync(connection, transaction, id, ChangeUserPasswordAuditAction, auditDetail, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task InsertUserAuditAsync(
        int? userId,
        string logAction,
        string logDetail,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await InsertUserAuditAsync(connection, transaction, userId, logAction, logDetail, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private bool VerifyPbkdf2Password(string rawPassword, string storedPassword)
    {
        var parts = storedPassword.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expectedHash = Convert.FromBase64String(parts[3]);
            var pepperedPassword = $"{_tokenKey}:{rawPassword}";

            var actualHash = Rfc2898DeriveBytes.Pbkdf2(
                pepperedPassword,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static AuthUserRecord MapAuthUser(SqlDataReader reader)
    {
        return new AuthUserRecord
        {
            Id = reader["ID"] is int id ? id : 0,
            Username = reader["USName"]?.ToString() ?? string.Empty,
            Password = reader["USPass"]?.ToString() ?? string.Empty,
            DisplayName = reader["DisplayName"]?.ToString() ?? string.Empty,
            Status = reader["Status"]?.ToString() ?? string.Empty,
            Avatar = reader["Avatar"]?.ToString(),
            UserType = reader["UserType"]?.ToString(),
            TenantId = reader["TenantID"] as int?,
            DeviceId = reader["DeviceID"] as int?,
            Phone = reader["Phone"]?.ToString(),
            Email = reader["Email"]?.ToString(),
            IdentificationNumber = reader["IdentificationNumber"]?.ToString(),
            LastOnlineTime = reader["Lastonlinetime"] as DateTime?,
            IPAccess = reader["IPAccess"]?.ToString(),
            LastUpdatePassword = reader["LastUpdatePassword"] as DateTime?,
            IsViewOnly = reader["IsViewOnly"] is bool isViewOnly && isViewOnly,
            CanManageTransactions = reader["CanManageTransactions"] is bool canManageTransactions && canManageTransactions
        };
    }

    private static UserListItemViewModel MapManagedUserListItem(SqlDataReader reader)
    {
        var group = ManagedUserType.Parse(reader["UserType"]?.ToString());

        return new UserListItemViewModel
        {
            Id = reader["ID"] is int id ? id : 0,
            Username = reader["USName"]?.ToString() ?? string.Empty,
            DisplayName = reader["DisplayName"]?.ToString() ?? string.Empty,
            Status = reader["Status"]?.ToString() ?? string.Empty,
            Avatar = reader["Avatar"]?.ToString(),
            Phone = reader["Phone"]?.ToString(),
            Email = reader["Email"]?.ToString(),
            IdentificationNumber = reader["IdentificationNumber"]?.ToString(),
            UserGroup = group,
            TenantId = reader["TenantID"] as int?,
            TenantName = reader["TenantName"]?.ToString(),
            DeviceId = reader["DeviceID"] as int?,
            VesselName = reader["VesselName"]?.ToString(),
            DeviceCode = reader["DeviceCode"]?.ToString(),
            LastOnlineTime = reader["Lastonlinetime"] as DateTime?,
            LastUpdatePassword = reader["LastUpdatePassword"] as DateTime?,
            IsViewOnly = reader["IsViewOnly"] is bool isViewOnly && isViewOnly,
            CanManageTransactions = reader["CanManageTransactions"] is bool canManageTransactions && canManageTransactions
        };
    }

    private static UserManagementFormViewModel MapManagedUserForm(SqlDataReader reader)
    {
        var group = ManagedUserType.Parse(reader["UserType"]?.ToString());

        return new UserManagementFormViewModel
        {
            Id = reader["ID"] is int id ? id : 0,
            Username = reader["USName"]?.ToString() ?? string.Empty,
            DisplayName = reader["DisplayName"]?.ToString() ?? string.Empty,
            Status = string.IsNullOrWhiteSpace(reader["Status"]?.ToString()) ? "active" : reader["Status"]?.ToString() ?? "active",
            ExistingLogoPath = reader["Avatar"]?.ToString(),
            UserGroup = group,
            TenantId = reader["TenantID"] as int?,
            DeviceId = reader["DeviceID"] as int?,
            Phone = reader["Phone"]?.ToString(),
            Email = reader["Email"]?.ToString(),
            IdentificationNumber = reader["IdentificationNumber"]?.ToString(),
            IsViewOnly = reader["IsViewOnly"] is bool isViewOnly && isViewOnly,
            CanManageTransactions = reader["CanManageTransactions"] is bool canManageTransactions && canManageTransactions
        };
    }

    private static string BuildManagedUserUpdateAuditDetail(
        UserManagementFormViewModel existingUser,
        UserManagementFormViewModel updatedUser,
        string auditUsername)
    {
        var changedFields = new List<string>();

        if (!string.Equals(NormalizeOptionalValue(existingUser.Username), NormalizeOptionalValue(updatedUser.Username), StringComparison.OrdinalIgnoreCase))
        {
            changedFields.Add("Username");
        }

        if (!string.Equals(NormalizeOptionalValue(existingUser.DisplayName), NormalizeOptionalValue(updatedUser.DisplayName), StringComparison.Ordinal))
        {
            changedFields.Add("DisplayName");
        }

        if (!string.Equals(NormalizeOptionalValue(existingUser.Phone), NormalizeOptionalValue(updatedUser.Phone), StringComparison.Ordinal))
        {
            changedFields.Add("Phone");
        }

        if (!string.Equals(NormalizeOptionalValue(existingUser.Email), NormalizeOptionalValue(updatedUser.Email), StringComparison.OrdinalIgnoreCase))
        {
            changedFields.Add("Email");
        }

        if (!string.Equals(NormalizeOptionalValue(existingUser.IdentificationNumber), NormalizeOptionalValue(updatedUser.IdentificationNumber), StringComparison.Ordinal))
        {
            changedFields.Add("IdentificationNumber");
        }

        if (!string.Equals(NormalizeOptionalValue(existingUser.ExistingLogoPath), NormalizeOptionalValue(updatedUser.ExistingLogoPath), StringComparison.OrdinalIgnoreCase))
        {
            changedFields.Add("Avatar");
        }

        if (!string.Equals(ManagedUserType.NormalizeGroup(existingUser.UserGroup), ManagedUserType.NormalizeGroup(updatedUser.UserGroup), StringComparison.OrdinalIgnoreCase))
        {
            changedFields.Add("UserType");
        }

        if (existingUser.TenantId != updatedUser.TenantId)
        {
            changedFields.Add("TenantID");
        }

        if (existingUser.DeviceId != updatedUser.DeviceId)
        {
            changedFields.Add("DeviceID");
        }

        if (!string.Equals(NormalizeOptionalValue(existingUser.Status), NormalizeOptionalValue(updatedUser.Status), StringComparison.OrdinalIgnoreCase))
        {
            changedFields.Add("Status");
        }

        if (existingUser.IsViewOnly != updatedUser.IsViewOnly)
        {
            changedFields.Add("IsViewOnly");
        }

        return changedFields.Count == 0
            ? $"Updated user '{updatedUser.Username}' (ID: {updatedUser.Id}) by '{auditUsername}'. No field changes detected."
            : $"Updated user '{updatedUser.Username}' (ID: {updatedUser.Id}) by '{auditUsername}'. Changed fields: {string.Join(", ", changedFields)}.";
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task EnsureShipUserColumnsAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (_shipUserColumnsEnsured)
        {
            return;
        }

        const string query = """
            IF COL_LENGTH('TblMRUser', 'DeviceID') IS NULL
            BEGIN
                ALTER TABLE [TblMRUser] ADD [DeviceID] int NULL;
            END

            IF COL_LENGTH('TblMRUser', 'IsViewOnly') IS NULL
            BEGIN
                ALTER TABLE [TblMRUser] ADD [IsViewOnly] bit NOT NULL CONSTRAINT [DF_TblMRUser_IsViewOnly] DEFAULT(0);
            END

            IF COL_LENGTH('TblMRUser', 'CanManageTransactions') IS NULL
            BEGIN
                ALTER TABLE [TblMRUser] ADD [CanManageTransactions] bit NOT NULL CONSTRAINT [DF_TblMRUser_CanManageTransactions] DEFAULT(0);
            END

            EXEC sys.sp_executesql N'
                UPDATE [TblMRUser]
                SET [CanManageTransactions] = 1
                WHERE LOWER(LTRIM(RTRIM([USName]))) = N''admin''
                  AND [CanManageTransactions] = 0;';
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _shipUserColumnsEnsured = true;
    }

    private static async Task InsertUserAuditAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int? userId,
        string logAction,
        string logDetail,
        CancellationToken cancellationToken)
    {
        const string query = """
            INSERT INTO [TblAudit]
                ([IDUser], [LogDate], [LogAction], [LogDetail], [IDDevice])
            VALUES
                (@userId, GETDATE(), @logAction, @logDetail, @deviceId)
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@logAction", SqlDbType.NVarChar, 100).Value = logAction;
        command.Parameters.Add("@logDetail", SqlDbType.NVarChar, -1).Value = logDetail;
        command.Parameters.Add("@deviceId", SqlDbType.Int).Value = DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
