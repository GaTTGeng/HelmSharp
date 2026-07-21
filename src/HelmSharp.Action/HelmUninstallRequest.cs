namespace HelmSharp.Action;

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
    /// Gets or sets whether the operation waits for Kubernetes delete requests to complete.
    /// Kubernetes API delete calls are awaited in either case; this option is retained for
    /// compatibility with the Helm request surface.
    /// </summary>
    public bool Wait { get; set; }

    /// <summary>Gets or sets the optional operation timeout in seconds.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>Gets or sets the Kubernetes deletion propagation preference.</summary>
    public HelmDeletionPropagation DeletionPropagation { get; set; } = HelmDeletionPropagation.Background;

    public string? KubeConfigPath { get; set; }

    public string? KubeConfigContent { get; set; }
}

/// <summary>Kubernetes owner-reference propagation preference for release deletion.</summary>
public enum HelmDeletionPropagation
{
    Background,
    Foreground,
    Orphan
}
