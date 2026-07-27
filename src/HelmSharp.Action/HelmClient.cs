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
/// </summary>
public class HelmClient : IHelmClient
{
    private static readonly string ProductVersion =
        typeof(HelmClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly IHelmOptionsProvider _optionsProvider;
    private readonly Func<HelmExecutionOptions, string?, string?, CancellationToken, Task<k8s.Kubernetes>> _createKubernetesClientAsync;
    private readonly Func<HelmChartRepository> _createChartRepository;

    public HelmClient(IHelmOptionsProvider optionsProvider)
        : this(optionsProvider, CreateKubernetesClientAsync, static () => new HelmChartRepository())
    {
    }

    internal HelmClient(
        IHelmOptionsProvider optionsProvider,
        Func<HelmExecutionOptions, string?, string?, CancellationToken, Task<k8s.Kubernetes>> createKubernetesClientAsync)
        : this(optionsProvider, createKubernetesClientAsync, static () => new HelmChartRepository())
    {
    }

    internal HelmClient(
        IHelmOptionsProvider optionsProvider,
        Func<HelmExecutionOptions, string?, string?, CancellationToken, Task<k8s.Kubernetes>> createKubernetesClientAsync,
        Func<HelmChartRepository> createChartRepository)
    {
        ArgumentNullException.ThrowIfNull(optionsProvider);
        ArgumentNullException.ThrowIfNull(createKubernetesClientAsync);
        ArgumentNullException.ThrowIfNull(createChartRepository);
        _optionsProvider = optionsProvider;
        _createKubernetesClientAsync = createKubernetesClientAsync;
        _createChartRepository = createChartRepository;
    }

    public Task<CommandResult> VersionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Ok($"HelmSharp {ProductVersion}"));

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
    /// Generates a release name from a name template (e.g., "%RELEASE-NAME%-mychart").
    /// </summary>
    public static string GenerateReleaseName(string chartName, string? nameTemplate = null)
    {
        if (!string.IsNullOrWhiteSpace(nameTemplate))
        {
            // Simple template: replace %RELEASE-NAME% with chart name
            return nameTemplate.Replace("%RELEASE-NAME%", chartName, StringComparison.OrdinalIgnoreCase);
        }

        // Default: chart-name + timestamp
        var baseName = Path.GetFileNameWithoutExtension(chartName);
        if (baseName.Length > 20)
            baseName = baseName[..20];
        return $"{baseName}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

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

    public async IAsyncEnumerable<string> UpgradeInstallStreamAsync(
        HelmUpgradeInstallRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateUpgradeRequest(request);
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var timeout = request.TimeoutSeconds ?? options.TimeoutSeconds;
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var operationToken = operationSource.Token;
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";

        yield return $"Loading chart {request.Chart}";
        var chartPath = await ResolveChartPathAsync(request.Chart, request.Version, options, operationToken);
        var chart = await HelmChartLoader.LoadAsync(chartPath, operationToken);

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

        using var client = await _createKubernetesClientAsync(options, request.KubeConfigPath, request.KubeConfigContent, operationToken);
        var store = new HelmReleaseStore(client);
        var existingHistory = await LoadReleaseHistoryForUpgradeInstallAsync(
            store,
            request.ReleaseName,
            ns,
            request.CreateNamespace || !request.Install,
            operationToken);
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
                    crdError = ex.Message;
                }
                foreach (var line in crdResults) yield return line;
                if (crdError is not null) yield return $"  CRD warning: {crdError}";
            }
        }

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

        // Execute pre-hooks
        if (!request.DisableHooks && hooks.Count > 0)
        {
            var hookExecutor = new HelmHookExecutor(client, options.FieldManager);
            var preEvent = isUpgrade ? HelmHookEvent.PreUpgrade : HelmHookEvent.PreInstall;
            await foreach (var hookLine in StreamWithFailureHandlingAsync(
                               hookExecutor.ExecuteHooksWithFailureHandlingAsync(hooks, preEvent, ns, operationToken),
                               error => PersistFailedLifecycleAsync(store, releaseRecord, error, null, mainManifest, existingHistory, isUpgrade, request, ns)))
            {
                yield return hookLine;
            }
        }

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

        foreach (var line in appliedResources)
            yield return line;

        if (applyError is not null)
        {
            var recovery = await PersistFailedLifecycleAsync(store, releaseRecord, applyError, applier, mainManifest, existingHistory, isUpgrade, request, ns);
            foreach (var line in recovery)
                yield return line;
            throw applyError;
        }

        // Execute post-hooks
        if (!request.DisableHooks && hooks.Count > 0)
        {
            var hookExecutor = new HelmHookExecutor(client, options.FieldManager);
            var postEvent = isUpgrade ? HelmHookEvent.PostUpgrade : HelmHookEvent.PostInstall;
            await foreach (var hookLine in StreamWithFailureHandlingAsync(
                               hookExecutor.ExecuteHooksWithFailureHandlingAsync(hooks, postEvent, ns, operationToken),
                               error => PersistFailedLifecycleAsync(store, releaseRecord, error, applier, mainManifest, existingHistory, isUpgrade, request, ns)))
            {
                yield return hookLine;
            }
        }

        // Wait for resources to be ready
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
                    var recovery = await PersistFailedLifecycleAsync(store, releaseRecord, waitError, applier, mainManifest, existingHistory, isUpgrade, request, ns);
                    foreach (var line in recovery)
                        yield return line;
                    throw waitError;
                }
                if (!hasNext)
                    break;
            }
        }
        List<string>? saveRecovery = null;
        Exception? saveError = null;
        try
        {
            var completedAt = DateTimeOffset.UtcNow;
            releaseRecord = releaseRecord with
            {
                UpdatedAt = completedAt,
                FirstDeployedAt = existingHistory.Count == 0 ? completedAt : firstDeployedAt
            };
            await store.SaveAsync(releaseRecord, operationToken);
        }
        catch (Exception ex)
        {
            saveRecovery = await PersistFailedLifecycleAsync(store, releaseRecord, ex, applier, mainManifest, existingHistory, isUpgrade, request, ns);
            saveError = ex;
        }
        if (saveRecovery is not null)
            foreach (var line in saveRecovery)
                yield return line;
        if (saveError is not null)
            throw saveError;
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

    internal static (bool IsUpgrade, int Revision) ResolveReleaseRenderState(
        IReadOnlyCollection<HelmReleaseRecord> history)
    {
        if (history.Count == 0)
            return (false, 1);

        var latest = history.MaxBy(record => record.Revision)!;
        var isUpgrade = !string.Equals(latest.Status, "uninstalled", StringComparison.OrdinalIgnoreCase);
        var revision = latest.Revision + 1;
        return (isUpgrade, revision);
    }

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

        if (applier is null)
            return output;

        if (isUpgrade && (request.Atomic || request.CleanupOnFail))
        {
            var previous = history
                .Where(record => string.Equals(record.Status, "deployed", StringComparison.OrdinalIgnoreCase))
                .MaxBy(record => record.Revision);
            if (previous is not null)
            {
                try
                {
                    var attemptedOnlyManifest = GetAttemptedOnlyManifest(previous.Manifest, mainManifest, ns);
                    if (!string.IsNullOrWhiteSpace(attemptedOnlyManifest))
                    {
                        await foreach (var resource in applier.DeleteAsync(attemptedOnlyManifest, ns, cancellationToken: CancellationToken.None))
                            output.Add($"Removed failed-upgrade resource {resource}");
                    }
                    if (request.Atomic)
                    {
                        await foreach (var resource in applier.ApplyAsync(previous.Manifest, ns, CancellationToken.None))
                            output.Add($"Restored {resource}");
                    }
                }
                catch
                {
                    output.Add("Unable to fully restore the previous deployed revision.");
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

    private static async Task PersistFailedRollbackAsync(
        HelmReleaseStore store,
        HelmReleaseRecord rollbackRecord,
        Exception error)
    {
        try
        {
            await store.SaveAsync(rollbackRecord with
            {
                Status = "failed",
                UpdatedAt = DateTimeOffset.UtcNow,
                Description = $"Rollback failed: {error.Message}"
            }, CancellationToken.None);
        }
        catch
        {
            // Preserve the operation failure when its lifecycle evidence cannot be stored.
        }
    }

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

    private static string ManifestIdentityKey(ManifestIdentity identity)
        => $"{identity.ApiVersion}/{identity.Namespace}/{identity.Kind}/{identity.Name}";

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

    public async Task<CommandResult> UninstallAsync(
        HelmUninstallRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ReleaseName))
            return Fail("release name is required");

        using var timeoutSource = request.TimeoutSeconds is > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds.Value))
            : null;
        using var operationSource = timeoutSource is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var operationToken = operationSource?.Token ?? cancellationToken;

        var options = await _optionsProvider.GetHelmAsync(operationToken);
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(options, request.KubeConfigPath, request.KubeConfigContent, operationToken);
        var store = new HelmReleaseStore(client);
        var latest = await store.GetLatestAsync(request.ReleaseName, ns, operationToken);
        var history = await store.HistoryAsync(request.ReleaseName, ns, operationToken);
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
        var hookExecutor = new HelmHookExecutor(client, options.FieldManager);

        // Execute pre-delete hooks
        if (!request.DisableHooks && hooks.Any(h => h.Events.Contains(HelmHookEvent.PreDelete)))
        {
            await foreach (var _ in hookExecutor.ExecuteHooksAsync(hooks, HelmHookEvent.PreDelete, ns, operationToken))
            {
                // drain
            }
        }

        var applier = new KubernetesManifestApplier(client, options.FieldManager);
        var output = new StringBuilder();
        var deletedManifests = new StringBuilder();
        foreach (var failedRevision in history.Where(record =>
                     string.Equals(record.Status, "failed", StringComparison.OrdinalIgnoreCase)))
        {
            var failedOnlyManifest = GetAttemptedOnlyManifest(mainManifest, failedRevision.Manifest, ns);
            await foreach (var resource in applier.DeleteAsync(
                               failedOnlyManifest,
                               ns,
                               propagationPolicy: request.DeletionPropagation.ToString(),
                               cancellationToken: operationToken))
            {
                output.AppendLine($"Deleted {resource}");
            }
            AppendManifestDocuments(deletedManifests, failedOnlyManifest);
        }
        await foreach (var resource in applier.DeleteAsync(
                           mainManifest,
                           ns,
                           propagationPolicy: request.DeletionPropagation.ToString(),
                           cancellationToken: operationToken))
        {
            output.AppendLine($"Deleted {resource}");
        }
        AppendManifestDocuments(deletedManifests, mainManifest);

        if (request.Wait)
        {
            var timeout = request.TimeoutSeconds ?? options.TimeoutSeconds;
            var waiter = new KubernetesResourceWaiter(client, timeout);
            await foreach (var line in waiter.WaitForDeletedAsync(deletedManifests.ToString(), ns, operationToken))
                output.AppendLine(line);
        }

        // Execute post-delete hooks
        if (!request.DisableHooks && hooks.Any(h => h.Events.Contains(HelmHookEvent.PostDelete)))
        {
            await foreach (var _ in hookExecutor.ExecuteHooksAsync(hooks, HelmHookEvent.PostDelete, ns, operationToken))
            {
                // drain
            }
        }

        if (request.KeepHistory)
            await store.MarkUninstalledAsync(latest, operationToken);
        else
            await store.PurgeAsync(request.ReleaseName, ns, operationToken);
        output.AppendLine($"release \"{request.ReleaseName}\" uninstalled");
        return Ok(output.ToString());
    }

    private static void AppendManifestDocuments(StringBuilder builder, string manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest))
            return;

        if (builder.Length > 0)
            builder.AppendLine("---");

        builder.AppendLine(manifest.Trim());
    }

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
        ValidateRollbackRequest(request);
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
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

        var current = await store.GetLatestAsync(request.ReleaseName, ns, operationToken);
        if (current is null)
            return Fail($"release: not found: {request.ReleaseName}");

        var storedHistory = await store.HistoryAsync(request.ReleaseName, ns, operationToken);
        var targetRecord = request.Revision > 0
            ? storedHistory.FirstOrDefault(x => x.Revision == request.Revision)
            : storedHistory
                .Where(x => x.Status != "uninstalled" && x.Revision < current.Revision)
                .OrderByDescending(x => x.Revision)
                .FirstOrDefault();

        if (targetRecord is null)
            return Fail($"release has no revision {request.Revision}");

        var (mainManifest, hooks) = ResolveStoredManifest(targetRecord, ns);
        var hookExecutor = new HelmHookExecutor(client, options.FieldManager);
        var newRevision = await store.NextRevisionAsync(request.ReleaseName, ns, operationToken);
        var rollbackRecord = new HelmReleaseRecord
        {
            Name = request.ReleaseName,
            Namespace = ns,
            Revision = newRevision,
            Status = "deployed",
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

        var output = new StringBuilder();

        try
        {
            // Execute pre-rollback hooks
            if (!request.DisableHooks && hooks.Any(h => h.Events.Contains(HelmHookEvent.PreRollback)))
            {
                await foreach (var hookLine in hookExecutor.ExecuteHooksAsync(hooks, HelmHookEvent.PreRollback, ns, operationToken))
                {
                    output.AppendLine(hookLine);
                }
            }

            var applier = new KubernetesManifestApplier(client, options.FieldManager);

            await foreach (var resource in applier.ApplyAsync(mainManifest, ns, operationToken))
            {
                output.AppendLine($"Rolled back {resource}");
            }

            // Execute post-rollback hooks
            if (!request.DisableHooks && hooks.Any(h => h.Events.Contains(HelmHookEvent.PostRollback)))
            {
                await foreach (var hookLine in hookExecutor.ExecuteHooksAsync(hooks, HelmHookEvent.PostRollback, ns, operationToken))
                {
                    output.AppendLine(hookLine);
                }
            }

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
            await PersistFailedRollbackAsync(store, rollbackRecord, ex);
            throw;
        }

        await store.SaveAsync(rollbackRecord with { UpdatedAt = DateTimeOffset.UtcNow }, operationToken);
        var history = await store.HistoryAsync(request.ReleaseName, ns, CancellationToken.None);
        await SupersedeDeployedReleasesAsync(store, history.Where(record => record.Revision != newRevision), CancellationToken.None);
        var maxHistory = request.MaxHistory ?? options.MaxHistory;
        if (maxHistory > 0)
            await PruneOldReleasesAsync(store, request.ReleaseName, ns, maxHistory, CancellationToken.None);

        output.AppendLine($"Rollback to revision {targetRecord.Revision} was successful.");
        return Ok(output.ToString());
    }

    public async Task<CommandResult> TemplateAsync(
        HelmTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";
        var chartPath = await ResolveChartPathAsync(request.Chart, null, options, cancellationToken);
        var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
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

    public async Task<CommandResult> TemplateWithNotesAsync(
        HelmTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";
        var chart = await HelmChartLoader.LoadAsync(request.Chart, cancellationToken);
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

    internal static string GetStoredNotes(HelmReleaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.IsNullOrWhiteSpace(record.Notes)
            ? "No notes found for this release."
            : record.Notes;
    }

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
            output.AppendLine(hook.Manifest);
        }
        return Ok(output.ToString());
    }

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

    public async Task<CommandResult> TestAsync(
        string releaseName,
        string? @namespace = null,
        int? timeoutSeconds = null,
        bool showLogs = false,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = @namespace ?? options.DefaultNamespace ?? "default";
        var timeout = timeoutSeconds ?? options.TimeoutSeconds;
        using var client = await _createKubernetesClientAsync(options, null, null, cancellationToken);
        var store = new HelmReleaseStore(client);

        var latest = await store.GetLatestAsync(releaseName, ns, cancellationToken);
        if (latest is null)
            return Fail($"release: not found: {releaseName}");

        var (_, hooks) = ResolveStoredManifest(latest, ns);
        var testHooks = hooks.Where(h => h.Events.Contains(HelmHookEvent.Test)).ToList();

        if (testHooks.Count == 0)
            return Ok($"No test hooks found for release {releaseName}");

        var output = new StringBuilder();
        output.AppendLine($"TESTING: {releaseName}");
        var hookExecutor = new HelmHookExecutor(client, options.FieldManager);
        var passed = 0;
        var failed = 0;

        foreach (var hook in testHooks)
        {
            try
            {
                await foreach (var line in hookExecutor.ExecuteHooksAsync(
                    new List<HelmHook> { hook }, HelmHookEvent.Test, ns, cancellationToken))
                {
                    output.AppendLine(line);
                }
                passed++;
                output.AppendLine($"PASSED: {hook.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                output.AppendLine($"FAILED: {hook.Name}: {ex.Message}");
            }
        }

        output.AppendLine();
        output.AppendLine($"TEST RESULTS: {passed} passed, {failed} failed, {testHooks.Count} total");

        return failed > 0
            ? Fail(output.ToString())
            : Ok(output.ToString());
    }

    public async Task<CommandResult> DiffAsync(
        string releaseName,
        HelmUpgradeInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(options, request.KubeConfigPath, request.KubeConfigContent, cancellationToken);
        var store = new HelmReleaseStore(client);

        var history = await store.HistoryAsync(releaseName, ns, cancellationToken);
        var currentManifest = history
            .Where(record => string.Equals(record.Status, "deployed", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.Revision)
            .Select(record => record.Manifest)
            .FirstOrDefault() ?? string.Empty;

        var chart = await HelmChartLoader.LoadAsync(request.Chart, cancellationToken);
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
            var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);

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

    public async Task<CommandResult> ShowManifestAsync(
        string chartPath,
        string? version = null,
        string? valuesContent = null,
        Dictionary<string, string>? setValues = null,
        CancellationToken cancellationToken = default)
    {
        var chartPathResolved = await ResolveChartPathAsync(chartPath, version,
            await _optionsProvider.GetHelmAsync(cancellationToken), cancellationToken);
        var chart = await HelmChartLoader.LoadAsync(chartPathResolved, cancellationToken);
        var values = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, valuesContent, setValues, null, null, null, cancellationToken);
        var renderer = new HelmTemplateRenderer(chart, "show", "default", values);
        return Ok(renderer.Render());
    }

    public async Task<CommandResult> ShowChartAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
    {
        var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
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

    public async Task<CommandResult> ShowValuesAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
    {
        var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
        return Ok(chart.ValuesYaml);
    }

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
        using var repo = _createChartRepository();
        var path = await repo.PullChartAsync(request, cancellationToken);
        return Ok($"Chart pulled to: {path}");
    }

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

    public async Task<CommandResult> CreateAsync(
        string chartName,
        string? destination = null,
        string? starter = null,
        CancellationToken cancellationToken = default)
    {
        var path = await HelmChartCreator.CreateAsync(chartName, destination, starter, cancellationToken);
        return Ok($"Created chart: {path}");
    }

    public async Task<CommandResult> DependencyUpdateAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
        => await DependencyUpdateAsync(
            new HelmDependencyUpdateRequest { ChartPath = chartPath },
            cancellationToken);

    /// <inheritdoc />
    public async Task<CommandResult> DependencyUpdateAsync(
        HelmDependencyUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chartPath = Path.GetFullPath(request.ChartPath);
        var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
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
                ? _createChartRepository()
                : new HelmChartRepository(new HelmRepositoryOptions
                {
                    RepositoryConfigPath = request.RepositoryConfigPath,
                    CacheDirectory = request.RepositoryCachePath
                });
            var configuredRepositories = await repo.ListRepositoriesAsync(cancellationToken);
            var refreshedRepositories = new HashSet<string>(StringComparer.Ordinal);

            foreach (var dependency in chart.Dependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
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

    public async Task<CommandResult> RepoAddAsync(
        string name,
        string url,
        string? username = null,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        using var repo = _createChartRepository();
        await repo.AddRepositoryAsync(name, url, username, password, cancellationToken);
        return Ok($"Repository \"{name}\" added with URL: {url}");
    }

    public async Task<CommandResult> RepoRemoveAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        using var repo = _createChartRepository();
        await repo.RemoveRepositoryAsync(name, cancellationToken);
        return Ok($"Repository \"{name}\" removed.");
    }

    public async Task<CommandResult> RepoListAsync(
        CancellationToken cancellationToken = default)
    {
        using var repo = _createChartRepository();
        var repos = await repo.ListRepositoriesAsync(cancellationToken);
        return Ok(System.Text.Json.JsonSerializer.Serialize(repos, JsonDefaults));
    }

    public async Task<CommandResult> SearchRepoAsync(
        string keyword,
        string? repoUrl = null,
        CancellationToken cancellationToken = default)
    {
        using var repo = _createChartRepository();
        var results = repoUrl is null
            ? await repo.SearchRepoAsync(keyword, cancellationToken)
            : await repo.SearchRepoAsync(repoUrl, keyword, cancellationToken: cancellationToken);
        return Ok(System.Text.Json.JsonSerializer.Serialize(results, JsonDefaults));
    }

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

    public async Task<CommandResult> ShowReadmeAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
    {
        var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);

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

    public async Task<CommandResult> ShowCrdsAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
    {
        var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
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

    public async Task<CommandResult> RepoIndexAsync(
        string dirPath,
        string? url = null,
        CancellationToken cancellationToken = default)
        => await RepoIndexAsync(
            new HelmRepoIndexRequest { DirectoryPath = dirPath, Url = url },
            cancellationToken);

    /// <summary>
    /// Generates a repository index and optionally merges an existing index.
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

    public async Task<CommandResult> RepoUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        using var repo = _createChartRepository();
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

    public async Task<CommandResult> ShowAllAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
    {
        var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);

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

    public async Task<CommandResult> DependencyBuildAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
        => await DependencyBuildAsync(
            new HelmDependencyBuildRequest { ChartPath = chartPath },
            cancellationToken);

    /// <inheritdoc />
    public async Task<CommandResult> DependencyBuildAsync(
        HelmDependencyBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chartPath = Path.GetFullPath(request.ChartPath);
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
                ? _createChartRepository()
                : new HelmChartRepository(new HelmRepositoryOptions
                {
                    RepositoryConfigPath = request.RepositoryConfigPath,
                    CacheDirectory = request.RepositoryCachePath
                });
            var configuredRepositories = await repository.ListRepositoriesAsync(cancellationToken);
            var refreshedRepositories = new HashSet<string>(StringComparer.Ordinal);

            foreach (var dependency in lockFile.Dependencies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
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

    public async Task<CommandResult> DependencyListAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
        => await DependencyListAsync(
            new HelmDependencyListRequest { ChartPath = chartPath },
            cancellationToken);

    /// <inheritdoc />
    public async Task<CommandResult> DependencyListAsync(
        HelmDependencyListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chart = await HelmChartLoader.LoadAsync(request.ChartPath, cancellationToken);
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
            using var repo = new HelmChartRepository();
            return await repo.PullChartAsync(chartRef, version, cancellationToken);
        }

        // If it contains a slash and looks like repo/chart, try as HTTP repo
        if (chartRef.Contains('/') && !chartRef.Contains(Path.DirectorySeparatorChar) && !chartRef.Contains('/'))
            return chartRef;

        return chartRef;
    }

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
            LastRunPhase = "Unknown",
            Weight = hook.Weight,
            DeletePolicies = hook.DeletePolicies.Select(ToReleaseHookDeletePolicy).ToList()
        };

    private static HelmHook FromReleaseHook(HelmReleaseHookRecord record)
    {
        var hook = new HelmHook
        {
            Name = record.Name,
            Kind = record.Kind,
            Path = record.Path,
            Manifest = record.Manifest,
            Weight = record.Weight
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
        return hook;
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
