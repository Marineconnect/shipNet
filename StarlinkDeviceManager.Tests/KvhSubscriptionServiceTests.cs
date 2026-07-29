using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Tests;

public sealed class KvhSubscriptionServiceTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string ServiceSource = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "KvhSubscriptionService.cs"));

    [Fact]
    public void GetSolutionsAsync_ClosesListReaderBeforeTenantQuery()
    {
        var getSolutionsBody = ExtractMethodBody(ServiceSource, "GetSolutionsAsync");
        var readerBlockStart = getSolutionsBody.IndexOf("await using (var reader = await command.ExecuteReaderAsync(cancellationToken))", StringComparison.Ordinal);
        var tenantQuery = getSolutionsBody.IndexOf("var tenants = await GetTenantOptionsAsync(connection, allowedTenantId, cancellationToken);", StringComparison.Ordinal);

        Assert.True(readerBlockStart >= 0, "GetSolutionsAsync should wrap the KVH solution SqlDataReader in an explicit await using block.");
        Assert.True(tenantQuery > readerBlockStart, "Tenant options must be queried after the solution reader block.");

        var between = getSolutionsBody[readerBlockStart..tenantQuery];
        Assert.Contains("while (await reader.ReadAsync(cancellationToken))", between);
        Assert.Contains("items.Add(MapListItem(reader));", between);
        Assert.EndsWith("}", between.TrimEnd());
    }

    [Fact]
    public void KvhSolutions_DoesNotUseMultipleActiveResultSetsAsFix()
    {
        Assert.DoesNotContain("MultipleActiveResultSets=True", ServiceSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MultipleActiveResultSets=True", ReadProjectFile("appsettings.json"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MultipleActiveResultSets=True", ReadProjectFile("appsettings.Development.json"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSolutionsAsync_KeepsTenantDeviceFiltersAndPagination()
    {
        var getSolutionsBody = ExtractMethodBody(ServiceSource, "GetSolutionsAsync");
        var whereBuilder = ExtractMethodBody(ServiceSource, "private static string BuildSolutionWhere");

        Assert.Contains("filter.TenantId = allowedTenantId;", getSolutionsBody);
        Assert.Contains("\"(@allowedTenantId IS NULL OR d.[TenantID] = @allowedTenantId)\"", whereBuilder);
        Assert.Contains("\"(@allowedDeviceId IS NULL OR d.[ID] = @allowedDeviceId)\"", whereBuilder);
        Assert.Contains("\"(@tenantId IS NULL OR d.[TenantID] = @tenantId)\"", whereBuilder);
        Assert.Contains("OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY", getSolutionsBody);
        Assert.Contains("command.Parameters.Add(\"@offset\", SqlDbType.Int).Value = (page - 1) * pageSize;", getSolutionsBody);
        Assert.Contains("command.Parameters.Add(\"@pageSize\", SqlDbType.Int).Value = pageSize;", getSolutionsBody);
    }

    [Fact]
    public void EmptyKvhSolutionPageResult_IsValidViewModelAndKeepsTenantList()
    {
        var model = new KvhSolutionPageResult
        {
            Items = [],
            Tenants = [new DeviceTenantOptionViewModel { Id = 7, TenantName = "Tenant A" }],
            CurrentPage = 2,
            PageSize = 10,
            TotalItems = 0,
            IsTenantScoped = true
        };

        Assert.Empty(model.Items);
        Assert.Single(model.Tenants);
        Assert.Equal("Tenant A", model.Tenants[0].TenantName);
        Assert.Equal(0, model.TotalPages);
        Assert.False(model.HasNextPage);
    }

    private static string ReadProjectFile(string relativePath)
    {
        var path = Path.Combine(ProjectRoot, relativePath);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Services", "KvhSubscriptionService.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the StarlinkDeviceManager project root.");
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var methodStart = source.IndexOf(methodName, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Could not find method {methodName}.");

        var braceStart = source.IndexOf('{', methodStart);
        Assert.True(braceStart >= 0, $"Could not find method body for {methodName}.");

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

        throw new InvalidOperationException($"Could not parse method body for {methodName}.");
    }
}
