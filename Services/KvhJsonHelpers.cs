using System.Text.Json;

namespace StarlinkDeviceManager.Services;

internal static class KvhJsonHelpers
{
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
}
