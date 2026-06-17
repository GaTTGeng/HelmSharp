using HelmSharp.Chart;

namespace HelmSharp.Tests;

public class HelmValuesTests
{
    [Fact]
    public async Task BuildAsync_MergesValuesCorrectly()
    {
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = """
                image:
                  repository: nginx
                  tag: "1.0"
                replicaCount: 1
                """
        };

        var setValues = new Dictionary<string, string>
        {
            ["image.tag"] = "2.0",
            ["replicaCount"] = "3"
        };

        var result = await HelmValues.BuildAsync(chart, null, null, setValues, null, null, null, CancellationToken.None);

        var image = result["image"] as Dictionary<string, object?>;
        Assert.NotNull(image);
        Assert.Equal("nginx", image["repository"]);
        Assert.Equal(2.0, Convert.ToDouble(image["tag"]));
        Assert.Equal(3L, result["replicaCount"]);
    }

    [Fact]
    public async Task BuildAsync_MergesValuesContent()
    {
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = """
                image:
                  repository: nginx
                  tag: "1.0"
                """
        };

        var valuesContent = """
            image:
              tag: "2.0"
              pullPolicy: Always
            """;

        var result = await HelmValues.BuildAsync(chart, null, valuesContent, null, null, null, null, CancellationToken.None);

        var image = result["image"] as Dictionary<string, object?>;
        Assert.NotNull(image);
        Assert.Equal("nginx", image["repository"]);
        Assert.Equal("2.0", image["tag"]);
        Assert.Equal("Always", image["pullPolicy"]);
    }

    [Fact]
    public async Task BuildAsync_SetValuesOverrideValuesContent()
    {
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = "tag: \"1.0\""
        };

        var result = await HelmValues.BuildAsync(
            chart, null, "tag: \"2.0\"",
            new Dictionary<string, string> { ["tag"] = "3.0" },
            null, null, null, CancellationToken.None);

        Assert.Equal(3.0, Convert.ToDouble(result["tag"]));
    }

    [Fact]
    public async Task BuildAsync_SetFileValues()
    {
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = "config: {}"
        };

        var setFileValues = new Dictionary<string, string>
        {
            ["config.data"] = "file-content-here"
        };

        var result = await HelmValues.BuildAsync(chart, null, null, null, setFileValues, null, null, CancellationToken.None);

        var config = result["config"] as Dictionary<string, object?>;
        Assert.NotNull(config);
        Assert.Equal("file-content-here", config["data"]);
    }

    [Fact]
    public void ToYaml_Serializes()
    {
        var values = new Dictionary<string, object?>
        {
            ["key"] = "value",
            ["nested"] = new Dictionary<string, object?>
            {
                ["inner"] = 42
            }
        };

        var yaml = HelmValues.ToYaml(values);
        Assert.Contains("key: value", yaml);
        Assert.Contains("inner: 42", yaml);
    }

    [Fact]
    public async Task BuildAsync_CoercesScalars()
    {
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = "count: 0\nenabled: false"
        };

        var result = await HelmValues.BuildAsync(
            chart, null, null,
            new Dictionary<string, string>
            {
                ["count"] = "42",
                ["enabled"] = "true"
            },
            null, null, null, CancellationToken.None);

        Assert.Equal(42L, result["count"]);
        Assert.Equal(true, result["enabled"]);
    }

    [Fact]
    public async Task BuildAsync_SetStringValues_NoCoercion()
    {
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = "count: 0"
        };

        var result = await HelmValues.BuildAsync(
            chart, null, null, null, null,
            new Dictionary<string, string> { ["count"] = "42" },
            null, CancellationToken.None);

        Assert.Equal("42", result["count"]);
    }

    [Fact]
    public async Task BuildAsync_DeepNestedMerge()
    {
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = """
                level1:
                  level2:
                    key1: original
                    key2: keep
                """
        };

        var valuesContent = """
            level1:
              level2:
                key1: overridden
                key3: added
            """;

        var result = await HelmValues.BuildAsync(chart, null, valuesContent, null, null, null, null, CancellationToken.None);

        var level1 = result["level1"] as Dictionary<string, object?>;
        Assert.NotNull(level1);
        var level2 = level1["level2"] as Dictionary<string, object?>;
        Assert.NotNull(level2);
        Assert.Equal("overridden", level2["key1"]);
        Assert.Equal("keep", level2["key2"]);
        Assert.Equal("added", level2["key3"]);
    }
}
