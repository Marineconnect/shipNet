namespace StarlinkDeviceManager.Tests;

public class KvhSubscriptionOperationTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    [Fact]
    public void OperationHistoryMigrationCreatesExpectedTablesAndIndexes()
    {
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "Database", "Scripts", "20260731_AddKvhSubscriptionOperationHistory.sql"));

        Assert.Contains("TblKvhSubscriptionOperationBatch", script);
        Assert.Contains("TblKvhSubscriptionOperationItem", script);
        Assert.Contains("TblKvhSubscriptionOperationAudit", script);
        Assert.Contains("UQ_KvhSubOperationItem_Batch_Kit", script);
        Assert.Contains("IX_KvhSubOperationItem_Status_NextSubmit", script);
        Assert.Contains("FK_KvhSubOperationItem_KvhCommand", script);
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
        Assert.Contains("Kết quả kiểm tra file import", operationDetails);
        Assert.Contains("kvh-stat-grid", operationDetails);
        Assert.Contains("kvh-flow-step", operationDetails);
        Assert.Contains("kvh-row-spinner", operationDetails);
        Assert.Contains("data-kvh-operation-live", operationDetails);
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
