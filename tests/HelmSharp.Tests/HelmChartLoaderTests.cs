using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using HelmSharp.Chart;
using HelmSharp.Engine;

namespace HelmSharp.Tests;

public class HelmChartLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public HelmChartLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "helmsharp-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task LoadAsync_ParsesChartMetadata()
    {
        var chartDir = CreateChartDir("my-app", "2.0.0", "v1.2.3", "My application description");
        var chart = await HelmChartLoader.LoadAsync(chartDir, CancellationToken.None);

        Assert.Equal("my-app", chart.Name);
        Assert.Equal("2.0.0", chart.Version);
        Assert.Equal("v1.2.3", chart.AppVersion);
        Assert.Equal("My application description", chart.Description);
    }

    [Fact]
    public async Task LoadAsync_ParsesDependencies()
    {
        var chartDir = CreateChartDir("my-app", "1.0.0");
        File.WriteAllText(Path.Combine(chartDir, "Chart.yaml"), """
            apiVersion: v2
            name: my-app
            version: 1.0.0
            dependencies:
            - name: redis
              version: "17.x.x"
              repository: https://charts.bitnami.com/bitnami
              condition: redis.enabled
              tags:
              - cache
              alias: cache
              import-values:
              - data
              - child: service
                parent: imported
            - name: postgresql
              version: "12.x.x"
              repository: https://charts.bitnami.com/bitnami
              enabled: true
            """);

        var chart = await HelmChartLoader.LoadAsync(chartDir, CancellationToken.None);

        Assert.Equal(2, chart.Dependencies.Count);
        Assert.Equal("redis", chart.Dependencies[0].Name);
        Assert.Equal("17.x.x", chart.Dependencies[0].Version);
        Assert.Equal("https://charts.bitnami.com/bitnami", chart.Dependencies[0].Repository);
        Assert.Equal("redis.enabled", chart.Dependencies[0].Condition);
        Assert.Equal(["cache"], chart.Dependencies[0].Tags);
        Assert.Equal("cache", chart.Dependencies[0].Alias);
        var importValues = Assert.IsType<List<object?>>(chart.Dependencies[0].ImportValues);
        Assert.Equal("data", importValues[0]);
        var importMapping = Assert.IsAssignableFrom<IDictionary<string, object?>>(
            importValues[1]);
        Assert.Equal("service", importMapping["child"]);
        Assert.Equal("imported", importMapping["parent"]);
        Assert.Equal("postgresql", chart.Dependencies[1].Name);
        Assert.True(chart.Dependencies[1].Enabled);
    }

    [Fact]
    public async Task LoadAsync_ParsesMaintainers()
    {
        var chartDir = CreateChartDir("my-app", "1.0.0");
        File.WriteAllText(Path.Combine(chartDir, "Chart.yaml"), """
            apiVersion: v2
            name: my-app
            version: 1.0.0
            maintainers:
            - name: John Doe
              email: john@example.com
            - name: Jane Smith
            """);

        var chart = await HelmChartLoader.LoadAsync(chartDir, CancellationToken.None);

        Assert.NotNull(chart.Maintainers);
        Assert.Equal(2, chart.Maintainers.Count);
    }

    [Fact]
    public async Task LoadAsync_ParsesHomeSourcesKeywords()
    {
        var chartDir = CreateChartDir("my-app", "1.0.0");
        File.WriteAllText(Path.Combine(chartDir, "Chart.yaml"), """
            apiVersion: v2
            name: my-app
            version: 1.0.0
            home: https://example.com
            sources:
            - https://github.com/example/my-app
            keywords:
            - web
            - nginx
            - proxy
            """);

        var chart = await HelmChartLoader.LoadAsync(chartDir, CancellationToken.None);

        Assert.Equal("https://example.com", chart.Home);
        Assert.NotNull(chart.Sources);
        Assert.Single(chart.Sources);
        Assert.NotNull(chart.Keywords);
        Assert.Equal(3, chart.Keywords.Count);
    }

    [Fact]
    public async Task LoadAsync_CollectsTemplates()
    {
        var chartDir = CreateChartDir("my-app", "1.0.0");
        var templatesDir = Path.Combine(chartDir, "templates");
        File.WriteAllText(Path.Combine(templatesDir, "deployment.yaml"), """
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: {{ .Release.Name }}
            """);
        File.WriteAllText(Path.Combine(templatesDir, "_helpers.tpl"), """
            {{- define "my-app.fullname" -}}{{ .Release.Name }}-my-app{{- end -}}
            """);

        var chart = await HelmChartLoader.LoadAsync(chartDir, CancellationToken.None);

        Assert.Equal(2, chart.Templates.Count);
        Assert.True(chart.Templates.ContainsKey("templates/deployment.yaml"));
        Assert.True(chart.Templates.ContainsKey("templates/_helpers.tpl"));
    }

    [Fact]
    public async Task LoadAsync_LoadsValuesYaml()
    {
        var chartDir = CreateChartDir("my-app", "1.0.0");
        File.WriteAllText(Path.Combine(chartDir, "values.yaml"), """
            replicaCount: 3
            image:
              repository: nginx
              tag: "1.25"
            """);

        var chart = await HelmChartLoader.LoadAsync(chartDir, CancellationToken.None);

        Assert.Contains("replicaCount: 3", chart.ValuesYaml);
        Assert.Contains("nginx", chart.ValuesYaml);
    }

    [Fact]
    public async Task LoadAsync_ParsesCrds()
    {
        var chartDir = CreateChartDir("my-app", "1.0.0");
        var crdsDir = Path.Combine(chartDir, "crds");
        Directory.CreateDirectory(crdsDir);
        File.WriteAllText(Path.Combine(crdsDir, "mycrd.yaml"), """
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

        var chart = await HelmChartLoader.LoadAsync(chartDir, CancellationToken.None);

        Assert.Single(chart.Crds);
    }

    [Fact]
    public async Task LoadAsync_TgzArchive()
    {
        var chartDir = CreateChartDir("archive-chart", "0.5.0");
        var tgzPath = Path.Combine(_tempDir, "archive-chart-0.5.0.tgz");
        CreateTgz(chartDir, tgzPath);

        var chart = await HelmChartLoader.LoadAsync(tgzPath, CancellationToken.None);

        Assert.Equal("archive-chart", chart.Name);
        Assert.Equal("0.5.0", chart.Version);
    }

    [Theory]
    [InlineData(".tgz")]
    [InlineData(".tar.gz")]
    public async Task LoadAsync_RootedArchiveRoundTripsChartContent(string extension)
    {
        var chartDir = CreateArchiveLayoutChartDir("source-folder");
        var archivePath = Path.Combine(_tempDir, "roundtrip-chart-1.2.3" + extension);
        CreateTgz(chartDir, archivePath, "roundtrip-chart");

        var chart = await HelmChartLoader.LoadAsync(archivePath, CancellationToken.None);

        Assert.Equal("roundtrip-chart", chart.Name);
        Assert.Equal("1.2.3", chart.Version);
        Assert.Contains("templates/deployment.yaml", chart.Templates.Keys);
        Assert.Single(chart.Crds);
        Assert.Equal([0, 1, 2, 127, 128, 255], chart.Files["files/payload.bin"]);
        Assert.True(chart.Subcharts.ContainsKey("child"));
        Assert.Equal("child", chart.Subcharts["child"].Name);
    }

    [Theory]
    [InlineData("../evil.txt", ".tgz")]
    [InlineData("/absolute.txt", ".tgz")]
    [InlineData("C:/absolute.txt", ".tgz")]
    [InlineData("safe/../evil.txt", ".tar.gz")]
    [InlineData("safe//evil.txt", ".tar.gz")]
    public async Task LoadAsync_RejectsUnsafeArchiveEntryNames(string unsafeEntryName, string extension)
    {
        var archivePath = Path.Combine(_tempDir, "unsafe" + extension);
        CreateArchive(
            archivePath,
            ("safe/Chart.yaml", Encoding.UTF8.GetBytes("""
                apiVersion: v2
                name: safe
                version: 1.0.0
                """)),
            (unsafeEntryName, Encoding.UTF8.GetBytes("evil")));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => HelmChartLoader.LoadAsync(archivePath, CancellationToken.None));
        Assert.Contains("unsafe", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadAsync_PreservesRawFileBytes(bool useArchive)
    {
        var chartDir = CreateChartDir("binary-files", "1.0.0");
        var expected = new byte[] { 0x00, 0x7F, 0x80, 0xC3, 0x28, 0xFF };
        await File.WriteAllBytesAsync(Path.Combine(chartDir, "payload.bin"), expected);
        await File.WriteAllTextAsync(
            Path.Combine(chartDir, "templates", "deployment.yaml"),
            """
            apiVersion: v1
            kind: Secret
            metadata:
              name: binary-files
            data:
              payload: {{ .Files.GetBytes "payload.bin" | b64enc }}
            """);

        var chartPath = chartDir;
        if (useArchive)
        {
            chartPath = Path.Combine(_tempDir, "binary-files-1.0.0.tgz");
            CreateTgz(chartDir, chartPath);
        }

        var chart = await HelmChartLoader.LoadAsync(chartPath, CancellationToken.None);
        var renderer = new HelmTemplateRenderer(
            chart,
            "binary-files",
            "default",
            new Dictionary<string, object?>());

        Assert.Equal(expected, chart.Files["payload.bin"]);
        Assert.Contains(
            $"payload: {Convert.ToBase64String(expected)}",
            renderer.Render());
    }

    [Fact]
    public async Task LoadAsync_SkipsTemplatesDirectory()
    {
        var chartDir = CreateChartDir("my-app", "1.0.0");
        File.WriteAllText(Path.Combine(chartDir, "README.md"), "# My App");
        File.WriteAllText(Path.Combine(chartDir, "templates", "NOTES.txt"), "Thank you!");

        var chart = await HelmChartLoader.LoadAsync(chartDir, CancellationToken.None);

        Assert.True(chart.Templates.ContainsKey("templates/NOTES.txt"));
    }

    [Fact]
    public async Task LoadAsync_ParsesLockEntries()
    {
        var chartDir = CreateChartDir("lock-test", "1.0.0");
        File.WriteAllText(Path.Combine(chartDir, "Chart.lock"), """
            dependencies:
            - name: redis
              version: 17.0.0
              repository: https://charts.bitnami.com/bitnami
              digest: sha256:abc123
            generated: "2024-01-01T00:00:00Z"
            """);

        var chart = await HelmChartLoader.LoadAsync(chartDir, CancellationToken.None);

        Assert.Single(chart.LockEntries);
        Assert.Equal("redis", chart.LockEntries[0].Name);
        Assert.Equal("17.0.0", chart.LockEntries[0].Version);
        Assert.Equal("sha256:abc123", chart.LockEntries[0].Digest);
    }

    private string CreateChartDir(string name, string version, string? appVersion = null, string? description = null)
    {
        var chartDir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(chartDir);
        Directory.CreateDirectory(Path.Combine(chartDir, "templates"));

        var yaml = $"""
            apiVersion: v2
            name: {name}
            version: {version}
            """;
        if (appVersion is not null) yaml += $"\nappVersion: {appVersion}";
        if (description is not null) yaml += $"\ndescription: {description}";

        File.WriteAllText(Path.Combine(chartDir, "Chart.yaml"), yaml);
        File.WriteAllText(Path.Combine(chartDir, "values.yaml"), "replicaCount: 1\n");
        File.WriteAllText(Path.Combine(chartDir, "templates", "deployment.yaml"),
            "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: test\n");

        return chartDir;
    }

    private string CreateArchiveLayoutChartDir(string directoryName)
    {
        var chartDir = Path.Combine(_tempDir, directoryName);
        Directory.CreateDirectory(chartDir);
        Directory.CreateDirectory(Path.Combine(chartDir, "templates"));
        Directory.CreateDirectory(Path.Combine(chartDir, "crds"));
        Directory.CreateDirectory(Path.Combine(chartDir, "files"));
        Directory.CreateDirectory(Path.Combine(chartDir, "charts", "child"));
        Directory.CreateDirectory(Path.Combine(chartDir, "empty-dir"));

        File.WriteAllText(Path.Combine(chartDir, "Chart.yaml"), """
            apiVersion: v2
            name: roundtrip-chart
            version: 1.2.3
            """);
        File.WriteAllText(Path.Combine(chartDir, "values.yaml"), "replicaCount: 1\n");
        File.WriteAllText(Path.Combine(chartDir, "templates", "deployment.yaml"), "kind: Deployment\n");
        File.WriteAllText(Path.Combine(chartDir, "crds", "widgets.yaml"), """
            apiVersion: apiextensions.k8s.io/v1
            kind: CustomResourceDefinition
            metadata:
              name: widgets.example.com
            """);
        File.WriteAllText(Path.Combine(chartDir, "charts", "child", "Chart.yaml"), """
            apiVersion: v2
            name: child
            version: 0.1.0
            """);
        File.WriteAllText(Path.Combine(chartDir, "charts", "child", "values.yaml"), "enabled: true\n");
        File.WriteAllBytes(Path.Combine(chartDir, "files", "payload.bin"), [0, 1, 2, 127, 128, 255]);

        return chartDir;
    }

    private static void CreateTgz(string sourceDir, string tgzPath)
    {
        using var fileStream = File.Create(tgzPath);
        using var gzip = new GZipStream(fileStream, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip);

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            tar.WriteEntry(file, relativePath);
        }
    }

    private static void CreateTgz(string sourceDir, string tgzPath, string archiveRoot)
    {
        using var fileStream = File.Create(tgzPath);
        using var gzip = new GZipStream(fileStream, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip);

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            tar.WriteEntry(file, $"{archiveRoot}/{relativePath}");
        }
    }

    private static void CreateArchive(string path, params (string Name, byte[] Content)[] entries)
    {
        using var fileStream = File.Create(path);
        using var gzip = new GZipStream(fileStream, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip);

        foreach (var (name, content) in entries)
        {
            var entry = new GnuTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(content)
            };
            tar.WriteEntry(entry);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }
}
