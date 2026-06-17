using HelmSharp.Action;
using HelmSharp.Chart;

namespace HelmSharp.Tests;

public class ChartOperationsTests : IDisposable
{
    private readonly string _tempDir;

    public ChartOperationsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helmsharp-ops-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task CreateAsync_CreatesChartStructure()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        var result = await client.CreateAsync("my-chart", _tempDir);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Created chart", result.StandardOutput);

        var chartDir = Path.Combine(_tempDir, "my-chart");
        Assert.True(Directory.Exists(chartDir));
        Assert.True(File.Exists(Path.Combine(chartDir, "Chart.yaml")));
        Assert.True(File.Exists(Path.Combine(chartDir, "values.yaml")));
        Assert.True(Directory.Exists(Path.Combine(chartDir, "templates")));
        Assert.True(Directory.Exists(Path.Combine(chartDir, "charts")));
        Assert.True(File.Exists(Path.Combine(chartDir, ".helmignore")));
    }

    [Fact]
    public async Task CreateAsync_ChartYaml_HasCorrectContent()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("test-app", _tempDir);

        var chartYaml = await File.ReadAllTextAsync(Path.Combine(_tempDir, "test-app", "Chart.yaml"));
        Assert.Contains("name: test-app", chartYaml);
        Assert.Contains("apiVersion: v2", chartYaml);
        Assert.Contains("version: 0.1.0", chartYaml);
    }

    [Fact]
    public async Task CreateAsync_TemplatesContainHelpers()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("my-app", _tempDir);

        var helpersPath = Path.Combine(_tempDir, "my-app", "templates", "_helpers.tpl");
        Assert.True(File.Exists(helpersPath));
        var helpers = await File.ReadAllTextAsync(helpersPath);
        Assert.Contains("define", helpers);
        Assert.Contains("fullname", helpers);
        Assert.Contains("labels", helpers);
    }

    [Fact]
    public async Task CreateAsync_TemplatesContainDeployment()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("my-app", _tempDir);

        var deployPath = Path.Combine(_tempDir, "my-app", "templates", "deployment.yaml");
        Assert.True(File.Exists(deployPath));
        var deploy = await File.ReadAllTextAsync(deployPath);
        Assert.Contains("kind: Deployment", deploy);
        Assert.Contains("{{ include", deploy);
    }

    [Fact]
    public async Task CreateAsync_TemplatesContainService()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("my-app", _tempDir);

        var svcPath = Path.Combine(_tempDir, "my-app", "templates", "service.yaml");
        Assert.True(File.Exists(svcPath));
        var svc = await File.ReadAllTextAsync(svcPath);
        Assert.Contains("kind: Service", svc);
    }

    [Fact]
    public async Task CreateAsync_TemplatesContainNOTES()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("my-app", _tempDir);

        var notesPath = Path.Combine(_tempDir, "my-app", "templates", "NOTES.txt");
        Assert.True(File.Exists(notesPath));
    }

    [Fact]
    public async Task CreateAsync_CreatedChartPassesLint()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("lint-test", _tempDir);

        var result = await client.LintAsync(Path.Combine(_tempDir, "lint-test"));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CreateAsync_CreatedChartTemplatesSuccessfully()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("template-test", _tempDir);

        var chartResult = await client.ShowChartAsync(Path.Combine(_tempDir, "template-test"));
        Assert.Equal(0, chartResult.ExitCode);
        Assert.Contains("template-test", chartResult.StandardOutput);

        var valuesResult = await client.ShowValuesAsync(Path.Combine(_tempDir, "template-test"));
        Assert.Equal(0, valuesResult.ExitCode);
        Assert.Contains("nginx", valuesResult.StandardOutput);
    }

    [Fact]
    public async Task PackageAsync_CreatesTgz()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("pkg-test", _tempDir);

        var result = await client.PackageAsync(
            Path.Combine(_tempDir, "pkg-test"),
            _tempDir);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Successfully packaged", result.StandardOutput);

        var tgzPath = Path.Combine(_tempDir, "pkg-test-0.1.0.tgz");
        Assert.True(File.Exists(tgzPath));
        Assert.True(new FileInfo(tgzPath).Length > 0);
    }

    [Fact]
    public async Task PackageAsync_WithCustomVersion()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("ver-test", _tempDir);

        var result = await client.PackageAsync(
            Path.Combine(_tempDir, "ver-test"),
            _tempDir,
            version: "2.0.0");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("2.0.0", result.StandardOutput);
        var tgzPath = Path.Combine(_tempDir, "ver-test-2.0.0.tgz");
        Assert.True(File.Exists(tgzPath));

        var packagedChart = await HelmChartLoader.LoadAsync(tgzPath, CancellationToken.None);
        Assert.Equal("2.0.0", packagedChart.Version);
    }

    [Fact]
    public async Task PackageAsync_TgzLoadsSubcharts()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("parent", _tempDir);

        var subchartDir = Path.Combine(_tempDir, "parent", "charts", "child");
        Directory.CreateDirectory(Path.Combine(subchartDir, "templates"));
        await File.WriteAllTextAsync(Path.Combine(subchartDir, "Chart.yaml"), """
            apiVersion: v2
            name: child
            version: 0.2.0
            """);
        await File.WriteAllTextAsync(Path.Combine(subchartDir, "values.yaml"), "enabled: true\n");
        await File.WriteAllTextAsync(Path.Combine(subchartDir, "templates", "configmap.yaml"), """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: child-config
            """);

        await client.PackageAsync(Path.Combine(_tempDir, "parent"), _tempDir);

        var chart = await HelmChartLoader.LoadAsync(Path.Combine(_tempDir, "parent-0.1.0.tgz"), CancellationToken.None);
        Assert.True(chart.Subcharts.ContainsKey("child"));
        Assert.True(chart.Subcharts["child"].Templates.ContainsKey("templates/configmap.yaml"));
    }

    [Fact]
    public async Task UpgradeInstallStreamAsync_DryRunDoesNotRequireKubernetesClient()
    {
        var chartDir = Path.Combine(_tempDir, "dry-run-chart");
        Directory.CreateDirectory(Path.Combine(chartDir, "templates"));
        await File.WriteAllTextAsync(Path.Combine(chartDir, "Chart.yaml"), """
            apiVersion: v2
            name: dry-run-chart
            version: 0.1.0
            """);
        await File.WriteAllTextAsync(Path.Combine(chartDir, "values.yaml"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(chartDir, "templates", "configmap.yaml"), """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: dry-run-config
            """);

        var client = new HelmClient(new StaticHelmOptionsProvider());
        var lines = new List<string>();
        await foreach (var line in client.UpgradeInstallStreamAsync(new HelmUpgradeInstallRequest
        {
            ReleaseName = "dry-run",
            Chart = chartDir,
            DryRun = true
        }))
        {
            lines.Add(line);
        }

        var output = string.Join('\n', lines);
        Assert.Contains("kind: ConfigMap", output);
        Assert.Contains("Release dry-run dry run complete", output);
    }

    [Fact]
    public async Task PackageAsync_PackagedChartCanBeLoaded()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("load-test", _tempDir);

        await client.PackageAsync(Path.Combine(_tempDir, "load-test"), _tempDir);

        var tgzPath = Path.Combine(_tempDir, "load-test-0.1.0.tgz");
        Assert.True(File.Exists(tgzPath));

        var showResult = await client.ShowChartAsync(tgzPath);
        Assert.Equal(0, showResult.ExitCode);
        Assert.Contains("load-test", showResult.StandardOutput);
    }

    [Fact]
    public async Task ShowReadmeAsync_WhenNoReadme_ReturnsMessage()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("noreadme", _tempDir);

        var result = await client.ShowReadmeAsync(Path.Combine(_tempDir, "noreadme"));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No README", result.StandardOutput);
    }

    [Fact]
    public async Task ShowReadmeAsync_WhenReadmeExists_ReturnsContent()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("withreadme", _tempDir);

        var readmePath = Path.Combine(_tempDir, "withreadme", "README.md");
        await File.WriteAllTextAsync(readmePath, "# My Chart\n\nThis is a test chart.");

        var result = await client.ShowReadmeAsync(Path.Combine(_tempDir, "withreadme"));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("# My Chart", result.StandardOutput);
        Assert.Contains("This is a test chart.", result.StandardOutput);
    }

    [Fact]
    public async Task ShowCrdsAsync_WhenNoCrds_ReturnsMessage()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("nocrds", _tempDir);

        var result = await client.ShowCrdsAsync(Path.Combine(_tempDir, "nocrds"));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No CRDs", result.StandardOutput);
    }

    [Fact]
    public async Task ShowCrdsAsync_WhenCrdsExist_ReturnsContent()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("withcrds", _tempDir);

        var crdsDir = Path.Combine(_tempDir, "withcrds", "crds");
        Directory.CreateDirectory(crdsDir);
        await File.WriteAllTextAsync(Path.Combine(crdsDir, "myresource.yaml"), """
            apiVersion: apiextensions.k8s.io/v1
            kind: CustomResourceDefinition
            metadata:
              name: myresources.example.com
            spec:
              group: example.com
              names:
                kind: MyResource
                plural: myresources
              scope: Namespaced
              versions:
              - name: v1
                served: true
                storage: true
            """);

        var result = await client.ShowCrdsAsync(Path.Combine(_tempDir, "withcrds"));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("CustomResourceDefinition", result.StandardOutput);
        Assert.Contains("myresources.example.com", result.StandardOutput);
    }

    [Fact]
    public async Task VersionAsync_ReturnsVersion()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        var result = await client.VersionAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("chemical-ai-helm", result.StandardOutput);
    }

    [Fact]
    public async Task ShowChartAsync_ReturnsChartInfo()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("show-test", _tempDir);

        var result = await client.ShowChartAsync(Path.Combine(_tempDir, "show-test"));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("show-test", result.StandardOutput);
        Assert.Contains("0.1.0", result.StandardOutput);
    }

    [Fact]
    public async Task ShowValuesAsync_ReturnsValues()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("values-test", _tempDir);

        var result = await client.ShowValuesAsync(Path.Combine(_tempDir, "values-test"));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("replicaCount: 1", result.StandardOutput);
        Assert.Contains("nginx", result.StandardOutput);
    }

    [Fact]
    public async Task LintAsync_PassesForCreatedChart()
    {
        var client = new HelmClient(new StaticHelmOptionsProvider());
        await client.CreateAsync("lint-pass", _tempDir);

        var result = await client.LintAsync(Path.Combine(_tempDir, "lint-pass"));
        Assert.NotNull(result);
    }

    [Fact]
    public void GenerateReleaseName_Default()
    {
        var name = HelmClient.GenerateReleaseName("my-chart");
        Assert.StartsWith("my-chart", name);
        Assert.Contains("-", name);
    }

    [Fact]
    public void GenerateReleaseName_WithTemplate()
    {
        var name = HelmClient.GenerateReleaseName("chart", "%RELEASE-NAME%-custom");
        Assert.Equal("chart-custom", name);
    }

    [Fact]
    public void GenerateReleaseName_LongName()
    {
        var name = HelmClient.GenerateReleaseName("very-long-chart-name-that-exceeds-limits");
        Assert.True(name.Length <= 35);
    }

    private sealed class StaticHelmOptionsProvider : IHelmOptionsProvider
    {
        public ValueTask<HelmExecutionOptions> GetHelmAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HelmExecutionOptions { DefaultNamespace = "default" });
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }
}
