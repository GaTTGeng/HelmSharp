# NuGet audit exceptions

HelmSharp fails restores for high and critical NuGet advisories (`NU1903` and
`NU1904`). Exceptions are not a suppression mechanism: an entry must be reviewed
by a maintainer, include an owner and expiry, and link to follow-up work. Remove
an entry as soon as the dependency is upgraded or the advisory is otherwise
resolved.

No active exceptions.

## Required entry format

```text
- Advisory: GHSA-... (or CVE-...)
  Package and affected range: Package.Name < 1.2.3
  Rationale and compensating control: explain why release cannot wait
  Owner: @maintainer
  Expires: YYYY-MM-DD (maximum 30 days)
  Follow-up: #issue-or-URL
```

An exception must never be added without a corresponding, reviewable issue.
The normal restore remains the gate; maintainers must not add `NoWarn` for
`NU1900`-`NU1904`.
