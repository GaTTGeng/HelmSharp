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

    public HelmClient(IHelmOptionsProvider optionsProvider)
        : this(optionsProvider, CreateKubernetesClientAsync)
    {
    }

    internal HelmClient(
        IHelmOptionsProvider optionsProvider,
        Func<HelmExecutionOptions, string?, string?, CancellationToken, Task<k8s.Kubernetes>> createKubernetesClientAsync)
    {
        _optionsProvider = optionsProvider;
        _createKubernetesClientAsync = createKubernetesClientAsync;
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
            var selectorParts = ParseLabelSelector(selector);
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

    private static Dictionary<string, string> ParseLabelSelector(string selector)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in selector.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex > 0)
                result[trimmed[..eqIndex].Trim()] = trimmed[(eqIndex + 1)..].Trim();
        }
        return result;
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
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";

        yield return $"Loading chart {request.Chart}";
        var chartPath = await ResolveChartPathAsync(request.Chart, request.Version, options, cancellationToken);
        var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);

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
        var values = await HelmValues.BuildAsync(chart, valuesFiles, request.ValuesContent, request.SetValues, request.SetFileValues, request.SetStringValues, request.SetJsonValues, cancellationToken);

        if (request.DryRun)
        {
            var dryRunRenderer = new HelmTemplateRenderer(
                chart,
                request.ReleaseName,
                ns,
                values,
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

        using var client = await _createKubernetesClientAsync(options, request.KubeConfigPath, request.KubeConfigContent, cancellationToken);
        var store = new HelmReleaseStore(client);
        var existingHistory = await LoadReleaseHistoryForUpgradeInstallAsync(
            store,
            request.ReleaseName,
            ns,
            request.CreateNamespace,
            cancellationToken);
        var (isUpgrade, revision) = ResolveReleaseRenderState(existingHistory);
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
            await KubernetesManifestApplier.EnsureNamespaceAsync(client, ns, cancellationToken);
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
                    await foreach (var resource in crdApplier.ApplyAsync(crdYaml, ns, cancellationToken))
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

        // Execute pre-hooks
        if (!request.DisableHooks && hooks.Count > 0)
        {
            var hookExecutor = new HelmHookExecutor(client, options.FieldManager);
            var preEvent = isUpgrade ? HelmHookEvent.PreUpgrade : HelmHookEvent.PreInstall;
            await foreach (var hookLine in hookExecutor.ExecuteHooksAsync(hooks, preEvent, ns, cancellationToken))
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
            await foreach (var resource in applier.ApplyAsync(mainManifest, ns, cancellationToken))
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

        if (applyError is not null && (request.Atomic || request.CleanupOnFail))
        {
            yield return $"Error: {applyError.Message}. Cleaning up...";
            await foreach (var resource in applier.DeleteAsync(mainManifest, ns, cancellationToken))
            {
                yield return $"Cleaned up {resource}";
            }
            throw applyError;
        }
        if (applyError is not null) throw applyError;

        // Execute post-hooks
        if (!request.DisableHooks && hooks.Count > 0)
        {
            var hookExecutor = new HelmHookExecutor(client, options.FieldManager);
            var postEvent = isUpgrade ? HelmHookEvent.PostUpgrade : HelmHookEvent.PostInstall;
            await foreach (var hookLine in hookExecutor.ExecuteHooksAsync(hooks, postEvent, ns, cancellationToken))
            {
                yield return hookLine;
            }
        }

        // Wait for resources to be ready
        if (request.Wait && !request.DryRun)
        {
            var timeout = request.TimeoutSeconds ?? options.TimeoutSeconds;
            yield return $"Waiting for resources to be ready (timeout: {timeout}s)...";
            var waiter = new KubernetesResourceWaiter(client, timeout);
            await foreach (var waitLine in waiter.WaitForReadyAsync(mainManifest, ns, waitForJobs: request.WaitForJobs, cancellationToken: cancellationToken))
            {
                yield return waitLine;
            }
        }
        await store.SaveAsync(new HelmReleaseRecord
        {
            Name = request.ReleaseName,
            Namespace = ns,
            Revision = revision,
            Status = "deployed",
            ChartName = chart.Name,
            ChartVersion = chart.Version,
            AppVersion = chart.AppVersion,
            Manifest = manifest,
            ValuesYaml = HelmValues.ToYaml(values),
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

        // Enforce max history
        var maxHistory = request.MaxHistory ?? options.MaxHistory;
        if (maxHistory > 0)
        {
            await PruneOldReleasesAsync(store, request.ReleaseName, ns, maxHistory, cancellationToken);
        }

        yield return $"Release {request.ReleaseName} revision {revision} deployed ({applied} resources)";
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

    private static async Task<List<HelmReleaseRecord>> LoadReleaseHistoryForUpgradeInstallAsync(
        HelmReleaseStore store,
        string releaseName,
        string ns,
        bool createNamespace,
        CancellationToken cancellationToken)
    {
        try
        {
            return await store.HistoryAsync(releaseName, ns, cancellationToken);
        }
        catch (HttpOperationException ex) when (createNamespace && (int)ex.Response.StatusCode == 404)
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

        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = request.Namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(options, request.KubeConfigPath, request.KubeConfigContent, cancellationToken);
        var store = new HelmReleaseStore(client);
        var latest = await store.GetLatestAsync(request.ReleaseName, ns, cancellationToken);
        if (latest is null)
            return Fail($"release: not found: {request.ReleaseName}");

        var (mainManifest, hooks) = HelmHookExecutor.ExtractHooks(latest.Manifest, ns);
        var hookExecutor = new HelmHookExecutor(client, options.FieldManager);

        // Execute pre-delete hooks
        if (hooks.Any(h => h.Events.Contains(HelmHookEvent.PreDelete)))
        {
            await foreach (var _ in hookExecutor.ExecuteHooksAsync(hooks, HelmHookEvent.PreDelete, ns, cancellationToken))
            {
                // drain
            }
        }

        var applier = new KubernetesManifestApplier(client, options.FieldManager);
        var output = new StringBuilder();
        await foreach (var resource in applier.DeleteAsync(mainManifest, ns, cancellationToken))
        {
            output.AppendLine($"Deleted {resource}");
        }

        // Execute post-delete hooks
        if (hooks.Any(h => h.Events.Contains(HelmHookEvent.PostDelete)))
        {
            await foreach (var _ in hookExecutor.ExecuteHooksAsync(hooks, HelmHookEvent.PostDelete, ns, cancellationToken))
            {
                // drain
            }
        }

        await store.MarkUninstalledAsync(latest, cancellationToken);
        output.AppendLine($"release \"{request.ReleaseName}\" uninstalled");
        return Ok(output.ToString());
    }

    public async Task<CommandResult> StatusAsync(
        string releaseName,
        string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        using var client = await _createKubernetesClientAsync(options, null, null, cancellationToken);
        var store = new HelmReleaseStore(client);
        var latest = await store.GetLatestAsync(releaseName, @namespace ?? options.DefaultNamespace ?? "default", cancellationToken);
        if (latest is null)
            return Fail($"release: not found: {releaseName}");

        var statusInfo = new
        {
            name = latest.Name,
            @namespace = latest.Namespace,
            revision = latest.Revision,
            status = latest.Status,
            chart = $"{latest.ChartName}-{latest.ChartVersion}",
            app_version = latest.AppVersion,
            updated = latest.UpdatedAt.ToString("o"),
            notes = ""
        };
        return Ok(JsonSerializer.Serialize(statusInfo, JsonDefaults));
    }

    public async Task<CommandResult> RollbackAsync(
        string releaseName,
        int revision,
        string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        var ns = @namespace ?? options.DefaultNamespace ?? "default";
        using var client = await _createKubernetesClientAsync(options, null, null, cancellationToken);
        var store = new HelmReleaseStore(client);

        var current = await store.GetLatestAsync(releaseName, ns, cancellationToken);
        if (current is null)
            return Fail($"release: not found: {releaseName}");

        var targetRecord = revision > 0
            ? (await store.HistoryAsync(releaseName, ns, cancellationToken)).FirstOrDefault(x => x.Revision == revision)
            : (await store.HistoryAsync(releaseName, ns, cancellationToken))
                .Where(x => x.Status != "uninstalled" && x.Revision < current.Revision)
                .OrderByDescending(x => x.Revision)
                .FirstOrDefault();

        if (targetRecord is null)
            return Fail($"release has no revision {revision}");

        var targetManifest = targetRecord.Manifest;
        var (mainManifest, hooks) = HelmHookExecutor.ExtractHooks(targetManifest, ns);
        var hookExecutor = new HelmHookExecutor(client, options.FieldManager);

        var output = new StringBuilder();

        // Execute pre-rollback hooks
        if (hooks.Any(h => h.Events.Contains(HelmHookEvent.PreRollback)))
        {
            await foreach (var hookLine in hookExecutor.ExecuteHooksAsync(hooks, HelmHookEvent.PreRollback, ns, cancellationToken))
            {
                output.AppendLine(hookLine);
            }
        }

        var applier = new KubernetesManifestApplier(client, options.FieldManager);

        await foreach (var resource in applier.ApplyAsync(mainManifest, ns, cancellationToken))
        {
            output.AppendLine($"Rolled back {resource}");
        }

        // Execute post-rollback hooks
        if (hooks.Any(h => h.Events.Contains(HelmHookEvent.PostRollback)))
        {
            await foreach (var hookLine in hookExecutor.ExecuteHooksAsync(hooks, HelmHookEvent.PostRollback, ns, cancellationToken))
            {
                output.AppendLine(hookLine);
            }
        }

        var newRevision = await store.NextRevisionAsync(releaseName, ns, cancellationToken);
        await store.SaveAsync(new HelmReleaseRecord
        {
            Name = releaseName,
            Namespace = ns,
            Revision = newRevision,
            Status = "deployed",
            ChartName = targetRecord.ChartName,
            ChartVersion = targetRecord.ChartVersion,
            AppVersion = targetRecord.AppVersion,
            Manifest = targetManifest,
            ValuesYaml = targetRecord.ValuesYaml,
            UpdatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);

        await store.MarkStatusAsync(current, "superseded", cancellationToken);

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
        return Ok(JsonSerializer.Serialize(history, JsonDefaults));
    }

    public async Task<CommandResult> GetValuesAsync(
        string releaseName,
        string? @namespace = null,
        bool allValues = false,
        CancellationToken cancellationToken = default)
    {
        var options = await _optionsProvider.GetHelmAsync(cancellationToken);
        using var client = await _createKubernetesClientAsync(options, null, null, cancellationToken);
        var store = new HelmReleaseStore(client);
        var latest = await store.GetLatestAsync(releaseName, @namespace ?? options.DefaultNamespace ?? "default", cancellationToken);
        return latest is null
            ? Fail($"release: not found: {releaseName}")
            : Ok(latest.ValuesYaml);
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

        var record = revision > 0
            ? (await store.HistoryAsync(releaseName, ns, cancellationToken)).FirstOrDefault(x => x.Revision == revision)
            : await store.GetLatestAsync(releaseName, ns, cancellationToken);

        return record is null
            ? Fail($"release: not found: {releaseName}")
            : Ok(record.Manifest);
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

        var record = revision > 0
            ? (await store.HistoryAsync(releaseName, ns, cancellationToken)).FirstOrDefault(x => x.Revision == revision)
            : await store.GetLatestAsync(releaseName, ns, cancellationToken);

        if (record is null)
            return Fail($"release: not found: {releaseName}");

        // Render NOTES.txt from the chart if available
        if (!string.IsNullOrWhiteSpace(record.Manifest))
        {
            try
            {
                var chart = await HelmChartLoader.LoadAsync(
                    await ResolveChartPathAsync(record.ChartName, record.ChartVersion, options, cancellationToken),
                    cancellationToken);
                var values = HelmYaml.DeserializeDictionary(record.ValuesYaml);
                var renderer = new HelmTemplateRenderer(chart, releaseName, ns, values);
                var notes = renderer.RenderNotes();
                if (!string.IsNullOrWhiteSpace(notes))
                    return Ok(notes);
            }
            catch
            {
                // Fall through to stored notes
            }
        }

        return Ok("No notes found for this release.");
    }

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

        var record = revision > 0
            ? (await store.HistoryAsync(releaseName, ns, cancellationToken)).FirstOrDefault(x => x.Revision == revision)
            : await store.GetLatestAsync(releaseName, ns, cancellationToken);

        if (record is null)
            return Fail($"release: not found: {releaseName}");

        var (mainManifest, hooks) = HelmHookExecutor.ExtractHooks(record.Manifest, ns);
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

        var record = revision > 0
            ? (await store.HistoryAsync(releaseName, ns, cancellationToken)).FirstOrDefault(x => x.Revision == revision)
            : await store.GetLatestAsync(releaseName, ns, cancellationToken);

        if (record is null)
            return Fail($"release: not found: {releaseName}");

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

        var (_, hooks) = HelmHookExecutor.ExtractHooks(latest.Manifest, ns);
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
    {
        using var repo = new HelmChartRepository();
        var path = await repo.PullChartAsync(chartRef, version, cancellationToken);
        return Ok($"Chart pulled to: {path}");
    }

    public async Task<CommandResult> PackageAsync(
        string chartPath,
        string? destination = null,
        string? version = null,
        string? appVersion = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = await HelmChartPackager.PackageAsync(chartPath, destination, version, appVersion, cancellationToken);
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
    {
        var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
        if (chart.Dependencies.Count == 0)
            return Ok("No dependencies found in Chart.yaml");

        var chartsDir = Path.Combine(chartPath, "charts");
        Directory.CreateDirectory(chartsDir);

        var output = new StringBuilder();
        using var repo = new HelmChartRepository();

        foreach (var dep in chart.Dependencies)
        {
            if (!dep.Enabled)
            {
                output.AppendLine($"Skipping disabled dependency: {dep.Name}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(dep.Repository))
            {
                output.AppendLine($"Skipping dependency {dep.Name}: no repository specified");
                continue;
            }

            try
            {
                var selected = await repo.ResolveChartVersionAsync(
                    dep.Repository.TrimEnd('/'),
                    dep.Name,
                    dep.Version,
                    cancellationToken: cancellationToken);

                var requestedVersion = string.IsNullOrWhiteSpace(dep.Version) ? selected.Version : dep.Version;
                output.AppendLine($"Downloading dependency: {dep.Name} ({requestedVersion}) from {dep.Repository}");
                var pulledPath = await repo.PullChartAsync(
                    $"{dep.Repository.TrimEnd('/')}/{dep.Name}",
                    selected.Version, cancellationToken);

                var destPath = Path.Combine(chartsDir, $"{dep.Name}-{selected.Version}.tgz");
                if (Directory.Exists(pulledPath))
                {
                    var tgzPath = await HelmChartPackager.PackageAsync(pulledPath, chartsDir, selected.Version, null, cancellationToken);
                    output.AppendLine($"Dependency {dep.Name} saved to {tgzPath}");
                }
                else if (File.Exists(pulledPath))
                {
                    File.Copy(pulledPath, destPath, overwrite: true);
                    output.AppendLine($"Dependency {dep.Name} saved to {destPath}");
                }
            }
            catch (Exception ex)
            {
                output.AppendLine($"Error downloading dependency {dep.Name}: {ex.Message}");
            }
        }

        return Ok(output.ToString());
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
        using var repo = new HelmChartRepository();
        await repo.AddRepositoryAsync(name, url, username, password, cancellationToken);
        return Ok($"Repository \"{name}\" added with URL: {url}");
    }

    public async Task<CommandResult> RepoRemoveAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        using var repo = new HelmChartRepository();
        await repo.RemoveRepositoryAsync(name, cancellationToken);
        return Ok($"Repository \"{name}\" removed.");
    }

    public async Task<CommandResult> RepoListAsync(
        CancellationToken cancellationToken = default)
    {
        using var repo = new HelmChartRepository();
        var repos = await repo.ListRepositoriesAsync();
        return Ok(System.Text.Json.JsonSerializer.Serialize(repos, JsonDefaults));
    }

    public async Task<CommandResult> SearchRepoAsync(
        string keyword,
        string? repoUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (repoUrl is null)
            return Fail("Repository URL is required for search");

        using var repo = new HelmChartRepository();
        var results = await repo.SearchRepoAsync(repoUrl, keyword, cancellationToken: cancellationToken);
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
        => await RepoIndexAsync(dirPath, url, cancellationToken, mergeIndexPath: null);

    public async Task<CommandResult> RepoIndexAsync(
        string dirPath,
        string? url,
        CancellationToken cancellationToken,
        string? mergeIndexPath)
    {
        var indexPath = await HelmRepoIndexer.GenerateIndexAsync(dirPath, url, cancellationToken, mergeIndexPath);
        return Ok($"Index generated at: {indexPath}");
    }

    public async Task<CommandResult> RepoUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        using var repo = new HelmChartRepository();
        var repos = await repo.ListRepositoriesAsync();
        var output = new StringBuilder();
        var updated = 0;
        var failed = 0;

        foreach (var r in repos)
        {
            try
            {
                await repo.FetchRepoIndexAsync(r.Url, r.Username, r.Password, r.Name, cancellationToken);
                updated++;
                output.AppendLine($"Successfully updated: {r.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                output.AppendLine($"Failed to update {r.Name}: {ex.Message}");
            }
        }

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
    {
        // Same as DependencyUpdate — build downloads and packages dependencies
        return await DependencyUpdateAsync(chartPath, cancellationToken);
    }

    public async Task<CommandResult> DependencyListAsync(
        string chartPath,
        CancellationToken cancellationToken = default)
    {
        var chart = await HelmChartLoader.LoadAsync(chartPath, cancellationToken);
        if (chart.Dependencies.Count == 0)
            return Ok("No dependencies found in Chart.yaml");

        var chartsDir = Path.Combine(chartPath, "charts");
        var output = new System.Text.StringBuilder();
        output.AppendLine("NAME\t\tVERSION\t\tREPOSITORY\t\tSTATUS");

        foreach (var dep in chart.Dependencies)
        {
            var status = "unknown";
            if (!dep.Enabled)
            {
                status = "disabled";
            }
            else
            {
                // Check if dependency is present in charts/
                var archiveVersions = new List<string>();
                if (Directory.Exists(chartsDir))
                {
                    archiveVersions.AddRange(
                        Directory.GetFiles(chartsDir, $"{dep.Name}-*.tgz")
                            .Select(path => GetDependencyArchiveVersion(dep.Name, path))
                            .OfType<string>());
                }

                var matchingVersion = HelmChartVersionResolver.ResolveVersion(
                    archiveVersions,
                    dep.Version);

                if (matchingVersion is not null)
                    status = "ok";
                else if (archiveVersions.Count > 0)
                    status = "mismatch";
                else
                    status = "missing";
            }

            output.AppendLine($"{dep.Name}\t\t{dep.Version ?? "?"}\t\t{dep.Repository ?? "local"}\t\t{status}");
        }

        return Ok(output.ToString());
    }

    private static string? GetDependencyArchiveVersion(string dependencyName, string archivePath)
    {
        var fileName = Path.GetFileName(archivePath);
        if (!fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            return null;

        var packageName = fileName[..^".tgz".Length];
        var prefix = dependencyName + "-";
        return packageName.StartsWith(prefix, StringComparison.Ordinal)
            ? packageName[prefix.Length..]
            : null;
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
        if (string.IsNullOrWhiteSpace(request.ReleaseName))
            throw new ArgumentException("ReleaseName is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Chart))
            throw new ArgumentException("Chart is required.", nameof(request));
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
    /// Prunes old releases beyond the max history limit.
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
            .Where(x => x.Status != "deployed") // Keep current deployed
            .OrderByDescending(x => x.Revision)
            .Skip(maxHistory - 1) // Keep maxHistory most recent
            .ToList();

        foreach (var old in toPrune)
        {
            try
            {
                await store.MarkStatusAsync(old, "superseded", ct);
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
