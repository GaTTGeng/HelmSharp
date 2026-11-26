# Versioning and compatibility policy

HelmSharp 2.x follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html). This policy applies to stable releases of the published HelmSharp NuGet packages. A package version describes HelmSharp compatibility, not a promise of complete parity with a particular Helm release. The [Helm compatibility guide](helm-compatibility.md) and [function matrix](template-function-compatibility.md) describe the supported behavior and its evidence.

## Choosing a version

| Release | Permitted examples |
| --- | --- |
| Patch | Correct a defect while preserving documented contracts; improve diagnostics without exposing secrets; fix a security vulnerability as described below. |
| Minor | Add a compatible API or opt-in feature; support a previously unsupported chart or Helm function without changing documented behavior for supported inputs; deprecate an API without removing it. |
| Major | Remove or change a stable public member, require new arguments, change a default or a documented result for supported input incompatibly, or stop reading a previously supported persisted release format without a compatible migration. |

Source and binary compatibility both matter. Adding an overload or interface member can break existing callers or implementers, so additions must be assessed in context rather than assumed compatible. Supported behavior includes documented defaults, errors, side effects, and data formats. Bug fixes can affect callers relying on a defect: describe observable changes in release notes and provide a migration when needed. If the old behavior is a documented contract, change it incompatibly in a major release except for the security exception below.

Helm parity fixes do not automatically qualify as patch changes. A fix for unsupported behavior can be a patch or minor according to its scope; an incompatible change to a documented supported behavior requires a major version, subject to the security exception. Record the Helm version and cases tested, and update the compatibility guidance. Generated API pages are an index of public declarations, not a separate stability designation or a guarantee of Helm parity.

## Stable and experimental APIs

Until an API is explicitly designated experimental, treat every public type and member shipped in a stable 2.x NuGet package as stable, including extension interfaces and public implementation helpers. This conservative baseline does not turn undocumented Helm behavior into supported parity. Internal and private members are outside the public contract. Existing APIs are not retroactively experimental merely because a guide recommends another entry point.

The repository maintainers own a package-by-package inventory of the 2.x public surface, including implementation helpers and extension points. Publish the inventory by the earlier of the 2.1.0 release or 2027-05-26, and link each classification to its evidence. Until then the baseline above applies. The inventory must not silently reclassify existing stable APIs as experimental; any incompatible removal or restriction follows the major-version and deprecation rules.

New experimental APIs require explicit opt-in and a visible experimental designation in API documentation and release notes, including the opt-in mechanism and scope. Their signatures and behavior may change or be removed in a minor release; announce the change and migration in the changelog. A patch must not deliberately remove or incompatibly change them except for security. Graduation requires a documented stable replacement and migration guidance; once stable, the normal policy applies. A prerelease package alone does not reclassify an existing stable API.

## Deprecation and migration

For a stable API planned for removal, mark it `[Obsolete]` with a message naming the replacement or migration guidance and, when known, the earliest removal major version. Keep it callable with a warning (`error: false`) through at least one stable minor release and for at least six months after the first stable release carrying the warning. Removal is no earlier than the next major release after both conditions are met. Document the deprecation, replacement, timeline, and migration example in the changelog and release notes. If an attribute cannot express the warning, provide equivalent prominent documentation. Security fixes may require an exception below.

For example, 2.0.0 removed `GenerateProvFileAsync` and the hash-only `VerifyAsync` overload. Applications should use `HelmProvenance.SignAsync` and `VerifyAsync` with signing and trusted keys, or `CheckDigestAsync` for integrity-only checks, and re-sign legacy pseudo-signature files. The [2.0.0 release notes](https://github.com/GaTTGeng/HelmSharp/releases/tag/2.0.0) identify the break and security advisory. This 1.x to 2.0 transition illustrates a major-only API break; it does not claim that the deprecation window above was observed retrospectively.

## Persisted releases and interoperability

Supported release records, including Kubernetes release Secrets and documented legacy HelmSharp payloads, are part of the compatibility contract. Within 2.x, a newer version must continue to read records written by earlier 2.x releases in supported formats. Changes to the writer must preserve documented interoperability with supported Helm versions and must not silently make existing history unreadable. Test old-reader/new-writer and new-reader/old-writer combinations when changing serialization; backward reading does not imply that an older version can read every newer format. If older readers cannot read newly written records, document the first affected version, downgrade limits and recovery or migration steps before release. An incompatible removal of supported read or interoperability behavior requires a major release, except for the security exception.

## Security fixes

A security fix may need to reject previously accepted unsafe inputs or change behavior in a patch or minor release when waiting for a major release would leave users exposed. Use the least disruptive effective fix, disclose the compatibility impact and migration or remediation in the advisory, changelog and release notes, and identify any data or downgrade consequences. Coordinate undisclosed vulnerabilities through [SECURITY.md](https://github.com/GaTTGeng/HelmSharp/blob/master/SECURITY.md).
