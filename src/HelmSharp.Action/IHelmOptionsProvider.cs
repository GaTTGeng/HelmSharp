namespace HelmSharp.Action;

/// <summary>
/// Supplies the shared execution settings (kubeconfig, defaults, timeouts) used by HelmSharp operations.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "ReSharper",
    "UnusedParameter.Global",
    Justification = "Implementations in external integrations consume the cancellation token even though repository-local fakes may not.")]
public interface IHelmOptionsProvider
{
    /// <summary>Resolves the options in effect for the upcoming operation.</summary>
    ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default);
}
