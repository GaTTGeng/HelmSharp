# Apply manifests directly

Use `HelmSharp.Kube` when your application already has multi-document Kubernetes YAML and needs lower-level apply, delete, identity, or readiness behavior. It does not create Helm release history; that is the responsibility of the higher-level release workflow.

```powershell
dotnet add package HelmSharp.Kube --version 1.3.1
```

## Apply rendered YAML

```csharp
using HelmSharp.Kube;
using k8s;

var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
using var kubernetes = new Kubernetes(config);

var applier = new KubernetesManifestApplier(
    kubernetes,
    fieldManager: "my-deployment-service");

await foreach (var resource in applier.ApplyAsync(
    manifest,
    @namespace: "platform",
    cancellationToken))
{
    Console.WriteLine($"Applied {resource}");
}
```

The applier splits YAML documents, derives each resource identity, and applies it through the Kubernetes .NET client. Give every product a stable field-manager name; it makes server-side ownership and troubleshooting more intelligible.

## Know what the namespace argument does

The namespace argument supplies a default for namespaced documents that do not declare `metadata.namespace`. An explicit namespace in the manifest wins. Cluster-scoped resources are not given a namespace.

The client resolves common resource kinds directly and discovers other API resources from the target cluster for apply and delete. If an API version has been removed or a custom resource kind is not discoverable, the operation fails with that identity in the diagnostic.

## Wait only for the readiness you need

`KubernetesResourceWaiter` observes common workload kinds: Deployments, StatefulSets, DaemonSets, ReplicaSets, Jobs, Pods, PVCs, Endpoints, and v2 HPAs. Jobs are only waited on when the caller requests it. Objects outside this set are accepted as applied; they are not proof that an operator-managed resource is ready.

For a full Helm-style lifecycle, including hooks and stored revisions, use [Install and upgrade releases](release-workflows.md). Keep direct delete operations behind the same authorization and approval path as apply.
