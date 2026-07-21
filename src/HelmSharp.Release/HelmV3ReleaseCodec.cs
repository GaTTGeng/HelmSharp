using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HelmSharp.Chart;

namespace HelmSharp.Release;

internal static class HelmV3ReleaseCodec
{
    private static readonly byte[] GzipMagic = [0x1f, 0x8b, 0x08];

    public static string Encode(HelmReleaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateIdentity(record.Name, record.Namespace, record.Revision);

        var chart = BuildChart(record);
        var info = new JsonObject
        {
            ["first_deployed"] = FormatTime(record.FirstDeployedAt ?? record.UpdatedAt),
            ["last_deployed"] = FormatTime(record.UpdatedAt),
            ["deleted"] = FormatTime(record.DeletedAt),
            ["description"] = record.Description,
            ["status"] = record.Status,
            ["notes"] = record.Notes
        };
        var hooks = new JsonArray(record.Hooks.Select(ToJsonHook).ToArray());
        var release = new JsonObject
        {
            ["name"] = record.Name,
            ["info"] = info,
            ["chart"] = chart,
            ["config"] = JsonSerializer.SerializeToNode(HelmYaml.DeserializeDictionary(record.ValuesYaml)),
            ["manifest"] = record.Manifest,
            ["hooks"] = hooks,
            ["version"] = record.Revision,
            ["namespace"] = record.Namespace
        };
        if (!string.IsNullOrWhiteSpace(record.ComputedValuesYaml))
            release["helmsharp_computed_values"] = JsonSerializer.SerializeToNode(HelmYaml.DeserializeDictionary(record.ComputedValuesYaml));

        var json = Encoding.UTF8.GetBytes(release.ToJsonString());
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(json);
        return Convert.ToBase64String(output.ToArray());
    }

    public static HelmReleaseRecord Decode(string encodedRelease)
    {
        if (string.IsNullOrWhiteSpace(encodedRelease))
            throw new InvalidDataException("The Helm release payload is empty.");

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(encodedRelease);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The Helm release payload is not valid Base64.", ex);
        }

        if (payload.AsSpan().StartsWith(GzipMagic))
        {
            try
            {
                using var compressed = new MemoryStream(payload, writable: false);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var decompressed = new MemoryStream();
                gzip.CopyTo(decompressed);
                payload = decompressed.ToArray();
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException("The Helm release payload contains invalid gzip data.", ex);
            }
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var name = GetString(root, "name");
            var namespaceName = GetString(root, "namespace");
            var revision = GetInt32(root, "version");
            ValidateIdentity(name, namespaceName, revision);

            var info = TryGetObject(root, "info");
            var chart = TryGetObject(root, "chart");
            var metadata = chart is { } chartElement ? TryGetObject(chartElement, "metadata") : null;
            var firstDeployed = ParseTime(GetString(info, "first_deployed"));
            var updatedAt = ParseTime(GetString(info, "last_deployed"))
                ?? firstDeployed
                ?? DateTimeOffset.MinValue;
            var deletedAt = ParseTime(GetString(info, "deleted"));
            if (deletedAt == DateTimeOffset.MinValue)
                deletedAt = null;

            var values = root.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object
                ? (Dictionary<string, object?>)JsonElementToObject(config)!
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            var computedValuesYaml = root.TryGetProperty("helmsharp_computed_values", out var computed) && computed.ValueKind == JsonValueKind.Object
                ? HelmYaml.Serialize((Dictionary<string, object?>)JsonElementToObject(computed)!)
                : string.Empty;
            var chartValues = chart is { } chartValue && chartValue.TryGetProperty("values", out var defaults) && defaults.ValueKind == JsonValueKind.Object
                ? (Dictionary<string, object?>)JsonElementToObject(defaults)!
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            var hooks = root.TryGetProperty("hooks", out var hookElements) && hookElements.ValueKind == JsonValueKind.Array
                ? hookElements.EnumerateArray().Select(FromJsonHook).ToList()
                : [];

            return new HelmReleaseRecord
            {
                Name = name!,
                Namespace = namespaceName!,
                Revision = revision,
                Status = GetString(info, "status") ?? string.Empty,
                ChartName = GetString(metadata, "name") ?? string.Empty,
                ChartVersion = GetString(metadata, "version") ?? string.Empty,
                AppVersion = GetString(metadata, "appVersion"),
                ChartApiVersion = GetString(metadata, "apiVersion"),
                ChartDescription = GetString(metadata, "description"),
                ChartType = GetString(metadata, "type"),
                ChartKubeVersion = GetString(metadata, "kubeVersion"),
                ChartValuesYaml = HelmYaml.Serialize(chartValues),
                RawChartJson = chart?.GetRawText(),
                Manifest = GetString(root, "manifest") ?? string.Empty,
                ValuesYaml = HelmYaml.Serialize(values),
                ComputedValuesYaml = computedValuesYaml,
                FirstDeployedAt = firstDeployed,
                UpdatedAt = updatedAt,
                DeletedAt = deletedAt,
                Description = GetString(info, "description"),
                Notes = GetString(info, "notes"),
                Hooks = hooks
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The Helm release payload does not contain valid release JSON.", ex);
        }
    }


    internal static string CreateChartSnapshot(HelmChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return CreateChartSnapshotNode(chart).ToJsonString();
    }

    private static JsonObject CreateChartSnapshotNode(HelmChart chart)
    {
        var metadata = new JsonObject
        {
            ["name"] = chart.Name,
            ["home"] = chart.Home,
            ["sources"] = JsonSerializer.SerializeToNode(chart.Sources),
            ["version"] = chart.Version,
            ["description"] = chart.Description,
            ["keywords"] = JsonSerializer.SerializeToNode(chart.Keywords),
            ["maintainers"] = JsonSerializer.SerializeToNode(chart.Maintainers),
            ["icon"] = chart.Icon,
            ["apiVersion"] = string.IsNullOrWhiteSpace(chart.ApiVersion) ? "v2" : chart.ApiVersion,
            ["appVersion"] = chart.AppVersion,
            ["deprecated"] = chart.Deprecated,
            ["annotations"] = JsonSerializer.SerializeToNode(chart.Annotations),
            ["kubeVersion"] = chart.KubeVersion,
            ["dependencies"] = new JsonArray(chart.Dependencies.Select(ToJsonDependency).ToArray()),
            ["type"] = chart.Type
        };

        var templates = new JsonArray(chart.Templates
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => ToJsonFile(pair.Key, Encoding.UTF8.GetBytes(pair.Value)))
            .ToArray());

        var schema = chart.Files.FirstOrDefault(pair =>
            string.Equals(pair.Key, "values.schema.json", StringComparison.OrdinalIgnoreCase));
        var files = new JsonArray(chart.Files
            .Where(pair =>
                !string.Equals(pair.Key, "values.schema.json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(pair.Key, "Chart.lock", StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => ToJsonFile(pair.Key, pair.Value))
            .ToArray());

        var hasLock = chart.LockEntries.Count > 0 ||
                      !string.IsNullOrWhiteSpace(chart.LockDigest) ||
                      !string.IsNullOrWhiteSpace(chart.LockGenerated);
        JsonNode? lockNode = hasLock
            ? new JsonObject
            {
                ["generated"] = chart.LockGenerated,
                ["digest"] = chart.LockDigest,
                ["dependencies"] = new JsonArray(chart.LockEntries.Select(ToJsonLockDependency).ToArray())
            }
            : null;

        return new JsonObject
        {
            ["metadata"] = metadata,
            ["lock"] = lockNode,
            ["templates"] = templates,
            ["values"] = JsonSerializer.SerializeToNode(HelmYaml.DeserializeDictionary(chart.ValuesYaml)),
            ["schema"] = schema.Value is null ? null : Convert.ToBase64String(schema.Value),
            ["files"] = files,
            ["crds"] = new JsonArray(chart.Crds
                .Select((crd, index) => ToJsonCrd(crd, index))
                .ToArray()),
            ["dependencies"] = new JsonArray(chart.Subcharts
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => CreateChartSnapshotNode(pair.Value))
                .ToArray())
        };
    }

    private static JsonNode ToJsonDependency(HelmChartDependency dependency)
        => new JsonObject
        {
            ["name"] = dependency.Name,
            ["version"] = dependency.Version,
            ["repository"] = dependency.Repository,
            ["condition"] = dependency.Condition,
            ["tags"] = JsonSerializer.SerializeToNode(dependency.Tags),
            ["enabled"] = dependency.Enabled,
            ["import-values"] = JsonSerializer.SerializeToNode(dependency.ImportValues),
            ["alias"] = dependency.Alias
        };

    private static JsonNode ToJsonLockDependency(HelmChartLockEntry dependency)
        => new JsonObject
        {
            ["name"] = dependency.Name,
            ["version"] = dependency.Version,
            ["repository"] = dependency.Repository,
            ["digest"] = dependency.Digest
        };

    private static JsonNode ToJsonFile(string name, byte[] data)
        => new JsonObject
        {
            ["name"] = name,
            ["data"] = Convert.ToBase64String(data)
        };

    private static JsonNode ToJsonCrd(Dictionary<string, object?> crd, int index)
    {
        var resourceName = crd.TryGetValue("metadata", out var metadataValue) &&
                           metadataValue is IDictionary<string, object?> metadata &&
                           metadata.TryGetValue("name", out var nameValue)
            ? Convert.ToString(nameValue)
            : null;
        var name = string.IsNullOrWhiteSpace(resourceName)
            ? $"crds/crd-{index + 1}.yaml"
            : $"crds/{resourceName}.yaml";
        return ToJsonFile(name, Encoding.UTF8.GetBytes(HelmYaml.Serialize(crd)));
    }

    private static JsonObject BuildChart(HelmReleaseRecord record)
    {
        JsonObject chart;
        if (string.IsNullOrWhiteSpace(record.RawChartJson))
        {
            chart = new JsonObject
            {
                ["lock"] = null,
                ["templates"] = new JsonArray(),
                ["schema"] = null,
                ["files"] = new JsonArray()
            };
        }
        else
        {
            try
            {
                chart = JsonNode.Parse(record.RawChartJson) as JsonObject
                    ?? throw new InvalidDataException("The preserved Helm chart payload is not a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The preserved Helm chart payload is not valid JSON.", ex);
            }
        }

        var metadata = chart["metadata"] as JsonObject ?? new JsonObject();
        chart["metadata"] = metadata;
        metadata["name"] = record.ChartName;
        metadata["version"] = record.ChartVersion;
        metadata["appVersion"] = record.AppVersion;
        metadata["apiVersion"] = string.IsNullOrWhiteSpace(record.ChartApiVersion) ? "v2" : record.ChartApiVersion;
        metadata["description"] = record.ChartDescription;
        metadata["type"] = record.ChartType;
        metadata["kubeVersion"] = record.ChartKubeVersion;
        chart["values"] = JsonSerializer.SerializeToNode(
            HelmYaml.DeserializeDictionary(record.ChartValuesYaml));
        return chart;
    }
    private static JsonNode ToJsonHook(HelmReleaseHookRecord hook)
        => new JsonObject
        {
            ["name"] = hook.Name,
            ["kind"] = hook.Kind,
            ["path"] = hook.Path,
            ["manifest"] = hook.Manifest,
            ["events"] = new JsonArray(hook.Events.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["last_run"] = new JsonObject
            {
                ["started_at"] = FormatTime(hook.LastRunStartedAt),
                ["completed_at"] = FormatTime(hook.LastRunCompletedAt),
                ["phase"] = hook.LastRunPhase ?? "Unknown"
            },
            ["weight"] = hook.Weight,
            ["delete_policies"] = new JsonArray(hook.DeletePolicies.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["output_log_policies"] = new JsonArray(hook.OutputLogPolicies.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
        };

    private static HelmReleaseHookRecord FromJsonHook(JsonElement hook)
    {
        var lastRun = TryGetObject(hook, "last_run");
        var startedAt = ParseTime(GetString(lastRun, "started_at"));
        var completedAt = ParseTime(GetString(lastRun, "completed_at"));
        return new HelmReleaseHookRecord
        {
            Name = GetString(hook, "name") ?? string.Empty,
            Kind = GetString(hook, "kind") ?? string.Empty,
            Path = GetString(hook, "path") ?? string.Empty,
            Manifest = GetString(hook, "manifest") ?? string.Empty,
            Events = GetStrings(hook, "events"),
            LastRunStartedAt = startedAt == DateTimeOffset.MinValue ? null : startedAt,
            LastRunCompletedAt = completedAt == DateTimeOffset.MinValue ? null : completedAt,
            LastRunPhase = GetString(lastRun, "phase"),
            Weight = GetInt32(hook, "weight"),
            DeletePolicies = GetStrings(hook, "delete_policies"),
            OutputLogPolicies = GetStrings(hook, "output_log_policies")
        };
    }

    private static JsonElement? TryGetObject(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? GetString(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } value
           && value.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int GetInt32(JsonElement? element, string name)
        => element is { ValueKind: JsonValueKind.Object } value
           && value.TryGetProperty(name, out var property)
           && property.TryGetInt32(out var result)
            ? result
            : 0;

    private static List<string> GetStrings(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToList()
            : [];

    private static void ValidateIdentity(string? name, string? namespaceName, int revision)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("The release name is missing.");
        if (string.IsNullOrWhiteSpace(namespaceName))
            throw new InvalidDataException("The release namespace is missing.");
        if (revision <= 0)
            throw new InvalidDataException("The release revision must be greater than zero.");
    }

    private static string FormatTime(DateTimeOffset? value)
        => value is null
            ? string.Empty
            : value.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;
        throw new InvalidDataException($"The Helm release contains an invalid timestamp '{value}'.");
    }

    private static object? JsonElementToObject(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => JsonElementToObject(property.Value),
                StringComparer.Ordinal),
            _ => null
        };
}
