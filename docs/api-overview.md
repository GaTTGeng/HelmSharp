# Choose a package and API

Choose packages by the operation your application needs to perform. Rendering loads chart content and values, then returns manifest text. Release operations also require Kubernetes credentials and maintain lifecycle history.

## Choose by task
| Goal | Start with | Main types | Do not choose it when… |
| --- | --- | --- | --- |
| Render a chart | `HelmSharp.Chart` + `HelmSharp.Engine` | `HelmChartLoader`, `HelmValues`, `HelmTemplateRenderer` | The application must apply resources and record release history. |
| Expose Helm-style operations | `HelmSharp.Action` | `HelmClient`, `IHelmClient`, request objects, `CommandResult` | You only need YAML and want no Kubernetes dependencies. |
| Apply existing YAML | `HelmSharp.Kube` | `KubernetesManifestApplier`, `KubernetesResourceWaiter`, `ManifestIdentity` | You need Helm release state, hooks, or values merging. |
| Work with releases directly | `HelmSharp.Release` | Release model and Kubernetes-backed store | `HelmClient` already owns the whole workflow. |
| Maintain an HTTP chart repository | `HelmSharp.Repo` | `HelmChartRepository`, `HelmRepoIndexer`, `HelmPullRequest` | The requirement is complete OCI registry parity. |
| Transform manifest text | `HelmSharp.PostRenderer` | `IPostRenderer` | The transformation belongs in a chart template. |

## High-level client or renderer?

Use `HelmTemplateRenderer` directly when an application exposes a rendering capability. It makes the operation visible in code: load the chart, compute values, configure capabilities, and return strings.

Use `HelmClient` when the public surface deliberately resembles a Helm operation. It returns `CommandResult` for template, package, repository, dependency, and lifecycle work, so an endpoint or CLI can expose standard output, standard error, and an exit code consistently.

```csharp
var result = await client.TemplateAsync(new HelmTemplateRequest
{
    ReleaseName = "preview",
    Namespace = "platform",
    Chart = chartPath,
    ValuesFiles = ["values.production.yaml"],
    KubeVersion = "1.30.0"
}, cancellationToken);

if (!result.Succeeded)
    return Results.BadRequest(result.StandardError);

return Results.Text(result.StandardOutput, "text/yaml");
```

## Read the generated API reference effectively

The generated pages list public types, properties, and methods from the current source tree. Use the package guide to decide *which* abstraction belongs in your code, then use generated reference for parameter-level lookup.

| Package | Guide | Generated API |
| --- | --- | --- |
| `HelmSharp.Action` | [Guide](packages/action.md) | [API](api/generated/action.md) |
| `HelmSharp.Chart` | [Guide](packages/chart.md) | [API](api/generated/chart.md) |
| `HelmSharp.Engine` | [Guide](packages/engine.md) | [API](api/generated/engine.md) |
| `HelmSharp.Kube` | [Guide](packages/kube.md) | [API](api/generated/kube.md) |
| Distribution and extensions | [All package guides](api/index.md) | [All generated pages](api/index.md) |

Template helper types under `HelmSharp.Engine.Functions` and `HelmSharp.Engine.Utilities` mainly exist to implement Helm/Sprig behavior. Application code should treat `HelmTemplateRenderer` as its renderer API and consult the [function matrix](template-function-compatibility.md) before using a helper in a chart.
