# Render for a target cluster

Charts often branch on `.Capabilities.KubeVersion` or `.Capabilities.APIVersions`. A preview should describe the target cluster, not whichever machine happens to run the preview service.

## Supply capabilities explicitly

```csharp
var renderer = new HelmTemplateRenderer(
    chart,
    releaseName: "preview",
    releaseNamespace: "platform",
    values: values,
    kubeVersion: "1.30.0",
    apiVersions:
    [
        "monitoring.coreos.com/v1",
        "policy/v1"
    ],
    isUpgrade: false);

var manifests = renderer.Render();
var notes = renderer.RenderNotes();
```

`kubeVersion` determines the version exposed to `.Capabilities.KubeVersion`. `apiVersions` extends `.Capabilities.APIVersions` with APIs that are present on the target, including CRDs. Set `isUpgrade` when the template must see `.Release.IsUpgrade` rather than `.Release.IsInstall`.

## What the renderer gives a template

| Template object | Comes from |
| --- | --- |
| `.Values` | The dictionary returned by `HelmValues.BuildAsync`. |
| `.Chart` | `Chart.yaml` metadata and chart dependencies. |
| `.Release` | The release name, namespace, revision, service, and install/upgrade state passed to the renderer. |
| `.Capabilities` | The Kubernetes version and known API versions. |
| `.Files` | Files bundled in the chart, excluding templates. |
| `.Template` | The current template name and base path. |

Named templates, `include`, `tpl`, `required`, `.Files`, and the implemented Helm/Sprig functions are evaluated in-process. The [function matrix](../template-function-compatibility.md) is the authority for individual helpers; do not assume a helper is present because another Helm environment has it.

## Keep manifests and notes separate

`NOTES.txt` is information for a person after a release. It is intentionally returned by `RenderNotes()` instead of being mixed into Kubernetes YAML. Store or display it beside a preview, but never pass it to a manifest applier.

For high-level template operations, `HelmTemplateRequest` provides `KubeVersion`, `ApiVersions`, and `IsUpgrade` at the client boundary. Render notes separately when needed; template output does not include CRDs from a chart's `crds/` directory. See [Install and upgrade releases](release-workflows.md) for the mutation path.
