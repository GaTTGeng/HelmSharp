using HelmSharp.Engine;

namespace HelmSharp.Tests;

public class ApiVersionSetTests
{
    [Fact]
    public void Constructor_TakesSnapshotOfInputList()
    {
        var source = new List<object?> { "v1" };
        var set = new ApiVersionSet(source);

        source.Add("apps/v1");
        source.Clear();

        Assert.Single(set);
        Assert.True(set.Has("v1"));
        Assert.False(set.Has("apps/v1"));
    }
}
