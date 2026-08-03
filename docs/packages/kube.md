# HelmSharp.Kube

`HelmSharp.Kube` is the lower-level Kubernetes layer. Install it when YAML already exists and your code needs managed apply, delete, resource identity, or selective readiness waiting.

```powershell
dotnet add package HelmSharp.Kube --version 1.3.1
```

| Type | Use |
| --- | --- |
| `KubernetesManifestApplier` | Split multi-document YAML and apply or delete every resource. |
| `KubernetesResourceWaiter` | Wait for supported workloads to become ready. |
| `ManifestIdentity` | Parse API version, kind, name, and namespace from a YAML document. |

The package uses the Kubernetes .NET client. It does not merge chart values, execute template hooks, or persist Helm revisions. Use it directly for a controller or custom deployment workflow; use `HelmSharp.Action` when those lifecycle responsibilities belong together.

The waiter covers common workload kinds, not arbitrary CRD readiness. See [Apply manifests directly](../guide/kubernetes-operations.md) and the [generated Kube API](../api/generated/kube.md).
