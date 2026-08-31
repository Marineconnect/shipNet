public sealed class TenantCommissionPaymentTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void MigrationCreatesLedgerTablesAndConstraints()
    {
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "Database", "Scripts", "20260831_AddTenantCommissionPayments.sql"));

        Assert.Contains("TblTenantCommissionPayment", script);
        Assert.Contains("TblTenantCommissionPaymentItem", script);
        Assert.Contains("CK_TblTenantCommissionPayment_Amount", script);
        Assert.Contains("CK_TblTenantCommissionPayment_SourceMode", script);
        Assert.Contains("CK_TblTenantCommissionPayment_Period", script);
        Assert.Contains("UX_TenantCommissionPaymentItem_SubscriptionId", script);
        Assert.Contains("FOREIGN KEY ([SubscriptionId]) REFERENCES [dbo].[TblMonthlySubscription]([ID])", script);
        Assert.Contains("DROP CONSTRAINT [FK_TenantCommissionPaymentItem_Payment]", script);
        Assert.Contains("FOREIGN KEY ([PaymentId]) REFERENCES [dbo].[TblTenantCommissionPayment]([ID]);", script);
        Assert.DoesNotContain("REFERENCES [dbo].[TblTenantCommissionPayment]([ID]) ON DELETE CASCADE", script);
    }

    [Fact]
    public void ServiceUsesLedgerBalanceFormulaAndRejectsOverpayment()
    {
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "TenantCommissionPaymentService.cs"));

        Assert.Contains("GrossCommission", service);
        Assert.Contains("PaidCommission", service);
        Assert.Contains("SUM([Amount]) AS [PaidCommission]", service);
        Assert.Contains("Số tiền thanh toán vượt quá hoa hồng còn phải trả của Tenant.", service);
        Assert.Contains("input.Amount > balance.RemainingCommission", service);
        Assert.Contains("amount > balance.RemainingCommission", service);
        Assert.Contains("AcquireTenantCommissionLockAsync(connection, transaction, input.TenantId, cancellationToken)", service);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", service);
    }

    [Fact]
    public void BillingCyclePaymentRequeriesEligibleCyclesAndSnapshotsCommission()
    {
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "TenantCommissionPaymentService.cs"));

        Assert.Contains("CreateBillingCyclePaymentAsync", service);
        Assert.Contains("QueryEligibleCyclesByIdsAsync", service);
        Assert.Contains("var amount = cycles.Sum(item => item.CommissionAmount)", service);
        Assert.Contains("InsertPaymentItemAsync(connection, transaction, paymentId, cycle.SubscriptionId, cycle.CommissionAmount", service);
        Assert.DoesNotContain("input.Amount", ExtractMethodBody(service, "CreateBillingCyclePaymentAsync"));
    }

    [Fact]
    public void EligibleCyclesExcludeLinkedUnpaidVoidAndRefundedInvoices()
    {
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "TenantCommissionPaymentService.cs"));

        Assert.Contains("TblTenantCommissionPaymentItem", service);
        Assert.Contains("NOT EXISTS", service);
        Assert.Contains("LOWER(COALESCE(i.[Status], N'')) NOT IN (N'void', N'cancelled', N'canceled', N'refunded')", service);
        Assert.Contains("LOWER(COALESCE(i.[Status], N'')) = N'paid' OR (i.[Amount] > 0 AND i.[PaidAmount] >= i.[Amount])", service);
        Assert.Contains("HAVING COUNT(i.[ID]) > 0", service);
    }

    [Fact]
    public void TenantScopeIsEnforcedInControllerAndService()
    {
        var controller = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "TenantCommissionPaymentController.cs"));
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "TenantCommissionPaymentService.cs"));

        Assert.Contains("[Authorize]", controller);
        Assert.Contains("CanAccessPayment(currentUser)", controller);
        Assert.Contains("GetAllowedTenantId(currentUser)", controller);
        Assert.Contains("EnsureTenantAccess(input.TenantId, allowedTenantId)", service);
        Assert.Contains("EnsureTenantAccess(tenantId, allowedTenantId)", service);
        Assert.Contains("Tenant scope mismatch", service);
    }

    [Fact]
    public void OnlyAdminCanCreatePayment()
    {
        var controller = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "TenantCommissionPaymentController.cs"));

        Assert.Contains("CanCreatePayment", controller);
        Assert.Contains("!user.IsViewOnly", controller);
        Assert.Contains("user.IsAdmin", controller);
        Assert.Contains("CreateManual", controller);
        Assert.Contains("CreateBillingCycles", controller);
        Assert.Contains("!CanCreatePayment(currentUser)", ExtractMethodBody(controller, "EligibleCycles"));
        Assert.Contains("return Forbid();", ExtractMethodBody(controller, "CreateManual"));
        Assert.Contains("return Forbid();", ExtractMethodBody(controller, "CreateBillingCycles"));
    }

    [Fact]
    public void UiUsesDualListAndPreservesSelectedCyclesAcrossSearches()
    {
        var view = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "TenantCommissionPayment", "Index.cshtml"));

        Assert.Contains("const selectedCycles = new Map()", view);
        Assert.Contains("data-cycle-available", view);
        Assert.Contains("data-cycle-selected", view);
        Assert.Contains("selectedCycles.set(id, cycle)", view);
        Assert.Contains("available.innerHTML = \"\"", view);
        Assert.Contains("input.name = \"SubscriptionIds\"", view);
        Assert.Contains("Invoice Number(s)", view);
        Assert.Contains("Transaction Reference(s)", view);
    }

    [Fact]
    public void BillingAndDashboardUseGrossCommissionAndPeriodLedgerAllocation()
    {
        var billingModel = File.ReadAllText(Path.Combine(ProjectRoot, "Models", "BillingInvoiceModels.cs"));
        var billingService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "BillingInvoiceReportService.cs"));
        var dashboardService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "DashboardKpiService.cs"));
        var billingView = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "BillingInvoice", "Index.cshtml"));

        Assert.Contains("public decimal GrossCommission", billingModel);
        Assert.Contains("public decimal PaidCommission", billingModel);
        Assert.Contains("RemainingCommission", billingModel);
        Assert.Contains("TblTenantCommissionPayment", billingService);
        Assert.Contains("TotalMargin = grossCommission", billingService);
        Assert.DoesNotContain("TotalMargin = filter.TenantIdScope.HasValue ? Math.Max(0, grossCommission - paidCommission) : grossCommission", billingService);
        Assert.Contains("cp.[SourceMode] = N'manual'", billingService);
        Assert.Contains("cp.[PeriodFrom] >= @reportFrom", billingService);
        Assert.Contains("cp.[PeriodTo] <= @reportTo", billingService);
        Assert.Contains("pi.[CommissionAmount]", billingService);
        Assert.Contains("ps.[StartDate] >= @reportFrom", billingService);
        Assert.Contains("ps.[EndDate] <= @reportTo", billingService);
        Assert.Contains("ResolveReportRange", billingService);
        Assert.DoesNotContain("TblTenantCommissionPaymentItem", dashboardService);
        Assert.Contains("AS [TotalCommission]", dashboardService);
        Assert.Contains("Hoa hồng còn lại", billingView);
    }

    [Fact]
    public void ModuleIsRegisteredAndLinkedAsRevenueReportTab()
    {
        var program = File.ReadAllText(Path.Combine(ProjectRoot, "Program.cs"));
        var nav = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Shared", "_PortalNav.cshtml"));
        var billingView = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "BillingInvoice", "Index.cshtml"));
        var transactionView = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Transactions", "Index.cshtml"));
        var commissionView = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "TenantCommissionPayment", "Index.cshtml"));

        Assert.Contains("ITenantCommissionPaymentService, TenantCommissionPaymentService", program);
        Assert.Contains("tenant-commission-payment", nav);
        Assert.DoesNotContain("Thanh toán hoa hồng / Commission Payments", nav);
        Assert.Contains("IsTransactionReupAdmin", commissionView);
        Assert.Contains("asp-controller=\"TenantCommissionPayment\"", billingView);
        Assert.Contains("asp-controller=\"TenantCommissionPayment\"", transactionView);
        Assert.Contains("class=\"is-active\" asp-controller=\"TenantCommissionPayment\"", commissionView);
        Assert.Contains("Chi trả hoa hồng", commissionView);
    }

    [Fact]
    public void GrossCommissionDoesNotRequireInvoicePaid()
    {
        var billingService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "BillingInvoiceReportService.cs"));
        var commissionService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "TenantCommissionPaymentService.cs"));

        Assert.Contains("LOWER(COALESCE(i.[Status], N'')) NOT IN (N'void', N'cancelled', N'canceled', N'refunded')", billingService);
        Assert.Contains("SUM(COALESCE(NULLIF(i.[MarginAmount], 0), i.[SalePrice] - i.[BuyPrice])) AS [GrossCommission]", commissionService);
        Assert.DoesNotContain("AND (LOWER(COALESCE(i.[Status], N'')) = N'paid' OR (i.[Amount] > 0 AND i.[PaidAmount] >= i.[Amount]))", ExtractMethodBody(commissionService, "QueryBalanceAsync"));
    }

    [Fact]
    public void CommissionPaymentSearchCoversBillingInvoiceAndTransactionReferences()
    {
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "TenantCommissionPaymentService.cs"));

        Assert.Contains("EXISTS (", service);
        Assert.Contains("TblTenantCommissionPaymentItem", service);
        Assert.Contains("TblSubscriptionInvoice", service);
        Assert.Contains("TblPaymentTransaction", service);
        Assert.Contains("TblNinePayQrSession", service);
        Assert.Contains("ProviderPaymentNo", service);
        Assert.Contains("BankAccountNo", service);
        Assert.Contains("TransferContent", service);
        Assert.Contains("InvoiceNumbers", service);
        Assert.Contains("TransactionReferences", service);
    }

    [Fact]
    public void TenantCanOpenTransactionHistoryWithServerSideScopeButCannotManage()
    {
        var controller = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "TransactionsController.cs"));
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "PaymentTransactionService.cs"));

        Assert.Contains("|| user.IsTenantUser", controller);
        Assert.Contains("user?.IsTenantUser != true", controller);
        Assert.Contains("GetAllowedTenantId(currentUser)", controller);
        Assert.Contains("filter.TenantId = allowedTenantId", service);
        Assert.Contains("(@allowedTenantId IS NULL OR s.[TenantId] = @allowedTenantId)", service);
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var start = source.IndexOf(methodName, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var braceStart = source.IndexOf('{', start);
        if (braceStart < 0)
        {
            return string.Empty;
        }

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

        return source[braceStart..];
    }
}
