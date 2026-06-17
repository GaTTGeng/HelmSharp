using HelmSharp.Chart;
using HelmSharp.Release;

namespace HelmSharp.Tests;

public class StorageTests
{
    [Fact]
    public void HelmReleaseRecord_DefaultValues()
    {
        var record = new HelmReleaseRecord();
        Assert.Equal(string.Empty, record.Name);
        Assert.Equal("default", record.Namespace);
        Assert.Equal(0, record.Revision);
        Assert.Equal("deployed", record.Status);
        Assert.Equal(string.Empty, record.ChartName);
        Assert.Equal(string.Empty, record.ChartVersion);
        Assert.Null(record.AppVersion);
        Assert.Equal(string.Empty, record.Manifest);
        Assert.Equal(string.Empty, record.ValuesYaml);
    }

    [Fact]
    public void HelmReleaseRecord_InitProperties()
    {
        var record = new HelmReleaseRecord
        {
            Name = "my-app",
            Namespace = "production",
            Revision = 3,
            Status = "deployed",
            ChartName = "mychart",
            ChartVersion = "1.2.0",
            AppVersion = "2.0.0",
            Manifest = "apiVersion: v1\nkind: ConfigMap\n",
            ValuesYaml = "key: value",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal("my-app", record.Name);
        Assert.Equal("production", record.Namespace);
        Assert.Equal(3, record.Revision);
        Assert.Equal("deployed", record.Status);
        Assert.Equal("mychart", record.ChartName);
        Assert.Equal("1.2.0", record.ChartVersion);
        Assert.Equal("2.0.0", record.AppVersion);
        Assert.Contains("ConfigMap", record.Manifest);
        Assert.Contains("key: value", record.ValuesYaml);
    }

    [Fact]
    public void HelmReleaseRecord_StatusIsMutable()
    {
        var record = new HelmReleaseRecord
        {
            Name = "app",
            Status = "deployed"
        };

        record.Status = "superseded";
        Assert.Equal("superseded", record.Status);
    }

    [Fact]
    public void HelmReleaseRecord_UpdatedAtIsMutable()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new HelmReleaseRecord
        {
            Name = "app",
            UpdatedAt = now
        };

        var later = now.AddHours(1);
        record.UpdatedAt = later;
        Assert.Equal(later, record.UpdatedAt);
    }

    [Fact]
    public void HelmReleaseRecord_WithLabels()
    {
        var record = new HelmReleaseRecord
        {
            Name = "app",
            Labels = new Dictionary<string, string>
            {
                ["owner"] = "helm",
                ["name"] = "app"
            }
        };

        Assert.NotNull(record.Labels);
        Assert.Equal("helm", record.Labels["owner"]);
        Assert.Equal("app", record.Labels["name"]);
    }

    [Fact]
    public void HelmReleaseRecord_Equality()
    {
        var now = DateTimeOffset.UtcNow;
        var r1 = new HelmReleaseRecord { Name = "app", Namespace = "default", Revision = 1, UpdatedAt = now };
        var r2 = new HelmReleaseRecord { Name = "app", Namespace = "default", Revision = 1, UpdatedAt = now };

        Assert.Equal(r1, r2);
    }

    [Fact]
    public void HelmChartDependency_DefaultValues()
    {
        var dep = new HelmChartDependency();
        Assert.Equal(string.Empty, dep.Name);
        Assert.Null(dep.Version);
        Assert.Null(dep.Repository);
        Assert.Null(dep.Condition);
        Assert.Null(dep.Tags);
        Assert.True(dep.Enabled);
    }

    [Fact]
    public void HelmChartDependency_InitProperties()
    {
        var dep = new HelmChartDependency
        {
            Name = "redis",
            Version = "17.0.0",
            Repository = "https://charts.bitnami.com/bitnami",
            Condition = "redis.enabled",
            Enabled = false
        };

        Assert.Equal("redis", dep.Name);
        Assert.Equal("17.0.0", dep.Version);
        Assert.Equal("https://charts.bitnami.com/bitnami", dep.Repository);
        Assert.Equal("redis.enabled", dep.Condition);
        Assert.False(dep.Enabled);
    }

    [Fact]
    public void HelmChartLockEntry_DefaultValues()
    {
        var entry = new HelmChartLockEntry();
        Assert.Equal(string.Empty, entry.Name);
        Assert.Equal(string.Empty, entry.Version);
        Assert.Null(entry.Repository);
        Assert.Null(entry.Digest);
    }

    [Fact]
    public void HelmChartLockEntry_InitProperties()
    {
        var entry = new HelmChartLockEntry
        {
            Name = "redis",
            Version = "17.0.0",
            Repository = "https://charts.bitnami.com/bitnami",
            Digest = "sha256:abc123"
        };

        Assert.Equal("redis", entry.Name);
        Assert.Equal("17.0.0", entry.Version);
        Assert.Equal("sha256:abc123", entry.Digest);
    }

    [Fact]
    public void HelmChart_DefaultValues()
    {
        var chart = new HelmChart();
        Assert.Equal(string.Empty, chart.Name);
        Assert.Equal(string.Empty, chart.Version);
        Assert.Null(chart.AppVersion);
        Assert.Null(chart.Description);
        Assert.Null(chart.Home);
        Assert.Null(chart.Type);
        Assert.False(chart.Deprecated);
        Assert.Equal(string.Empty, chart.ValuesYaml);
        Assert.Empty(chart.Dependencies);
        Assert.Empty(chart.LockEntries);
        Assert.Empty(chart.Templates);
        Assert.Empty(chart.Crds);
        Assert.Empty(chart.Subcharts);
    }

    [Fact]
    public void HelmChart_InitProperties()
    {
        var chart = new HelmChart
        {
            Name = "my-chart",
            Version = "1.0.0",
            AppVersion = "2.0.0",
            Description = "A test chart",
            Type = "application",
            KubeVersion = ">=1.20"
        };

        Assert.Equal("my-chart", chart.Name);
        Assert.Equal("1.0.0", chart.Version);
        Assert.Equal("2.0.0", chart.AppVersion);
        Assert.Equal("A test chart", chart.Description);
        Assert.Equal("application", chart.Type);
        Assert.Equal(">=1.20", chart.KubeVersion);
    }

    [Fact]
    public void HelmChart_TemplatesCollection()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0" };
        chart.Templates["templates/deployment.yaml"] = "apiVersion: apps/v1\nkind: Deployment\n";
        chart.Templates["templates/_helpers.tpl"] = "{{- define \"test.name\" -}}test{{- end -}}";

        Assert.Equal(2, chart.Templates.Count);
        Assert.True(chart.Templates.ContainsKey("templates/deployment.yaml"));
        Assert.True(chart.Templates.ContainsKey("templates/_helpers.tpl"));
    }

    [Fact]
    public void HelmChart_DependenciesCollection()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0" };
        chart.Dependencies.Add(new HelmChartDependency { Name = "redis", Version = "17.0.0" });
        chart.Dependencies.Add(new HelmChartDependency { Name = "postgres", Version = "12.0.0" });

        Assert.Equal(2, chart.Dependencies.Count);
        Assert.Equal("redis", chart.Dependencies[0].Name);
        Assert.Equal("postgres", chart.Dependencies[1].Name);
    }
}
