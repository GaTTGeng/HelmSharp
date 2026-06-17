namespace HelmSharp.Tests;

public class PostRendererTests
{
    [Fact]
    public void HelmSharp_PostRenderer_ProjectExists()
    {
        var assembly = typeof(HelmSharp.PostRenderer.IPostRenderer).Assembly;
        Assert.NotNull(assembly);
        Assert.Contains("HelmSharp.PostRenderer", assembly.FullName);
    }

    [Fact]
    public void HelmSharp_Storage_ProjectExists()
    {
        var assembly = typeof(HelmSharp.Storage.IHelmReleaseStore).Assembly;
        Assert.NotNull(assembly);
        Assert.Contains("HelmSharp.Storage", assembly.FullName);
    }

    [Fact]
    public void HelmSharp_Registry_ProjectExists()
    {
        var assembly = typeof(HelmSharp.Registry.IOciRegistryClient).Assembly;
        Assert.NotNull(assembly);
        Assert.Contains("HelmSharp.Registry", assembly.FullName);
    }
}
