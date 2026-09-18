# Maintainer Release Guide

This repository publishes NuGet packages with NuGet Trusted Publishing from GitHub Actions. Do not create or store a long-lived NuGet API key in GitHub secrets for normal releases.

## One-time NuGet.org setup

Create a trusted publishing policy on NuGet.org for this repository:

- Package Owner: the NuGet.org account that owns the HelmSharp packages.
- Repository Owner: `GaTTGeng`
- Repository: `HelmSharp`
- Workflow File: `release-nuget.yml`
- Environment: leave empty unless the workflow is later changed to use a GitHub environment.

The workflow uses `NuGet/login@v1`, so `.github/workflows/release-nuget.yml` must keep the `id-token: write` permission.

## GitHub setup

If the NuGet.org username differs from the GitHub repository owner, create a repository variable named `NUGET_USER` with the NuGet.org username. If it is not set, the workflow uses the GitHub repository owner.

No `NUGET_API_KEY` repository secret is required.

For branch protection, require the `CI / Build, test, and pack` status check before merging to `master`. Keep conversation resolution required.

For Actions hardening, prefer allowing only GitHub-owned and verified Marketplace actions. If stricter supply-chain controls are required, pin third-party actions by full commit SHA and enable SHA pinning at the repository or organization level.

## Release a version

Use NuGet SemVer tags without a leading `v`:

```powershell
git tag 1.0.1
git push origin 1.0.1
```

The release workflow rejects tags with a leading `v`. A release tag must point to a commit reachable from `origin/master`.

To run a manual release, open the `Release NuGet` workflow, enter the package version, and enable publishing. Manual prerelease package versions such as `1.0.1-preview.1` are supported; the workflow derives stable `AssemblyVersion` and `FileVersion` values from the numeric version core.

## Re-run a failed release

After fixing workflow configuration, re-run the failed workflow from GitHub Actions or with:

```powershell
gh run rerun <run-id> --repo GaTTGeng/HelmSharp
```

For the failed `1.0.1` release created before Trusted Publishing was configured, updating the `1.0.1` tag to a commit that contains the Trusted Publishing workflow will start a new release run.
