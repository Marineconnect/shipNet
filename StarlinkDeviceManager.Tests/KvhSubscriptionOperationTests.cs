namespace StarlinkDeviceManager.Tests;

public class KvhSubscriptionOperationTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    [Fact]
    public void OperationHistoryMigrationCreatesExpectedTablesAndIndexes()
    {
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "Database", "Scripts", "20260731_AddKvhSubscriptionOperationHistory.sql"));
        var reconcileScript = File.ReadAllText(Path.Combine(ProjectRoot, "Database", "Scripts", "20260731_AddKvhSubscriptionStateReconcile.sql"));

        Assert.Contains("TblKvhSubscriptionOperationBatch", script);
        Assert.Contains("TblKvhSubscriptionOperationItem", script);
        Assert.Contains("TblKvhSubscriptionOperationAudit", script);
        Assert.Contains("UQ_KvhSubOperationItem_Batch_Kit", script);
        Assert.Contains("IX_KvhSubOperationItem_Status_NextSubmit", script);
        Assert.Contains("FK_KvhSubOperationItem_KvhCommand", script);
        Assert.Contains("CurrentSubscriptionStatus", reconcileScript);
        Assert.Contains("CurrentScheduledAction", reconcileScript);
        Assert.Contains("CurrentScheduleId", reconcileScript);
        Assert.Contains("CurrentScheduledEffectiveDateUtc", reconcileScript);
        Assert.Contains("LastSubscriptionCheckedAtUtc", reconcileScript);
        Assert.Contains("ReconciliationStatus", reconcileScript);
        Assert.Contains("SubscriptionResponseJson", reconcileScript);
        Assert.Contains("IX_KvhSubOperationItem_WaitingEffective", reconcileScript);
    }

    [Fact]
    public void OperationWorkerClaimsQueuedItemsWithReadPastLocking()
    {
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhSubscriptionOperationService.cs"));
        var worker = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhSubscriptionOperationWorker.cs"));

        Assert.Contains("UPDLOCK, READPAST, ROWLOCK", service);
        Assert.Contains("ClaimQueuedItemsAsync", worker);
        Assert.Contains("SubmitItemAsync", worker);
        Assert.Contains("SyncCommandStatusesAsync", worker);
        Assert.Contains("MonitorWaitingEffectiveAsync", worker);
    }

    [Fact]
    public void SubscriptionOperationControllerExposesRequiredRoutes()
    {
        var controller = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "KvhSubscriptionOperationsController.cs"));

        Assert.Contains("[Route(\"KvhSolutions/SubscriptionOperations\")]", controller);
        Assert.Contains("[HttpPost(\"Create\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/ImportPreview\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/Start\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/RetryFailed\")]", controller);
        Assert.Contains("[HttpGet(\"{id:long}/Export\")]", controller);
        Assert.Contains("[HttpGet(\"DownloadTemplate\")]", controller);
    }

    [Fact]
    public void KvhJobPollingUsesTwoMinuteConfiguration()
    {
        var models = File.ReadAllText(Path.Combine(ProjectRoot, "Models", "KvhCommandModels.cs"));
        var jobService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhJobService.cs"));
        var subscriptionService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhSubscriptionService.cs"));

        Assert.Contains("JobPollIntervalSeconds", models);
        Assert.Contains("Math.Max(120, monitorOptions.Value.JobPollIntervalSeconds)", jobService);
        Assert.Contains("Math.Max(120, monitorOptions.Value.JobPollIntervalSeconds)", subscriptionService);
    }

    [Fact]
    public void SubscriptionPauseClassifiesKvh409AsStateConflict()
    {
        var models = File.ReadAllText(Path.Combine(ProjectRoot, "Models", "KvhCommandModels.cs"));
        var subscriptionService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhSubscriptionService.cs"));

        Assert.Contains("StateConflict = \"kvh_state_conflict\"", models);
        Assert.Contains("response.HttpStatusCode == StatusCodes.Status409Conflict", subscriptionService);
        Assert.Contains("ResolveSubscriptionSubmitError(response, action)", subscriptionService);
        Assert.Contains("if (!response.Success)", subscriptionService);
        Assert.Contains("if (string.IsNullOrWhiteSpace(jobId))", subscriptionService);
    }

    [Fact]
    public void SubscriptionOperationsReconcileStateConflictBeforeRetrying()
    {
        var models = File.ReadAllText(Path.Combine(ProjectRoot, "Models", "KvhSubscriptionOperationModels.cs"));
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhSubscriptionOperationService.cs"));
        var subscriptionService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhSubscriptionService.cs"));
        var jobService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhJobService.cs"));
        var view = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "KvhSolutions", "SubscriptionOperations", "Details.cshtml"));

        Assert.Contains("WaitingEffective = \"WAITING_EFFECTIVE\"", models);
        Assert.Contains("Conflict = \"CONFLICT\"", models);
        Assert.Contains("SyncAndResolveSubscriptionSnapshotAsync", service);
        Assert.Contains("EvaluateSubscriptionState", service);
        Assert.Contains("afterStateConflict: true", service);
        Assert.Contains("KvhJsonHelpers.NormalizeScheduledAction(snapshot.ScheduledAction)", service);
        Assert.Contains("targetScheduledAction = isPause ? \"SUSPEND\" : \"RESUME\"", service);
        Assert.Contains("KvhErrorCodes.StateConflictUnresolved", service);
        Assert.Contains("KvhVerificationStatuses.VerifiedScheduled", service);
        Assert.Contains("KvhJobStatuses.NotRequired", service);
        Assert.Contains("StateMonitorCheckIntervalMinutes", service);
        Assert.Contains("ScheduledCreatedAtUtc", subscriptionService);
        Assert.Contains("KvhJsonHelpers.ResolveScheduledAction(item)", subscriptionService);
        Assert.Contains("KvhVerificationStatuses.VerifiedScheduled", jobService);
        Assert.Contains("OperationStateText", view);
        Assert.Contains("DisplayScheduledAction", view);
    }

    [Fact]
    public void KvhScheduledArrayAndVietnamTimezoneAreSupported()
    {
        var helpers = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhJsonHelpers.cs"));
        var timezone = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "ShipNetTimeZone.cs"));
        var policy = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "IKvhSubscriptionActionPolicy.cs"));
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "Database", "Scripts", "20260731_AddKvhSubscriptionStateReconcile.sql"));

        Assert.Contains("ResolveScheduledAction", helpers);
        Assert.Contains("\"scheduled\", \"schedule\", \"scheduled_actions\", \"scheduledActions\"", helpers);
        Assert.Contains("NormalizeScheduledAction", helpers);
        Assert.Contains("return \"SUSPEND\";", helpers);
        Assert.Contains("effective_date", helpers);
        Assert.Contains("created_at", helpers);
        Assert.Contains("Asia/Ho_Chi_Minh", timezone);
        Assert.Contains("UTC+7", timezone);
        Assert.Contains("IKvhSubscriptionActionPolicy", policy);
        Assert.Contains("kvh_pause_already_scheduled", policy);
        Assert.Contains("ScheduledEffectiveDateUtc", policy);
        Assert.Contains("ScheduledCreatedAtUtc", script);
        Assert.Contains("ScheduledRawJson", script);
        Assert.Contains("OperationStatus", script);
    }

    [Fact]
    public void KvhScheduledSuspendUiAndBackendGuardAreSeparatedFromSubscriptionEffectiveDate()
    {
        var models = File.ReadAllText(Path.Combine(ProjectRoot, "Models", "KvhSubscriptionModels.cs"));
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhSubscriptionService.cs"));
        var policy = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "IKvhSubscriptionActionPolicy.cs"));
        var detail = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "KvhSolutions", "Details.cshtml"));
        var index = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "KvhSolutions", "Index.cshtml"));

        Assert.Contains("SubscriptionEffectiveDateUtc", models);
        Assert.Contains("ScheduledEffectiveDateUtc", models);
        Assert.Contains("NormalizedScheduledAction", models);
        Assert.Contains("HasScheduledPause => NormalizedScheduledAction == \"SUSPEND\"", models);
        Assert.Contains("CanPause => !MissingTrafficId && IsActive && !HasScheduledPause", models);
        Assert.Contains("ScheduleNote => HasScheduledPause", models);
        Assert.Contains("OperationStateDisplay => HasScheduledPause", models);

        Assert.Contains("s.[EffectiveDateUtc], s.[ScheduledEffectiveDateUtc]", service);
        Assert.Contains("ScheduleId = context.ScheduleId", service);
        Assert.Contains("ScheduledEffectiveDateUtc = context.ScheduledEffectiveDateUtc", service);
        Assert.Contains("HasPendingCommand = context.HasPendingCommand", service);
        Assert.Contains("InsertSubscriptionCommandAsync", service);

        Assert.Contains("kvh_pause_already_scheduled", policy);
        Assert.Contains("A previous Pause request already exists", policy);
        Assert.Contains("ShipNetTimeZone.FormatVietnam(context.ScheduledEffectiveDateUtc", policy);

        Assert.Contains("Ngày hiệu lực subscription", detail);
        Assert.Contains("Ngày hiệu lực scheduled", detail);
        Assert.Contains("Trạng thái thao tác", detail);
        Assert.Contains("entry.CanPause", detail);
        Assert.DoesNotContain("ScheduledAction.Contains(\"pause\"", detail);
        Assert.Contains("entry.ScheduleNote", detail);
        Assert.Contains("entry.CanCancelSchedule", detail);

        Assert.Contains("Subscription effective", index);
        Assert.Contains("item.NormalizedScheduledAction", index);
        Assert.Contains("item.ScheduleNote", index);
        Assert.Contains("item.CanCancelSchedule", index);
        Assert.DoesNotContain("ScheduledAction.Contains(\"pause\"", index);
    }

    [Fact]
    public void KvhSubscriptionOperationCanStartReadyRowsWhenSomeRowsAreInvalid()
    {
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhSubscriptionOperationService.cs"));

        Assert.Contains("ready > 0 ? KvhSubscriptionOperationBatchStatuses.Ready : KvhSubscriptionOperationBatchStatuses.Draft", service);
        Assert.Contains("WHERE [BatchId] = @batchId AND [Status] = 'READY'", service);
        Assert.Contains("if (ready <= 0)", service);
    }

    [Fact]
    public void KvhSubscriptionOperationImportUsesThreeColumnTemplate()
    {
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhSubscriptionOperationService.cs"));
        var detail = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "KvhSolutions", "SubscriptionOperations", "Details.cshtml"));

        Assert.Contains("var headers = new[] { \"KIT Number (*)\", \"Region\", \"Loại thao tác\" }", service);
        Assert.Contains("ResolveImportColumnMap(sheet)", service);
        Assert.Contains("new ImportColumnMap(KitNumber: 0, Region: 1, OperationType: 2)", service);
        Assert.Contains("new ImportColumnMap(KitNumber: 1, Region: 4, OperationType: 5", service);
        Assert.Contains("Tải mẫu import", detail);
        Assert.Contains("ImportPreview", detail);
        Assert.Contains("kvh-inline-import-form", detail);
    }

    [Fact]
    public void KvhSubscriptionSyncUpsertsByDeviceTrafficAndRegion()
    {
        var subscriptionService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhSubscriptionService.cs"));
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "Database", "Scripts", "20260731_FixKvhSubscriptionUpsertIndex.sql"));

        Assert.Contains("MERGE [dbo].[TblKvhSubscription] WITH (HOLDLOCK)", subscriptionService);
        Assert.Contains("target.[TrafficId] = source.[TrafficId]", subscriptionService);
        Assert.Contains("ISNULL(target.[Region], '') = ISNULL(source.[Region], '')", subscriptionService);
        Assert.Contains("UX_TblKvhSubscription_Device_Traffic_Region", script);
    }

    [Fact]
    public void KvhSolutionsUiLinksToSubscriptionOperations()
    {
        var index = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "KvhSolutions", "Index.cshtml"));
        var operationIndex = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "KvhSolutions", "SubscriptionOperations", "Index.cshtml"));
        var operationDetails = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "KvhSolutions", "SubscriptionOperations", "Details.cshtml"));

        Assert.Contains("KvhSubscriptionOperations", index);
        Assert.Contains("Lịch sử Pause/Resume", operationIndex);
        Assert.Contains("ImportPreview", operationDetails);
        Assert.Contains("kvh-stat-grid", operationDetails);
        Assert.Contains("kvh-flow-step", operationDetails);
        Assert.Contains("kvh-row-spinner", operationDetails);
        Assert.Contains("data-kvh-operation-live", operationDetails);
    }

    [Fact]
    public void KvhPauseResumeCommandsAreRestrictedToAdminUsername()
    {
        var controller = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "KvhSolutionsController.cs"));
        var models = File.ReadAllText(Path.Combine(ProjectRoot, "Models", "KvhSubscriptionModels.cs"));
        var index = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "KvhSolutions", "Index.cshtml"));
        var detail = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "KvhSolutions", "Details.cshtml"));

        Assert.Contains("CanControlSubscriptionCommands(currentUser)", controller);
        Assert.Contains("string.Equals(user?.Username?.Trim(), \"admin\", StringComparison.OrdinalIgnoreCase)", controller);
        Assert.Contains("public bool CanControlSubscriptionCommands", models);
        Assert.Contains("Model.CanControlSubscriptionCommands && item.KvhSubscriptionId.HasValue && item.CanPause", index);
        Assert.Contains("Model.CanControlSubscriptionCommands && item.KvhSubscriptionId.HasValue && item.CanResume", index);
        Assert.Contains("Model.CanControlSubscriptionCommands && item.KvhSubscriptionId.HasValue && item.CanCancelSchedule", index);
        Assert.Contains("Model.CanControlSubscriptionCommands && currentEntry is not null", detail);
    }

    [Fact]
    public void KvhSubscriptionOperationWritesAreRestrictedToAdminUsername()
    {
        var controller = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "KvhSubscriptionOperationsController.cs"));
        var operationIndex = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "KvhSolutions", "SubscriptionOperations", "Index.cshtml"));

        Assert.Contains("CanControlSubscriptionCommands(currentUser)", controller);
        Assert.Contains("string.Equals(user?.Username?.Trim(), \"admin\", StringComparison.OrdinalIgnoreCase)", controller);
        Assert.Contains("operationService.GetBatchesAsync", controller);
        Assert.Contains("CanControlSubscriptionCommands(currentUser),", controller);
        Assert.Contains("if (!CanControlSubscriptionCommands(currentUser)) return Forbid();", controller);
        Assert.Contains("public async Task<IActionResult> DownloadTemplate()", controller);
        Assert.DoesNotContain("IsAdminPrincipal", controller);
        Assert.Contains("asp-action=\"DownloadTemplate\"", operationIndex);
        Assert.Contains("@if (Model.CanManage)", operationIndex);
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Services", "KvhSubscriptionOperationService.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the StarlinkDeviceManager project root.");
    }
}
