namespace HelmSharp.Action;

public class HelmExecutionOptions
{
    public string? KubeConfigPath { get; set; }

    public string? KubeConfigContent { get; set; }

    public string? KubernetesContext { get; set; }

    public string? DefaultNamespace { get; set; }

    public string? WorkingDirectory { get; set; }

    public int TimeoutSeconds { get; set; } = 300;

    public string FieldManager { get; set; } = "chemical-ai-helm";

    /// <summary>
    /// Maximum number of revisions to keep per release (0 = unlimited).
    /// </summary>
    public int MaxHistory { get; set; }

    /// <summary>
    /// If true, use server-side apply for resource updates.
    /// </summary>
    public bool ServerSideApply { get; set; }

    /// <summary>
    /// Kubernetes cluster version for kubeVersion compatibility checks.
    /// If null, the check is skipped.
    /// </summary>
    public string? KubeVersion { get; set; }
}
