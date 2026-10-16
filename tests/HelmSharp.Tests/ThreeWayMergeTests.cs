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
    public void MergeResource_RemovesEditedChildrenWhenChartRemovesTheirParentMapping()
    {
        var merged = ThreeWayMerge.MergeResource(
            "data:\n  setting: old\n  nested:\n    owned: before\n",
            "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: settings\n",
            "data:\n  setting: custom\n  nested:\n    owned: changed\n    operatorAdded: retain\n  operatorAdded: retain\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var data = (IDictionary<string, object?>)document["data"]!;
        Assert.Equal("retain", data["operatorAdded"]);
        Assert.False(data.ContainsKey("setting"));
        var nested = (IDictionary<string, object?>)data["nested"]!;
        Assert.False(nested.ContainsKey("owned"));
        Assert.Equal("retain", nested["operatorAdded"]);
    }

    [Fact]
    public void MergeResource_PreservesLiveOnlyNamedListItemsWhenChartChangesAnItem()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  template:\n    spec:\n      containers:\n      - name: app\n        image: app:v1\n",
            "spec:\n  template:\n    spec:\n      containers:\n      - name: app\n        image: app:v2\n",
            "spec:\n  template:\n    spec:\n      containers:\n      - name: app\n        image: app:v1\n      - name: injected\n        image: sidecar:v1\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var template = (IDictionary<string, object?>)spec["template"]!;
        var podSpec = (IDictionary<string, object?>)template["spec"]!;
        var containers = (IList<object?>)podSpec["containers"]!;
        Assert.Equal(2, containers.Count);
        Assert.Equal("app:v2", ((IDictionary<string, object?>)containers[0]!)["image"]);
        Assert.Equal("injected", ((IDictionary<string, object?>)containers[1]!)["name"]);
    }

    [Fact]
    public void MergeResource_PreservesLiveOnlyContainerPortsUsingKubernetesCompositeKeys()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  ports:\n  - containerPort: 8080\n    protocol: TCP\n",
            "spec:\n  ports:\n  - containerPort: 8080\n    protocol: UDP\n",
            "spec:\n  ports:\n  - containerPort: 8080\n    protocol: TCP\n  - containerPort: 9090\n    protocol: TCP\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var ports = (IList<object?>)spec["ports"]!;

        Assert.Equal(2, ports.Count);
        Assert.Equal(8080L, ((IDictionary<string, object?>)ports[0]!)["containerPort"]);
        Assert.Equal("UDP", ((IDictionary<string, object?>)ports[0]!)["protocol"]);
        Assert.Equal(9090L, ((IDictionary<string, object?>)ports[1]!)["containerPort"]);
    }

    [Fact]
    public void MergeResource_UsesCompositePortKeysWhenItemsAlsoHaveNames()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  ports:\n  - name: web\n    port: 80\n    protocol: TCP\n",
            "spec:\n  ports:\n  - name: http\n    port: 80\n    protocol: TCP\n",
            "spec:\n  ports:\n  - name: web\n    port: 80\n    protocol: TCP\n    nodePort: 30080\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var ports = (IList<object?>)spec["ports"]!;
        var port = (IDictionary<string, object?>)ports[0]!;

        Assert.Single(ports);
        Assert.Equal("http", port["name"]);
        Assert.Equal(30080L, port["nodePort"]);
    }

    [Fact]
    public void MergeResource_PreservesLiveVolumeMountFieldsWhenVolumeNameChanges()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  template:\n    spec:\n      containers:\n      - name: app\n        volumeMounts:\n        - name: old-volume\n          mountPath: /data\n          readOnly: false\n",
            "spec:\n  template:\n    spec:\n      containers:\n      - name: app\n        volumeMounts:\n        - name: new-volume\n          mountPath: /data\n          readOnly: false\n",
            "spec:\n  template:\n    spec:\n      containers:\n      - name: app\n        volumeMounts:\n        - name: old-volume\n          mountPath: /data\n          readOnly: true\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var template = (IDictionary<string, object?>)spec["template"]!;
        var podSpec = (IDictionary<string, object?>)template["spec"]!;
        var containers = (IList<object?>)podSpec["containers"]!;
        var container = (IDictionary<string, object?>)containers[0]!;
        var volumeMounts = (IList<object?>)container["volumeMounts"]!;
        var volumeMount = (IDictionary<string, object?>)volumeMounts[0]!;

        Assert.Equal("new-volume", volumeMount["name"]);
        Assert.Equal(true, volumeMount["readOnly"]);
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
    public void MergeResource_PreservesLiveStatusWhenChartOmitsIt()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: example.com/v1\nkind: Thing\nmetadata:\n  name: thing\n",
            "apiVersion: example.com/v1\nkind: Thing\nmetadata:\n  name: thing\n",
            """
            apiVersion: example.com/v1
            kind: Thing
            metadata:
              name: thing
            status:
              phase: Ready
              observedGeneration: 3
            """);

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var status = (IDictionary<string, object?>)document["status"]!;
        Assert.Equal("Ready", status["phase"]);
        Assert.Equal(3L, status["observedGeneration"]);
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

    [Fact]
    public void MergeResource_MergesDeclaredKeyedListWhenBaselineIsEmpty()
    {
        var merged = ThreeWayMerge.MergeResource(
            """
            spec:
              entries: []
            """,
            """
            spec:
              entries:
                - name: chart
                  value: desired
            """,
            """
            spec:
              entries:
                - name: operator
                  value: retained
            """,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["spec.entries"] = ["name"]
            });

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var entries = (IList<object?>)spec["entries"]!;

        Assert.Equal(2, entries.Count);
        Assert.Equal("chart", ((IDictionary<string, object?>)entries[0]!)["name"]);
        Assert.Equal("operator", ((IDictionary<string, object?>)entries[1]!)["name"]);
    }

    [Fact]
    public void MergeResource_UsesDeclaredCustomListMapKeys()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  entries:\n  - name: chart\n    value: before\n",
            "spec:\n  entries:\n  - name: chart\n    value: after\n",
            "spec:\n  entries:\n  - name: chart\n    value: before\n  - name: operator\n    value: retained\n",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["spec.entries"] = ["name"]
            });

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var entries = (IList<object?>)spec["entries"]!;
        Assert.Equal(2, entries.Count);
        Assert.Equal("after", ((IDictionary<string, object?>)entries[0]!)["value"]);
        Assert.Equal("operator", ((IDictionary<string, object?>)entries[1]!)["name"]);
    }

    [Fact]
    public void MergeResource_TreatsUnmappedCustomListsAsAtomic()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  entries:\n  - name: chart\n    value: before\n",
            "spec:\n  entries:\n  - name: chart\n    value: after\n",
            "spec:\n  entries:\n  - name: chart\n    value: before\n  - name: operator\n    value: retained\n",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var entries = (IList<object?>)spec["entries"]!;
        Assert.Single(entries);
        Assert.Equal("after", ((IDictionary<string, object?>)entries[0]!)["value"]);
    }
}
