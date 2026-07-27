namespace HelmSharp.Action;

/// <summary>
/// Options for rolling a release back to a stored revision.
/// </summary>
public sealed class HelmRollbackRequest
{
    /// <summary>Release to roll back.</summary>
    public string ReleaseName { get; set; } = string.Empty;

    /// <summary>
    /// Target revision. A value of zero selects the previous non-uninstalled revision.
    /// </summary>
    public int Revision { get; set; }

    public string? Namespace { get; set; }

    /// <summary>Wait for the restored resources before recording a deployed revision.</summary>
    public bool Wait { get; set; } = true;

    /// <summary>When waiting, also wait for Jobs to complete.</summary>
    public bool WaitForJobs { get; set; }

    /// <summary>Operation timeout in seconds. When omitted, the configured Helm timeout is used.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>Skip pre-rollback and post-rollback hooks.</summary>
    public bool DisableHooks { get; set; }

    /// <summary>Optional description stored on the new rollback revision.</summary>
    public string? Description { get; set; }

    /// <summary>Labels applied to the new rollback revision, overriding labels from the target revision.</summary>
    public Dictionary<string, string>? Labels { get; set; }

    /// <summary>Maximum number of stored revisions to retain. Zero means no limit.</summary>
    public int? MaxHistory { get; set; }

    public string? KubeConfigPath { get; set; }

    public string? KubeConfigContent { get; set; }
}
