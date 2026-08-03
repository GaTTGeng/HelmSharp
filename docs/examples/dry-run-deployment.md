# Turn a review into a deployment

Use one release request for the review and another, equivalent request for the approved apply. The important invariant is that only `DryRun` changes after approval; chart identity, values, namespace, and the options-provider configuration stay fixed.

```csharp
var preview = new HelmUpgradeInstallRequest
{
    ReleaseName = releaseName,
    Namespace = targetNamespace,
    Chart = approvedChartPath,
    ValuesFiles = approvedValuesFiles,
    CreateNamespace = true,
    Wait = true,
    TimeoutSeconds = 300,
    DryRun = true
};

var previewResult = await client.UpgradeInstallAsync(preview, cancellationToken);
if (!previewResult.Succeeded)
    return Results.BadRequest(previewResult.StandardError);

await approvals.SaveAsync(preview, previewResult.StandardOutput, cancellationToken);
```

When the approval service has verified that record, rebuild the request from the persisted fields and apply it:

```csharp
var apply = await approvals.CreateApprovedRequestAsync(approvalId, cancellationToken);
apply.DryRun = false;

var result = await client.UpgradeInstallAsync(apply, cancellationToken);
if (!result.Succeeded)
    return Results.Problem(result.StandardError, statusCode: 409);

return Results.Ok(new { result.StandardOutput });
```

Do not trust a browser to send a second copy of the values or chart version. The service that applies must read the reviewed record itself. Keep `CommandResult.StandardError`, exit code, and operation ID on failure; do not turn Kubernetes, hook, or template diagnostics into a generic HTTP 500.

`Wait`, `WaitForJobs`, `Atomic`, and `TimeoutSeconds` determine the operation's completion contract. See [Install and upgrade releases](../guide/release-workflows.md) before selecting them.
