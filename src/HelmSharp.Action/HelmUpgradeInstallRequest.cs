namespace HelmSharp.Action;

public class HelmUpgradeInstallRequest
{
    public string ReleaseName { get; set; } = string.Empty;

    public string Chart { get; set; } = string.Empty;

    public string? Namespace { get; set; }

    public string? Version { get; set; }

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
    /// Key is the values path, value is a JSON string.
    /// </summary>
    public Dictionary<string, string>? SetJsonValues { get; set; }

    public bool CreateNamespace { get; set; } = true;

    public bool Wait { get; set; } = true;

    /// <summary>
    /// If true and Wait is true, wait for all Jobs to complete before marking release as successful.
    /// </summary>
    public bool WaitForJobs { get; set; }

    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Limit the maximum number of revisions saved per release. 0 = no limit.
    /// </summary>
    public int? MaxHistory { get; set; }

    public bool Atomic { get; set; }

    public bool CleanupOnFail { get; set; }

    /// <summary>直接传入 values YAML 内容。</summary>
    public string? ValuesContent { get; set; }

    public string? KubeConfigPath { get; set; }

    public string? KubeConfigContent { get; set; }

    /// <summary>
    /// If true, reuse the last release's values and merge with provided values.
    /// </summary>
    public bool ReuseValues { get; set; }

    /// <summary>
    /// If true, reset values to chart defaults before applying.
    /// </summary>
    public bool ResetValues { get; set; }

    /// <summary>
    /// If true, skip CRD installation.
    /// </summary>
    public bool SkipCRDs { get; set; }

    /// <summary>
    /// If true, disable hooks (pre-install, post-install, etc.).
    /// </summary>
    public bool DisableHooks { get; set; }

    /// <summary>
    /// If true, perform a dry run only (render but do not apply).
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// If true, render a dry run with upgrade release state.
    /// </summary>
    public bool DryRunIsUpgrade { get; set; }

    /// <summary>
    /// Release revision exposed while rendering a dry run.
    /// </summary>
    public int DryRunRevision { get; set; } = 1;

    /// <summary>
    /// If true, force resource replacement.
    /// </summary>
    public bool Force { get; set; }

    /// <summary>
    /// If true, install if the release does not exist.
    /// </summary>
    public bool Install { get; set; } = true;

    /// <summary>
    /// Description for the release.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Labels to apply to the release metadata.
    /// </summary>
    public Dictionary<string, string>? Labels { get; set; }

    /// <summary>
    /// If true, use development versions (alpha, beta, rc).
    /// </summary>
    public bool Devel { get; set; }

    /// <summary>
    /// If true, automatically generate the release name.
    /// </summary>
    public bool GenerateName { get; set; }

    /// <summary>
    /// Template for generating release name (e.g., "%RELEASE-NAME%-mychart").
    /// </summary>
    public string? NameTemplate { get; set; }

    /// <summary>
    /// If true, ignore helm annotations and take ownership of existing resources.
    /// </summary>
    public bool TakeOwnership { get; set; }

    /// <summary>
    /// If true, rollback the upgrade to previous release on failure.
    /// </summary>
    public bool RollbackOnFailure { get; set; }

    /// <summary>
    /// If true, render subchart notes along with the parent.
    /// </summary>
    public bool RenderSubchartNotes { get; set; }

    /// <summary>
    /// If true, hide Kubernetes Secrets in dry-run output.
    /// </summary>
    public bool HideSecret { get; set; }

    /// <summary>
    /// Server-side apply mode: "true", "false", or "auto".
    /// </summary>
    public string? ServerSideApply { get; set; }

    /// <summary>
    /// CA bundle for HTTPS certificate verification.
    /// </summary>
    public string? CaFile { get; set; }

    /// <summary>
    /// Client SSL certificate file.
    /// </summary>
    public string? CertFile { get; set; }

    /// <summary>
    /// Client SSL key file.
    /// </summary>
    public string? KeyFile { get; set; }

    /// <summary>
    /// If true, skip TLS certificate verification.
    /// </summary>
    public bool InsecureSkipTlsVerify { get; set; }

    /// <summary>
    /// Chart repository username.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Chart repository password.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Chart repository URL.
    /// </summary>
    public string? RepoUrl { get; set; }

    /// <summary>
    /// If true, pass credentials to all domains.
    /// </summary>
    public bool PassCredentials { get; set; }

    /// <summary>
    /// If true, use plain HTTP for chart download.
    /// </summary>
    public bool PlainHttp { get; set; }

    /// <summary>
    /// Path to the keyring for provenance verification.
    /// </summary>
    public string? Keyring { get; set; }

    /// <summary>
    /// If true, verify the chart provenance before installing.
    /// </summary>
    public bool Verify { get; set; }

    /// <summary>
    /// If true, disable OpenAPI validation.
    /// </summary>
    public bool DisableOpenApiValidation { get; set; }

    /// <summary>
    /// If true, skip JSON schema validation.
    /// </summary>
    public bool SkipSchemaValidation { get; set; }

    /// <summary>
    /// If true, enable DNS lookups when rendering templates.
    /// </summary>
    public bool EnableDns { get; set; }

    /// <summary>
    /// If true, update dependencies before installing.
    /// </summary>
    public bool DependencyUpdate { get; set; }
}
