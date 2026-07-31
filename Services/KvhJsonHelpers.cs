using System.Text.Json;

namespace StarlinkDeviceManager.Services;

internal static class KvhJsonHelpers
{
    public static KvhScheduledActionInfo? ResolveScheduledAction(JsonElement element, DateTime? nowUtc = null)
    {
        var candidates = EnumerateScheduleContainers(element)
            .SelectMany(EnumerateScheduleItems)
            .Select(ParseScheduledAction)
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Type))
            .Select(item => item!)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        var now = nowUtc ?? DateTime.UtcNow;
        return candidates
            .OrderBy(item => IsExpired(item.EffectiveDateUtc, now) ? 1 : 0)
            .ThenBy(item => item.Type is "SUSPEND" or "RESUME" ? 0 : 1)
            .ThenBy(item => item.EffectiveDateUtc ?? DateTime.MaxValue)
            .ThenByDescending(item => item.CreatedAtUtc ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    public static string FindStringValue(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                if (!element.TryGetProperty(propertyName, out var propertyValue))
                {
                    continue;
                }

                var value = ScalarToString(propertyValue);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var value = FindStringValue(property.Value, propertyNames);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var value = FindStringValue(item, propertyNames);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }

    public static bool? FindBooleanValue(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                if (!element.TryGetProperty(propertyName, out var propertyValue))
                {
                    continue;
                }

                if (propertyValue.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (propertyValue.ValueKind == JsonValueKind.False)
                {
                    return false;
                }

                if (propertyValue.ValueKind == JsonValueKind.String &&
                    bool.TryParse(propertyValue.GetString(), out var parsed))
                {
                    return parsed;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var value = FindBooleanValue(property.Value, propertyNames);
                if (value.HasValue)
                {
                    return value;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var value = FindBooleanValue(item, propertyNames);
                if (value.HasValue)
                {
                    return value;
                }
            }
        }

        return null;
    }

    public static long? FindLongValue(JsonElement element, params string[] propertyNames)
    {
        var value = FindStringValue(element, propertyNames);
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    public static string ExtractJobId(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            return FindStringValue(document.RootElement, "job_id", "jobId", "jobID", "id");
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public static string MaskWifiSecrets(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return rawJson;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var masked = MaskElement(document.RootElement);
            return JsonSerializer.Serialize(masked);
        }
        catch (JsonException)
        {
            return rawJson;
        }
    }

    private static object? MaskElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => IsSecretName(property.Name) ? "***" : MaskElement(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(MaskElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue : element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static bool IsSecretName(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "psk", StringComparison.OrdinalIgnoreCase);

    private static string ScalarToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static IEnumerable<JsonElement> EnumerateScheduleContainers(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var name in new[] { "scheduled", "schedule", "scheduled_actions", "scheduledActions" })
        {
            if (element.TryGetProperty(name, out var child) &&
                child.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<JsonElement> EnumerateScheduleItems(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    yield return item;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
        }
    }

    private static KvhScheduledActionInfo? ParseScheduledAction(JsonElement element)
    {
        var type = NormalizeScheduledAction(FindStringValue(element, "type", "action", "scheduled_action", "scheduledAction"));
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        return new KvhScheduledActionInfo
        {
            Type = type,
            ScheduleId = FindStringValue(element, "id", "schedule_id", "scheduleId"),
            EffectiveDateUtc = TryFindDate(element, "effective_date", "effectiveDate", "scheduled_effective_date", "scheduledEffectiveDate"),
            CreatedAtUtc = TryFindDate(element, "created_at", "createdAt"),
            RawJson = element.GetRawText()
        };
    }

    public static string NormalizeScheduledAction(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", "_");
        if (normalized is "PAUSE" or "SUSPEND" or "SUSPENDED")
        {
            return "SUSPEND";
        }

        if (normalized is "RESUME" or "ACTIVATE" or "ACTIVE")
        {
            return "RESUME";
        }

        return normalized;
    }

    public static DateTime? TryFindDate(JsonElement element, params string[] names)
    {
        var value = FindStringValue(element, names);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed.UtcDateTime : null;
    }

    private static bool IsExpired(DateTime? effectiveDateUtc, DateTime nowUtc) =>
        effectiveDateUtc.HasValue && effectiveDateUtc.Value < nowUtc.AddMinutes(-5);
}

internal sealed class KvhScheduledActionInfo
{
    public string Type { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public DateTime? EffectiveDateUtc { get; set; }
    public DateTime? CreatedAtUtc { get; set; }
    public string RawJson { get; set; } = string.Empty;
}
