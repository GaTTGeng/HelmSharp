using HelmSharp.Chart;
using HelmSharp.Kube;

namespace HelmSharp.Tests;

/// <summary>Regression coverage for reviewed three-way list merge behavior.</summary>
public sealed class ThreeWayMergeReviewTests
{
    [Fact]
    public void MergeResource_PreservesPositionOfLiveOnlyInitContainer()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  initContainers:\n  - name: chart\n    image: chart:v1\n",
            "spec:\n  initContainers:\n  - name: chart\n    image: chart:v2\n",
            "spec:\n  initContainers:\n  - name: injected\n    image: webhook:v1\n  - name: chart\n    image: chart:v1\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var items = (IList<object?>)spec["initContainers"]!;
        Assert.Equal("injected", ((IDictionary<string, object?>)items[0]!)["name"]);
        Assert.Equal("chart:v2", ((IDictionary<string, object?>)items[1]!)["image"]);
    }

    [Fact]
    public void MergeResource_PreservesControllerAddedFinalizer()
    {
        var merged = ThreeWayMerge.MergeResource(
            "metadata:\n  finalizers:\n  - chart.example/old\n",
            "metadata:\n  finalizers:\n  - chart.example/new\n",
            "metadata:\n  finalizers:\n  - chart.example/old\n  - controller.example/guard\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var metadata = (IDictionary<string, object?>)document["metadata"]!;
        var finalizers = (IList<object?>)metadata["finalizers"]!;
        Assert.Equal(["chart.example/new", "controller.example/guard"], finalizers);
    }

    [Fact]
    public void MergeResource_PreservesLiveOnlyCustomListItemUnderMap()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  groups:\n    one:\n      entries:\n      - name: chart\n        value: before\n",
            "spec:\n  groups:\n    one:\n      entries:\n      - name: chart\n        value: after\n",
            "spec:\n  groups:\n    one:\n      entries:\n      - name: chart\n        value: before\n      - name: operator\n        value: retained\n",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["spec.groups.*.entries"] = ["name"]
            });

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var groups = (IDictionary<string, object?>)spec["groups"]!;
        var group = (IDictionary<string, object?>)groups["one"]!;
        var entries = (IList<object?>)group["entries"]!;
        Assert.Equal("after", ((IDictionary<string, object?>)entries[0]!)["value"]);
        Assert.Equal("operator", ((IDictionary<string, object?>)entries[1]!)["name"]);
    }

    [Fact]
    public void MergeResource_PreservesLiveOnlyCustomSetElement()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  values:\n  - old\n",
            "spec:\n  values:\n  - new\n",
            "spec:\n  values:\n  - old\n  - operator\n",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["spec.values"] = Array.Empty<string>()
            });

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        Assert.Equal(["new", "operator"], (IList<object?>)spec["values"]!);
    }

    [Fact]
    public void MergeResource_MergesCrdVersionsByName()
    {
        var merged = ThreeWayMerge.MergeResource(
            "kind: CustomResourceDefinition\nspec:\n  versions:\n  - name: v1\n    served: true\n",
            "kind: CustomResourceDefinition\nspec:\n  versions:\n  - name: v1\n    served: true\n    storage: true\n",
            "kind: CustomResourceDefinition\nspec:\n  versions:\n  - name: v1\n    served: true\n  - name: v2\n    served: true\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var versions = (IList<object?>)spec["versions"]!;
        Assert.Equal(2, versions.Count);
        Assert.Equal("v2", ((IDictionary<string, object?>)versions[1]!)["name"]);
    }
}
