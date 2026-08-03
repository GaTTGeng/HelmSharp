# Turn a review into a deployment

Use one release request for the review and another, equivalent request for the approved apply. Before rendering the review, resolve the full release history from an application-owned release store so the preview has the same install/upgrade state and next revision as the apply. After approval, only `DryRun` changes; chart identity, values, namespace, release state, and the options-provider configuration stay fixed.

```csharp
var releaseHistory = await releases.LoadHistoryForUpgradeInstallAsync(
    releaseName,
    targetNamespace,
    createNamespace: true,
    cancellationToken);
var latestRelease = releaseHistory.MaxBy(release => release.Revision);
var isUpgrade = latestRelease is not null &&
    !string.Equals(latestRelease.Status, "uninstalled", StringComparison.OrdinalIgnoreCase);
var nextRevision = latestRelease?.Revision + 1 ?? 1;

var preview = new HelmUpgradeInstallRequest
{
    ReleaseName = releaseName,
    Namespace = targetNamespace,
    Chart = approvedChartPath,
    ValuesFiles = approvedValuesFiles,
    SkipCRDs = true,
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300,
    DryRun = true,
    DryRunIsUpgrade = isUpgrade,
    DryRunRevision = nextRevision
};

var previewResult = await client.UpgradeInstallAsync(preview, cancellationToken);
if (!previewResult.Succeeded)
    return Results.BadRequest("The release preview failed.");

await approvals.SaveAsync(preview, previewResult.StandardOutput, cancellationToken);
```

When the approval service has verified that record, rebuild the request from the persisted fields and apply it:

```csharp
var apply = await approvals.CreateApprovedRequestAsync(approvalId, cancellationToken);
apply.DryRun = false;

var result = await client.UpgradeInstallAsync(apply, cancellationToken);
if (!result.Succeeded)
    return Results.Problem("The approved release could not be applied.", statusCode: 409);

return Results.Ok(new { result.StandardOutput });
```

Do not trust a browser to send a second copy of the values or chart version. The service that applies must read the reviewed record itself. Store a content-addressed immutable chart archive and snapshotted values content (or verify their hashes) with the approval; paths alone are not stable inputs. This example sets `SkipCRDs = true` for both preview and apply: review and install CRDs as a separate, explicitly approved operation. `LoadHistoryForUpgradeInstallAsync` is an application-owned adapter around `HistoryAsync`: when `CreateNamespace` is enabled, it treats a missing target namespace as empty history, just like the apply path. Derive the preview state from the highest revision in that complete history, including failed and retained-uninstall revisions, then persist that resolved state with the approval. Reject it if the release state has changed before apply; otherwise re-render and require a new approval. This workflow requires deterministic templates: the approval service must reject charts that use nondeterministic functions such as `now`, `uuidv4`, or `randAlphaNum`, because the apply renders the chart again. To support such a chart, use a separate deployment path that applies the exact approved manifest. Keep `CommandResult.StandardError`, exit code, and operation ID in a restricted operation record; return a generic failure to untrusted callers.

`Wait`, `WaitForJobs`, `Atomic`, and `TimeoutSeconds` determine the operation's completion contract. See [Install and upgrade releases](../guide/release-workflows.md) before selecting them.
