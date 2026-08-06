# ASP.NET Core chart preview

Use this pattern when an HTTP API must return rendered YAML but must not create a release or contact a cluster.

## Packages

Install `HelmSharp.Chart` and `HelmSharp.Engine`. The endpoint resolves chart and values identifiers through application-owned catalogs, then creates a `HelmTemplateRenderer`.

## Request flow

1. Authenticate the caller and validate its chart and values identifiers.
2. Resolve identifiers to application-authorized content.
3. Load the chart, merge values, and render with explicit Kubernetes capability inputs.
4. Return YAML and, when needed, notes in a separate response field.
5. Record the chart version and effective inputs if a later approval can deploy the preview.

The full code is in [Build a render-preview endpoint](../examples/render-preview-api.md).

## Do not cross the boundary

Do not accept caller-provided file paths, create a `HelmClient` lifecycle request, or apply the rendered manifest in this endpoint. An approval-to-deployment workflow needs immutable inputs and release-state controls; use [Turn a review into a deployment](../examples/dry-run-deployment.md) for that case.
