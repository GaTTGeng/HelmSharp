# Kubernetes deployment service

Use `HelmSharp.Action` when your service owns release lifecycle operations and the Kubernetes identity used to perform them.

## Service responsibilities

A deployment service chooses the chart version and namespace, supplies Kubernetes credentials, applies an authorization policy, persists operation diagnostics, and exposes status/history to callers. `HelmClient` performs the Helm-style lifecycle work inside that boundary.

## Recommended flow

1. Resolve a chart and values from trusted, versioned application records.
2. Create a `HelmUpgradeInstallRequest` with explicit namespace, wait, timeout, and hook policy.
3. Run a dry run for an operator preview when appropriate.
4. Rebuild the apply request from recorded inputs; do not reuse a mutable preview request.
5. Read `CommandResult` and release history for the operation record.

Start with [Install and upgrade releases](../guide/release-workflows.md). For an approval system, use [Turn a review into a deployment](../examples/dry-run-deployment.md), which covers release-state checks and immutable inputs.

## When not to use this layer

If another controller owns cluster mutation, render manifests for that controller instead. See [Generate manifests for GitOps](../examples/gitops-pr-generator.md).
