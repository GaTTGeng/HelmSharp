namespace HelmSharp.Action;

public class HelmTemplateRequest
{
    public string ReleaseName { get; set; } = string.Empty;

    public string Chart { get; set; } = string.Empty;

    public string? Namespace { get; set; }

    public string? ValuesFile { get; set; }

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

    /// <summary>直接传入 values YAML 内容。</summary>
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
