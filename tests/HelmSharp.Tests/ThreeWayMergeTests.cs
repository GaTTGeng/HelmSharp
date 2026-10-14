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
}
