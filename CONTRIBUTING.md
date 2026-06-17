# Contributing

Thanks for considering a contribution to HelmSharp.

## Development Setup

Install the .NET 8, .NET 9, and .NET 10 SDKs, then run:

```powershell
dotnet restore HelmSharp.sln
dotnet build HelmSharp.sln --configuration Release --no-restore
dotnet test HelmSharp.sln --configuration Release --no-build --no-restore
```

## Pull Request Guidelines

- Keep changes focused. Prefer small pull requests that solve one problem well.
- Add or update tests for behavior changes.
- Do not commit `bin/`, `obj/`, IDE metadata, generated packages, or local secrets.
- When adding Helm compatibility, include a chart or template test that captures the behavior.
- When adding Kubernetes resource support, include parsing/apply identity coverage where possible.
- Keep public API changes deliberate and document them in the README or XML comments when useful.

## Commit and Branch Names

There is no strict required format yet. Clear conventional names such as `fix/dry-run`, `docs/readme`, or `feat/oci-auth` are preferred.

## Release Process

Maintainers publish by pushing a version tag such as:

```powershell
git tag 1.0.0
git push origin 1.0.0
```

The release workflow packs the projects and publishes packages to NuGet.org through NuGet Trusted Publishing. Maintainer setup and release steps are documented in [docs/maintainer-release.md](docs/maintainer-release.md).
