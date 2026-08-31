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

    [Fact]
    public void NinePayPaidPostCommitActionsIsolateKvhResumeFromRabbitMqFailures()
    {
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "PaymentTransactionService.cs"));
        var postCommitBody = ExtractMethodBody(service, "private async Task RunNinePayPaidPostCommitActionsSafeAsync");

        Assert.Contains("HandleNinePayPaidKvhResumeSafeAsync", postCommitBody);
        Assert.Contains("SendNinePayRabbitMqSafeAsync", postCommitBody);
        Assert.True(
            postCommitBody.IndexOf("HandleNinePayPaidKvhResumeSafeAsync", StringComparison.Ordinal) <
            postCommitBody.IndexOf("SendNinePayRabbitMqSafeAsync", StringComparison.Ordinal));
        Assert.DoesNotContain("WriteNinePayPaidActivityAndResumeSafeAsync", service);
        Assert.Contains("catch (Exception exception) when (!cancellationToken.IsCancellationRequested)", ExtractMethodBody(service, "private async Task SendNinePayRabbitMqSafeAsync"));
    }

    [Fact]
    public void KvhPaymentResumeActivityWriteCannotTurnSuccessfulSubmitIntoFailure()
    {
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhPaymentResumeService.cs"));
        var model = File.ReadAllText(Path.Combine(ProjectRoot, "Models", "DeviceActivityModels.cs"));
        var handleBody = ExtractMethodBody(service, "public async Task<KvhPaymentResumeResult> HandlePaidSubscriptionAsync(KvhPaymentResumeRequest");

        Assert.Contains("WriteActivitySafeAsync", service);
        Assert.Contains("catch (Exception exception) when (!cancellationToken.IsCancellationRequested)", ExtractMethodBody(service, "private async Task<bool> WriteActivitySafeAsync"));
        Assert.Contains("ResumeSubmitted = true", handleBody);
        Assert.Contains("AuditWriteSuccess = requestedAuditWriteSuccess", handleBody);
        Assert.Contains("public bool AuditWriteSuccess { get; set; } = true;", model);
    }

    [Fact]
    public void KvhAutoResumeIsControlledBySystemSettingAndOnlyAdminCanEditIt()
    {
        var settingsService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "SystemSettingsService.cs"));
        var settingsController = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "SystemSettingsController.cs"));
        var resumeService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhPaymentResumeService.cs"));
        var settingsView = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "SystemSettings", "Index.cshtml"));
        var handleBody = ExtractMethodBody(resumeService, "public async Task<KvhPaymentResumeResult> HandlePaidSubscriptionAsync(KvhPaymentResumeRequest");
        var precheckBody = ExtractMethodBody(resumeService, "public async Task<KvhPaymentResumePrecheckResult> PrecheckAsync");

        Assert.Contains("KvhAutoResumeEnabledSettingCode = \"kvh_auto_resume_enabled\"", settingsService);
        Assert.Contains("Enable or disable automatic KVH resume", settingsService);
        Assert.Contains("IsKvhAutoResumeSetting(existing.SettingCode) && !IsExactAdmin(currentUser)", settingsController);
        Assert.Contains("string.Equals(currentUser?.Username?.Trim(), \"admin\", StringComparison.OrdinalIgnoreCase)", settingsController);
        Assert.Contains("setting.CanEdit", settingsView);
        Assert.Contains("StarlinkDeviceManager.Services.SystemSettingsService.KvhAutoResumeEnabledSettingCode", settingsView);
        Assert.Contains("<option value=\"true\">", settingsView);
        Assert.Contains("<option value=\"false\">", settingsView);
        Assert.Contains("IsAutoResumeEnabledAsync", resumeService);
        Assert.True(
            handleBody.IndexOf("IsAutoResumeEnabledAsync", StringComparison.Ordinal) <
            handleBody.IndexOf("GetSubscriptionContextAsync", StringComparison.Ordinal));
        Assert.True(
            precheckBody.IndexOf("IsAutoResumeEnabledAsync", StringComparison.Ordinal) <
            precheckBody.IndexOf("GetSubscriptionContextAsync", StringComparison.Ordinal));
        Assert.Contains("kvh_auto_resume_disabled", handleBody);
        Assert.Contains("CanResume = false", precheckBody);
    }

    [Fact]
    public void BillingInvoiceModuleUsesExistingInvoiceDataWithScopedReadOnlyAccess()
    {
        var controller = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "BillingInvoiceController.cs"));
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "BillingInvoiceReportService.cs"));
        var program = File.ReadAllText(Path.Combine(ProjectRoot, "Program.cs"));
        var nav = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Shared", "_PortalNav.cshtml"));
        var view = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "BillingInvoice", "Index.cshtml"));

        Assert.Contains("[Authorize]", controller);
        Assert.Contains("IBillingInvoiceReportService", program);
        Assert.Contains("asp-controller=\"BillingInvoice\"", nav);
        Assert.Contains("TblSubscriptionInvoice", service);
        Assert.Contains("TblMonthlySubscription", service);
        Assert.DoesNotContain("CREATE TABLE", service, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TblBilling", service, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetAllowedTenantId", controller);
        Assert.Contains("GetAllowedDeviceId", controller);
        Assert.Contains("ExportCsvAsync", service);
        Assert.Contains("TotalInvoiceAmount", view);
        Assert.Contains("asp-all-route-data=\"@CurrentRoutes", view);
    }

    [Fact]
    public void DashboardRevenueKpiUsesBillingCycleScopeAndExistingInvoiceData()
    {
        var controller = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "DashboardController.cs"));
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "DashboardKpiService.cs"));
        var billingService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "BillingInvoiceReportService.cs"));
        var billingModel = File.ReadAllText(Path.Combine(ProjectRoot, "Models", "BillingInvoiceModels.cs"));
        var program = File.ReadAllText(Path.Combine(ProjectRoot, "Program.cs"));
        var nav = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Shared", "_PortalNav.cshtml"));
        var view = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Dashboard", "Index.cshtml"));
        var billingView = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "BillingInvoice", "Index.cshtml"));

        Assert.Contains("IDashboardKpiService", program);
        Assert.Contains("public async Task<IActionResult> Kpi(int month, int year)", controller);
        Assert.Contains("GetAllowedTenantId(currentUser)", controller);
        Assert.Contains("GetAllowedDeviceId(currentUser)", controller);
        Assert.Contains("s.[UsageMonth] >= @periodStart", service);
        Assert.Contains("s.[UsageMonth] < @periodEndExclusive", service);
        Assert.Contains("month is >= 0 and <= 12", service);
        Assert.Contains("periodStart.AddYears(1)", service);
        Assert.Contains("LOWER(COALESCE(i.[Status], N'')) NOT IN (N'void', N'cancelled', N'canceled', N'refunded')", service);
        Assert.Contains("COALESCE(NULLIF(v.[MarginAmount], 0), v.[SalePrice] - v.[BuyPrice])", service);
        Assert.DoesNotContain("CREATE TABLE", service, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dashboardKpiEndpoint", view);
        Assert.Contains("loadDashboardKpi", view);
        Assert.Contains("dashboard_kpi_all_months", view);
        Assert.Contains("billingYear", view);
        Assert.Contains("source = \"dashboard-revenue\"", view);
        Assert.Contains("source = \"dashboard-commission\"", view);
        Assert.Contains("invoiceValidity = \"valid\"", view);
        Assert.Contains("public int? BillingYear", billingModel);
        Assert.Contains("public string? InvoiceValidity", billingModel);
        Assert.Contains("public string? Source", billingModel);
        Assert.Contains("s.[UsageMonth] >= @billingYearStart", billingService);
        Assert.Contains("s.[UsageMonth] < @billingYearEnd", billingService);
        Assert.Contains("private const string ValidInvoiceStatusSql", billingService);
        Assert.Contains("private const string InvalidInvoiceStatusSql", billingService);
        Assert.Contains("string.Equals(filter.InvoiceValidity, \"valid\"", billingService);
        Assert.Contains("string.Equals(filter.InvoiceValidity, \"invalid\"", billingService);
        Assert.DoesNotContain("filter.MetricFilter, \"margin\"", billingService);
        Assert.DoesNotContain("COALESCE(NULLIF(i.[MarginAmount], 0), i.[SalePrice] - i.[BuyPrice]) <> 0", billingService);
        Assert.DoesNotContain("IsDashboardDrillDown", billingService);
        Assert.Contains("dashboard-commission", billingService);
        Assert.Contains("dashboard-revenue", billingService);
        Assert.Contains("invoiceValidity", billingView);
        Assert.Contains("Tính hợp lệ invoice", billingView);
        Assert.Contains("source", billingView);
        Assert.Contains("billingYear", billingView);
        Assert.True(
            nav.IndexOf("Quản lý gói cước / Pricing Management", StringComparison.Ordinal) <
            nav.IndexOf("Chu kỳ tính cước / Billing Cycle", StringComparison.Ordinal));
        Assert.True(
            nav.IndexOf("Chu kỳ tính cước / Billing Cycle", StringComparison.Ordinal) <
            nav.IndexOf("Báo cáo doanh thu / Billing &amp; Invoice", StringComparison.Ordinal));
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

    private static string ExtractMethodBody(string source, string methodName)
    {
        var methodStart = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Method {methodName} was not found.");
        var braceStart = source.IndexOf('{', methodStart);
        Assert.True(braceStart >= 0, $"Method {methodName} body was not found.");
        var depth = 0;
        for (var index = braceStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[braceStart..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Method {methodName} body was not closed.");
    }
}
