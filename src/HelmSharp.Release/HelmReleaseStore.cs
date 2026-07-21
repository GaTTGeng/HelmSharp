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
        try
        {
            var existing = await _client.CoreV1.ReadNamespacedSecretAsync(secretName, record.Namespace, cancellationToken: cancellationToken);
            var secret = BuildSecret(record, existing, DateTimeOffset.UtcNow);
            await _client.CoreV1.ReplaceNamespacedSecretAsync(secret, secretName, record.Namespace, cancellationToken: cancellationToken);
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            var secret = BuildSecret(record, existing: null, DateTimeOffset.UtcNow);
            await _client.CoreV1.CreateNamespacedSecretAsync(secret, record.Namespace, cancellationToken: cancellationToken);
        }
    }

    public async Task<List<HelmReleaseRecord>> ListAsync(string? ns, bool allNamespaces, CancellationToken cancellationToken)
    {
        var secrets = allNamespaces
            ? await _client.CoreV1.ListSecretForAllNamespacesAsync(labelSelector: "owner=helm", cancellationToken: cancellationToken)
            : await _client.CoreV1.ListNamespacedSecretAsync(ns ?? "default", labelSelector: "owner=helm", cancellationToken: cancellationToken);

        return secrets.Items
            .Select(ReadRecord)
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
            .Select(ReadRecord)
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
            UpdatedAt = updatedAt,
            DeletedAt = updatedAt
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

    internal static HelmReleaseRecord ReadRecord(V1Secret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var secretName = secret.Metadata?.Name ?? "<unknown>";
        var namespaceName = secret.Metadata?.NamespaceProperty ?? "default";

        if (TryGetPayload(secret, "release", out var helmPayload))
        {
            try
            {
                return ApplySecretMetadata(
                    HelmV3ReleaseCodec.Decode(Encoding.UTF8.GetString(helmPayload)),
                    secret);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                throw new HelmReleaseStoreException(secretName, namespaceName, "Helm v3 release", ex.Message, ex);
            }
        }

        if (TryGetPayload(secret, "release.json", out var legacyPayload))
        {
            try
            {
                var record = JsonSerializer.Deserialize<HelmReleaseRecord>(legacyPayload, JsonDefaults)
                    ?? throw new InvalidDataException("The legacy release JSON was empty.");
                return ApplySecretMetadata(record, secret);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                throw new HelmReleaseStoreException(secretName, namespaceName, "legacy release.json", ex.Message, ex);
            }
        }

        throw new HelmReleaseStoreException(
            secretName,
            namespaceName,
            "release",
            "Neither data.release nor the legacy release.json key was present.");
    }

    internal static V1Secret BuildSecret(
        HelmReleaseRecord record,
        V1Secret? existing,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(record);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        MergeCustomLabels(labels, existing?.Metadata?.Labels);
        MergeCustomLabels(labels, record.Labels);
        labels[existing is null ? "createdAt" : "modifiedAt"] = timestamp.ToUnixTimeSeconds().ToString();
        labels["name"] = record.Name;
        labels["owner"] = "helm";
        labels["status"] = record.Status;
        labels["version"] = record.Revision.ToString();

        return new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = SecretName(record.Name, record.Revision),
                NamespaceProperty = record.Namespace,
                ResourceVersion = existing?.Metadata?.ResourceVersion,
                Labels = labels
            },
            Type = "helm.sh/release.v1",
            Data = new Dictionary<string, byte[]>
            {
                ["release"] = Encoding.UTF8.GetBytes(HelmV3ReleaseCodec.Encode(record))
            }
        };
    }

    private static bool TryGetPayload(V1Secret secret, string key, out byte[] payload)
    {
        if (secret.Data is not null && secret.Data.TryGetValue(key, out payload!))
            return true;
        if (secret.StringData is not null && secret.StringData.TryGetValue(key, out var text))
        {
            payload = Encoding.UTF8.GetBytes(text);
            return true;
        }
        payload = [];
        return false;
    }

    private static HelmReleaseRecord ApplySecretMetadata(HelmReleaseRecord record, V1Secret secret)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        MergeCustomLabels(labels, secret.Metadata?.Labels);
        return record with
        {
            Namespace = string.IsNullOrWhiteSpace(record.Namespace)
                ? secret.Metadata?.NamespaceProperty ?? "default"
                : record.Namespace,
            Labels = labels.Count == 0 ? null : labels
        };
    }

    private static void MergeCustomLabels(
        Dictionary<string, string> target,
        IDictionary<string, string>? source)
    {
        if (source is null)
            return;
        foreach (var (key, value) in source)
        {
            if (!SystemLabels.Contains(key))
                target[key] = value;
        }
    }

    internal static string SecretName(string releaseName, int revision)
        => $"sh.helm.release.v1.{releaseName}.v{revision}";

    private static readonly HashSet<string> SystemLabels =
        ["name", "owner", "status", "version", "createdAt", "modifiedAt"];

    private static bool IsActiveRelease(HelmReleaseRecord record)
        => string.Equals(record.Status, "deployed", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonDefaults = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
