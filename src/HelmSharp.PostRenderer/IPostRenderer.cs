namespace HelmSharp.PostRenderer;

public interface IPostRenderer
{
    Task<string> RunAsync(string renderedManifest, CancellationToken cancellationToken = default);
}
