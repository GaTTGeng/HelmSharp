using HelmSharp.Chart;
using HelmSharp.Engine;

namespace HelmSharp.Tests;

public class ApiVersionSetTests
{
    [Fact]
    public void Constructor_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ApiVersionSet(null!));
    }

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

    // ────────────────────────────────────────────────────────────
    //  kubeVersion-aware API version filtering (#49)
    // ────────────────────────────────────────────────────────────

    [Fact]
    public void NullKubeVersion_IncludesAllApiVersions()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            v1: {{ .Capabilities.APIVersions.Has "v1" }}
            extensions-v1beta1: {{ .Capabilities.APIVersions.Has "extensions/v1beta1" }}
            batch-v1beta1: {{ .Capabilities.APIVersions.Has "batch/v1beta1" }}
            """;

        // null kubeVersion → backward-compatible: include all
        var renderer = new HelmTemplateRenderer(chart, "rel", "ns",
            new Dictionary<string, object?>(), kubeVersion: null, apiVersions: null, isUpgrade: false);
        var result = renderer.Render();

        Assert.Contains("v1: true", result);
        Assert.Contains("extensions-v1beta1: true", result);
        Assert.Contains("batch-v1beta1: true", result);
    }

    [Fact]
    public void KubeVersion122_ExcludesApisRemovedBy122()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            v1: {{ .Capabilities.APIVersions.Has "v1" }}
            extensions-v1beta1: {{ .Capabilities.APIVersions.Has "extensions/v1beta1" }}
            batch-v1beta1: {{ .Capabilities.APIVersions.Has "batch/v1beta1" }}
            autoscaling-v2beta2: {{ .Capabilities.APIVersions.Has "autoscaling/v2beta2" }}
            """;

        // v1.22 removes extensions/v1beta1 but NOT batch/v1beta1 (removed 1.25) or autoscaling/v2beta2 (removed 1.26)
        var renderer = new HelmTemplateRenderer(chart, "rel", "ns",
            new Dictionary<string, object?>(), kubeVersion: "v1.22.0", apiVersions: null, isUpgrade: false);
        var result = renderer.Render();

        Assert.Contains("v1: true", result);
        Assert.Contains("extensions-v1beta1: false", result); // removed in 1.22
        Assert.Contains("batch-v1beta1: true", result);       // still available in 1.22
        Assert.Contains("autoscaling-v2beta2: true", result); // still available in 1.22
    }

    [Fact]
    public void KubeVersion125_ExcludesApisRemovedBy125()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            batch-v1beta1: {{ .Capabilities.APIVersions.Has "batch/v1beta1" }}
            events-v1beta1: {{ .Capabilities.APIVersions.Has "events.k8s.io/v1beta1" }}
            policy-v1beta1: {{ .Capabilities.APIVersions.Has "policy/v1beta1" }}
            rbac-v1alpha1: {{ .Capabilities.APIVersions.Has "rbac.authorization.k8s.io/v1alpha1" }}
            extensions-v1beta1: {{ .Capabilities.APIVersions.Has "extensions/v1beta1" }}
            """;

        // v1.25 removes batch/v1beta1, events/v1beta1, policy/v1beta1, rbac/v1alpha1
        // (extensions/v1beta1 was already removed in 1.22)
        var renderer = new HelmTemplateRenderer(chart, "rel", "ns",
            new Dictionary<string, object?>(), kubeVersion: "v1.25.0", apiVersions: null, isUpgrade: false);
        var result = renderer.Render();

        Assert.Contains("batch-v1beta1: false", result);
        Assert.Contains("events-v1beta1: false", result);
        Assert.Contains("policy-v1beta1: false", result);
        Assert.Contains("rbac-v1alpha1: false", result);
        Assert.Contains("extensions-v1beta1: false", result);
    }

    [Fact]
    public void KubeVersion130_ExcludesEvenMore()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            autoscaling-v2beta2: {{ .Capabilities.APIVersions.Has "autoscaling/v2beta2" }}
            autoscaling-v2beta1: {{ .Capabilities.APIVersions.Has "autoscaling/v2beta1" }}
            flowcontrol-v1beta2: {{ .Capabilities.APIVersions.Has "flowcontrol.apiserver.k8s.io/v1beta2" }}
            storage-v1alpha1: {{ .Capabilities.APIVersions.Has "storage.k8s.io/v1alpha1" }}
            storage-v1: {{ .Capabilities.APIVersions.Has "storage.k8s.io/v1" }}
            networking-v1alpha1: {{ .Capabilities.APIVersions.Has "networking.k8s.io/v1alpha1" }}
            batch-v1beta1: {{ .Capabilities.APIVersions.Has "batch/v1beta1" }}
            """;

        // v1.30 excludes everything removed ≤1.30:
        // 1.26: autoscaling/v2beta2, autoscaling/v2beta1
        // 1.27: storage/v1alpha1, networking/v1alpha1
        // 1.29: flowcontrol/v1beta2
        // 1.25: batch/v1beta1 (already excluded)
        var renderer = new HelmTemplateRenderer(chart, "rel", "ns",
            new Dictionary<string, object?>(), kubeVersion: "v1.30.0", apiVersions: null, isUpgrade: false);
        var result = renderer.Render();

        Assert.Contains("autoscaling-v2beta2: false", result);
        Assert.Contains("autoscaling-v2beta1: false", result);
        Assert.Contains("flowcontrol-v1beta2: false", result);
        Assert.Contains("storage-v1alpha1: false", result);
        Assert.Contains("networking-v1alpha1: false", result);
        Assert.Contains("batch-v1beta1: false", result);
        Assert.Contains("storage-v1: true", result);   // still available
    }

    [Fact]
    public void KubeVersion116_ExcludesRemovedBetaApis()
    {
        var chart = new HelmChart { Name = "test", Version = "1.0.0", ValuesYaml = "" };
        chart.Templates["templates/test.yaml"] = """
            apps-v1beta1: {{ .Capabilities.APIVersions.Has "apps/v1beta1" }}
            apps-v1beta2: {{ .Capabilities.APIVersions.Has "apps/v1beta2" }}
            """;

        // v1.16 removed apps/v1beta1 and apps/v1beta2
        var renderer = new HelmTemplateRenderer(chart, "rel", "ns",
            new Dictionary<string, object?>(), kubeVersion: "v1.16.0", apiVersions: null, isUpgrade: false);
        var result = renderer.Render();

        Assert.Contains("apps-v1beta1: false", result);
        Assert.Contains("apps-v1beta2: false", result);
    }
}
