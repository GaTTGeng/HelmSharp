using HelmSharp.Chart;
using HelmSharp.Kube;

namespace HelmSharp.Tests;

/// <summary>Three-way Kubernetes manifest merge behavior for release upgrades.</summary>
public sealed class ThreeWayMergeTests
{
    [Fact]
    public void MergeResource_PreservesLiveOnlyFieldsAndChartChangesAndRemovals()
    {
        var merged = ThreeWayMerge.MergeResource(
            """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data:
              chartOwned: before
              chartRemoved: obsolete
            """,
            """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data:
              chartOwned: after
            """,
            """
            apiVersion: v1
            kind: ConfigMap
            metadata:
              name: settings
            data:
              chartOwned: before
              chartRemoved: obsolete
              operatorAdded: retain
            """);

        var document = HelmYaml.DeserializeDictionary(merged);
        var data = (IDictionary<string, object?>)document["data"]!;
        Assert.Equal("after", data["chartOwned"]);
        Assert.False(data.ContainsKey("chartRemoved"));
        Assert.Equal("retain", data["operatorAdded"]);
    }

    [Fact]
    public void MergeResource_PreservesLiveValueWhenChartDidNotChangeIt()
    {
        var merged = ThreeWayMerge.MergeResource(
            "data:\n  replicas: 2\n",
            "data:\n  replicas: 2\n",
            "data:\n  replicas: 5\n");

        var document = HelmYaml.DeserializeDictionary(merged);
        var data = (IDictionary<string, object?>)document["data"]!;
        Assert.Equal(5L, data["replicas"]);
    }

    [Fact]
    public void MergeResource_UsesChartValueWhenChartAndLiveBothChangedTheField()
    {
        var merged = ThreeWayMerge.MergeResource(
            "data:\n  marker: before\n",
            "data:\n  marker: chart\n",
            "data:\n  marker: operator\n");

        var document = HelmYaml.DeserializeDictionary(merged);
        var data = (IDictionary<string, object?>)document["data"]!;
        Assert.Equal("chart", data["marker"]);
    }

    [Fact]
    public void MergeResource_PreservesLiveDeletionWhenChartValueIsUnchanged()
    {
        var merged = ThreeWayMerge.MergeResource(
            "data:\n  operatorRemoved: value\n",
            "data:\n  operatorRemoved: value\n",
            "data: {}\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var data = (IDictionary<string, object?>)document["data"]!;
        Assert.False(data.ContainsKey("operatorRemoved"));
    }

    [Fact]
    public void MergeResource_ReconcilesChildrenWhenChartRemovesAMapping()
    {
        var merged = ThreeWayMerge.MergeResource(
            """
            metadata:
              name: settings
              annotations:
                chart-owned: unchanged
            """,
            """
            metadata:
              name: settings
            """,
            """
            metadata:
              name: settings
              annotations:
                chart-owned: unchanged
                operator-owned: retain
            """);

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var metadata = (IDictionary<string, object?>)document["metadata"]!;
        var annotations = (IDictionary<string, object?>)metadata["annotations"]!;
        Assert.False(annotations.ContainsKey("chart-owned"));
        Assert.Equal("retain", annotations["operator-owned"]);
    }

    [Fact]
    public void MergeResource_ScopesKubernetesManagedFieldExclusionsToTheirSchemaPaths()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: example.com/v1\nkind: Thing\nmetadata:\n  name: thing\n",
            "apiVersion: example.com/v1\nkind: Thing\nmetadata:\n  name: thing\n",
            """
            apiVersion: example.com/v1
            kind: Thing
            metadata:
              name: thing
            data:
              status: operator-data
            spec:
              uid: user-field
            status:
              phase: Running
            """);

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var data = (IDictionary<string, object?>)document["data"]!;
        var spec = (IDictionary<string, object?>)document["spec"]!;
        Assert.Equal("operator-data", data["status"]);
        Assert.Equal("user-field", spec["uid"]);
        Assert.False(document.ContainsKey("status"));
    }

    [Fact]
    public void MergeResource_RemovesChartSuppliedClusterManagedFields()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: example.com/v1\nkind: Thing\nmetadata:\n  name: thing\n",
            """
            apiVersion: example.com/v1
            kind: Thing
            metadata:
              name: thing
              resourceVersion: chart-version
              uid: chart-uid
              generation: 99
              creationTimestamp: chart-time
              managedFields: []
              selfLink: chart-link
            status:
              phase: chart-status
            """,
            """
            apiVersion: example.com/v1
            kind: Thing
            metadata:
              name: thing
              resourceVersion: live-version
              uid: live-uid
            status:
              phase: Running
            """);

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var metadata = (IDictionary<string, object?>)document["metadata"]!;
        Assert.Equal("thing", metadata["name"]);
        Assert.DoesNotContain("resourceVersion", metadata.Keys);
        Assert.DoesNotContain("uid", metadata.Keys);
        Assert.DoesNotContain("generation", metadata.Keys);
        Assert.DoesNotContain("creationTimestamp", metadata.Keys);
        Assert.DoesNotContain("managedFields", metadata.Keys);
        Assert.DoesNotContain("selfLink", metadata.Keys);
        Assert.False(document.ContainsKey("status"));
    }

    [Fact]
    public void MergeResource_PreservesCaseSensitiveKubernetesMapKeys()
    {
        var merged = ThreeWayMerge.MergeResource(
            "data:\n  FOO: upper\n  foo: lower\n",
            "data:\n  FOO: upper\n  foo: lower\n",
            "data:\n  FOO: upper\n  foo: lower\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var data = (IDictionary<string, object?>)document["data"]!;
        Assert.Equal(2, data.Count);
        Assert.Equal("upper", data["FOO"]);
        Assert.Equal("lower", data["foo"]);
    }
}
