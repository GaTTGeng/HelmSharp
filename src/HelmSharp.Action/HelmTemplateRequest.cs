namespace HelmSharp.Action;

/// <summary>Options for rendering a chart's templates without applying them.</summary>
public class HelmTemplateRequest
{
    /// <summary>Release name used while rendering.</summary>
    public string ReleaseName { get; set; } = string.Empty;

    /// <summary>Chart reference: local path, archive path, or repo/chart name.</summary>
    public string Chart { get; set; } = string.Empty;

    /// <summary>Namespace exposed to templates as <c>.Release.Namespace</c>.</summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Path to a single values file (equivalent to helm -f).
    /// For multiple values files, use <see cref="ValuesFiles"/> instead.
    /// </summary>
    public string? ValuesFile { get; set; }

    /// <summary>
    /// Paths to multiple values files (equivalent to helm -f file1 -f file2).
    /// Applied in order; later files override earlier ones.
    /// </summary>
    public List<string>? ValuesFiles { get; set; }

    /// <summary>Equivalent to helm --set: key is a values path, value is a scalar expression.</summary>
    public Dictionary<string, string>? SetValues { get; set; }

    /// <summary>
    /// Equivalent to helm --set-file: key is a values path, value is the file content.
    /// </summary>
    public Dictionary<string, string>? SetFileValues { get; set; }

    /// <summary>
    /// Equivalent to helm --set-string: forces string values (no type coercion).
    /// </summary>
    public Dictionary<string, string>? SetStringValues { get; set; }

    /// <summary>
    /// Equivalent to helm --set-json: sets JSON values from command line.
    /// </summary>
    public Dictionary<string, string>? SetJsonValues { get; set; }

    /// <summary>Raw values YAML content passed directly (equivalent to helm -f from stdin content).</summary>
    public string? ValuesContent { get; set; }

    /// <summary>
    /// If true, show the chart's NOTES.txt output.
    /// </summary>
    public bool ShowNotes { get; set; }

    /// <summary>
    /// If true, include CRDs in the output.
    /// </summary>
    public bool IncludeCRDs { get; set; }

    /// <summary>
    /// If true, use release name as output directory prefix.
    /// </summary>
    public bool UseReleaseName { get; set; }

    /// <summary>
    /// Output directory for rendered templates (for helm template --output-dir).
    /// </summary>
    public string? OutputDir { get; set; }

    /// <summary>
    /// Kubernetes version to use for Capabilities.
    /// </summary>
    public string? KubeVersion { get; set; }

    /// <summary>
    /// API versions to use for Capabilities.
    /// </summary>
    public List<string>? ApiVersions { get; set; }

    /// <summary>
    /// If true, render with .Release.IsUpgrade = true.
    /// </summary>
    public bool IsUpgrade { get; set; }
}
