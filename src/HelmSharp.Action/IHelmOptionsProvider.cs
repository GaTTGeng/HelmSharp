namespace HelmSharp.Action;

public interface IHelmOptionsProvider
{
    ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default);
}
