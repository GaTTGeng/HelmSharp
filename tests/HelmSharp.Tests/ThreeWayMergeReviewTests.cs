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
    public void MergeResource_PreservesControllerFinalizerWhenChartOmitsList()
    {
        var merged = ThreeWayMerge.MergeResource(
            "metadata:\n  finalizers:\n  - chart.example/owned\n",
            "metadata:\n  labels:\n    app: chart\n",
            "metadata:\n  finalizers:\n  - chart.example/owned\n  - controller.example/guard\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var metadata = (IDictionary<string, object?>)document["metadata"]!;
        Assert.Equal(["controller.example/guard"], (IList<object?>)metadata["finalizers"]!);
    }

    [Fact]
    public void MergeResource_PreservesAdmissionAddedInitContainerWhenChartOmitsList()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  initContainers:\n  - name: chart\n    image: chart:v1\n",
            "spec:\n  containers:\n  - name: app\n    image: app:v1\n",
            "spec:\n  initContainers:\n  - name: chart\n    image: chart:v1\n  - name: injected\n    image: webhook:v1\n  containers:\n  - name: app\n    image: app:v1\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var initContainers = (IList<object?>)spec["initContainers"]!;
        Assert.Single(initContainers);
        Assert.Equal("injected", ((IDictionary<string, object?>)initContainers[0]!)["name"]);
    }

    [Fact]
    public void MergeResource_PreservesInjectedWebhookCaBundleWhenAnotherWebhookFieldChanges()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: admissionregistration.k8s.io/v1\nkind: MutatingWebhookConfiguration\nwebhooks:\n- name: mutate.example\n  failurePolicy: Ignore\n  clientConfig:\n    service:\n      name: hook\n",
            "apiVersion: admissionregistration.k8s.io/v1\nkind: MutatingWebhookConfiguration\nwebhooks:\n- name: mutate.example\n  failurePolicy: Fail\n  clientConfig:\n    service:\n      name: hook\n",
            "apiVersion: admissionregistration.k8s.io/v1\nkind: MutatingWebhookConfiguration\nwebhooks:\n- name: mutate.example\n  failurePolicy: Ignore\n  clientConfig:\n    service:\n      name: hook\n    caBundle: injected\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var webhook = (IDictionary<string, object?>)((IList<object?>)document["webhooks"]!)[0]!;
        var clientConfig = (IDictionary<string, object?>)webhook["clientConfig"]!;
        Assert.Equal("Fail", webhook["failurePolicy"]);
        Assert.Equal("injected", clientConfig["caBundle"]);
    }

    [Fact]
    public void MergeResource_PreservesControllerAddedOwnerReferenceWhenAnotherOwnerChanges()
    {
        var merged = ThreeWayMerge.MergeResource(
            "metadata:\n  ownerReferences:\n  - uid: chart-owner\n    name: chart\n    controller: true\n",
            "metadata:\n  ownerReferences:\n  - uid: chart-owner\n    name: chart-v2\n    controller: true\n",
            "metadata:\n  ownerReferences:\n  - uid: chart-owner\n    name: chart\n    controller: true\n  - uid: operator-owner\n    name: operator\n    controller: false\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var metadata = (IDictionary<string, object?>)document["metadata"]!;
        var owners = (IList<object?>)metadata["ownerReferences"]!;

        Assert.Equal(2, owners.Count);
        Assert.Equal("chart-v2", ((IDictionary<string, object?>)owners[0]!)["name"]);
        Assert.Equal("operator-owner", ((IDictionary<string, object?>)owners[1]!)["uid"]);
    }

    [Fact]
    public void MergeResource_PreservesControllerAddedWebhookMatchConditionWhenAnotherFieldChanges()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: admissionregistration.k8s.io/v1\nkind: ValidatingWebhookConfiguration\nwebhooks:\n- name: validate.example\n  failurePolicy: Ignore\n  matchConditions:\n  - name: chart-condition\n    expression: request.userInfo.username != ''\n",
            "apiVersion: admissionregistration.k8s.io/v1\nkind: ValidatingWebhookConfiguration\nwebhooks:\n- name: validate.example\n  failurePolicy: Fail\n  matchConditions:\n  - name: chart-condition\n    expression: request.userInfo.username != ''\n",
            "apiVersion: admissionregistration.k8s.io/v1\nkind: ValidatingWebhookConfiguration\nwebhooks:\n- name: validate.example\n  failurePolicy: Ignore\n  matchConditions:\n  - name: chart-condition\n    expression: request.userInfo.username != ''\n  - name: controller-condition\n    expression: request.operation == 'CREATE'\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var webhook = (IDictionary<string, object?>)((IList<object?>)document["webhooks"]!)[0]!;
        var conditions = (IList<object?>)webhook["matchConditions"]!;

        Assert.Equal("Fail", webhook["failurePolicy"]);
        Assert.Equal(2, conditions.Count);
        Assert.Equal("controller-condition", ((IDictionary<string, object?>)conditions[1]!)["name"]);
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
    public void MergeResource_PreservesLiveDeletionFromUnchangedCustomSet()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  values:\n  - keep\n  - removed\n",
            "spec:\n  values:\n  - keep\n  - removed\n",
            "spec:\n  values:\n  - keep\n",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["spec.values"] = Array.Empty<string>()
            });

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        Assert.Equal(["keep"], (IList<object?>)spec["values"]!);
    }

    [Fact]
    public void MergeResource_PreservesSmallLiveNumericDrift()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  threshold: 0\n",
            "spec:\n  threshold: 0\n",
            "spec:\n  threshold: 0.00000000005\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        Assert.Equal(0.00000000005d, Assert.IsType<double>(spec["threshold"]));
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
