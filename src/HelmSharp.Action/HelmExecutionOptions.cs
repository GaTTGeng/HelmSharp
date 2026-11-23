using HelmSharp.Chart;

namespace HelmSharp.Action;

// ReSharper disable UnusedAutoPropertyAccessor.Global, PropertyCanBeMadeInitOnly.Global, UnusedMember.Global // Public option setters and settings are consumed by library callers.
/// <summary>
/// Application-owned default options supplied to <see cref="HelmClient"/> through <see cref="IHelmOptionsProvider"/>.
/// </summary>
public class HelmExecutionOptions
{
    /// <summary>Optional path to a kubeconfig file used for Kubernetes API access.</summary>
    public string? KubeConfigPath { get; set; }

    /// <summary>Optional raw kubeconfig YAML content used instead of <see cref="KubeConfigPath"/>.</summary>
    public string? KubeConfigContent { get; set; }

    /// <summary>Optional kubeconfig context name to select when creating the Kubernetes client.</summary>
    public string? KubernetesContext { get; set; }

    /// <summary>Default target Kubernetes namespace used when a request omits an explicit namespace.</summary>
    public string? DefaultNamespace { get; set; }

    /// <summary>Optional working directory used to resolve relative chart and values paths.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Default operation timeout in seconds covering apply, hooks, readiness waiting, and deletion waiting.
    /// Defaults to 300 seconds and must be greater than zero when used by lifecycle waits.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Field manager name sent on dynamically discovered create/replace paths (such as custom resources).
    /// Common typed resources do not send this value, and it does not enable server-side apply.
    /// </summary>
    public string FieldManager { get; set; } = "helmsharp";

    /// <summary>
    /// Maximum number of revisions to keep per release (0 = unlimited).
    /// </summary>
    public int MaxHistory { get; set; }

    /// <summary>
    /// Requests server-side apply for resource updates. Server-side apply is not currently
    /// supported; lifecycle operations reject this option before creating or mutating Kubernetes resources.
    /// </summary>
    public bool ServerSideApply { get; set; }

    /// <summary>
    /// Kubernetes cluster version for kubeVersion compatibility checks.
    /// If null, the check is skipped.
    /// </summary>
    public string? KubeVersion { get; set; }

    /// <summary>
    /// Additional Kubernetes API versions exposed through template capabilities.
    /// </summary>
    public List<string>? ApiVersions { get; set; }

    /// <summary>
    /// Decompression budgets enforced while loading chart archives. Null applies
    /// <see cref="HelmChartArchiveLimits.Default"/>; raise them only for trusted chart sources.
    /// </summary>
    public HelmChartArchiveLimits? ArchiveLimits { get; set; }
}
// ReSharper restore UnusedAutoPropertyAccessor.Global
