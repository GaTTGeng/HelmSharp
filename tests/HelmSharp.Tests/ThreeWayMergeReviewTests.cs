using HelmSharp.Chart;
using HelmSharp.Kube;

namespace HelmSharp.Tests;

/// <summary>Regression coverage for reviewed three-way list merge behavior.</summary>
public sealed class ThreeWayMergeReviewTests
{
    [Fact]
    public void MergeResource_DropsAllocatedServiceFieldsWhenChangingToClusterIP()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: v1\nkind: Service\nspec:\n  type: LoadBalancer\n  externalTrafficPolicy: Local\n  healthCheckNodePort: 30001\n  ports:\n  - port: 80\n    targetPort: 8080\n    nodePort: 30080\n",
            "apiVersion: v1\nkind: Service\nspec:\n  type: ClusterIP\n  ports:\n  - port: 80\n    targetPort: 8080\n",
            "apiVersion: v1\nkind: Service\nspec:\n  type: LoadBalancer\n  externalTrafficPolicy: Local\n  allocateLoadBalancerNodePorts: true\n  healthCheckNodePort: 30001\n  clusterIP: 10.0.0.20\n  clusterIPs:\n  - 10.0.0.20\n  ports:\n  - port: 80\n    targetPort: 8080\n    nodePort: 30080\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var port = (IDictionary<string, object?>)((IList<object?>)spec["ports"]!)[0]!;

        Assert.Equal("ClusterIP", spec["type"]);
        Assert.False(spec.ContainsKey("healthCheckNodePort"));
        Assert.False(spec.ContainsKey("allocateLoadBalancerNodePorts"));
        Assert.False(port.ContainsKey("nodePort"));
    }

    [Fact]
    public void MergeResource_MergesValidationActionsAsASet()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: admissionregistration.k8s.io/v1\nkind: ValidatingAdmissionPolicyBinding\nspec:\n  validationActions:\n  - Deny\n",
            "apiVersion: admissionregistration.k8s.io/v1\nkind: ValidatingAdmissionPolicyBinding\nspec:\n  validationActions:\n  - Warn\n",
            "apiVersion: admissionregistration.k8s.io/v1\nkind: ValidatingAdmissionPolicyBinding\nspec:\n  validationActions:\n  - Deny\n  - Audit\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        Assert.Equal(["Warn", "Audit"], ((IList<object?>)spec["validationActions"]!).Cast<string>());
    }

    [Fact]
    public void MergeResource_ReplacesBuiltInAtomicParamRefAsAWholeMap()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: admissionregistration.k8s.io/v1\nkind: ValidatingAdmissionPolicyBinding\nspec:\n  paramRef:\n    name: policy-params\n    namespace: before\n",
            "apiVersion: admissionregistration.k8s.io/v1\nkind: ValidatingAdmissionPolicyBinding\nspec:\n  paramRef:\n    name: policy-params\n    namespace: after\n",
            "apiVersion: admissionregistration.k8s.io/v1\nkind: ValidatingAdmissionPolicyBinding\nspec:\n  paramRef:\n    selector:\n      matchLabels:\n        app: operator\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var paramRef = (IDictionary<string, object?>)spec["paramRef"]!;
        Assert.Equal("policy-params", paramRef["name"]);
        Assert.Equal("after", paramRef["namespace"]);
        Assert.False(paramRef.ContainsKey("selector"));
    }

    [Fact]
    public void MergeResource_DropsAllocatedServiceFieldsWhenChangingToExternalName()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: v1\nkind: Service\nspec:\n  type: NodePort\n  ports:\n  - port: 80\n    nodePort: 30080\n",
            "apiVersion: v1\nkind: Service\nspec:\n  type: ExternalName\n  externalName: example.test\n  ports:\n  - name: https\n    port: 443\n",
            "apiVersion: v1\nkind: Service\nspec:\n  type: NodePort\n  clusterIP: 10.0.0.20\n  clusterIPs:\n  - 10.0.0.20\n  ports:\n  - port: 80\n    nodePort: 30080\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;

        Assert.Equal("example.test", spec["externalName"]);
        Assert.False(spec.ContainsKey("clusterIP"));
        Assert.False(spec.ContainsKey("clusterIPs"));
        var port = (IDictionary<string, object?>)((IList<object?>)spec["ports"]!)[0]!;
        Assert.Equal(443L, port["port"]);
    }

    [Fact]
    public void MergeResource_ReplacesAtomicCustomMapAsOneField()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  config:\n    chart: before\n",
            "spec:\n  config:\n    chart: after\n",
            "spec:\n  config:\n    chart: before\n    injected: retained\n",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["spec.config"] = [ThreeWayMerge.AtomicListSentinel]
            });

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var config = (IDictionary<string, object?>)spec["config"]!;

        Assert.Equal("after", config["chart"]);
        Assert.False(config.ContainsKey("injected"));
    }

    [Fact]
    public void MergeResource_ReplacesNetworkPolicySelectorAsOneField()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: networking.k8s.io/v1\nkind: NetworkPolicy\nspec:\n  podSelector:\n    matchLabels:\n      app: web\n",
            "apiVersion: networking.k8s.io/v1\nkind: NetworkPolicy\nspec:\n  podSelector:\n    matchLabels:\n      app: api\n",
            "apiVersion: networking.k8s.io/v1\nkind: NetworkPolicy\nspec:\n  podSelector:\n    matchLabels:\n      app: web\n      env: prod\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var selector = (IDictionary<string, object?>)spec["podSelector"]!;
        var labels = (IDictionary<string, object?>)selector["matchLabels"]!;

        Assert.Equal("api", labels["app"]);
        Assert.False(labels.ContainsKey("env"));
    }

    [Fact]
    public void MergeResource_MatchesSchemaPathsWithDottedDynamicMapKeys()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  tenants:\n    example.com:\n      rules:\n      - name: chart\n        value: before\n",
            "spec:\n  tenants:\n    example.com:\n      rules:\n      - name: chart\n        value: after\n",
            "spec:\n  tenants:\n    example.com:\n      rules:\n      - name: chart\n        value: before\n      - name: injected\n        value: retained\n",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["spec.tenants.*.rules"] = ["name"]
            });

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var tenants = (IDictionary<string, object?>)((IDictionary<string, object?>)document["spec"]!)["tenants"]!;
        var tenant = (IDictionary<string, object?>)tenants["example.com"]!;
        var rules = (IList<object?>)tenant["rules"]!;

        Assert.Equal(2, rules.Count);
        Assert.Equal("after", ((IDictionary<string, object?>)rules[0]!)["value"]);
        Assert.Equal("injected", ((IDictionary<string, object?>)rules[1]!)["name"]);
    }

    [Fact]
    public void MergeResource_UsesCustomSchemaDefaultsForListMapKeys()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: example.com/v1\nkind: Widget\nspec:\n  rules:\n  - port: 53\n    value: before\n",
            "apiVersion: example.com/v1\nkind: Widget\nspec:\n  rules:\n  - port: 53\n    value: after\n",
            "apiVersion: example.com/v1\nkind: Widget\nspec:\n  rules:\n  - port: 53\n    protocol: UDP\n    value: before\n  - port: 54\n    protocol: UDP\n    value: injected\n",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["spec.rules"] = ["port", "protocol"]
            },
            customListMapKeyDefaults: new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["spec.rules"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["protocol"] = "\"UDP\""
                }
            });

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var rules = (IList<object?>)spec["rules"]!;

        Assert.Equal(2, rules.Count);
        Assert.Equal("after", ((IDictionary<string, object?>)rules[0]!)["value"]);
        Assert.Equal("injected", ((IDictionary<string, object?>)rules[1]!)["value"]);
    }

    [Fact]
    public void GetListMapKeySets_UsesAtomicDefaultsWhenCustomSchemaHasNoPath()
    {
        Assert.Empty(ThreeWayMerge.GetListMapKeySets(
            "spec.containers",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)));
    }

    [Fact]
    public void MergeResource_ReplacesAtomicCustomListInsteadOfUsingBuiltInMapKeys()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: example.com/v1\nkind: Widget\nspec:\n  containers:\n  - name: chart\n    value: before\n",
            "apiVersion: example.com/v1\nkind: Widget\nspec:\n  containers:\n  - name: chart\n    value: after\n",
            "apiVersion: example.com/v1\nkind: Widget\nspec:\n  containers:\n  - name: chart\n    value: before\n  - name: injected\n    value: retained\n",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["spec.containers"] = [ThreeWayMerge.AtomicListSentinel]
            });

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var containers = (IList<object?>)spec["containers"]!;

        Assert.Single(containers);
        Assert.Equal("after", ((IDictionary<string, object?>)containers[0]!)["value"]);
    }

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
    public void MergeResource_PreservesControllerAddedReadinessGate()
    {
        var merged = ThreeWayMerge.MergeResource(
            "spec:\n  template:\n    spec:\n      readinessGates:\n      - conditionType: chart.example/Ready\n",
            "spec:\n  template:\n    spec:\n      readinessGates:\n      - conditionType: chart.example/Ready\n",
            "spec:\n  template:\n    spec:\n      readinessGates:\n      - conditionType: chart.example/Ready\n      - conditionType: controller.example/Ready\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var spec = (IDictionary<string, object?>)document["spec"]!;
        var template = (IDictionary<string, object?>)spec["template"]!;
        var podSpec = (IDictionary<string, object?>)template["spec"]!;
        var gates = (IList<object?>)podSpec["readinessGates"]!;

        Assert.Equal(2, gates.Count);
        Assert.Equal("controller.example/Ready", ((IDictionary<string, object?>)gates[1]!)["conditionType"]);
    }

    [Fact]
    public void MergeResource_PreservesLiveRootStatusWhenChartOmitsStatusSubresourceField()
    {
        var merged = ThreeWayMerge.MergeResource(
            "status:\n  phase: previous\n",
            "spec:\n  replicas: 1\n",
            "status:\n  phase: live\n",
            preserveRootStatus: true);

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var status = (IDictionary<string, object?>)document["status"]!;

        Assert.Equal("live", status["phase"]);
    }

    [Fact]
    public void MergeResource_AllowsChartToUpdateRootStatusWithoutStatusSubresource()
    {
        var merged = ThreeWayMerge.MergeResource(
            "status:\n  phase: previous\n",
            "status:\n  phase: chart\n",
            "status:\n  phase: live\n",
            preserveRootStatus: false);

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var status = (IDictionary<string, object?>)document["status"]!;

        Assert.Equal("chart", status["phase"]);
    }

    [Fact]
    public void MergeResource_MergesServiceAccountSecretsByName()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: v1\nkind: ServiceAccount\nsecrets:\n- name: chart-old\n",
            "apiVersion: v1\nkind: ServiceAccount\nsecrets:\n- name: chart-new\n",
            "apiVersion: v1\nkind: ServiceAccount\nsecrets:\n- name: chart-old\n- name: controller-token\n");

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var secrets = (IList<object?>)document["secrets"]!;
        Assert.Equal(2, secrets.Count);
        Assert.Equal("chart-new", ((IDictionary<string, object?>)secrets[0]!)["name"]);
        Assert.Equal("controller-token", ((IDictionary<string, object?>)secrets[1]!)["name"]);
    }

    [Fact]
    public void MergeResource_PreservesControllerOwnerReferenceForCustomResource()
    {
        var merged = ThreeWayMerge.MergeResource(
            "apiVersion: example.com/v1\nkind: Widget\nmetadata:\n  ownerReferences:\n  - uid: chart-owner\n    name: chart\nspec:\n  values:\n  - old\n",
            "apiVersion: example.com/v1\nkind: Widget\nmetadata:\n  ownerReferences:\n  - uid: chart-owner\n    name: chart-v2\nspec:\n  values:\n  - new\n",
            "apiVersion: example.com/v1\nkind: Widget\nmetadata:\n  ownerReferences:\n  - uid: chart-owner\n    name: chart\n  - uid: controller-owner\n    name: controller\nspec:\n  values:\n  - old\n",
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["spec.values"] = Array.Empty<string>()
            });

        var document = HelmYaml.DeserializeDictionary(merged, StringComparer.Ordinal);
        var metadata = (IDictionary<string, object?>)document["metadata"]!;
        var owners = (IList<object?>)metadata["ownerReferences"]!;
        Assert.Equal(2, owners.Count);
        Assert.Equal("chart-v2", ((IDictionary<string, object?>)owners[0]!)["name"]);
        Assert.Equal("controller-owner", ((IDictionary<string, object?>)owners[1]!)["uid"]);
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
