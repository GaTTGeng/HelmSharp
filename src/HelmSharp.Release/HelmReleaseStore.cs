using System.Text;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace HelmSharp.Release;

/// <summary>
/// Stores release revisions as Kubernetes Secrets in Helm v3 layout:
/// type <c>helm.sh/release.v1</c>, name <c>sh.helm.release.v1.&lt;name&gt;.v&lt;revision&gt;</c>,
/// payload Base64(gzip(JSON)) under the <c>release</c> data key.
/// </summary>
public sealed class HelmReleaseStore
{
    private readonly k8s.Kubernetes _client;

    /// <param name="client">Kubernetes client used for Secret reads and writes.</param>
    public HelmReleaseStore(k8s.Kubernetes client)
    {
        _client = client;
    }

    /// <summary>
    /// Returns the next revision number for a release (max stored revision + 1, or 1 when empty).
    /// </summary>
    public async Task<int> NextRevisionAsync(string name, string ns, CancellationToken cancellationToken)
    {
        var history = await HistoryAsync(name, ns, cancellationToken);
        return history.Count == 0 ? 1 : history.Max(x => x.Revision) + 1;
    }

    /// <summary>
    /// Writes a release revision, replacing its existing Secret when present and creating it otherwise.
    /// </summary>
    public async Task SaveAsync(HelmReleaseRecord record, CancellationToken cancellationToken)
    {
        // One Secret per (release, revision) named sh.helm.release.v1.<name>.v<rev>.
        // Upsert: replace carries the prior resourceVersion so a concurrent writer
        // conflicts instead of silently clobbering the revision.
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

    /// <summary>
    /// Creates a release revision only when it does not already exist.
    /// </summary>
    /// <returns><see langword="true"/> when the revision was created; otherwise, <see langword="false"/> when it already exists.</returns>
    public async Task<bool> TryCreateAsync(
        HelmReleaseRecord record,
        CancellationToken cancellationToken,
        string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var secret = BuildSecret(record, existing: null, DateTimeOffset.UtcNow);
        if (!string.IsNullOrWhiteSpace(operationId))
            secret.Metadata.Labels![RollbackOperationIdLabel] = operationId;
        try
        {
            await _client.CoreV1.CreateNamespacedSecretAsync(secret, record.Namespace, cancellationToken: cancellationToken);
            return true;
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 409)
        {
            return false;
        }
    }

    /// <summary>
    /// Marks a pending rollback reservation as failed only when it belongs to the supplied operation.
    /// </summary>
    /// <returns><see langword="true"/> when the owned pending reservation was marked failed; otherwise, <see langword="false"/>.</returns>
    public async Task<bool> TryMarkPendingRollbackFailedAsync(
        HelmReleaseRecord record,
        string operationId,
        string description,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var secretName = SecretName(record.Name, record.Revision);
        V1Secret existing;
        try
        {
            existing = await _client.CoreV1.ReadNamespacedSecretAsync(
                secretName,
                record.Namespace,
                cancellationToken: cancellationToken);
        }
        catch (HttpOperationException ex) when ((int)ex.Response.StatusCode == 404)
        {
            return false;
        }

        if (existing.Metadata?.Labels is not { } labels ||
            !labels.TryGetValue(RollbackOperationIdLabel, out var storedOperationId) ||
            storedOperationId != operationId)
            return false;

        var persisted = ReadRecord(existing);
        if (!string.Equals(persisted.Status, "pending-rollback", StringComparison.OrdinalIgnoreCase))
            return false;

        var failed = persisted with
        {
            Status = "failed",
            UpdatedAt = DateTimeOffset.UtcNow,
            Description = description
        };
        var secret = BuildSecret(failed, existing, DateTimeOffset.UtcNow);
        await _client.CoreV1.ReplaceNamespacedSecretAsync(
            secret,
            secretName,
            record.Namespace,
            cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>
    /// Lists the latest active revision per release in a namespace (or all namespaces).
    /// Only revisions with status <c>deployed</c> count as active.
    /// </summary>
    /// <param name="ns">Namespace to search; ignored when <paramref name="allNamespaces"/> is true.</param>
    /// <param name="allNamespaces">Search every namespace via the <c>owner=helm</c> label selector.</param>
    /// <param name="cancellationToken">Cancels Secret list calls.</param>
    public async Task<List<HelmReleaseRecord>> ListAsync(string? ns, bool allNamespaces, CancellationToken cancellationToken)
    {
        // Label selector owner=helm is the index Helm keeps on release Secrets; it
        // lets one list call find every release without enumerating secret names.
        var secrets = allNamespaces
            ? await _client.CoreV1.ListSecretForAllNamespacesAsync(labelSelector: "owner=helm", cancellationToken: cancellationToken)
            : await _client.CoreV1.ListNamespacedSecretAsync(ns ?? "default", labelSelector: "owner=helm", cancellationToken: cancellationToken);

        // Revision supersede model: among each release's revisions only "deployed"
        // counts as active, and the highest of those wins — superseded and
        // uninstalled revisions never surface in the list.
        return secrets.Items
            .Select(ReadRecord)
            .Where(IsActiveRelease)
            .GroupBy(x => new { x.Namespace, x.Name })
            .Select(g => g.OrderByDescending(x => x.Revision).First())
            .OrderBy(x => x.Namespace, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Returns every stored revision of a release ordered by revision number,
    /// including superseded and uninstalled revisions.
    /// </summary>
    public async Task<List<HelmReleaseRecord>> HistoryAsync(string name, string ns, CancellationToken cancellationToken)
    {
        // name=<release> narrows the owner=helm set to one release's full revision
        // chain (history is append-only and keeps superseded/uninstalled revisions).
        var secrets = await _client.CoreV1.ListNamespacedSecretAsync(
            ns,
            labelSelector: $"owner=helm,name={name}",
            cancellationToken: cancellationToken);

        return secrets.Items
            .Select(ReadRecord)
            .OrderBy(x => x.Revision)
            .ToList();
    }

    /// <summary>
    /// Returns the highest-revision active release, or null when the release has no
    /// deployed revision (never installed, or uninstalled).
    /// </summary>
    public async Task<HelmReleaseRecord?> GetLatestAsync(string name, string ns, CancellationToken cancellationToken)
    {
        // Latest active only: the supersede filter drops stale revisions and the
        // uninstall tombstone, so a deleted release reports as absent.
        var history = await HistoryAsync(name, ns, cancellationToken);
        return history
            .Where(IsActiveRelease)
            .OrderByDescending(x => x.Revision)
            .FirstOrDefault();
    }

    /// <summary>
    /// Records an uninstall following Helm's revision model: the current revision is
    /// marked <c>superseded</c> and a new revision with status <c>uninstalled</c>
    /// (carrying the deletion timestamp) is appended as the tombstone.
    /// </summary>
    public async Task MarkUninstalledAsync(HelmReleaseRecord record, CancellationToken cancellationToken)
    {
        // Uninstall tombstone model: history is append-only. The live revision is
        // flipped to "superseded" first, then a new revision numbered +1 with status
        // "uninstalled" is appended as the tombstone (never a Secret deletion).
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

    /// <summary>Deletes all Helm release Secret records for the supplied release.</summary>
    public async Task PurgeAsync(string name, string ns, CancellationToken cancellationToken)
    {
        var history = await HistoryAsync(name, ns, cancellationToken);
        foreach (var record in history)
            await DeleteAsync(record, cancellationToken);
    }

    /// <summary>Deletes one stored release revision.</summary>
    public Task DeleteAsync(HelmReleaseRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _client.CoreV1.DeleteNamespacedSecretAsync(
            SecretName(record.Name, record.Revision),
            record.Namespace,
            cancellationToken: cancellationToken);
    }

    /// <summary>Updates a revision's status and timestamp and persists it.</summary>
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

        // Helm v3 payload key is "release" (Base64(gzip(JSON))). A "release.json"
        // key indicates a legacy HelmSharp layout kept readable for migration.
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
        // System labels (name/owner/status/version/createdAt/modifiedAt) are managed
        // here and filtered out of user-visible labels on read; custom labels from the
        // existing Secret and the record are preserved across rewrites.
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        MergeCustomLabels(labels, existing?.Metadata?.Labels);
        MergeCustomLabels(labels, record.Labels);
        if (existing?.Metadata?.Labels?.TryGetValue(RollbackOperationIdLabel, out var operationId) == true)
            labels[RollbackOperationIdLabel] = operationId;
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

    // Helm v3 Secret naming scheme; `helm ls` / `helm history` discover revisions
    // only through this exact name shape and the owner=helm label.
    internal static string SecretName(string releaseName, int revision)
        => $"sh.helm.release.v1.{releaseName}.v{revision}";

    private static readonly HashSet<string> SystemLabels =
        ["name", "owner", "status", "version", "createdAt", "modifiedAt", RollbackOperationIdLabel];

    private const string RollbackOperationIdLabel = "helmsharp.sh/rollback-operation-id";

    private static bool IsActiveRelease(HelmReleaseRecord record)
        => string.Equals(record.Status, "deployed", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonDefaults = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
