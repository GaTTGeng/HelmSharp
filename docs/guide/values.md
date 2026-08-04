# Values and overrides

Values are the boundary between your application and a chart. Build them once, keep the inputs that produced them, and use the same result for every preview or deployment decision that must be reproducible.

## Precedence

`HelmValues.BuildAsync` merges sources from top to bottom; a source later in the table overrides an earlier value at the same path.

| Order | Source | Typical use |
| ---: | --- | --- |
| 1 | Chart and subchart defaults | The chart's `values.yaml` files. |
| 2 | `valuesFiles` | Environment or product defaults, applied left to right. |
| 3 | `valuesContent` | YAML held by a database, API request, or generated configuration. |
| 4 | `setFileValues` | File content assigned to a value path. |
| 5 | `setStringValues` | A value that must remain a string. |
| 6 | `setValues` | Helm `--set`-style scalar overrides. |
| 7 | `setJsonValues` | A JSON object or array assigned to a path. |

For example, this preserves an image tag with leading zeroes while passing a structured service-port list:

```csharp
var license = await File.ReadAllTextAsync(licensePath, cancellationToken);

var values = await HelmValues.BuildAsync(
    chart,
    valuesFiles: ["values.base.yaml", "values.production.yaml"],
    valuesContent: """
        global:
          environment: production
        """,
    setValues: new Dictionary<string, string> { ["replicaCount"] = "3" },
    setFileValues: new Dictionary<string, string> { ["license.text"] = license },
    setStringValues: new Dictionary<string, string> { ["image.tag"] = "001" },
    setJsonValues: new Dictionary<string, string>
    {
        ["service.ports"] = """[{"name":"http","port":80}]"""
    },
    cancellationToken: cancellationToken);
```

## Choose the right override form

| Input | Use it when | Avoid it when |
| --- | --- | --- |
| `setValues` | You have simple scalar input such as `replicaCount=3`. | The value's type or leading zeroes matter. |
| `setStringValues` | A tag, ID, or code must remain text. | You intend the chart to receive a boolean or number. |
| `setJsonValues` | The caller owns a list or object and can validate JSON. | A human is editing YAML; use a values file instead. |
| `setFileValues` | The value is the contents of a certificate, license, or other file. | You only have a path. Read the file first. |

## Keep input handling safe

Treat values as untrusted configuration when they come from a user or tenant. Limit inline YAML size, validate allowed override paths, and retain the values-file names and explicit overrides alongside any rendered artifact. Secrets may appear in values and rendered YAML; do not put either in general-purpose logs.

To render conditionals for a real cluster version, see [Render for a target cluster](template-rendering.md). To send the result to a cluster, use [Install and upgrade releases](release-workflows.md).
