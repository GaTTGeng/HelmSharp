using System.Text;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace HelmSharp.Release;

public sealed class HelmReleaseStore
{
    private readonly k8s.Kubernetes _client;

    public HelmReleaseStore(k8s.Kubernetes client)
    {
        _client = client;
    }

    public async Task<int> NextRevisionAsync(string name, string ns, CancellationToken cancellationToken)
    {
        var history = await HistoryAsync(name, ns, cancellationToken);
        return history.Count == 0 ? 1 : history.Max(x => x.Revision) + 1;
    }

    public async Task SaveAsync(HelmReleaseRecord record, CancellationToken cancellationToken)
    {
        var secretName = SecretName(record.Name, record.Revision);
        var json = JsonSerializer.Serialize(record, JsonDefaults);
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = record.Namespace,
                Labels = new Dictionary<string, string>
                {
                    ["owner"] = "helm",
                    ["name"] = record.Name,
                    ["status"] = record.Status,
                    ["version"] = record.Revision.ToString()
                }
            },
            Type = "helm.sh/release.v1",
            StringData = new Dictionary<string, string>
            {
                ["release.json"] = json,
                ["manifest"] = record.Manifest,
                ["values.yaml"] = record.ValuesYaml
            }
        };

        try
        {
            var existing = await _client.CoreV1.ReadNamespacedSecretAsync(secretName, record.Namespace, cancellationToken: cancellationToken);
            secret.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
            await _client.CoreV1.ReplaceNamespacedSecretAsync(secret, secretName, record.Namespace, cancellationToken: cancellationToken);
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            await _client.CoreV1.CreateNamespacedSecretAsync(secret, record.Namespace, cancellationToken: cancellationToken);
        }
    }

    public async Task<List<HelmReleaseRecord>> ListAsync(string? ns, bool allNamespaces, CancellationToken cancellationToken)
    {
        var secrets = allNamespaces
            ? await _client.CoreV1.ListSecretForAllNamespacesAsync(labelSelector: "owner=helm", cancellationToken: cancellationToken)
            : await _client.CoreV1.ListNamespacedSecretAsync(ns ?? "default", labelSelector: "owner=helm", cancellationToken: cancellationToken);

        return secrets.Items
            .Select(TryReadRecord)
            .Where(x => x is not null)
            .Select(x => x!)
            .Where(IsActiveRelease)
            .GroupBy(x => new { x.Namespace, x.Name })
            .Select(g => g.OrderByDescending(x => x.Revision).First())
            .OrderBy(x => x.Namespace, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<List<HelmReleaseRecord>> HistoryAsync(string name, string ns, CancellationToken cancellationToken)
    {
        var secrets = await _client.CoreV1.ListNamespacedSecretAsync(
            ns,
            labelSelector: $"owner=helm,name={name}",
            cancellationToken: cancellationToken);

        return secrets.Items
            .Select(TryReadRecord)
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderBy(x => x.Revision)
            .ToList();
    }

    public async Task<HelmReleaseRecord?> GetLatestAsync(string name, string ns, CancellationToken cancellationToken)
    {
        var history = await HistoryAsync(name, ns, cancellationToken);
        return history
            .Where(IsActiveRelease)
            .OrderByDescending(x => x.Revision)
            .FirstOrDefault();
    }

    public async Task MarkUninstalledAsync(HelmReleaseRecord record, CancellationToken cancellationToken)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var uninstalled = record with
        {
            Revision = record.Revision + 1,
            Status = "uninstalled",
            UpdatedAt = updatedAt
        };

        record.Status = "superseded";
        record.UpdatedAt = updatedAt;
        await SaveAsync(record, cancellationToken);

        record.Status = "uninstalled";
        record.UpdatedAt = updatedAt;
        await SaveAsync(uninstalled, cancellationToken);
    }

    public async Task MarkStatusAsync(HelmReleaseRecord record, string status, CancellationToken cancellationToken)
    {
        record.Status = status;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(record, cancellationToken);
    }

    private static HelmReleaseRecord? TryReadRecord(V1Secret secret)
    {
        try
        {
            string? json = null;
            if (secret.StringData is not null && secret.StringData.TryGetValue("release.json", out var stringData))
                json = stringData;
            else if (secret.Data is not null && secret.Data.TryGetValue("release.json", out var data))
                json = Encoding.UTF8.GetString(data);

            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<HelmReleaseRecord>(json, JsonDefaults);
        }
        catch
        {
            return null;
        }
    }

    private static string SecretName(string releaseName, int revision)
        => $"sh.helm.release.v1.{releaseName}.v{revision}";

    private static bool IsActiveRelease(HelmReleaseRecord record)
        => string.Equals(record.Status, "deployed", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonDefaults = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

public sealed record HelmReleaseRecord
{
    public string Name { get; init; } = string.Empty;
    public string Namespace { get; init; } = "default";
    public int Revision { get; init; }
    public string Status { get; set; } = "deployed";
    public string ChartName { get; init; } = string.Empty;
    public string ChartVersion { get; init; } = string.Empty;
    public string? AppVersion { get; init; }
    public string Manifest { get; init; } = string.Empty;
    public string ValuesYaml { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public Dictionary<string, string>? Labels { get; init; }
}
