namespace HelmSharp.Tests;

public class PostRendererTests
{
    [Fact]
    public void HelmSharp_PostRenderer_ProjectExists()
    {
        var assembly = typeof(HelmSharp.PostRenderer.Class1).Assembly;
        Assert.NotNull(assembly);
        Assert.Contains("HelmSharp.PostRenderer", assembly.FullName);
    }

    [Fact]
    public void HelmSharp_Storage_ProjectExists()
    {
        var assembly = typeof(HelmSharp.Storage.Class1).Assembly;
        Assert.NotNull(assembly);
        Assert.Contains("HelmSharp.Storage", assembly.FullName);
    }
}
