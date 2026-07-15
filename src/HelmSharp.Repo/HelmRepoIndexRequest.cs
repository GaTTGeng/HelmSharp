namespace HelmSharp.Repo;

/// <summary>
/// Describes generation of a Helm repository index.
/// </summary>
public sealed class HelmRepoIndexRequest
{
    /// <summary>Gets or sets the directory containing chart archives.</summary>
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the base URL prepended to generated chart archive URLs.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets an existing index.yaml path whose entries are merged into the generated index.</summary>
    public string? MergeIndexPath { get; set; }

    /// <summary>Gets or sets the generated index output path. By default, index.yaml is written under <see cref="DirectoryPath"/>.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets whether an invalid chart archive fails generation. Invalid archives are skipped by default.</summary>
    public bool FailOnInvalidPackage { get; set; }
}
