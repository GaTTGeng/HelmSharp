namespace HelmSharp.Action;

// ReSharper disable PropertyCanBeMadeInitOnly.Global, UnusedMember.Global // Public request setters and Helm-compatible enum values are API surface.
public class HelmUninstallRequest
{
    /// <summary>Gets or sets the name of the release to uninstall.</summary>
    public string ReleaseName { get; set; } = string.Empty;

    /// <summary>Gets or sets the namespace containing the release.</summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Gets or sets whether the release history is retained. Defaults to <see langword="false"/>,
    /// matching Helm's purge-by-default uninstall behavior.
    /// </summary>
    public bool KeepHistory { get; set; }

    /// <summary>
    /// Gets or sets whether Helm hooks are skipped during uninstall.
    /// </summary>
    public bool DisableHooks { get; set; }

    /// <summary>
    /// Gets or sets whether the operation polls via <see cref="HelmSharp.Kube.KubernetesResourceWaiter.WaitForDeletedAsync"/>
    /// until every deleted release resource is observed absent (or the timeout expires).
    /// </summary>
    public bool Wait { get; set; }

    /// <summary>
    /// Gets or sets the optional operation timeout in seconds. When omitted or non-positive,
    /// uninstall skips the operation-wide timeout token while hooks and <see cref="Wait"/> fall back
    /// to <see cref="HelmExecutionOptions.TimeoutSeconds"/>.
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets how Kubernetes handles dependents of every release resource deleted by uninstall.
    /// Defaults to <see cref="HelmDeletionPropagation.Background"/>. Hook cleanup uses background propagation.
    /// </summary>
    public HelmDeletionPropagation DeletionPropagation { get; set; } = HelmDeletionPropagation.Background;

    /// <summary>Gets or sets an optional path to a kubeconfig file for this uninstall call.</summary>
    public string? KubeConfigPath { get; set; }

    /// <summary>Gets or sets optional raw kubeconfig content used instead of <see cref="KubeConfigPath"/>.</summary>
    public string? KubeConfigContent { get; set; }
}
// ReSharper restore PropertyCanBeMadeInitOnly.Global, UnusedMember.Global

/// <summary>Kubernetes owner-reference propagation preference for release deletion.</summary>
public enum HelmDeletionPropagation
{
    /// <summary>Delete the owner immediately and let Kubernetes remove dependents asynchronously.</summary>
    Background,

    /// <summary>Keep the owner until Kubernetes has removed blocking dependents.</summary>
    Foreground,

    /// <summary>Delete the owner while leaving dependents orphaned.</summary>
    // ReSharper disable once UnusedMember.Global // Helm exposes this propagation choice for compatibility.
    Orphan
}
