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

        var result = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, null, setValues, null, null, null, CancellationToken.None);

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

        var result = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, valuesContent, null, null, null, null, CancellationToken.None);

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
            chart, (IEnumerable<string>?)null, "tag: \"2.0\"",
            new Dictionary<string, string> { ["tag"] = "3.0" },
            null, null, null, CancellationToken.None);

        Assert.Equal(3.0, Convert.ToDouble(result["tag"]));
    }

    [Fact]
    public async Task BuildAsync_MergesSubchartDefaultsForEveryAliasInstance()
    {
        var chart = new HelmChart
        {
            Name = "parent",
            Version = "1.0.0",
            ValuesYaml = ""
        };
        chart.Dependencies.Add(new HelmChartDependency
        {
            Name = "child",
            Alias = "cache",
            Version = "1.0.0"
        });
        chart.Dependencies.Add(new HelmChartDependency
        {
            Name = "child",
            Alias = "session",
            Version = "1.0.0"
        });
        chart.Subcharts["child"] = new HelmChart
        {
            Name = "child",
            Version = "1.0.0",
            ValuesYaml = """
                marker: default
                nested:
                  enabled: true
                """
        };

        var result = await HelmValues.BuildAsync(
            chart,
            (IEnumerable<string>?)null,
            """
            session:
              marker: session-override
            """,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        var cache = Assert.IsType<Dictionary<string, object?>>(result["cache"]);
        var session = Assert.IsType<Dictionary<string, object?>>(result["session"]);
        Assert.Equal("default", cache["marker"]);
        Assert.Equal("session-override", session["marker"]);
        Assert.True(Assert.IsType<Dictionary<string, object?>>(cache["nested"]).ContainsKey("enabled"));
        Assert.True(Assert.IsType<Dictionary<string, object?>>(session["nested"]).ContainsKey("enabled"));
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

        var result = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, null, null, setFileValues, null, null, CancellationToken.None);

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
            chart, (IEnumerable<string>?)null, null,
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
            chart, (IEnumerable<string>?)null, null, null, null,
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

        var result = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, valuesContent, null, null, null, null, CancellationToken.None);

        var level1 = result["level1"] as Dictionary<string, object?>;
        Assert.NotNull(level1);
        var level2 = level1["level2"] as Dictionary<string, object?>;
        Assert.NotNull(level2);
        Assert.Equal("overridden", level2["key1"]);
        Assert.Equal("keep", level2["key2"]);
        Assert.Equal("added", level2["key3"]);
    }

    // ────────────────────────────────────────────────────────────
    //  values precedence tests (#6)
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_SetOverridesSetFile()
    {
        // Helm precedence: --set overrides --set-file
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = "key: default"
        };

        var setFileValues = new Dictionary<string, string> { ["key"] = "from-set-file" };
        var setValues = new Dictionary<string, string> { ["key"] = "from-set" };

        var result = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, null, setValues, setFileValues, null, null, CancellationToken.None);

        Assert.Equal("from-set", result["key"]); // --set wins over --set-file
    }

    [Fact]
    public async Task BuildAsync_SetOverridesSetString()
    {
        // Helm precedence: --set overrides --set-string
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = "key: default"
        };

        var setStringValues = new Dictionary<string, string> { ["key"] = "from-set-string" };
        var setValues = new Dictionary<string, string> { ["key"] = "from-set" };

        var result = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, null, setValues, null, setStringValues, null, CancellationToken.None);

        Assert.Equal("from-set", result["key"]); // --set wins over --set-string
    }

    [Fact]
    public async Task BuildAsync_SetStringOverridesSetFile()
    {
        // Helm precedence: --set-string overrides --set-file
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = "key: default"
        };

        var setFileValues = new Dictionary<string, string> { ["key"] = "from-set-file" };
        var setStringValues = new Dictionary<string, string> { ["key"] = "42" };

        var result = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, null, null, setFileValues, setStringValues, null, CancellationToken.None);

        Assert.Equal("42", result["key"]); // --set-string wins over --set-file (and no coercion, stays string)
    }

    [Fact]
    public async Task BuildAsync_SetJsonHasHighestPrecedence()
    {
        // Helm precedence: --set-json overrides everything (highest)
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = "key: default"
        };

        var setValues = new Dictionary<string, string> { ["key"] = "from-set" };
        var setStringValues = new Dictionary<string, string> { ["key"] = "from-set-string" };
        var setFileValues = new Dictionary<string, string> { ["key"] = "from-set-file" };
        var setJsonValues = new Dictionary<string, string> { ["key"] = "\"json-value\"" };

        var result = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, null, setValues, setFileValues, setStringValues, setJsonValues, CancellationToken.None);

        Assert.Equal("json-value", result["key"]); // --set-json trumps all
    }

    [Fact]
    public async Task BuildAsync_FullPrecedenceChain()
    {
        // Verify full precedence chain:
        // chart defaults < values files < values content < set-file < set-string < set < set-json
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = "precedence: chart-default\nfromValuesContent: x"
        };

        var valuesContent = "precedence: from-values-content\nfromSetFile: x\nfromSetString: x\nfromSet: x\nfromSetJson: x";
        var setFileValues = new Dictionary<string, string> { ["precedence"] = "from-set-file", ["fromSetFile"] = "setfile-wins" };
        var setStringValues = new Dictionary<string, string> { ["precedence"] = "from-set-string", ["fromSetString"] = "setstring-wins" };
        var setValues = new Dictionary<string, string> { ["precedence"] = "from-set", ["fromSet"] = "set-wins" };
        var setJsonValues = new Dictionary<string, string> { ["precedence"] = "\"from-set-json\"", ["fromSetJson"] = "\"setjson-wins\"" };

        var result = await HelmValues.BuildAsync(chart, (IEnumerable<string>?)null, valuesContent, setValues, setFileValues, setStringValues, setJsonValues, CancellationToken.None);

        Assert.Equal("from-set-json", result["precedence"]); // --set-json wins overall
        Assert.Equal("setfile-wins", result["fromSetFile"]);
        Assert.Equal("setstring-wins", result["fromSetString"]);
        Assert.Equal("set-wins", result["fromSet"]);
        Assert.Equal("setjson-wins", result["fromSetJson"]);
        Assert.Equal("x", result["fromValuesContent"]);
    }

    [Fact]
    public async Task BuildAsync_MultipleValuesFiles_MergeInOrder()
    {
        // Multiple -f files: later files override earlier ones
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = "key1: default\nkey2: default\nkey3: default"
        };

        // Write temp files
        var dir = Path.Combine(Path.GetTempPath(), "helm-values-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file1 = Path.Combine(dir, "values1.yaml");
            var file2 = Path.Combine(dir, "values2.yaml");
            await File.WriteAllTextAsync(file1, "key1: from-file1\nkey2: from-file1");
            await File.WriteAllTextAsync(file2, "key1: from-file2\nkey3: from-file2");

            var result = await HelmValues.BuildAsync(chart, new[] { file1, file2 }, null, null, null, null, null, CancellationToken.None);

            Assert.Equal("from-file2", result["key1"]); // file2 overrides file1
            Assert.Equal("from-file1", result["key2"]); // only in file1, preserved
            Assert.Equal("from-file2", result["key3"]); // only in file2
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort — Windows file locks may delay deletion */ }
        }
    }

    [Fact]
    public async Task BuildAsync_SingleValuesFile_BackwardCompatible()
    {
        // Single values file still works (via IEnumerable)
        var chart = new HelmChart
        {
            Name = "test",
            Version = "1.0.0",
            ValuesYaml = "key: default"
        };

        var dir = Path.Combine(Path.GetTempPath(), "helm-values-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "values.yaml");
            await File.WriteAllTextAsync(file, "key: from-file");
            await File.WriteAllTextAsync(Path.Combine(dir, "values2.yaml"), "key: should-not-load");

            var result = await HelmValues.BuildAsync(chart, new[] { file }, null, null, null, null, null, CancellationToken.None);

            Assert.Equal("from-file", result["key"]);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
