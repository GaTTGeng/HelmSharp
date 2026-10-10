using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using HelmSharp.Chart;
using HelmSharp.Engine;
using HelmSharp.Kube;
using HelmSharp.Release;
using HelmSharp.Repo;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace HelmSharp.Action;

/// <summary>
/// Managed Helm-compatible client. It renders charts and applies Kubernetes resources without invoking helm.
/// Release state is stored as Kubernetes secrets via <see cref="HelmReleaseStore"/>, mirroring Helm's secret driver.
/// </summary>
public class HelmClient : IHelmClient
{
    private static readonly string ProductVersion =
        typeof(HelmClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly IHelmOptionsProvider _optionsProvider;
    private readonly Func<HelmExecutionOptions, string?, string?, CancellationToken, Task<k8s.Kubernetes>> _createKubernetesClientAsync;
    private readonly Func<HelmRepositoryOptions?, HelmChartRepository> _createChartRepository;

    /// <summary>
    /// Creates a client using default Kubernetes and chart-repository factories.
    /// </summary>
    /// <param name="optionsProvider">Provides per-operation <see cref="HelmExecutionOptions"/> (timeouts, namespace, kubeconfig).</param>
    public HelmClient(IHelmOptionsProvider optionsProvider)
        : this(optionsProvider, CreateKubernetesClientAsync, static options => options is null
            ? new HelmChartRepository()
            : new HelmChartRepository(options))
    {
    }

    internal HelmClient(
        IHelmOptionsProvider optionsProvider,
        Func<HelmExecutionOptions, string?, string?, CancellationToken, Task<k8s.Kubernetes>> createKubernetesClientAsync)
        : this(optionsProvider, createKubernetesClientAsync, static options => options is null
            ? new HelmChartRepository()
            : new HelmChartRepository(options))
    {
    }

    internal HelmClient(
        IHelmOptionsProvider optionsProvider,
        Func<HelmExecutionOptions, string?, string?, CancellationToken, Task<k8s.Kubernetes>> createKubernetesClientAsync,
        Func<HelmRepositoryOptions?, HelmChartRepository> createChartRepository)
    {
        ArgumentNullException.ThrowIfNull(optionsProvider);
        ArgumentNullException.ThrowIfNull(createKubernetesClientAsync);
        ArgumentNullException.ThrowIfNull(createChartRepository);
        _optionsProvider = optionsProvider;
        _createKubernetesClientAsync = createKubernetesClientAsync;
        _createChartRepository = createChartRepository;
    }

    /// <summary>Reports the HelmSharp product version (no cluster access).</summary>
    public Task<CommandResult> VersionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Ok($"HelmSharp {ProductVersion}"));

    /// <summary>
    /// Lists stored release records, like <c>helm list</c>. Only exact
    /// <c>key=value</c> comma-separated label selectors are supported.
    /// </summary>
    /// <param name="namespace">Namespace to list. Ignored when <paramref name="allNamespaces"/> is true.</param>
    /// <param name="allNamespaces">List releases across all namespaces.</param>
    /// <param name="selector">Label selector, e.g. <c>app=web,tier=frontend</c>. Set and set-not operators are rejected.</param>
    /// <param name="limit">Maximum number of releases to return; null or zero returns all.</param>
    /// <returns>JSON array of release records ordered as stored.</returns>
    public async Task<CommandResult> ListReleasesAsync(
        string? @namespace = null,
        bool allNamespaces = false,
        string? selector = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        using var client = await _createKubernetesClientAsync(options, null, null, cancellationToken);
        var store = new HelmReleaseStore(client);
        var releases = await store.ListAsync(@namespace ?? options.DefaultNamespace, allNamespaces, cancellationToken);

        // Filter by label selector
        if (!string.IsNullOrWhiteSpace(selector))
        {
            if (!TryParseExactLabelSelector(selector, out var selectorParts))
            {
                return Fail(
                    $"unsupported label selector: {selector}. Only comma-separated exact key=value matches are supported.");
            }

            releases = releases.Where(r =>
            {
                if (r.Labels is null) return false;
                return selectorParts.All(kv => r.Labels.TryGetValue(kv.Key, out var v) && v == kv.Value);
            }).ToList();
        }

        // Apply limit
        if (limit.HasValue && limit.Value > 0)
            releases = releases.Take(limit.Value).ToList();

        return Ok(JsonSerializer.Serialize(releases, JsonDefaults));
    }

    /// <summary>
    /// Generates a release name from a name template (e.g., "%RELEASE-NAME%-mychart"), equivalent
    /// to <c>helm install --name-template</c>. Only the <c>%RELEASE-NAME%</c> placeholder is expanded.
    /// When no template is given, produces <c>{chart-stem}-{unix-seconds}</c> with the stem truncated to 20 characters.
    /// </summary>
    public static string GenerateReleaseName(string chartName, string? nameTemplate = null)
    {
        if (!string.IsNullOrWhiteSpace(nameTemplate))
        {
            // Helm's name template supports Go template functions; only the
            // %RELEASE-NAME% placeholder is expanded here.
            return nameTemplate.Replace("%RELEASE-NAME%", chartName, StringComparison.OrdinalIgnoreCase);
        }

        // Default: chart-name + timestamp
        var baseName = Path.GetFileNameWithoutExtension(chartName);
        if (baseName.Length > 20)
            baseName = baseName[..20];
        return $"{baseName}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    // Helm list selectors support set-based operators (!=, in, !key). Only exact
    // equality terms are accepted so filtering stays deterministic in the managed store.
    private static bool TryParseExactLabelSelector(
        string selector,
        out Dictionary<string, string> result)
    {
        result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in selector.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0 || trimmed.IndexOf('=', eqIndex + 1) >= 0)
            {
                return false;
            }

            var key = trimmed[..eqIndex].Trim();
            var value = trimmed[(eqIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key) || key.Contains('!') || !result.TryAdd(key, value))
                return false;
        }

        return result.Count > 0;
    }

    /// <summary>
    /// Installs or upgrades a release (equivalent to <c>helm install</c> / <c>helm upgrade</c>).
    /// Output is buffered from <see cref="UpgradeInstallStreamAsync"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The request fails validation (missing names, conflicting flags).</exception>
    /// <exception cref="NotSupportedException">The request uses an option the managed lifecycle does not implement.</exception>
    public async Task<CommandResult> UpgradeInstallAsync(
        HelmUpgradeInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        await foreach (var line in UpgradeInstallStreamAsync(request, cancellationToken))
        {
            output.AppendLine(line);
        }

        return Ok(output.ToString());
    }

    /// <summary>
    /// Streams progress lines for an install/upgrade: chart load, CRD install, pre-hooks,
    /// apply, post-hooks, readiness wait, and release persistence. Failure recovery lines
    /// (atomic restore / cleanup-on-fail) are streamed before the original error is thrown.
    /// Applies the whole operation under a single timeout that also covers hooks and waiting.
    /// </summary>
    public async IAsyncEnumerable<string> UpgradeInstallStreamAsync(
        HelmUpgradeInstallRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // --- 1. Validate request and pin down the operation scope ---
        // A single timeout covers hooks, apply, and readiness wait, matching Helm's
        // --timeout semantics for the whole lifecycle operation.
        ValidateUpgradeRequest(request);
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        ValidateServerSideApplyOption(options);
        var timeout = request.TimeoutSeconds ?? options.TimeoutSeconds;
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var operationToken = operationSource.Token;
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";

        // --- 2. Load chart and collect the user-supplied values overrides ---
        yield return $"Loading chart {request.Chart}";
        var chartPath = await ResolveChartPathAsync(request.Chart, request.Version, options, operationToken);
        var chart = await LoadChartAsync(chartPath, operationToken);

        // Validate kubeVersion compatibility
        if (!string.IsNullOrWhiteSpace(chart.KubeVersion) && !string.IsNullOrWhiteSpace(options.KubeVersion))
        {
            var (compatible, message) = KubeVersionValidator.Validate(chart.KubeVersion, options.KubeVersion);
            if (!compatible)
            {
                yield return $"[WARNING] {message}";
                throw new InvalidOperationException(message);
            }
        }

        var valuesFiles = CombineValuesFiles(request.ValuesFile, request.ValuesFiles);
        var providedOverrides = await HelmValues.BuildOverridesAsync(valuesFiles, request.ValuesContent, request.SetValues, request.SetFileValues, request.SetStringValues, request.SetJsonValues, operationToken);

        // --- 3. Dry-run early exit ---
        // Renders the manifest and returns before any cluster access or release-store
        // writes; DryRunIsUpgrade/DryRunRevision let callers preview an upgrade render.
        if (request.DryRun)
        {
            var dryRunValues = HelmValues.BuildFromOverrides(chart, providedOverrides);
            var dryRunRenderer = new HelmTemplateRenderer(
                chart,
                request.ReleaseName,
                ns,
                dryRunValues,
                options.KubeVersion,
                options.ApiVersions,
                request.DryRunIsUpgrade,
                request.DryRunRevision);
            var dryRunManifest = dryRunRenderer.Render();
            if (!string.IsNullOrWhiteSpace(dryRunManifest))
                yield return dryRunManifest.TrimEnd();
            yield return $"Release {request.ReleaseName} dry run complete";
            yield break;
        }

        // --- 4. Load release history and guard against concurrent operations ---
        using var client = await _createKubernetesClientAsync(options, request.KubeConfigPath, request.KubeConfigContent, operationToken);
        var store = new HelmReleaseStore(client);
        var existingHistory = await LoadReleaseHistoryForUpgradeInstallAsync(
            store,
            request.ReleaseName,
            ns,
            request.CreateNamespace || !request.Install,
            operationToken);
        if (HasActivePendingOperation(existingHistory))
            throw new InvalidOperationException($"another operation is in progress for release {request.ReleaseName}");

        // --- 5. Resolve install-vs-upgrade state and render the manifest ---
        // isUpgrade drives .Release.IsUpgrade and which pre/post hook events fire;
        // revision is the number this operation will store if it succeeds.
        var (isUpgrade, revision) = ResolveReleaseRenderState(existingHistory);
        if (!isUpgrade && !request.Install)
            throw new InvalidOperationException($"release: not found: {request.ReleaseName}");

        var overrides = ResolveUpgradeOverrides(existingHistory, isUpgrade, request.ReuseValues, providedOverrides);
        var values = HelmValues.BuildFromOverrides(chart, overrides);
        var renderer = new HelmTemplateRenderer(
            chart,
            request.ReleaseName,
            ns,
            values,
            options.KubeVersion,
            options.ApiVersions,
            isUpgrade,
            revision);
        var manifest = renderer.Render();

        // --- 6. Namespace and CRDs, before any release resources are applied ---
        if (request.CreateNamespace)
        {
            await KubernetesManifestApplier.EnsureNamespaceAsync(client, ns, operationToken);
            yield return $"Namespace {ns} is ready";
        }

        // Pre-install CRDs from the chart's crds/ directory
        if (!request.SkipCRDs && chart.Crds.Count > 0)
        {
            yield return $"Installing {chart.Crds.Count} CRDs...";
            var crdApplier = new KubernetesManifestApplier(client, options.FieldManager);
            foreach (var crd in chart.Crds)
            {
                var crdYaml = HelmYaml.Serialize(crd);
                var crdResults = new List<string>();
                var crdError = (string?)null;
                try
                {
                    await foreach (var resource in crdApplier.ApplyAsync(crdYaml, ns, operationToken))
                    {
                        crdResults.Add($"  CRD applied: {resource}");
                    }
                }
                catch (Exception ex)
                {
                    // CRD failures are downgraded to warnings: a partially-managed CRD set
                    // must not abort the release. The main apply below is the hard failure point.
                    crdError = ex.Message;
                }
                foreach (var line in crdResults) yield return line;
                if (crdError is not null) yield return $"  CRD warning: {crdError}";
            }
        }

        // --- 7. Build the release record for this attempt ---
        // Status is optimistically "deployed"; any later failure rewrites it via
        // PersistFailedLifecycleAsync before the original error is rethrown.
        // Extract hooks from manifest
        var (mainManifest, hooks) = HelmHookExecutor.ExtractHooks(manifest, ns);
        var attemptedAt = DateTimeOffset.UtcNow;
        var firstDeployedAt = existingHistory.Count == 0
            ? attemptedAt
            : existingHistory.Min(record => record.FirstDeployedAt ?? record.UpdatedAt);
        var releaseRecord = new HelmReleaseRecord
        {
            Name = request.ReleaseName,
            Namespace = ns,
            Revision = revision,
            Status = "deployed",
            ChartName = chart.Name,
            ChartVersion = chart.Version,
            AppVersion = chart.AppVersion,
            ChartApiVersion = chart.ApiVersion,
            ChartDescription = chart.Description,
            ChartType = chart.Type,
            ChartKubeVersion = chart.KubeVersion,
            ChartValuesYaml = chart.ValuesYaml,
            RawChartJson = HelmV3ReleaseCodec.CreateChartSnapshot(chart),
            Manifest = mainManifest,
            ValuesYaml = HelmValues.ToYaml(overrides),
            ComputedValuesYaml = HelmValues.ToYaml(values),
            FirstDeployedAt = firstDeployedAt,
            UpdatedAt = attemptedAt,
            Description = request.Description ?? (isUpgrade ? "Upgrade complete" : "Install complete"),
            Notes = renderer.RenderNotes(),
            Hooks = hooks.Select(ToReleaseHook).ToList(),
            Labels = ResolveReleaseLabels(existingHistory, isUpgrade, request.Labels)
        };

        // --- 8. Pre-hooks: run before any resource is applied ---
        if (!request.DisableHooks && hooks.Count > 0)
        {
            var hookExecutor = new HelmHookExecutor(client, options.FieldManager, timeout);
            var preEvent = isUpgrade ? HelmHookEvent.PreUpgrade : HelmHookEvent.PreInstall;
            await foreach (var hookLine in StreamWithFailureHandlingAsync(
                               hookExecutor.ExecuteHooksWithFailureHandlingAsync(hooks, preEvent, ns, operationToken),
                               error => PersistFailedLifecycleAsync(store, WithHookExecution(releaseRecord, hooks), error, null, mainManifest, existingHistory, isUpgrade, request, ns)))
            {
                yield return hookLine;
            }
        }

        // --- 9. Apply the main manifest ---
        var applier = new KubernetesManifestApplier(client, options.FieldManager);
        var applied = 0;
        var appliedResources = new List<string>();
        Exception? applyError = null;
        try
        {
            await foreach (var resource in applier.ApplyAsync(mainManifest, ns, operationToken))
            {
                applied++;
                appliedResources.Add($"Applied {resource}");
            }
        }
        catch (Exception ex)
        {
            applyError = ex;
        }

        // Emit progress lines before recovery output so the failure reads chronologically.
        foreach (var line in appliedResources)
            yield return line;

        if (applyError is not null)
        {
            // Failure-recovery branch: persist the failed record, run atomic/cleanup-on-fail
            // recovery, stream its lines, then rethrow the original apply error.
            var recovery = await PersistFailedLifecycleAsync(store, WithHookExecution(releaseRecord, hooks), applyError, applier, mainManifest, existingHistory, isUpgrade, request, ns);
            foreach (var line in recovery)
                yield return line;
            throw applyError;
        }

        // --- 10. Post-hooks: run only after a successful apply ---
        if (!request.DisableHooks && hooks.Count > 0)
        {
            var hookExecutor = new HelmHookExecutor(client, options.FieldManager, timeout);
            var postEvent = isUpgrade ? HelmHookEvent.PostUpgrade : HelmHookEvent.PostInstall;
            await foreach (var hookLine in StreamWithFailureHandlingAsync(
                               hookExecutor.ExecuteHooksWithFailureHandlingAsync(hooks, postEvent, ns, operationToken),
                               error => PersistFailedLifecycleAsync(store, WithHookExecution(releaseRecord, hooks), error, applier, mainManifest, existingHistory, isUpgrade, request, ns)))
            {
                yield return hookLine;
            }
        }

        // --- 11. Wait for resources to become ready ---
        // The wait is driven by a hand-rolled enumerator (not await foreach) so a wait
        // failure can run recovery and stream its lines before the error is rethrown.
        if ((request.Wait || request.Atomic) && !request.DryRun)
        {
            yield return $"Waiting for resources to be ready (timeout: {timeout}s)...";
            var waiter = new KubernetesResourceWaiter(client, timeout);
            await using var waitEnumerator = waiter
                .WaitForReadyAsync(mainManifest, ns, waitForJobs: request.WaitForJobs, cancellationToken: operationToken)
                .GetAsyncEnumerator(operationToken);
            while (true)
            {
                string? waitLine = null;
                Exception? waitError = null;
                var hasNext = false;
                try
                {
                    hasNext = await waitEnumerator.MoveNextAsync();
                    if (hasNext)
                        waitLine = waitEnumerator.Current;
                }
                catch (Exception ex)
                {
                    waitError = ex;
                }

                if (waitLine is not null)
                    yield return waitLine;
                if (waitError is not null)
                {
                    // Failure-recovery branch, same as apply: atomic restores the previous
                    // revision, cleanup-on-fail deletes what this attempt introduced.
                    var recovery = await PersistFailedLifecycleAsync(store, WithHookExecution(releaseRecord, hooks), waitError, applier, mainManifest, existingHistory, isUpgrade, request, ns);
                    foreach (var line in recovery)
                        yield return line;
                    throw waitError;
                }
                if (!hasNext)
                    break;
            }
        }

        // --- 12. Persist the completed revision ---
        List<string>? saveRecovery = null;
        Exception? saveError = null;
        try
        {
            var completedAt = DateTimeOffset.UtcNow;
            releaseRecord = releaseRecord with
            {
                UpdatedAt = completedAt,
                FirstDeployedAt = existingHistory.Count == 0 ? completedAt : firstDeployedAt,
                Hooks = hooks.Select(ToReleaseHook).ToList()
            };
            await store.SaveAsync(releaseRecord, operationToken);
        }
        catch (Exception ex)
        {
            // Resources are already live even though the record could not be stored.
            // Recovery still runs so atomic/cleanup-on-fail semantics hold here too.
            saveRecovery = await PersistFailedLifecycleAsync(store, WithHookExecution(releaseRecord, hooks), ex, applier, mainManifest, existingHistory, isUpgrade, request, ns);
            saveError = ex;
        }
        if (saveRecovery is not null)
            foreach (var line in saveRecovery)
                yield return line;
        if (saveError is not null)
            throw saveError;

        // --- 13. Supersede prior revisions and prune history ---
        // Final-save vs operation-timeout: everything below runs under CancellationToken.None.
        // Once the new revision is durable, preserve the single-active-revision invariant
        // even if the caller's operation timeout expires during finalization.
        await SupersedeDeployedReleasesAsync(store, existingHistory, CancellationToken.None);

        // Enforce max history
        var maxHistory = request.MaxHistory ?? options.MaxHistory;
        if (maxHistory > 0)
        {
            await PruneOldReleasesAsync(store, request.ReleaseName, ns, maxHistory, CancellationToken.None);
        }

        yield return $"Release {request.ReleaseName} revision {revision} deployed ({applied} resources)";
    }

    /// <summary>
    /// Computes the labels stored on the new revision: upgrades inherit the latest revision's
    /// labels, then requested labels override them. Returns null when the result is empty.
    /// </summary>
    internal static Dictionary<string, string>? ResolveReleaseLabels(
        IReadOnlyCollection<HelmReleaseRecord> history,
        bool isUpgrade,
        IDictionary<string, string>? requestedLabels)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (isUpgrade)
        {
            var inherited = history.MaxBy(record => record.Revision)?.Labels;
            if (inherited is not null)
            {
                foreach (var (key, value) in inherited)
                    labels[key] = value;
            }
        }

        if (requestedLabels is not null)
        {
            foreach (var (key, value) in requestedLabels)
                labels[key] = value;
        }

        return labels.Count == 0 ? null : labels;
    }

    /// <summary>
    /// Determines whether the operation is an upgrade and which revision number to render/store.
    /// An "uninstalled" latest record restarts at revision 1 as an install (Helm parity).
    /// </summary>
    internal static (bool IsUpgrade, int Revision) ResolveReleaseRenderState(
        IReadOnlyCollection<HelmReleaseRecord> history)
    {
        if (history.Count == 0)
            return (false, 1);

        var latest = history.MaxBy(record => record.Revision)!;
        // Helm treats reinstall-after-uninstall as a fresh install with revision 1.
        var isUpgrade = !string.Equals(latest.Status, "uninstalled", StringComparison.OrdinalIgnoreCase);
        var revision = latest.Revision + 1;
        return (isUpgrade, revision);
    }

    // Guards the single-active-operation invariant: a pending-* record means another
    // lifecycle is mid-flight and must not be interleaved with a new one.
    private static bool HasActivePendingOperation(IReadOnlyCollection<HelmReleaseRecord> history)
    {
        var latest = history.MaxBy(record => record.Revision);
        return latest?.Status.StartsWith("pending-", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Resolves the user-supplied values overrides for an upgrade. With reuse-values, the last
    /// deployed revision's stored user values are used as the base and the provided overrides are
    /// merged on top; otherwise the provided overrides are used as-is.
    /// </summary>
    /// <exception cref="InvalidOperationException">Reuse-values was requested without an existing release.</exception>
    internal static Dictionary<string, object?> ResolveUpgradeOverrides(
        IReadOnlyCollection<HelmReleaseRecord> history,
        bool isUpgrade,
        bool reuseValues,
        Dictionary<string, object?> providedOverrides)
    {
        if (!reuseValues)
            return providedOverrides;

        if (!isUpgrade)
            throw new InvalidOperationException("ReuseValues requires an existing release.");

        var latest = history
            .Where(record => string.Equals(record.Status, "deployed", StringComparison.OrdinalIgnoreCase))
            .MaxBy(record => record.Revision)
            ?? history.MaxBy(record => record.Revision);
        if (latest is null)
            throw new InvalidOperationException("ReuseValues requires an existing release.");

        var result = HelmYaml.DeserializeDictionary(latest.ValuesYaml);
        MergeValues(result, providedOverrides);
        return result;
    }

    // Recursive map merge matching Helm's values coalescing: nested maps are merged key by
    // key, while scalars and lists from the source replace the target wholesale.
    private static void MergeValues(
        Dictionary<string, object?> target,
        IReadOnlyDictionary<string, object?> source)
    {
        foreach (var (key, value) in source)
        {
            if (target.TryGetValue(key, out var existing) &&
                existing is Dictionary<string, object?> targetMap &&
                value is Dictionary<string, object?> sourceMap)
            {
                MergeValues(targetMap, sourceMap);
                continue;
            }

            target[key] = value;
        }
    }

    // Marks every previously deployed revision as superseded so history retains exactly one
    // deployed revision (Helm's single-active-revision invariant).
    private static async Task SupersedeDeployedReleasesAsync(
        HelmReleaseStore store,
        IEnumerable<HelmReleaseRecord> history,
        CancellationToken cancellationToken)
    {
        foreach (var record in history.Where(record =>
                     string.Equals(record.Status, "deployed", StringComparison.OrdinalIgnoreCase)))
        {
            await store.MarkStatusAsync(record, "superseded", cancellationToken);
        }
    }

    // Relays a streamed operation while intercepting failures so the recover callback can
    // persist lifecycle evidence (failed record, cleanup, restore) before rethrowing.
    private static async IAsyncEnumerable<string> StreamWithFailureHandlingAsync(
        IAsyncEnumerable<string> lines,
        Func<Exception, Task<List<string>>> recover)
    {
        await using var enumerator = lines.GetAsyncEnumerator();
        while (true)
        {
            string? line = null;
            Exception? error = null;
            var hasNext = false;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
                if (hasNext)
                    line = enumerator.Current;
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (line is not null)
                yield return line;
            if (error is not null)
            {
                var recovery = await recover(error);
                foreach (var recoveryLine in recovery)
                    yield return recoveryLine;
                throw error;
            }
            if (!hasNext)
                yield break;
        }
    }

    /// <summary>
    /// Records a failed install/upgrade and performs request-driven recovery: with atomic or
    /// cleanup-on-fail, deletes resources introduced by the attempted revision (or the full
    /// manifest for a fresh install) and, for atomic upgrades, re-applies the previous deployed
    /// manifest. Recovery storage writes ignore the caller's cancellation so the failure evidence
    /// remains inspectable. Returns human-readable recovery lines for the caller's output stream.
    /// </summary>
    private static async Task<List<string>> PersistFailedLifecycleAsync(
        HelmReleaseStore store,
        HelmReleaseRecord attemptedRecord,
        Exception error,
        KubernetesManifestApplier? applier,
        string mainManifest,
        IReadOnlyCollection<HelmReleaseRecord> history,
        bool isUpgrade,
        HelmUpgradeInstallRequest request,
        string ns)
    {
        var output = new List<string>();
        var failedRecord = attemptedRecord with
        {
            Status = "failed",
            UpdatedAt = DateTimeOffset.UtcNow,
            Description = $"{(isUpgrade ? "Upgrade" : "Install")} failed: {error.Message}"
        };

        try
        {
            // Do not use the caller's cancellation token here: a cancellation is itself
            // lifecycle evidence that must remain inspectable.
            await store.SaveAsync(failedRecord, CancellationToken.None);
        }
        catch
        {
            // Preserve the operation error; storage failures cannot safely replace it.
        }

        // Nothing was applied (e.g. a pre-hook failure), so there is nothing to clean up.
        if (applier is null)
            return output;

        // Recovery decision tree (only when atomic or cleanup-on-fail is requested):
        //   upgrade with a deployed predecessor -> delete attempted-only resources, and
        //     with atomic also re-apply the previous deployed manifest;
        //   upgrade with history but no deployed predecessor -> delete the full manifest
        //     (retry after a failed first install; everything belongs to failed attempts);
        //   fresh install -> delete the full manifest (nothing pre-existed to preserve).
        if (isUpgrade && (request.Atomic || request.CleanupOnFail))
        {
            var previous = history
                .Where(record => string.Equals(record.Status, "deployed", StringComparison.OrdinalIgnoreCase))
                .MaxBy(record => record.Revision);
            if (previous is not null)
            {
                try
                {
                    var attemptedOnlyManifest = await GetAttemptedOnlyManifestAsync(
                        applier,
                        previous.Manifest,
                        mainManifest,
                        ns,
                        CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(attemptedOnlyManifest))
                    {
                        await foreach (var resource in applier.DeleteAsync(attemptedOnlyManifest, ns, cancellationToken: CancellationToken.None))
                            output.Add($"Removed failed-upgrade resource {resource}");
                    }
                }
                catch
                {
                    output.Add("Unable to fully clean up resources from the failed upgrade.");
                }

                // Restoration is independent of failed-upgrade cleanup. In particular,
                // discovery for an API version removed by the attempted revision may
                // fail before re-applying the previous CRD can make that API available.
                if (request.Atomic)
                {
                    try
                    {
                        await foreach (var resource in applier.ApplyAsync(previous.Manifest, ns, CancellationToken.None))
                            output.Add($"Restored {resource}");
                    }
                    catch
                    {
                        output.Add("Unable to fully restore the previous deployed revision.");
                    }
                }
            }
            else
            {
                // A retry after a failed initial install has a revision history but no
                // deployed predecessor. Its resources belong solely to failed attempts.
                try
                {
                    await foreach (var resource in applier.DeleteAsync(mainManifest, ns, cancellationToken: CancellationToken.None))
                        output.Add($"Cleaned up {resource}");
                }
                catch
                {
                    output.Add("Unable to fully clean up resources from the failed installation.");
                }
            }
            return output;
        }

        // A full-manifest delete is safe only for a new installation. During an upgrade,
        // it could remove resources owned by the previously deployed revision.
        if (!isUpgrade && (request.Atomic || request.CleanupOnFail))
        {
            try
            {
                await foreach (var resource in applier.DeleteAsync(mainManifest, ns, cancellationToken: CancellationToken.None))
                    output.Add($"Cleaned up {resource}");
            }
            catch
            {
                output.Add("Unable to fully clean up resources from the failed installation.");
            }
        }

        return output;
    }

    /// <summary>
    /// Records a failed rollback: reserves the pending-rollback record if it is not yet
    /// observable, then marks it failed. Storage errors never replace the operation error.
    /// </summary>
    private static async Task PersistFailedRollbackAsync(
        HelmReleaseStore store,
        HelmReleaseRecord rollbackRecord,
        string operationId,
        Exception error)
    {
        try
        {
            // A create response can be canceled before the API server makes its write
            // observable. Retrying the same create-only reservation settles that
            // ambiguity without replacing another operation's revision.
            await store.TryCreateAsync(rollbackRecord, CancellationToken.None, operationId);
            var markedFailed = await store.TryMarkPendingRollbackFailedAsync(
                rollbackRecord,
                operationId,
                $"Rollback failed: {error.Message}",
                CancellationToken.None);
            if (markedFailed)
            {
                await store.SaveAsync(rollbackRecord with
                {
                    Status = "failed",
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Description = $"Rollback failed: {error.Message}"
                }, CancellationToken.None);
            }
        }
        catch
        {
            // Preserve the operation failure when its lifecycle evidence cannot be stored.
        }
    }

    /// <summary>
    /// Returns the documents present in <paramref name="attemptedManifest"/> but absent from
    /// <paramref name="previousManifest"/>, compared by full identity (group/kind/namespace/name).
    /// Used to delete only the resources a failed upgrade introduced.
    /// </summary>
    internal static string GetAttemptedOnlyManifest(string previousManifest, string attemptedManifest, string defaultNamespace)
    {
        var previousIdentities = KubernetesManifestApplier.SplitDocumentsPublic(previousManifest)
            .Select(document => ManifestIdentity.Parse(document, defaultNamespace))
            .Where(identity => identity is not null)
            .Select(identity => ManifestIdentityKey(identity!))
            .ToHashSet(StringComparer.Ordinal);

        var attemptedOnly = KubernetesManifestApplier.SplitDocumentsPublic(attemptedManifest)
            .Where(document =>
            {
                var identity = ManifestIdentity.Parse(document, defaultNamespace);
                return identity is not null && !previousIdentities.Contains(ManifestIdentityKey(identity));
            });

        return string.Join(Environment.NewLine + "---" + Environment.NewLine, attemptedOnly);
    }

    /// <summary>
    /// Scope-aware variant of <see cref="GetAttemptedOnlyManifest"/>. A document counts as
    /// attempted-only when the previous revision has no resource with the same group/kind/name;
    /// when names collide across namespaces, cluster-scoped resources are treated as the same
    /// object while namespaced ones in a different namespace are kept. Scope resolution falls
    /// back to API discovery for CRDs (see <see cref="ResolveResourceScopeAsync"/>).
    /// </summary>
    internal static async Task<string> GetAttemptedOnlyManifestAsync(
        KubernetesManifestApplier applier,
        string previousManifest,
        string attemptedManifest,
        string defaultNamespace,
        CancellationToken cancellationToken)
    {
        var previousDocuments = KubernetesManifestApplier.SplitDocumentsPublic(previousManifest)
            .Select(document => (Document: document, Identity: ManifestIdentity.Parse(document, defaultNamespace)))
            .Where(item => item.Identity is not null)
            .Select(item => (item.Document, Identity: item.Identity!))
            .ToList();
        var scopeCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        // Three-way match per attempted document: no same-type/name candidate means it is
        // new; an exact full-identity hit means it already existed; otherwise the name
        // collides across namespaces and scope decides (see ResolveResourceScopeAsync).
        var attemptedOnly = new List<string>();
        foreach (var document in KubernetesManifestApplier.SplitDocumentsPublic(attemptedManifest))
        {
            var identity = ManifestIdentity.Parse(document, defaultNamespace);
            if (identity is null)
                continue;

            var candidates = previousDocuments
                .Where(item => string.Equals(
                    ManifestResourceKey(item.Identity),
                    ManifestResourceKey(identity),
                    StringComparison.Ordinal))
                .ToList();
            if (candidates.Count == 0)
            {
                attemptedOnly.Add(document);
                continue;
            }

            if (candidates.Any(candidate => string.Equals(
                    ManifestIdentityKey(candidate.Identity),
                    ManifestIdentityKey(identity),
                    StringComparison.Ordinal)))
            {
                continue;
            }

            var namespaced = await ResolveResourceScopeAsync(
                applier,
                candidates.Append((document, identity)),
                defaultNamespace,
                scopeCache,
                cancellationToken);
            if (namespaced is not false)
                attemptedOnly.Add(document);
        }

        return string.Join(Environment.NewLine + "---" + Environment.NewLine, attemptedOnly);
    }

    /// <summary>
    /// Resolves whether a resource type is namespaced: a known typed scope first, otherwise
    /// API discovery against the cluster. Results are cached per group/kind. Returns null when
    /// scope cannot be determined (caller then conservatively keeps the document).
    /// </summary>
    private static async Task<bool?> ResolveResourceScopeAsync(
        KubernetesManifestApplier applier,
        IEnumerable<(string Document, ManifestIdentity Identity)> documents,
        string defaultNamespace,
        Dictionary<string, bool> scopeCache,
        CancellationToken cancellationToken)
    {
        foreach (var (document, identity) in documents)
        {
            var resourceKey = ManifestResourceTypeKey(identity);
            if (scopeCache.TryGetValue(resourceKey, out var cachedScope))
                return cachedScope;

            if (KubernetesManifestApplier.TryGetTypedResourceScope(identity, out var typedScope))
            {
                scopeCache[resourceKey] = typedScope;
                return typedScope;
            }

            try
            {
                var resolved = await applier.ResolveIdentityAsync(document, defaultNamespace, cancellationToken);
                if (resolved is null)
                    continue;

                var namespaced = !string.IsNullOrWhiteSpace(resolved.Namespace);
                scopeCache[resourceKey] = namespaced;
                return namespaced;
            }
            catch (KubernetesResourceOperationException ex)
                when (ex.InnerException is KubernetesApiResourceNotFoundException ||
                      ex.InnerException is HttpOperationException { Response.StatusCode: System.Net.HttpStatusCode.NotFound })
            {
                // A stored revision can reference an API version that its current CRD no longer serves.
                // Resource scope is invariant across versions, so try another document for this type.
            }
        }

        return null;
    }

    /// <summary>
    /// Splits a manifest into deletable documents and resources annotated
    /// <c>helm.sh/resource-policy: keep</c> (returned separately so they are never deleted).
    /// Documents without a parseable identity are kept in the deletable set.
    /// </summary>
    internal static (string Manifest, IReadOnlyList<string> KeptResources) FilterManifestForDeletion(
        string manifest,
        string defaultNamespace)
    {
        var keptResources = new List<string>();
        var deletableDocuments = KubernetesManifestApplier.SplitDocumentsPublic(manifest)
            .Where(document =>
            {
                var identity = ManifestIdentity.Parse(document, defaultNamespace);
                if (identity is null)
                    return true;

                var parsed = HelmYaml.DeserializeDictionary(document);
                if (!parsed.TryGetValue("metadata", out var metadataObject) ||
                    metadataObject is not IDictionary<string, object?> metadata ||
                    !metadata.TryGetValue("annotations", out var annotationsObject) ||
                    annotationsObject is not IDictionary<string, object?> annotations ||
                    !annotations.TryGetValue("helm.sh/resource-policy", out var policy) ||
                    !string.Equals(Convert.ToString(policy), "keep", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                keptResources.Add(identity.DisplayName);
                return false;
            });

        return (
            string.Join(Environment.NewLine + "---" + Environment.NewLine, deletableDocuments),
            keptResources);
    }

    // Full identity: group/kind/namespace/name — distinguishes same-named resources across namespaces.
    private static string ManifestIdentityKey(ManifestIdentity identity)
    {
        return $"{ManifestResourceTypeKey(identity)}/{identity.Namespace}/{identity.Name}";
    }

    // Type + name without namespace — used to find candidates before scope disambiguation.
    private static string ManifestResourceKey(ManifestIdentity identity)
        => $"{ManifestResourceTypeKey(identity)}/{identity.Name}";

    // Group/kind key (empty group for core types). Scope is invariant across apiVersions of
    // the same group/kind, so this is the cache key for scope lookups.
    private static string ManifestResourceTypeKey(ManifestIdentity identity)
    {
        var separator = identity.ApiVersion.IndexOf('/');
        var apiGroup = separator < 0 ? string.Empty : identity.ApiVersion[..separator];
        return $"{apiGroup}/{identity.Kind}";
    }

    // Treats a missing namespace as empty history so a first install into a not-yet-created
    // namespace does not fail before CreateNamespace can create it.
    private static async Task<List<HelmReleaseRecord>> LoadReleaseHistoryForUpgradeInstallAsync(
        HelmReleaseStore store,
        string releaseName,
        string ns,
        bool treatMissingNamespaceAsEmptyHistory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await store.HistoryAsync(releaseName, ns, cancellationToken);
        }
        catch (HttpOperationException ex) when (treatMissingNamespaceAsEmptyHistory && (int)ex.Response.StatusCode == 404)
        {
            return [];
        }
    }

    /// <summary>
    /// Convenience overload that uninstalls with default options (no hooks disabled, no wait).
    /// </summary>
    public async Task<CommandResult> UninstallAsync(
        string releaseName,
        string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        return await UninstallAsync(new HelmUninstallRequest
        {
            ReleaseName = releaseName,
            Namespace = @namespace ?? options.DefaultNamespace,
            KubeConfigPath = options.KubeConfigPath,
            KubeConfigContent = options.KubeConfigContent
        }, cancellationToken);
    }

    /// <summary>
    /// Uninstalls a release (equivalent to <c>helm uninstall</c>): runs pre-delete hooks,
    /// deletes resources from failed revisions and the latest manifest (respecting
    /// <c>helm.sh/resource-policy: keep</c>), optionally waits for deletion, runs post-delete
    /// hooks, then purges history unless <c>KeepHistory</c> is set. A second uninstall of an
    /// already uninstalled release purges the remaining history.
    /// </summary>
    public async Task<CommandResult> UninstallAsync(
        HelmUninstallRequest request,
        CancellationToken cancellationToken = default)
    {
        // --- 1. Validate request and pin down the operation scope ---
        if (string.IsNullOrWhiteSpace(request.ReleaseName))
            return Fail("release name is required");
        if (!Enum.IsDefined(request.DeletionPropagation))
            return Fail($"unsupported Kubernetes deletion propagation value: {(int)request.DeletionPropagation}");

        using var timeoutSource = request.TimeoutSeconds is > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds.Value))
            : null;
        using var operationSource = timeoutSource is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var operationToken = operationSource?.Token ?? cancellationToken;

        var options = await _optionsProvider.GetHelmAsync(operationToken);
        ValidateServerSideApplyOption(options);
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(options, request.KubeConfigPath, request.KubeConfigContent, operationToken);
        var store = new HelmReleaseStore(client);

        // --- 2. Load release history and guard against concurrent operations ---
        var latest = await store.GetLatestAsync(request.ReleaseName, ns, operationToken);
        var history = await store.HistoryAsync(request.ReleaseName, ns, operationToken);
        if (HasActivePendingOperation(history))
            return Fail($"another operation is in progress for release {request.ReleaseName}");

        // --- 3. Second-uninstall purge path ---
        // When KeepHistory was used on the first uninstall, the records remain with status
        // "uninstalled" and no live revision. A follow-up uninstall without KeepHistory is
        // how those leftovers get purged; this is the only path that deletes them.
        if (latest is null && !request.KeepHistory)
        {
            if (history is { Count: > 0 } && string.Equals(history[^1].Status, "uninstalled", StringComparison.OrdinalIgnoreCase))
            {
                await store.PurgeAsync(request.ReleaseName, ns, operationToken);
                return Ok($"release \"{request.ReleaseName}\" uninstalled{Environment.NewLine}");
            }
        }
        if (latest is null)
            return Fail($"release: not found: {request.ReleaseName}");

        var (mainManifest, hooks) = ResolveStoredManifest(latest, ns);
        var hookTimeout = request.TimeoutSeconds is > 0 ? request.TimeoutSeconds.Value : options.TimeoutSeconds;
        var hookExecutor = new HelmHookExecutor(client, options.FieldManager, hookTimeout);

        // --- 4. Pre-delete hooks: run before any resource is removed ---
        if (!request.DisableHooks && hooks.Any(h => h.Events.Contains(HelmHookEvent.PreDelete)))
        {
            try
            {
                await foreach (var _ in hookExecutor.ExecuteHooksAsync(hooks, HelmHookEvent.PreDelete, ns, operationToken))
                {
                    // drain
                }
            }
            finally
            {
                await PersistHookExecutionAsync(store, latest, hooks);
            }
        }

        // --- 5. Delete resources ---
        // Order matters: resources introduced only by failed revisions are cleaned up first,
        // then the latest deployed manifest. Both passes honor helm.sh/resource-policy: keep.
        var applier = new KubernetesManifestApplier(client, options.FieldManager);
        var output = new StringBuilder();
        var deletedManifests = new StringBuilder();
        foreach (var failedRevision in history.Where(record =>
                     string.Equals(record.Status, "failed", StringComparison.OrdinalIgnoreCase)))
        {
            // Only documents the failed revision introduced relative to the current manifest;
            // shared resources are left for the main deletion pass below.
            var failedOnlyManifest = await GetAttemptedOnlyManifestAsync(
                applier,
                mainManifest,
                failedRevision.Manifest,
                ns,
                operationToken);
            var failedDeletion = FilterManifestForDeletion(failedOnlyManifest, ns);
            foreach (var keptResource in failedDeletion.KeptResources)
                output.AppendLine($"Kept {keptResource} (helm.sh/resource-policy: keep)");
            await foreach (var resource in applier.DeleteAsync(
                               failedDeletion.Manifest,
                               ns,
                               propagationPolicy: request.DeletionPropagation.ToString(),
                               cancellationToken: operationToken))
            {
                output.AppendLine($"Deleted {resource}");
            }
            AppendManifestDocuments(deletedManifests, failedDeletion.Manifest);
        }
        var mainDeletion = FilterManifestForDeletion(mainManifest, ns);
        foreach (var keptResource in mainDeletion.KeptResources)
            output.AppendLine($"Kept {keptResource} (helm.sh/resource-policy: keep)");
        await foreach (var resource in applier.DeleteAsync(
                           mainDeletion.Manifest,
                           ns,
                           propagationPolicy: request.DeletionPropagation.ToString(),
                           cancellationToken: operationToken))
        {
            output.AppendLine($"Deleted {resource}");
        }
        AppendManifestDocuments(deletedManifests, mainDeletion.Manifest);

        // --- 6. Optionally wait until deletions are observed gone ---
        if (request.Wait)
        {
            var timeout = request.TimeoutSeconds ?? options.TimeoutSeconds;
            var waiter = new KubernetesResourceWaiter(client, timeout);
            await foreach (var line in waiter.WaitForDeletedAsync(deletedManifests.ToString(), ns, operationToken))
                output.AppendLine(line);
        }

        // --- 7. Post-delete hooks: run only after resources are removed ---
        if (!request.DisableHooks && hooks.Any(h => h.Events.Contains(HelmHookEvent.PostDelete)))
        {
            try
            {
                await foreach (var _ in hookExecutor.ExecuteHooksAsync(hooks, HelmHookEvent.PostDelete, ns, operationToken))
                {
                    // drain
                }
            }
            finally
            {
                await PersistHookExecutionAsync(store, latest, hooks);
            }
        }

        // --- 8. Persist the final state ---
        // KeepHistory marks the revision "uninstalled" (a later install restarts at revision 1);
        // otherwise the entire history is purged and the release name is free again.
        if (request.KeepHistory)
            await store.MarkUninstalledAsync(WithHookExecution(latest, hooks), operationToken);
        else
            await store.PurgeAsync(request.ReleaseName, ns, operationToken);
        output.AppendLine($"release \"{request.ReleaseName}\" uninstalled");
        return Ok(output.ToString());
    }

    // Joins manifests into a single multi-document stream with --- separators for waiter input.
    private static void AppendManifestDocuments(StringBuilder builder, string manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest))
            return;

        if (builder.Length > 0)
            builder.AppendLine("---");

        builder.AppendLine(manifest.Trim());
    }

    /// <summary>Gets the durable status of the latest stored revision of a release.</summary>
    public async Task<CommandResult> StatusAsync(
        string releaseName,
        string? @namespace = null,
        CancellationToken cancellationToken = default)
        => await StatusRevisionAsync(releaseName, revision: 0, @namespace, cancellationToken);

    /// <summary>Gets the durable status for a release revision. A revision of zero selects the latest stored revision.</summary>
    public async Task<CommandResult> StatusRevisionAsync(
        string releaseName,
        int revision,
        string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = @namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(options, null, null, cancellationToken);
        var store = new HelmReleaseStore(client);
        var lookup = await FindReleaseRecordAsync(store, releaseName, ns, revision, cancellationToken);
        if (lookup.Error is not null)
            return Fail(lookup.Error);

        var record = lookup.Record!;

        var statusInfo = new
        {
            name = record.Name,
            @namespace = record.Namespace,
            revision = record.Revision,
            status = record.Status,
            chart = $"{record.ChartName}-{record.ChartVersion}",
            app_version = record.AppVersion,
            updated = record.UpdatedAt.ToString("o"),
            description = record.Description,
            notes = GetStoredNotes(record)
        };
        return Ok(JsonSerializer.Serialize(statusInfo, JsonDefaults));
    }

    /// <summary>
    /// Rolls a release back to <paramref name="revision"/> without waiting for readiness.
    /// A revision of zero selects the previous non-uninstalled revision.
    /// </summary>
    public async Task<CommandResult> RollbackAsync(
        string releaseName,
        int revision,
        string? @namespace = null,
        CancellationToken cancellationToken = default)
        => await RollbackAsync(new HelmRollbackRequest
        {
            ReleaseName = releaseName,
            Revision = revision,
            Namespace = @namespace,
            Wait = false
        }, cancellationToken);

    /// <inheritdoc />
    public async Task<CommandResult> RollbackAsync(
        HelmRollbackRequest request,
        CancellationToken cancellationToken = default)
    {
        // --- 1. Validate request and pin down the operation scope ---
        ValidateRollbackRequest(request);
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        ValidateServerSideApplyOption(options);
        var timeout = request.TimeoutSeconds ?? options.TimeoutSeconds;
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var operationToken = operationSource.Token;
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(
            options,
            request.KubeConfigPath,
            request.KubeConfigContent,
            operationToken);
        var store = new HelmReleaseStore(client);

        // --- 2. Load release history and guard against concurrent operations ---
        var current = await store.GetLatestAsync(request.ReleaseName, ns, operationToken);
        if (current is null)
            return Fail($"release: not found: {request.ReleaseName}");

        var storedHistory = await store.HistoryAsync(request.ReleaseName, ns, operationToken);
        if (HasActivePendingOperation(storedHistory))
            return Fail($"another operation is in progress for release {request.ReleaseName}");

        // --- 3. Select the target revision ---
        // Explicit revision > 0 picks that exact record; revision 0 means "previous", the
        // highest revision below the current one (skipping uninstalled records).
        var targetRecord = request.Revision > 0
            ? storedHistory.FirstOrDefault(x => x.Revision == request.Revision)
            : storedHistory
                .Where(x => x.Status != "uninstalled" && x.Revision < current.Revision)
                .OrderByDescending(x => x.Revision)
                .FirstOrDefault();

        if (targetRecord is null)
            return Fail($"release has no revision {request.Revision}");

        // --- 4. Build and reserve the pending-rollback record ---
        // The rollback is stored as a NEW revision (next number) carrying the target's
        // chart/manifest; reserving it up front marks the operation in-flight so a
        // concurrent lifecycle is rejected by the pending-op guard above.
        var (mainManifest, hooks) = ResolveStoredManifest(targetRecord, ns);
        var (currentMainManifest, _) = ResolveStoredManifest(current, ns);
        var hookExecutor = new HelmHookExecutor(client, options.FieldManager, timeout);
        var newRevision = await store.NextRevisionAsync(request.ReleaseName, ns, operationToken);
        var rollbackRecord = new HelmReleaseRecord
        {
            Name = request.ReleaseName,
            Namespace = ns,
            Revision = newRevision,
            Status = "pending-rollback",
            ChartName = targetRecord.ChartName,
            ChartVersion = targetRecord.ChartVersion,
            AppVersion = targetRecord.AppVersion,
            ChartApiVersion = targetRecord.ChartApiVersion,
            ChartDescription = targetRecord.ChartDescription,
            ChartType = targetRecord.ChartType,
            ChartKubeVersion = targetRecord.ChartKubeVersion,
            ChartValuesYaml = targetRecord.ChartValuesYaml,
            RawChartJson = targetRecord.RawChartJson,
            Manifest = mainManifest,
            ValuesYaml = targetRecord.ValuesYaml,
            ComputedValuesYaml = targetRecord.ComputedValuesYaml,
            FirstDeployedAt = targetRecord.FirstDeployedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Description = request.Description ?? "Rollback complete",
            Notes = targetRecord.Notes,
            Hooks = hooks.Select(ToReleaseHook).ToList(),
            Labels = ResolveReleaseLabels([targetRecord], true, request.Labels)
        };

        var operationId = Guid.NewGuid().ToString("N");
        bool reserved;
        try
        {
            reserved = await store.TryCreateAsync(rollbackRecord, operationToken, operationId);
        }
        catch (Exception ex)
        {
            await PersistFailedRollbackAsync(store, WithHookExecution(rollbackRecord, hooks), operationId, ex);
            throw;
        }
        if (!reserved)
            return Fail($"release revision {newRevision} already exists for {request.ReleaseName}");

        var output = new StringBuilder();

        try
        {
            // --- 5. Pre-rollback hooks ---
            if (!request.DisableHooks && hooks.Any(h => h.Events.Contains(HelmHookEvent.PreRollback)))
            {
                await foreach (var hookLine in hookExecutor.ExecuteHooksWithFailureHandlingAsync(hooks, HelmHookEvent.PreRollback, ns, operationToken))
                {
                    output.AppendLine(hookLine);
                }
            }

            // --- 6. Re-apply the target manifest, then remove current-only resources ---
            // Apply first so shared resources transition in place; afterwards delete what the
            // current revision added on top of the target (rollback-only delta), so nothing
            // from the rolled-away revision is left running. Keep-annotated resources survive.
            var applier = new KubernetesManifestApplier(client, options.FieldManager);
            var rollbackOnlyManifest = await GetAttemptedOnlyManifestAsync(
                applier,
                mainManifest,
                currentMainManifest,
                ns,
                operationToken);
            var rollbackDeletion = FilterManifestForDeletion(rollbackOnlyManifest, ns);

            await foreach (var resource in applier.ApplyAsync(mainManifest, ns, operationToken))
            {
                output.AppendLine($"Rolled back {resource}");
            }

            foreach (var keptResource in rollbackDeletion.KeptResources)
                output.AppendLine($"Kept rollback resource {keptResource} (helm.sh/resource-policy: keep)");
            await foreach (var resource in applier.DeleteAsync(
                               rollbackDeletion.Manifest,
                               ns,
                               propagationPolicy: "Background",
                               cancellationToken: operationToken))
            {
                output.AppendLine($"Removed rollback resource {resource}");
            }

            // --- 7. Post-rollback hooks ---
            if (!request.DisableHooks && hooks.Any(h => h.Events.Contains(HelmHookEvent.PostRollback)))
            {
                await foreach (var hookLine in hookExecutor.ExecuteHooksWithFailureHandlingAsync(hooks, HelmHookEvent.PostRollback, ns, operationToken))
                {
                    output.AppendLine(hookLine);
                }
            }

            // --- 8. Optionally wait for the restored resources to become ready ---
            if (request.Wait)
            {
                output.AppendLine($"Waiting for resources to be ready (timeout: {timeout}s)...");
                var waiter = new KubernetesResourceWaiter(client, timeout);
                await foreach (var line in waiter.WaitForReadyAsync(mainManifest, ns, request.WaitForJobs, operationToken))
                    output.AppendLine(line);
            }
        }
        catch (Exception ex)
        {
            // Any failure above marks the pending-rollback record failed before rethrowing.
            await PersistFailedRollbackAsync(store, WithHookExecution(rollbackRecord, hooks), operationId, ex);
            throw;
        }

        try
        {
            // --- 9. Persist the new deployed revision ---
            // The manifest has been applied. Complete the durable release-state transition
            // independently of the operation deadline so history cannot retain two deployed
            // revisions when the timeout expires during this final save.
            await store.SaveAsync(rollbackRecord with
            {
                Status = "deployed",
                UpdatedAt = DateTimeOffset.UtcNow,
                Hooks = hooks.Select(ToReleaseHook).ToList()
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await PersistFailedRollbackAsync(store, WithHookExecution(rollbackRecord, hooks), operationId, ex);
            throw;
        }
        // --- 10. Supersede prior revisions and prune history ---
        var history = await store.HistoryAsync(request.ReleaseName, ns, CancellationToken.None);
        await SupersedeDeployedReleasesAsync(store, history.Where(record => record.Revision != newRevision), CancellationToken.None);
        var maxHistory = request.MaxHistory ?? options.MaxHistory;
        if (maxHistory > 0)
            await PruneOldReleasesAsync(store, request.ReleaseName, ns, maxHistory, CancellationToken.None);

        output.AppendLine($"Rollback to revision {targetRecord.Revision} was successful.");
        return Ok(output.ToString());
    }

    /// <summary>
    /// Renders chart templates without applying anything (equivalent to <c>helm template</c>).
    /// With <c>OutputDir</c>, writes one YAML file per manifest document
    /// (<c>{kind}-{name}.yaml</c>); with <c>UseReleaseName</c>, nests under a release-named subdirectory.
    /// </summary>
    public async Task<CommandResult> TemplateAsync(
        HelmTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        // Load chart, merge values, render — no cluster access and no release-store writes.
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";
        var chartPath = await ResolveChartPathAsync(request.Chart, null, options, cancellationToken);
        var chart = await LoadChartAsync(chartPath, cancellationToken);
        var valuesFiles = CombineValuesFiles(request.ValuesFile, request.ValuesFiles);
        var values = await HelmValues.BuildAsync(chart, valuesFiles, request.ValuesContent, request.SetValues, request.SetFileValues, request.SetStringValues, request.SetJsonValues, cancellationToken);
        var renderer = new HelmTemplateRenderer(
            chart,
            request.ReleaseName,
            ns,
            values,
            request.KubeVersion,
            request.ApiVersions,
            request.IsUpgrade);
        var manifest = renderer.Render();

        // Output to directory if specified
        if (!string.IsNullOrWhiteSpace(request.OutputDir))
        {
            var outputDir = request.UseReleaseName
                ? Path.Combine(request.OutputDir, request.ReleaseName)
                : request.OutputDir;
            Directory.CreateDirectory(outputDir);

            // One file per manifest document: {kind}-{name}.yaml when the document has a
            // parseable identity, otherwise a positional manifest-{n}.yaml fallback.
            var docs = KubernetesManifestApplier.SplitDocumentsPublic(manifest);
            var fileIndex = 0;
            foreach (var doc in docs)
            {
                var identity = ManifestIdentity.Parse(doc, ns);
                var fileName = identity is not null
                    ? $"{identity.Kind.ToLower()}-{identity.Name}.yaml"
                    : $"manifest-{fileIndex}.yaml";
                var filePath = Path.Combine(outputDir, fileName);
                await File.WriteAllTextAsync(filePath, doc, cancellationToken);
                fileIndex++;
            }
            return Ok($"Templates written to: {outputDir}");
        }

        return Ok(manifest);
    }

    /// <summary>
    /// Renders templates and appends rendered NOTES.txt (like <c>helm template --notes</c>).
    /// Unlike <see cref="TemplateAsync"/>, the chart path is loaded directly without remote resolution.
    /// </summary>
    public async Task<CommandResult> TemplateWithNotesAsync(
        HelmTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";
        var chart = await LoadChartAsync(request.Chart, cancellationToken);
        var valuesFiles = CombineValuesFiles(request.ValuesFile, request.ValuesFiles);
        var values = await HelmValues.BuildAsync(chart, valuesFiles, request.ValuesContent, request.SetValues, request.SetFileValues, request.SetStringValues, request.SetJsonValues, cancellationToken);
        var renderer = new HelmTemplateRenderer(
            chart,
            request.ReleaseName,
            ns,
            values,
            request.KubeVersion,
            request.ApiVersions,
            request.IsUpgrade);
        var manifest = renderer.Render();
        var notes = renderer.RenderNotes();
        return Ok(manifest + "\n---\n# NOTES.txt:\n" + notes);
    }

    /// <summary>Lists stored revision history for a release as JSON (like <c>helm history</c>).</summary>
    public async Task<CommandResult> HistoryAsync(
        string releaseName,
        string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        using var client = await _createKubernetesClientAsync(options, null, null, cancellationToken);
        var store = new HelmReleaseStore(client);
        var history = await store.HistoryAsync(releaseName, @namespace ?? options.DefaultNamespace ?? "default", cancellationToken);
        return history.Count == 0
            ? Fail($"release: not found: {releaseName}")
            : Ok(JsonSerializer.Serialize(history, JsonDefaults));
    }

    /// <summary>
    /// Gets values stored for the latest release revision (like <c>helm get values</c>).
    /// With <paramref name="allValues"/>, returns computed values (chart defaults merged with
    /// user overrides) instead of user-supplied values only.
    /// </summary>
    public async Task<CommandResult> GetValuesAsync(
        string releaseName,
        string? @namespace = null,
        bool allValues = false,
        CancellationToken cancellationToken = default)
        => await GetValuesRevisionAsync(releaseName, revision: 0, @namespace, allValues, cancellationToken);

    /// <summary>Gets values stored for a release revision. A revision of zero selects the latest stored revision.</summary>
    public async Task<CommandResult> GetValuesRevisionAsync(
        string releaseName,
        int revision,
        string? @namespace = null,
        bool allValues = false,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = @namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(options, null, null, cancellationToken);
        var store = new HelmReleaseStore(client);
        var lookup = await FindReleaseRecordAsync(store, releaseName, ns, revision, cancellationToken);
        return lookup.Error is not null
            ? Fail(lookup.Error)
            : Ok(GetStoredValuesYaml(lookup.Record!, allValues));
    }

    /// <summary>
    /// Gets the stored manifest for a release revision (like <c>helm get manifest</c>).
    /// A revision of zero selects the latest stored revision.
    /// </summary>
    public async Task<CommandResult> GetManifestAsync(
        string releaseName,
        string? @namespace = null,
        int revision = 0,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = @namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(options, null, null, cancellationToken);
        var store = new HelmReleaseStore(client);

        var lookup = await FindReleaseRecordAsync(store, releaseName, ns, revision, cancellationToken);
        return lookup.Error is not null
            ? Fail(lookup.Error)
            : Ok(lookup.Record!.Manifest);
    }

    /// <summary>
    /// Gets the rendered NOTES.txt stored on a release revision (like <c>helm get notes</c>).
    /// A revision of zero selects the latest stored revision; empty notes yield a placeholder message.
    /// </summary>
    public async Task<CommandResult> GetNotesAsync(
        string releaseName,
        string? @namespace = null,
        int revision = 0,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = @namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(options, null, null, cancellationToken);
        var store = new HelmReleaseStore(client);

        var lookup = await FindReleaseRecordAsync(store, releaseName, ns, revision, cancellationToken);
        return lookup.Error is not null
            ? Fail(lookup.Error)
            : Ok(GetStoredNotes(lookup.Record!));
    }

    /// <summary>
    /// Returns user-supplied values, or computed values (chart defaults merged with user
    /// overrides) when <paramref name="allValues"/> is true. Older records without a stored
    /// computed snapshot are recomputed on demand.
    /// </summary>
    internal static string GetStoredValuesYaml(HelmReleaseRecord record, bool allValues)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!allValues)
            return record.ValuesYaml;

        if (!string.IsNullOrWhiteSpace(record.ComputedValuesYaml))
            return record.ComputedValuesYaml;

        var values = HelmYaml.DeserializeDictionary(record.ChartValuesYaml);
        HelmValues.MergeInto(values, HelmYaml.DeserializeDictionary(record.ValuesYaml));
        return HelmValues.ToYaml(values);
    }

    /// <summary>Returns the stored NOTES.txt, or a placeholder message when none were rendered.</summary>
    internal static string GetStoredNotes(HelmReleaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.IsNullOrWhiteSpace(record.Notes)
            ? "No notes found for this release."
            : record.Notes;
    }

    /// <summary>
    /// Looks up a release record by revision (zero selects the highest revision) and converts
    /// lookup failures into Helm-style error messages.
    /// </summary>
    private static async Task<ReleaseRecordLookup> FindReleaseRecordAsync(
        HelmReleaseStore store,
        string releaseName,
        string namespaceName,
        int revision,
        CancellationToken cancellationToken)
    {
        if (revision < 0)
        {
            return new ReleaseRecordLookup(
                null,
                $"release: revision must be zero or a positive integer: {revision}");
        }

        var history = await store.HistoryAsync(releaseName, namespaceName, cancellationToken);
        if (history.Count == 0)
            return new ReleaseRecordLookup(null, $"release: not found: {releaseName}");

        var record = revision == 0
            ? history.MaxBy(candidate => candidate.Revision)
            : history.FirstOrDefault(candidate => candidate.Revision == revision);

        return record is null
            ? new ReleaseRecordLookup(null, $"release: revision {revision} not found: {releaseName}")
            : new ReleaseRecordLookup(record, null);
    }

    private sealed record ReleaseRecordLookup(HelmReleaseRecord? Record, string? Error);

    /// <summary>
    /// Lists hook resources stored on a release revision (like <c>helm get hooks</c>),
    /// including events, weight, delete policies, and last-run metadata.
    /// </summary>
    public async Task<CommandResult> GetHooksAsync(
        string releaseName,
        string? @namespace = null,
        int revision = 0,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = @namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(options, null, null, cancellationToken);
        var store = new HelmReleaseStore(client);

        var lookup = await FindReleaseRecordAsync(store, releaseName, ns, revision, cancellationToken);
        if (lookup.Error is not null)
            return Fail(lookup.Error);

        var record = lookup.Record!;

        var (_, hooks) = ResolveStoredManifest(record, ns);
        if (hooks.Count == 0)
            return Ok("No hooks found for this release.");

        var output = new StringBuilder();
        foreach (var hook in hooks)
        {
            output.AppendLine($"---");
            output.AppendLine($"# Hook: {hook.Name}");
            output.AppendLine($"# Events: {string.Join(", ", hook.Events)}");
            output.AppendLine($"# Weight: {hook.Weight}");
            output.AppendLine($"# Delete Policies: {string.Join(", ", hook.DeletePolicies)}");
            output.AppendLine($"# Last Run Phase: {hook.LastRunPhase ?? "Unknown"}");
            if (hook.LastRunStartedAt is not null)
                output.AppendLine($"# Last Run Started: {hook.LastRunStartedAt:O}");
            if (hook.LastRunCompletedAt is not null)
                output.AppendLine($"# Last Run Completed: {hook.LastRunCompletedAt:O}");
            output.AppendLine(hook.Manifest);
        }
        return Ok(output.ToString());
    }

    /// <summary>
    /// Returns release metadata, manifest, and user values in one output (like <c>helm get all</c>).
    /// </summary>
    public async Task<CommandResult> GetAllAsync(
        string releaseName,
        string? @namespace = null,
        int revision = 0,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = @namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(options, null, null, cancellationToken);
        var store = new HelmReleaseStore(client);

        var lookup = await FindReleaseRecordAsync(store, releaseName, ns, revision, cancellationToken);
        if (lookup.Error is not null)
            return Fail(lookup.Error);

        var record = lookup.Record!;

        var output = new StringBuilder();
        output.AppendLine($"NAME: {record.Name}");
        output.AppendLine($"NAMESPACE: {record.Namespace}");
        output.AppendLine($"REVISION: {record.Revision}");
        output.AppendLine($"STATUS: {record.Status}");
        output.AppendLine($"CHART: {record.ChartName}-{record.ChartVersion}");
        output.AppendLine($"APP VERSION: {record.AppVersion ?? "N/A"}");
        output.AppendLine($"UPDATED: {record.UpdatedAt:yyyy-MM-dd HH:mm:ss K}");
        output.AppendLine();
        output.AppendLine("MANIFEST:");
        output.AppendLine(record.Manifest);
        output.AppendLine();
        output.AppendLine("VALUES:");
        output.AppendLine(record.ValuesYaml);

        return Ok(output.ToString());
    }

    /// <summary>
    /// Runs test hooks for a release (like <c>helm test</c>). Hooks run in weight order, then
    /// name/kind/path for determinism. A single failing hook fails the command; the remaining
    /// hooks still run unless the operation timeout expires. Caller cancellation propagates,
    /// while timeout is reported as a failed hook. <paramref name="showLogs"/> is accepted for
    /// Helm CLI compatibility but hook logs are not yet streamed.
    /// </summary>
    /// <returns>Success when every test hook passed; failure output otherwise.</returns>
    public async Task<CommandResult> TestAsync(
        string releaseName,
        string? @namespace = null,
        int? timeoutSeconds = null,
        bool showLogs = false,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        ValidateServerSideApplyOption(options);
        var ns = @namespace ?? options.DefaultNamespace ?? "default";
        var timeout = timeoutSeconds ?? options.TimeoutSeconds;
        using var timeoutSource = timeout > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeout))
            : null;
        using var operationSource = timeoutSource is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var operationToken = operationSource?.Token ?? cancellationToken;
        using var client = await _createKubernetesClientAsync(options, null, null, operationToken);
        var store = new HelmReleaseStore(client);

        var latest = await store.GetLatestAsync(releaseName, ns, operationToken);
        if (latest is null)
            return Fail($"release: not found: {releaseName}");

        // Test hooks are ordered by weight first (Helm semantics), then name/kind/path so
        // equal-weight hooks still run in a deterministic order across invocations.
        var (_, hooks) = ResolveStoredManifest(latest, ns);
        var testHooks = hooks
            .Where(h => h.Events.Contains(HelmHookEvent.Test))
            .OrderBy(h => h.Weight)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .ThenBy(h => h.Kind, StringComparer.Ordinal)
            .ThenBy(h => h.Path, StringComparer.Ordinal)
            .ToList();

        if (testHooks.Count == 0)
            return Ok($"No test hooks found for release {releaseName}");

        // Run hooks one at a time: a failing hook is recorded but does not stop the rest,
        // except that an operation timeout ends the run immediately (nothing left to wait for).
        var output = new StringBuilder();
        output.AppendLine($"TESTING: {releaseName}");
        var hookExecutor = new HelmHookExecutor(client, options.FieldManager, timeout);
        var passed = 0;
        var failed = 0;

        try
        {
            foreach (var hook in testHooks)
            {
                try
                {
                    await foreach (var line in hookExecutor.ExecuteHooksAsync(
                        new List<HelmHook> { hook }, HelmHookEvent.Test, ns, operationToken))
                    {
                        output.AppendLine(line);
                    }
                    passed++;
                    output.AppendLine($"PASSED: {hook.Name}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Caller cancellation propagates as-is; it is not a test failure.
                    throw;
                }
                catch (OperationCanceledException ex) when (timeoutSource?.IsCancellationRequested == true)
                {
                    // Operation timeout is reported as a failed hook and stops the run.
                    failed++;
                    output.AppendLine($"FAILED: {hook.Name}: {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    output.AppendLine($"FAILED: {hook.Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            await PersistHookExecutionAsync(store, latest, hooks);
        }

        output.AppendLine();
        output.AppendLine($"TEST RESULTS: {passed} passed, {failed} failed, {testHooks.Count} total");

        return failed > 0
            ? Fail(output.ToString())
            : Ok(output.ToString());
    }

    /// <summary>
    /// Prints the current deployed manifest and the manifest the given request would produce,
    /// without applying changes. This is a side-by-side manifest listing, not a unified diff.
    /// </summary>
    public async Task<CommandResult> DiffAsync(
        string releaseName,
        HelmUpgradeInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(options, request.KubeConfigPath, request.KubeConfigContent, cancellationToken);
        var store = new HelmReleaseStore(client);

        // --- 1. Current side: the latest deployed revision's stored manifest ---
        // Only "deployed" records count; a failed/pending latest must not be shown as current.
        var history = await store.HistoryAsync(releaseName, ns, cancellationToken);
        var currentManifest = history
            .Where(record => string.Equals(record.Status, "deployed", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.Revision)
            .Select(record => record.Manifest)
            .FirstOrDefault() ?? string.Empty;

        // --- 2. New side: render with the same install/upgrade render-state rules ---
        var chart = await LoadChartAsync(request.Chart, cancellationToken);
        var valuesFiles = CombineValuesFiles(request.ValuesFile, request.ValuesFiles);
        var values = await HelmValues.BuildAsync(chart, valuesFiles, request.ValuesContent, request.SetValues, request.SetFileValues, request.SetStringValues, request.SetJsonValues, cancellationToken);
        var newManifest = RenderDiffManifest(chart, releaseName, ns, values, options, history);

        var output = new StringBuilder();
        output.AppendLine("=== Current Manifest ===");
        output.AppendLine(currentManifest);
        output.AppendLine("=== New Manifest ===");
        output.AppendLine(newManifest);
        return Ok(output.ToString());
    }

    /// <summary>
    /// Renders the prospective manifest for a diff using the same upgrade/install render state
    /// rules (<c>.Release.IsUpgrade</c> and next revision number) as a real lifecycle operation.
    /// </summary>
    internal static string RenderDiffManifest(
        HelmChart chart,
        string releaseName,
        string releaseNamespace,
        Dictionary<string, object?> values,
        HelmExecutionOptions options,
        IReadOnlyCollection<HelmReleaseRecord> history)
    {
        var (isUpgrade, revision) = ResolveReleaseRenderState(history);
        var renderer = new HelmTemplateRenderer(
            chart,
            releaseName,
            releaseNamespace,
            values,
            options.KubeVersion,
            options.ApiVersions,
            isUpgrade,
            revision);
        return renderer.Render();
    }

    /// <summary>
    /// Checks a chart for structural problems (like <c>helm lint</c>): required Chart.yaml
    /// fields, template renderability, and unclosed template expressions. Renders under the
    /// fixed identity "lint-test" in namespace "default". Errors fail the command; warnings do not.
    /// </summary>
    public async Task<CommandResult> LintAsync(
        string chartPath,
        string? valuesContent = null,
        Dictionary<string, string>? setValues = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var errors = new List<string>();

        try
        {
            var chart = await LoadChartAsync(chartPath, cancellationToken);

            // Validate Chart.yaml
            if (string.IsNullOrWhiteSpace(chart.Name))
                errors.Add("Chart.yaml: name is required");
            if (string.IsNullOrWhiteSpace(chart.Version))
                errors.Add("Chart.yaml: version is required");

            // Validate templates render
            if (chart.Templates.Count == 0)
                warnings.Add("No templates found in chart");

            var values = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, valuesContent, setValues, null, null, null, cancellationToken);
            var renderer = new HelmTemplateRenderer(chart, "lint-test", "default", values);

            try
            {
                var manifest = renderer.Render();
                if (string.IsNullOrWhiteSpace(manifest))
                    warnings.Add("Chart renders to empty manifest");
            }
            catch (Exception ex)
            {
                errors.Add($"Template rendering failed: {ex.Message}");
            }

            // Check for common issues
            foreach (var (path, content) in chart.Templates)
            {
                if (content.Contains("{{", StringComparison.Ordinal) && !content.Contains("}}", StringComparison.Ordinal))
                    warnings.Add($"{path}: unclosed template expression");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to load chart: {ex.Message}");
        }

        var output = new StringBuilder();
        if (warnings.Count > 0)
        {
            output.AppendLine("[WARNING]");
            foreach (var w in warnings)
                output.AppendLine($"  {w}");
        }
        if (errors.Count > 0)
        {
            output.AppendLine("[ERROR]");
            foreach (var e in errors)
                output.AppendLine($"  {e}");
        }
        if (warnings.Count == 0 && errors.Count == 0)
            output.AppendLine("Lint OK: no issues found");

        return errors.Count > 0 ? Fail(output.ToString()) : Ok(output.ToString());
    }

    /// <summary>
    /// Renders a chart's manifest (like <c>helm show manifest</c>) under the fixed identity
    /// "show". The chart reference may be a local path or a remote/OCI reference to pull.
    /// </summary>
    public async Task<CommandResult> ShowManifestAsync(
        string chartPath,
        string? version = null,
        string? valuesContent = null,
        Dictionary<string, string>? setValues = null,
        CancellationToken cancellationToken = default)
    {
        var chartPathResolved = await ResolveChartPathAsync(chartPath, version,
            await _optionsProvider.GetHelmAsync(cancellationToken), cancellationToken);
        var chart = await LoadChartAsync(chartPathResolved, cancellationToken);
        var values = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, valuesContent, setValues, null, null, null, cancellationToken);
        var renderer = new HelmTemplateRenderer(chart, "show", "default", values);
        return Ok(renderer.Render());
    }

    /// <summary>Prints Chart.yaml metadata as JSON (like <c>helm show chart</c>).</summary>
    public async Task<CommandResult> ShowChartAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
    {
        var chart = await LoadChartAsync(chartPath, cancellationToken);
        var info = new
        {
            name = chart.Name,
            version = chart.Version,
            appVersion = chart.AppVersion,
            description = chart.Description,
            type = chart.Type ?? "application",
            deprecated = chart.Deprecated,
            home = chart.Home,
            sources = chart.Sources,
            keywords = chart.Keywords,
            maintainers = chart.Maintainers,
            dependencies = chart.Dependencies.Select(d => new { d.Name, d.Version, d.Repository, d.Condition, d.Enabled }),
            templates = chart.Templates.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
        };
        return Ok(System.Text.Json.JsonSerializer.Serialize(info, JsonDefaults));
    }

    /// <summary>Prints the chart's default values.yaml (like <c>helm show values</c>).</summary>
    public async Task<CommandResult> ShowValuesAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
    {
        var chart = await LoadChartAsync(chartPath, cancellationToken);
        return Ok(chart.ValuesYaml);
    }

    /// <summary>
    /// Downloads a chart archive from a repository URL or OCI reference to <paramref name="destination"/>
    /// (current directory when null), like <c>helm pull</c>.
    /// </summary>
    public async Task<CommandResult> PullAsync(
        string chartRef,
        string? version = null,
        string? destination = null,
        CancellationToken cancellationToken = default)
        => await PullAsync(
            new HelmPullRequest
            {
                ChartReference = chartRef,
                Version = version,
                Destination = destination
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<CommandResult> PullAsync(
        HelmPullRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var repo = _createChartRepository(null);
        var path = await repo.PullChartAsync(request, cancellationToken);
        return Ok($"Chart pulled to: {path}");
    }

    /// <summary>
    /// Packages a chart directory into a versioned <c>.tgz</c> archive (like <c>helm package</c>).
    /// <paramref name="version"/> and <paramref name="appVersion"/> override Chart.yaml values.
    /// </summary>
    public async Task<CommandResult> PackageAsync(
        string chartPath,
        string? destination = null,
        string? version = null,
        string? appVersion = null,
        CancellationToken cancellationToken = default)
        => await PackageAsync(
            new HelmPackageRequest
            {
                ChartPath = chartPath,
                Destination = destination,
                Version = version,
                AppVersion = appVersion
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<CommandResult> PackageAsync(
        HelmPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (request.DependencyUpdate)
            {
                var dependencyResult = await DependencyUpdateAsync(
                    new HelmDependencyUpdateRequest { ChartPath = request.ChartPath },
                    cancellationToken);
                if (!dependencyResult.Succeeded)
                    return dependencyResult;
            }

            var path = await HelmChartPackager.PackageAsync(
                request.ChartPath,
                request.Destination,
                request.Version,
                request.AppVersion,
                cancellationToken);
            return Ok($"Successfully packaged chart and saved it to: {path}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Scaffolds a new chart from the default starter (or the given starter path),
    /// like <c>helm create</c>.
    /// </summary>
    public async Task<CommandResult> CreateAsync(
        string chartName,
        string? destination = null,
        string? starter = null,
        CancellationToken cancellationToken = default)
    {
        var path = await HelmChartCreator.CreateAsync(chartName, destination, starter, cancellationToken);
        return Ok($"Created chart: {path}");
    }

    /// <summary>Resolves Chart.yaml dependencies and rewrites Chart.lock (like <c>helm dependency update</c>).</summary>
    public async Task<CommandResult> DependencyUpdateAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
        => await DependencyUpdateAsync(
            new HelmDependencyUpdateRequest { ChartPath = chartPath },
            cancellationToken);

    /// <summary>
    /// Resolves each Chart.yaml dependency into <c>charts/</c> and rewrites Chart.lock.
    /// Archives are staged first and only moved into place after every dependency resolves,
    /// so a partial failure never leaves a half-updated charts directory. Locally vendored
    /// dependencies (empty repository) are validated in place and preserved.
    /// </summary>
    /// <inheritdoc />
    public async Task<CommandResult> DependencyUpdateAsync(
        HelmDependencyUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chartPath = Path.GetFullPath(request.ChartPath);
        var chart = await LoadChartAsync(chartPath, cancellationToken);
        if (chart.Dependencies.Count == 0)
            return Ok("No dependencies found in Chart.yaml");

        var chartsDir = Path.Combine(chartPath, "charts");
        var stagingDirectory = Path.Combine(chartPath, $".helmsharp-dependency-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var output = new StringBuilder();
        var resolvedDependencies = new List<HelmResolvedDependency>(chart.Dependencies.Count);
        var stagedArchives = new List<string>(chart.Dependencies.Count);
        var localDependencyNames = chart.Dependencies
            .Where(dependency => string.IsNullOrWhiteSpace(dependency.Repository))
            .Select(dependency => dependency.Name)
            .ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();

        try
        {
            using var repo = request.RepositoryConfigPath is null && request.RepositoryCachePath is null
                ? _createChartRepository(null)
                : _createChartRepository(new HelmRepositoryOptions
                {
                    RepositoryConfigPath = request.RepositoryConfigPath,
                    CacheDirectory = request.RepositoryCachePath
                });
            var configuredRepositories = await repo.ListRepositoriesAsync(cancellationToken);
            var refreshedRepositories = new HashSet<string>(StringComparer.Ordinal);

            // --- Stage: resolve every dependency into staging (all-or-nothing commit below) ---
            foreach (var dependency in chart.Dependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Local/vendored deps (empty repository) are validated in place and never
                    // staged or deleted; only remote deps produce staged archives.
                    if (string.IsNullOrWhiteSpace(dependency.Repository))
                    {
                        var local = await ResolveVendoredDependencyAsync(
                            chartPath,
                            dependency.Name,
                            dependency.Version,
                            exactVersion: false,
                            cancellationToken);
                        output.AppendLine(
                            $"Resolved local dependency: {dependency.Name} ({local.Version}) from charts/{dependency.Name}");
                        resolvedDependencies.Add(new HelmResolvedDependency(
                            dependency.Name,
                            dependency.Version ?? local.Version,
                            string.Empty));
                        continue;
                    }

                    var staged = await HelmDependencySource.StageAsync(
                        repo,
                        configuredRepositories,
                        refreshedRepositories,
                        chartPath,
                        dependency.Name,
                        dependency.Version,
                        dependency.Repository,
                        stagingDirectory,
                        verifyDigest: true,
                        refreshConfiguredRepository: !request.SkipRepositoryRefresh,
                        requireConfiguredCache: request.SkipRepositoryRefresh,
                        exactVersion: false,
                        cancellationToken);
                    output.AppendLine(
                        $"Resolved dependency: {dependency.Name} ({staged.Version}) from {dependency.Repository}");

                    resolvedDependencies.Add(new HelmResolvedDependency(
                        dependency.Name,
                        staged.Version,
                        dependency.Repository));
                    stagedArchives.Add(staged.ArchivePath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"Dependency '{dependency.Name}' failed: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                foreach (var error in errors)
                    output.AppendLine($"Error: {error}");
                return Fail(output.ToString());
            }

            // --- Stage: commit staged archives and rewrite Chart.lock ---
            // Reached only when every dependency resolved; a partial failure above returns
            // before this point so charts/ is never half-updated.
            var requestedDependencies = await HelmDependencyLockFile.LoadRequestedDependenciesAsync(
                chartPath,
                cancellationToken);
            var digest = HelmDependencyLockFile.ComputeDigest(requestedDependencies, resolvedDependencies);
            await InstallStagedDependencyArchivesAsync(
                chartsDir,
                stagedArchives,
                localDependencyNames,
                output,
                cancellationToken);

            var lockChanged = await HelmDependencyLockFile.WriteIfChangedAsync(
                chartPath,
                resolvedDependencies,
                digest,
                cancellationToken);
            output.AppendLine(lockChanged ? "Chart.lock updated." : "Chart.lock is already up to date.");
            return Ok(output.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"Dependency update failed: {ex.Message}{Environment.NewLine}{output}");
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    // SHA-256 content comparison used to avoid rewriting unchanged dependency archives
    // (keeps file timestamps stable for incremental tooling).
    private static async Task<bool> FilesHaveSameDigestAsync(
        string leftPath,
        string rightPath,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(leftPath).Length != new FileInfo(rightPath).Length)
            return false;

        await using var left = File.OpenRead(leftPath);
        await using var right = File.OpenRead(rightPath);
        var leftDigest = await System.Security.Cryptography.SHA256.HashDataAsync(left, cancellationToken);
        var rightDigest = await System.Security.Cryptography.SHA256.HashDataAsync(right, cancellationToken);
        return leftDigest.AsSpan().SequenceEqual(rightDigest);
    }

    /// <summary>
    /// Moves staged dependency archives into <c>charts/</c>, skipping identical existing files,
    /// then deletes archives that no longer correspond to a resolved dependency. Locally vendored
    /// dependencies (charts without a repository) are never deleted here.
    /// </summary>
    private static async Task InstallStagedDependencyArchivesAsync(
        string chartsDirectory,
        IReadOnlyList<string> stagedArchives,
        IReadOnlySet<string> localDependencyNames,
        StringBuilder output,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(chartsDirectory);
        var desiredArchiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stagedArchive in stagedArchives
                     .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.Last()))
        {
            var archiveName = Path.GetFileName(stagedArchive);
            desiredArchiveNames.Add(archiveName);
            var destinationPath = Path.Combine(chartsDirectory, archiveName);
            if (File.Exists(destinationPath) && await FilesHaveSameDigestAsync(
                    stagedArchive,
                    destinationPath,
                    cancellationToken))
            {
                File.Delete(stagedArchive);
            }
            else
            {
                File.Move(stagedArchive, destinationPath, overwrite: true);
            }
            output.AppendLine($"Dependency saved to {destinationPath}");
        }

        foreach (var existingArchive in Directory.EnumerateFiles(
                     chartsDirectory,
                     "*.tgz",
                     SearchOption.TopDirectoryOnly))
        {
            if (!desiredArchiveNames.Contains(Path.GetFileName(existingArchive)))
            {
                var existingChart = await HelmChartLoader.LoadAsync(existingArchive, cancellationToken);
                if (localDependencyNames.Contains(existingChart.Name))
                    continue;

                File.Delete(existingArchive);
                output.AppendLine($"Deleted outdated dependency: {existingArchive}");
            }
        }
    }

    /// <summary>
    /// Pushes a chart package to a remote registry. Currently validates and packages the chart
    /// locally (directories are packaged first); the actual registry upload is not performed yet.
    /// </summary>
    public async Task<CommandResult> PushAsync(
        string chartRef,
        string remote,
        CancellationToken cancellationToken = default)
    {
        var chartPath = chartRef;
        if (File.Exists(chartRef) && chartRef.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            return Ok($"Chart pushed to: {remote}");
        }

        if (Directory.Exists(chartRef))
        {
            var tgzPath = await HelmChartPackager.PackageAsync(chartRef, cancellationToken: cancellationToken);
            return Ok($"Chart packaged and pushed to: {remote}");
        }

        return Fail($"Chart not found: {chartRef}");
    }

    /// <summary>
    /// Adds or updates a named chart repository (like <c>helm repo add</c>). Credentials are
    /// optional and stored with the repository configuration.
    /// </summary>
    public async Task<CommandResult> RepoAddAsync(
        string name,
        string url,
        string? username = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        using var repo = _createChartRepository(null);
        await repo.AddRepositoryAsync(name, url, username, password, cancellationToken);
        return Ok($"Repository \"{name}\" added with URL: {url}");
    }

    /// <summary>Removes a named chart repository (like <c>helm repo remove</c>).</summary>
    public async Task<CommandResult> RepoRemoveAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        using var repo = _createChartRepository(null);
        await repo.RemoveRepositoryAsync(name, cancellationToken);
        return Ok($"Repository \"{name}\" removed.");
    }

    /// <summary>Lists configured chart repositories as JSON (like <c>helm repo list</c>).</summary>
    public async Task<CommandResult> RepoListAsync(
        CancellationToken cancellationToken = default)
    {
        using var repo = _createChartRepository(null);
        var repos = await repo.ListRepositoriesAsync(cancellationToken);
        return Ok(System.Text.Json.JsonSerializer.Serialize(repos, JsonDefaults));
    }

    /// <summary>
    /// Searches chart repositories for a keyword (like <c>helm search repo</c>), optionally
    /// limited to a single repository URL. Results are returned as JSON.
    /// </summary>
    public async Task<CommandResult> SearchRepoAsync(
        string keyword,
        string? repoUrl = null,
        CancellationToken cancellationToken = default)
    {
        using var repo = _createChartRepository(null);
        var results = repoUrl is null
            ? await repo.SearchRepoAsync(keyword, cancellationToken)
            : await repo.SearchRepoAsync(repoUrl, keyword, cancellationToken: cancellationToken);
        return Ok(System.Text.Json.JsonSerializer.Serialize(results, JsonDefaults));
    }

    /// <summary>
    /// Stores registry credentials for a host (like <c>helm registry login</c>). Credentials are
    /// written as plain JSON to <c>~/.helmsharp/registry/config.json</c> (base64 auth is not
    /// encryption) and overwrite any previous entry for the same host.
    /// </summary>
    public Task<CommandResult> RegistryLoginAsync(
        string host,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".helmsharp", "registry");
        Directory.CreateDirectory(configDir);

        var configFile = Path.Combine(configDir, "config.json");
        var config = File.Exists(configFile)
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(configFile))
              ?? new Dictionary<string, object>()
            : new Dictionary<string, object>();

        var credentials = new Dictionary<string, object>
        {
            ["username"] = username,
            ["password"] = password,
            ["auth"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"))
        };

        var auths = config.ContainsKey("auths")
            ? config["auths"] as Dictionary<string, object> ?? new Dictionary<string, object>()
            : new Dictionary<string, object>();

        auths[$"https://{host}"] = credentials;
        config["auths"] = auths;

        File.WriteAllText(configFile, System.Text.Json.JsonSerializer.Serialize(config,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return Task.FromResult(Ok($"Login Succeeded for: https://{host}"));
    }

    /// <summary>
    /// Removes stored registry credentials for a host (like <c>helm registry logout</c>).
    /// Succeeds even when no credentials are stored.
    /// </summary>
    public Task<CommandResult> RegistryLogoutAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".helmsharp", "registry");
        var configFile = Path.Combine(configDir, "config.json");

        if (!File.Exists(configFile))
            return Task.FromResult(Ok($"Not logged in to: https://{host}"));

        var config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(configFile))
                     ?? new Dictionary<string, object>();

        if (config.TryGetValue("auths", out var authsObj) && authsObj is System.Text.Json.JsonElement authsElement)
        {
            var auths = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(authsElement.GetRawText())
                        ?? new Dictionary<string, object>();
            var key = $"https://{host}";
            if (auths.Remove(key))
            {
                config["auths"] = auths;
                File.WriteAllText(configFile, System.Text.Json.JsonSerializer.Serialize(config,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return Task.FromResult(Ok($"Removed login credentials for: https://{host}"));
            }
        }

        return Task.FromResult(Ok($"Not logged in to: https://{host}"));
    }

    /// <summary>
    /// Returns the chart's README.md (searched in templates first, then the chart root).
    /// Missing README is not an error; a placeholder message is returned.
    /// </summary>
    public async Task<CommandResult> ShowReadmeAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
    {
        var chart = await LoadChartAsync(chartPath, cancellationToken);

        foreach (var (path, content) in chart.Templates)
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                return Ok(content);
        }

        // Check for README.md in chart root
        var readmePath = Path.Combine(chartPath, "README.md");
        if (File.Exists(readmePath))
            return Ok(await File.ReadAllTextAsync(readmePath, System.Text.Encoding.UTF8, cancellationToken));

        return Ok("No README found for this chart.");
    }

    /// <summary>Prints the chart's crds/ resources as YAML documents (like <c>helm show crds</c>).</summary>
    public async Task<CommandResult> ShowCrdsAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
    {
        var chart = await LoadChartAsync(chartPath, cancellationToken);
        if (chart.Crds.Count == 0)
            return Ok("No CRDs found in this chart.");

        var output = new StringBuilder();
        foreach (var crd in chart.Crds)
        {
            output.AppendLine("---");
            output.AppendLine(HelmYaml.Serialize(crd));
        }
        return Ok(output.ToString());
    }

    /// <summary>Generates a repository index for <paramref name="dirPath"/> (like <c>helm repo index</c>).</summary>
    public async Task<CommandResult> RepoIndexAsync(
        string dirPath,
        string? url = null,
        CancellationToken cancellationToken = default)
        => await RepoIndexAsync(
            new HelmRepoIndexRequest { DirectoryPath = dirPath, Url = url },
            cancellationToken);

    /// <summary>
    /// Generates a repository index and optionally merges an existing index at
    /// <paramref name="mergeIndexPath"/>.
    /// </summary>
    public async Task<CommandResult> RepoIndexAsync(
        string dirPath,
        string? url,
        CancellationToken cancellationToken,
        string? mergeIndexPath)
        => await RepoIndexAsync(
            new HelmRepoIndexRequest
            {
                DirectoryPath = dirPath,
                Url = url,
                MergeIndexPath = mergeIndexPath
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<CommandResult> RepoIndexAsync(
        HelmRepoIndexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var indexPath = await HelmRepoIndexer.GenerateIndexAsync(request, cancellationToken);
        return Ok($"Index generated at: {indexPath}");
    }

    /// <summary>
    /// Refreshes cached indexes for every configured repository (like <c>helm repo update</c>).
    /// Per-repository failures are reported but do not fail the command.
    /// </summary>
    public async Task<CommandResult> RepoUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        using var repo = _createChartRepository(null);
        var results = await repo.UpdateConfiguredRepositoriesAsync(cancellationToken);
        var output = new StringBuilder();
        foreach (var result in results)
        {
            output.AppendLine(result.Succeeded
                ? $"Successfully updated: {result.Name}"
                : $"Failed to update {result.Name}: {result.Error}");
        }

        var updated = results.Count(result => result.Succeeded);
        var failed = results.Count - updated;
        output.AppendLine($"Update complete. {updated} updated, {failed} failed.");
        return Ok(output.ToString());
    }

    /// <summary>
    /// Searches Artifact Hub for charts (like <c>helm search hub</c>), returning up to 20
    /// matching packages as raw JSON.
    /// </summary>
    public async Task<CommandResult> SearchHubAsync(
        string keyword,
        CancellationToken cancellationToken = default)
    {
        using var http = new System.Net.Http.HttpClient();
        var url = $"https://artifacthub.io/api/v1/packages/search?kind=0&offset=0&limit=20&ts_query={Uri.EscapeDataString(keyword)}";
        try
        {
            var response = await http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return Ok(json);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to search hub: {ex.Message}");
        }
    }

    /// <summary>
    /// Prints Chart.yaml metadata, values.yaml, all templates, CRDs, and rendered NOTES.txt
    /// in one combined listing (like <c>helm show all</c>).
    /// </summary>
    public async Task<CommandResult> ShowAllAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
    {
        var chart = await LoadChartAsync(chartPath, cancellationToken);

        var output = new StringBuilder();

        // Chart metadata
        output.AppendLine("---");
        output.AppendLine("# Chart.yaml");
        output.AppendLine($"apiVersion: v2");
        output.AppendLine($"name: {chart.Name}");
        output.AppendLine($"version: {chart.Version}");
        if (chart.AppVersion is not null) output.AppendLine($"appVersion: {chart.AppVersion}");
        if (chart.Description is not null) output.AppendLine($"description: {chart.Description}");
        if (chart.Type is not null) output.AppendLine($"type: {chart.Type}");
        if (chart.Home is not null) output.AppendLine($"home: {chart.Home}");

        // Values
        output.AppendLine();
        output.AppendLine("---");
        output.AppendLine("# values.yaml");
        output.AppendLine(chart.ValuesYaml);

        // Templates
        output.AppendLine();
        output.AppendLine("---");
        output.AppendLine("# Templates");
        foreach (var (path, content) in chart.Templates.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            output.AppendLine($"# {path}");
            output.AppendLine(content);
        }

        // CRDs
        if (chart.Crds.Count > 0)
        {
            output.AppendLine();
            output.AppendLine("---");
            output.AppendLine("# CRDs");
            foreach (var crd in chart.Crds)
                output.AppendLine(HelmYaml.Serialize(crd));
        }

        // NOTES.txt
        var renderer = new HelmTemplateRenderer(chart, "show-all", "default", new Dictionary<string, object?>());
        var notes = renderer.RenderNotes();
        if (!string.IsNullOrWhiteSpace(notes))
        {
            output.AppendLine();
            output.AppendLine("---");
            output.AppendLine("# NOTES.txt");
            output.AppendLine(notes);
        }

        return Ok(output.ToString());
    }

    /// <summary>
    /// Prints the Helm-style environment variables HelmSharp honors (like <c>helm env</c>).
    /// Values come from the process environment with Helm-compatible defaults.
    /// </summary>
    public Task<CommandResult> EnvAsync(CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        output.AppendLine($"HELM_DRIVER=secret");
        output.AppendLine($"HELM_NAMESPACE={Environment.GetEnvironmentVariable("HELM_NAMESPACE") ?? "default"}");
        output.AppendLine($"HELM_KUBECONFIG={Environment.GetEnvironmentVariable("HELM_KUBECONFIG") ?? "~/.kube/config"}");
        output.AppendLine($"HELM_CONFIG_HOME={Environment.GetEnvironmentVariable("HELM_CONFIG_HOME") ?? "~/.config/helm"}");
        output.AppendLine($"HELM_CACHE_HOME={Environment.GetEnvironmentVariable("HELM_CACHE_HOME") ?? "~/.cache/helm"}");
        output.AppendLine($"HELM_DATA_HOME={Environment.GetEnvironmentVariable("HELM_DATA_HOME") ?? "~/.local/share/helm"}");
        return Task.FromResult(Ok(output.ToString()));
    }

    /// <summary>Rebuilds dependency archives from Chart.lock (like <c>helm dependency build</c>).</summary>
    public async Task<CommandResult> DependencyBuildAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
        => await DependencyBuildAsync(
            new HelmDependencyBuildRequest { ChartPath = chartPath },
            cancellationToken);

    /// <summary>
    /// Rebuilds <c>charts/</c> from the locked versions in Chart.lock, verifying the lock digest
    /// against Chart.yaml first. Fails when Chart.lock is missing or out of sync — run dependency
    /// update first. Exact locked versions are used (no constraint re-resolution), and archive
    /// digests can be verified with <c>VerifyDigests</c>.
    /// </summary>
    /// <inheritdoc />
    public async Task<CommandResult> DependencyBuildAsync(
        HelmDependencyBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chartPath = Path.GetFullPath(request.ChartPath);

        // --- Stage: validate Chart.lock exists and matches Chart.yaml ---
        // Build never re-resolves constraints; an out-of-sync lock must be fixed by
        // dependency update first so Chart.lock stays the single source of truth.
        IReadOnlyList<Dictionary<string, object?>> requestedDependencies;
        HelmDependencyLock? lockFile;
        try
        {
            requestedDependencies = await HelmDependencyLockFile.LoadRequestedDependenciesAsync(
                chartPath,
                cancellationToken);
            if (requestedDependencies.Count == 0)
                return Ok("No dependencies found in Chart.yaml");

            lockFile = await HelmDependencyLockFile.LoadAsync(chartPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"Invalid Chart.lock: {ex.Message}");
        }

        if (lockFile is null)
            return Fail("Chart.lock is missing. Run dependency update before dependency build.");

        string expectedLockDigest;
        try
        {
            expectedLockDigest = HelmDependencyLockFile.ComputeDigest(
                requestedDependencies,
                lockFile.Dependencies);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"Chart.lock is inconsistent with Chart.yaml: {ex.Message}");
        }
        if (!string.Equals(lockFile.Digest, expectedLockDigest, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "Chart.lock is out of sync with Chart.yaml. Run dependency update before dependency build.");
        }

        var chartsDirectory = Path.Combine(chartPath, "charts");
        var stagingDirectory = Path.Combine(chartPath, $".helmsharp-dependency-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        var output = new StringBuilder();
        var stagedArchives = new List<string>(lockFile.Dependencies.Count);
        var localDependencyNames = lockFile.Dependencies
            .Where(dependency => string.IsNullOrWhiteSpace(dependency.Repository))
            .Select(dependency => dependency.Name)
            .ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();

        try
        {
            using var repository = request.RepositoryConfigPath is null && request.RepositoryCachePath is null
                ? _createChartRepository(null)
                : _createChartRepository(new HelmRepositoryOptions
                {
                    RepositoryConfigPath = request.RepositoryConfigPath,
                    CacheDirectory = request.RepositoryCachePath
                });
            var configuredRepositories = await repository.ListRepositoriesAsync(cancellationToken);
            var refreshedRepositories = new HashSet<string>(StringComparer.Ordinal);

            // --- Stage: download exact locked versions into staging ---
            foreach (var dependency in lockFile.Dependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Local/vendored deps are validated in place (exact locked version) and
                    // never restaged; only remote deps are downloaded as archives.
                    if (string.IsNullOrWhiteSpace(dependency.Repository))
                    {
                        await ResolveVendoredDependencyAsync(
                            chartPath,
                            dependency.Name,
                            dependency.Version,
                            exactVersion: false,
                            cancellationToken);
                        output.AppendLine(
                            $"Using local locked dependency: {dependency.Name} ({dependency.Version}) " +
                            $"from charts/{dependency.Name}");
                        continue;
                    }

                    output.AppendLine(
                        $"Downloading locked dependency: {dependency.Name} ({dependency.Version}) " +
                        $"from {dependency.Repository}");
                    var staged = await HelmDependencySource.StageAsync(
                        repository,
                        configuredRepositories,
                        refreshedRepositories,
                        chartPath,
                        dependency.Name,
                        dependency.Version,
                        dependency.Repository,
                        stagingDirectory,
                        request.VerifyDigests,
                        refreshConfiguredRepository: false,
                        requireConfiguredCache: true,
                        exactVersion: true,
                        cancellationToken);
                    var archivePath = staged.ArchivePath;
                    if (!File.Exists(archivePath))
                        throw new InvalidDataException($"Dependency download did not produce an archive: {archivePath}");
                    if (request.VerifyDigests && !string.IsNullOrWhiteSpace(dependency.ArchiveDigest))
                    {
                        await VerifyDependencyArchiveDigestAsync(
                            archivePath,
                            dependency.ArchiveDigest,
                            dependency.Name,
                            cancellationToken);
                    }

                    stagedArchives.Add(archivePath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"Dependency '{dependency.Name}' failed: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                foreach (var error in errors)
                    output.AppendLine($"Error: {error}");
                return Fail(output.ToString());
            }

            // --- Stage: commit staged archives into charts/ (all-or-nothing, as in update) ---
            await InstallStagedDependencyArchivesAsync(
                chartsDirectory,
                stagedArchives,
                localDependencyNames,
                output,
                cancellationToken);
            output.AppendLine("Dependencies rebuilt from Chart.lock.");
            return Ok(output.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"Dependency build failed: {ex.Message}{Environment.NewLine}{output}");
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies a dependency archive against a Chart.lock digest (optional <c>sha256:</c> prefix).
    /// Throws <see cref="InvalidDataException"/> on malformed or mismatched digests.
    /// </summary>
    private static async Task VerifyDependencyArchiveDigestAsync(
        string archivePath,
        string expectedDigest,
        string dependencyName,
        CancellationToken cancellationToken)
    {
        const string sha256Prefix = "sha256:";
        var expectedHash = expectedDigest.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase)
            ? expectedDigest[sha256Prefix.Length..]
            : expectedDigest;
        if (expectedHash.Length != 64 || expectedHash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException(
                $"Dependency '{dependencyName}' has invalid SHA-256 digest '{expectedDigest}' in Chart.lock.");

        await using var archive = File.OpenRead(archivePath);
        var actualHash = Convert.ToHexString(
            await System.Security.Cryptography.SHA256.HashDataAsync(archive, cancellationToken));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Dependency '{dependencyName}' digest mismatch: expected {expectedDigest}, " +
                $"actual sha256:{actualHash.ToLowerInvariant()}.");
        }
    }

    /// <summary>
    /// Loads a locally vendored dependency from <c>charts/{name}</c> and validates its name and
    /// version. <paramref name="exactVersion"/> switches between locked-version equality (build)
    /// and constraint satisfaction (update).
    /// </summary>
    private static async Task<HelmChart> ResolveVendoredDependencyAsync(
        string parentChartPath,
        string dependencyName,
        string? requestedVersion,
        bool exactVersion,
        CancellationToken cancellationToken)
    {
        var dependencyPath = Path.Combine(parentChartPath, "charts", dependencyName);
        if (!Directory.Exists(dependencyPath))
        {
            throw new DirectoryNotFoundException(
                $"Local dependency directory was not found: {dependencyPath}");
        }

        var chart = await HelmChartLoader.LoadAsync(dependencyPath, cancellationToken);
        if (!string.Equals(chart.Name, dependencyName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Local dependency chart '{chart.Name}' does not match dependency '{dependencyName}'.");
        }

        var versionMatches = exactVersion
            ? string.Equals(chart.Version, requestedVersion?.Trim(), StringComparison.Ordinal)
            : HelmChartVersionResolver.Satisfies(chart.Version, requestedVersion);
        if (!versionMatches)
        {
            var expectation = exactVersion
                ? $"locked version '{requestedVersion}'"
                : $"constraint '{requestedVersion}'";
            throw new InvalidDataException(
                $"Local dependency '{dependencyName}' version '{chart.Version}' does not match {expectation}.");
        }

        return chart;
    }

    /// <summary>Lists Chart.yaml dependencies and their status (like <c>helm dependency list</c>).</summary>
    public async Task<CommandResult> DependencyListAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
        => await DependencyListAsync(
            new HelmDependencyListRequest { ChartPath = chartPath },
            cancellationToken);

    /// <summary>
    /// Lists dependencies as a tab-separated NAME/VERSION/REPOSITORY/STATUS table.
    /// Emits a warning instead of a table when the chart has no dependencies.
    /// </summary>
    /// <inheritdoc />
    public async Task<CommandResult> DependencyListAsync(
        HelmDependencyListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chart = await LoadChartAsync(request.ChartPath, cancellationToken);
        if (chart.Dependencies.Count == 0)
            return Ok($"WARNING: no dependencies at {Path.Combine(request.ChartPath, "charts")}{Environment.NewLine}");

        var output = new StringBuilder();
        output.AppendLine("NAME\tVERSION\tREPOSITORY\tSTATUS");

        foreach (var dep in chart.Dependencies)
        {
            var status = await HelmDependencyStatusInspector.InspectAsync(
                request.ChartPath,
                chart,
                dep,
                cancellationToken);
            output.AppendLine($"{dep.Name}\t{dep.Version ?? string.Empty}\t{dep.Repository ?? string.Empty}\t{status}");
        }

        output.AppendLine();
        return Ok(output.ToString());
    }

    /// <summary>
    /// Loads a chart with the decompression budgets configured on
    /// <see cref="HelmExecutionOptions.ArchiveLimits"/>, falling back to
    /// <see cref="HelmChartArchiveLimits.Default"/> so untrusted archives are always bounded.
    /// </summary>
    private async Task<HelmChart> LoadChartAsync(string chartPath, CancellationToken cancellationToken)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        return await HelmChartLoader.LoadAsync(chartPath, options.ArchiveLimits, cancellationToken);
    }

    /// <summary>
    /// Resolves a chart reference to a local path: existing paths pass through, http(s)/oci
    /// references are pulled first. Remote "repo/chart" shorthand is returned unchanged for
    /// downstream resolution.
    /// </summary>
    private static async Task<string> ResolveChartPathAsync(
        string chartRef,
        string? version,
        HelmExecutionOptions options,
        CancellationToken cancellationToken)
    {
        // Local path — return as-is
        if (Directory.Exists(chartRef) || File.Exists(chartRef))
            return chartRef;

        // URL or OCI reference — use repository client
        if (chartRef.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            chartRef.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
        {
            using var repo = new HelmChartRepository(new HelmRepositoryOptions
            {
                ArchiveLimits = options.ArchiveLimits
            });
            return await repo.PullChartAsync(chartRef, version, cancellationToken);
        }

        // repo/chart shorthand without path separators is left for repository resolution
        if (chartRef.Contains('/') && !chartRef.Contains(Path.DirectorySeparatorChar) && !chartRef.Contains('/'))
            return chartRef;

        return chartRef;
    }

    /// <summary>
    /// Validates an upgrade/install request: required fields, mutually exclusive flags
    /// (ReuseValues/ResetValues), dependent flags (WaitForJobs requires Wait or Atomic), and
    /// rejects options the managed lifecycle does not implement.
    /// </summary>
    private static void ValidateUpgradeRequest(HelmUpgradeInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ReleaseName))
            throw new ArgumentException("ReleaseName is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Chart))
            throw new ArgumentException("Chart is required.", nameof(request));
        if (request.TimeoutSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "TimeoutSeconds must be greater than zero.");
        if (request.MaxHistory is < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxHistory cannot be negative.");
        if (request.ReuseValues && request.ResetValues)
            throw new ArgumentException("ReuseValues and ResetValues cannot both be enabled.", nameof(request));
        if (request.WaitForJobs && !request.Wait && !request.Atomic)
            throw new ArgumentException("WaitForJobs requires Wait or Atomic.", nameof(request));
        if (request.ReuseValues && request.DryRun)
            throw new NotSupportedException("ReuseValues is not supported for DryRun because it requires stored release values.");

        var unsupported = new List<string>();
        if (request.Force) unsupported.Add(nameof(request.Force));
        if (request.Devel) unsupported.Add(nameof(request.Devel));
        if (request.GenerateName) unsupported.Add(nameof(request.GenerateName));
        if (!string.IsNullOrWhiteSpace(request.NameTemplate)) unsupported.Add(nameof(request.NameTemplate));
        if (request.TakeOwnership) unsupported.Add(nameof(request.TakeOwnership));
        if (request.RollbackOnFailure) unsupported.Add(nameof(request.RollbackOnFailure));
        if (request.RenderSubchartNotes) unsupported.Add(nameof(request.RenderSubchartNotes));
        if (request.HideSecret) unsupported.Add(nameof(request.HideSecret));
        if (!string.IsNullOrWhiteSpace(request.ServerSideApply)) unsupported.Add(nameof(request.ServerSideApply));
        if (!string.IsNullOrWhiteSpace(request.CaFile)) unsupported.Add(nameof(request.CaFile));
        if (!string.IsNullOrWhiteSpace(request.CertFile)) unsupported.Add(nameof(request.CertFile));
        if (!string.IsNullOrWhiteSpace(request.KeyFile)) unsupported.Add(nameof(request.KeyFile));
        if (request.InsecureSkipTlsVerify) unsupported.Add(nameof(request.InsecureSkipTlsVerify));
        if (!string.IsNullOrWhiteSpace(request.Username)) unsupported.Add(nameof(request.Username));
        if (!string.IsNullOrWhiteSpace(request.Password)) unsupported.Add(nameof(request.Password));
        if (!string.IsNullOrWhiteSpace(request.RepoUrl)) unsupported.Add(nameof(request.RepoUrl));
        if (request.PassCredentials) unsupported.Add(nameof(request.PassCredentials));
        if (request.PlainHttp) unsupported.Add(nameof(request.PlainHttp));
        if (!string.IsNullOrWhiteSpace(request.Keyring)) unsupported.Add(nameof(request.Keyring));
        if (request.Verify) unsupported.Add(nameof(request.Verify));
        if (request.DisableOpenApiValidation) unsupported.Add(nameof(request.DisableOpenApiValidation));
        if (request.SkipSchemaValidation) unsupported.Add(nameof(request.SkipSchemaValidation));
        if (request.EnableDns) unsupported.Add(nameof(request.EnableDns));
        if (request.DependencyUpdate) unsupported.Add(nameof(request.DependencyUpdate));
        if (unsupported.Count > 0)
        {
            throw new NotSupportedException(
                $"The managed lifecycle API does not support: {string.Join(", ", unsupported)}.");
        }
    }

    // The managed lifecycle only performs client-side apply; server-side apply would change
    // field ownership semantics and is rejected rather than silently downgraded.
    private static void ValidateServerSideApplyOption(HelmExecutionOptions options)
    {
        if (options.ServerSideApply)
        {
            throw new NotSupportedException(
                "HelmExecutionOptions.ServerSideApply is not supported by the managed lifecycle API. " +
                "Set it to false before applying resources.");
        }
    }

    /// <summary>
    /// Validates a rollback request: required fields, non-negative revision, positive timeout,
    /// and dependent flags (WaitForJobs requires Wait).
    /// </summary>
    private static void ValidateRollbackRequest(HelmRollbackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ReleaseName))
            throw new ArgumentException("ReleaseName is required.", nameof(request));
        if (request.Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Revision cannot be negative.");
        if (request.TimeoutSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "TimeoutSeconds must be greater than zero.");
        if (request.MaxHistory is < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaxHistory cannot be negative.");
        if (request.WaitForJobs && !request.Wait)
            throw new ArgumentException("WaitForJobs requires Wait.", nameof(request));
    }

    /// <summary>
    /// Splits a stored record into main manifest and hooks. Records written with explicit hook
    /// metadata are used as-is; older records re-extract hooks from the manifest so upgrades stay
    /// compatible with releases stored before hook persistence existed.
    /// </summary>
    internal static (string MainManifest, List<HelmHook> Hooks) ResolveStoredManifest(
        HelmReleaseRecord record,
        string defaultNamespace)
    {
        if (record.Hooks.Count > 0)
            return (record.Manifest, record.Hooks.Select(FromReleaseHook).ToList());

        return HelmHookExecutor.ExtractHooks(record.Manifest, defaultNamespace);
    }

    private static HelmReleaseHookRecord ToReleaseHook(HelmHook hook)
        => new()
        {
            Name = hook.Name,
            Kind = hook.Kind,
            Path = hook.Path,
            Manifest = hook.Manifest,
            Events = hook.Events.Select(ToReleaseHookEvent).ToList(),
            LastRunStartedAt = hook.LastRunStartedAt,
            LastRunCompletedAt = hook.LastRunCompletedAt,
            LastRunPhase = hook.LastRunPhase ?? "Unknown",
            Weight = hook.Weight,
            DeletePolicies = hook.DeletePolicies.Select(ToReleaseHookDeletePolicy).ToList(),
            OutputLogPolicies = hook.OutputLogPolicies.ToList()
        };

    private static HelmHook FromReleaseHook(HelmReleaseHookRecord record)
    {
        var hook = new HelmHook
        {
            Name = record.Name,
            Kind = record.Kind,
            Path = record.Path,
            Manifest = record.Manifest,
            // Namespace is not persisted on the record; recover it from the manifest identity.
            Namespace = ManifestIdentity.Parse(record.Manifest, string.Empty)?.Namespace ?? string.Empty,
            Weight = record.Weight,
            LastRunStartedAt = record.LastRunStartedAt,
            LastRunCompletedAt = record.LastRunCompletedAt,
            LastRunPhase = record.LastRunPhase
        };
        foreach (var value in record.Events)
        {
            if (TryParseReleaseHookEvent(value, out var hookEvent))
                hook.Events.Add(hookEvent);
        }
        foreach (var value in record.DeletePolicies)
        {
            if (TryParseReleaseHookDeletePolicy(value, out var deletePolicy))
                hook.DeletePolicies.Add(deletePolicy);
        }
        hook.OutputLogPolicies.AddRange(record.OutputLogPolicies);
        return hook;
    }

    private static HelmReleaseRecord WithHookExecution(
        HelmReleaseRecord releaseRecord,
        IEnumerable<HelmHook> hooks)
        => releaseRecord with { Hooks = hooks.Select(ToReleaseHook).ToList() };

    // Persists hook run metadata (phase, timestamps) onto the still-current revision record.
    // Uses CancellationToken.None so cancellation of the hook run itself does not lose the
    // last-run evidence. Reloads the record first to avoid clobbering concurrent status changes.
    private static async Task PersistHookExecutionAsync(
        HelmReleaseStore store,
        HelmReleaseRecord originalRecord,
        IEnumerable<HelmHook> hooks)
    {
        var current = (await store.HistoryAsync(
                originalRecord.Name,
                originalRecord.Namespace,
                CancellationToken.None))
            .FirstOrDefault(record => record.Revision == originalRecord.Revision);
        if (current is not null)
            await store.SaveAsync(WithHookExecution(current, hooks), CancellationToken.None);
    }

    private static string ToReleaseHookEvent(HelmHookEvent value)
        => value switch
        {
            HelmHookEvent.PreInstall => "pre-install",
            HelmHookEvent.PostInstall => "post-install",
            HelmHookEvent.PreUpgrade => "pre-upgrade",
            HelmHookEvent.PostUpgrade => "post-upgrade",
            HelmHookEvent.PreDelete => "pre-delete",
            HelmHookEvent.PostDelete => "post-delete",
            HelmHookEvent.PreRollback => "pre-rollback",
            HelmHookEvent.PostRollback => "post-rollback",
            HelmHookEvent.Test => "test",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    private static string ToReleaseHookDeletePolicy(HelmHookDeletePolicy value)
        => value switch
        {
            HelmHookDeletePolicy.BeforeHookCreation => "before-hook-creation",
            HelmHookDeletePolicy.HookSucceeded => "hook-succeeded",
            HelmHookDeletePolicy.HookFailed => "hook-failed",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    private static bool TryParseReleaseHookEvent(string value, out HelmHookEvent result)
    {
        result = value switch
        {
            "pre-install" => HelmHookEvent.PreInstall,
            "post-install" => HelmHookEvent.PostInstall,
            "pre-upgrade" => HelmHookEvent.PreUpgrade,
            "post-upgrade" => HelmHookEvent.PostUpgrade,
            "pre-delete" => HelmHookEvent.PreDelete,
            "post-delete" => HelmHookEvent.PostDelete,
            "pre-rollback" => HelmHookEvent.PreRollback,
            "post-rollback" => HelmHookEvent.PostRollback,
            "test" => HelmHookEvent.Test,
            _ => default
        };
        return value is "pre-install" or "post-install" or "pre-upgrade" or "post-upgrade"
            or "pre-delete" or "post-delete" or "pre-rollback" or "post-rollback" or "test";
    }

    private static bool TryParseReleaseHookDeletePolicy(string value, out HelmHookDeletePolicy result)
    {
        result = value switch
        {
            "before-hook-creation" => HelmHookDeletePolicy.BeforeHookCreation,
            "hook-succeeded" => HelmHookDeletePolicy.HookSucceeded,
            "hook-failed" => HelmHookDeletePolicy.HookFailed,
            _ => default
        };
        return value is "before-hook-creation" or "hook-succeeded" or "hook-failed";
    }

    // Builds a Kubernetes client: request-supplied kubeconfig content/path wins over options,
    // falling back to the default kubeconfig chain (~/.kube/config, in-cluster, etc.).
    private static async Task<k8s.Kubernetes> CreateKubernetesClientAsync(
        HelmExecutionOptions options,
        string? requestKubeConfigPath,
        string? requestKubeConfigContent,
        CancellationToken cancellationToken)
    {
        var kubeConfigContent = requestKubeConfigContent ?? options.KubeConfigContent;
        if (!string.IsNullOrWhiteSpace(kubeConfigContent))
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kubeConfigContent));
            return new k8s.Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(stream));
        }

        var kubeConfigPath = requestKubeConfigPath ?? options.KubeConfigPath;
        if (!string.IsNullOrWhiteSpace(kubeConfigPath))
        {
            await using var stream = File.OpenRead(kubeConfigPath);
            return new k8s.Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(stream));
        }

        return new k8s.Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig());
    }

    /// <summary>
    /// Deletes old release records beyond the max history limit.
    /// </summary>
    private static async Task PruneOldReleasesAsync(
        HelmReleaseStore store,
        string releaseName,
        string ns,
        int maxHistory,
        CancellationToken ct)
    {
        var history = await store.HistoryAsync(releaseName, ns, ct);
        var toPrune = history
            .OrderByDescending(x => x.Revision)
            .Skip(maxHistory)
            .ToList();

        foreach (var old in toPrune)
        {
            try
            {
                await store.DeleteAsync(old, ct);
            }
            catch
            {
                // Best effort pruning
            }
        }
    }

    /// <summary>
    /// Combines a single values file path and a list of values file paths into one enumerable.
    /// Ordering matches Helm's precedence: <paramref name="valuesFile"/> comes first (lower
    /// precedence), <paramref name="valuesFiles"/> are applied after (higher precedence on
    /// conflict, since later files override earlier ones).
    /// Exact string duplicates are silently deduplicated (does not resolve relative paths).
    /// </summary>
    private static IEnumerable<string>? CombineValuesFiles(string? valuesFile, List<string>? valuesFiles)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(valuesFile))
            result.Add(valuesFile);
        if (valuesFiles is { Count: > 0 })
            result.AddRange(valuesFiles.Where(f => f != valuesFile));
        return result.Count > 0 ? result : null;
    }

    private static CommandResult Ok(string output)
        => new() { ExitCode = 0, StandardOutput = output };

    private static CommandResult Fail(string error)
        => new() { ExitCode = 1, StandardError = error };

    private static readonly JsonSerializerOptions JsonDefaults = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
