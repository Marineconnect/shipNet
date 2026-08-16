public sealed class DeviceActivityAuditHardeningTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    [Fact]
    public void TenantAdminsKeepDeviceAndSubscriptionManagementPermissions()
    {
        var dashboard = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "DashboardController.cs"));
        var subscriptions = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "MonthlySubscriptionController.cs"));

        Assert.Contains("return user is not null && !user.IsViewOnly && (IsAdminAccount(user) || IsTenantAdmin(user));", dashboard);
        Assert.Contains("return user is not null && !user.IsViewOnly && (IsAdminAccount(user) || IsTenantAdmin(user));", subscriptions);
    }

    [Fact]
    public void DeviceActivityLogHasIdempotentEventKeyAndSeparateRecordedTime()
    {
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "DeviceActivityLogService.cs"));
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "Database", "Scripts", "20260816_AddDeviceActivityLog.sql"));

        Assert.Contains("UX_TblDeviceActivityLog_EventKey", service);
        Assert.Contains("IsDuplicateKey", service);
        Assert.Contains("[OccurredAtUtc]", service);
        Assert.Contains("[RecordedAtUtc]", service);
        Assert.Contains("CREATE UNIQUE INDEX [UX_TblDeviceActivityLog_EventKey]", script);
    }

    [Fact]
    public void KvhJobCompletionUsesRequestedDataOptInDirectionAndExplicitFailures()
    {
        var jobService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhJobService.cs"));
        var models = File.ReadAllText(Path.Combine(ProjectRoot, "Models", "DeviceActivityModels.cs"));

        Assert.Contains("TryReadRequestedBool(requestedValue, \"enabled\")", jobService);
        Assert.Contains("DeviceActivityActions.DataOptInFailed", jobService);
        Assert.Contains("DeviceActivityActions.DataOptOutFailed", jobService);
        Assert.DoesNotContain("success ? DeviceActivityActions.DataOptInCompleted : DeviceActivityActions.DataOptOutCompleted", jobService);
        Assert.Contains("public const string DataOptInFailed", models);
        Assert.Contains("public const string DataOptOutFailed", models);
    }

    [Fact]
    public void ManualPaidPrecheckUsesPostAndDoesNotSilentlyContinueOnFailure()
    {
        var controller = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "MonthlySubscriptionController.cs"));
        var view = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "MonthlySubscription", "Details.cshtml"));

        Assert.Contains("[ValidateAntiForgeryToken]", controller);
        Assert.Contains("[FromForm] int invoiceId", controller);
        Assert.Contains("method: \"POST\"", view);
        Assert.Contains("Tiep tuc cap nhat invoice thanh Paid ma KHONG gui lenh Resume KVH?", view);
        Assert.Contains("if (!shouldContinue) return;", view);
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StarlinkDeviceManager.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Project root was not found.");
    }
}
