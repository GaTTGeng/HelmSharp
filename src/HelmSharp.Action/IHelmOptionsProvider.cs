namespace HelmSharp.Action;

/// <summary>
/// Supplies the shared execution settings (kubeconfig, defaults, timeouts) used by HelmSharp operations.
/// </summary>
public interface IHelmOptionsProvider
{
    /// <summary>Resolves the options in effect for the upcoming operation.</summary>
    ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default);
}
