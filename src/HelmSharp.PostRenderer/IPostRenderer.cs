namespace HelmSharp.PostRenderer;

/// <summary>
/// Transforms rendered manifests after template rendering (equivalent to Helm's --post-renderer).
/// </summary>
public interface IPostRenderer
{
    /// <summary>Runs the post-renderer over the rendered manifest YAML and returns the rewritten YAML.</summary>
    Task<string> RunAsync(string renderedManifest, CancellationToken cancellationToken = default);
}
