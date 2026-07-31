# Template-function compatibility matrix

This is the maintained inventory for HelmSharp's managed renderer. It is an implementation-status document, not a blanket assertion that arbitrary charts render identically. The golden suite remains chart-scoped regression evidence.

## Baseline and method

The inventory was audited against:

- Helm [`v3.21.3` `pkg/engine/funcs.go`](https://github.com/helm/helm/blob/v3.21.3/pkg/engine/funcs.go)
- Sprig [`v3.3.0` `functions.go`](https://github.com/Masterminds/sprig/blob/v3.3.0/functions.go), the version declared by [Helm's `go.mod`](https://github.com/helm/helm/blob/v3.21.3/go.mod)

Helm begins with `sprig.TxtFuncMap()`, deletes `env` and `expandenv`, then adds its own helpers. The matrix therefore contains 217 Helm-visible names: 206 remaining Sprig names and 11 Helm-only names. The renderer's dispatch surface and focused test coverage live in `HelmTemplateRenderer` and `TemplateFunctionTests`; Helm CLI tests are optional and skip clearly when the CLI is unavailable. Direct per-function Helm CLI parity coverage for the existing supported surface is tracked by #214.

| Status | Count | Meaning |
| --- | ---: | --- |
| Supported | 150 | The managed renderer dispatches the function. Focused and golden tests protect the current surface; direct per-function Helm CLI coverage is tracked by #214. |
| Unsupported | 67 | The renderer produces `Helm template function '<name>' is not supported by the managed renderer.`; `Render()` adds the template path. |
| Intentionally excluded | 2 | Helm removes this Sprig helper from its function map. HelmSharp rejects it with the same path-aware diagnostic. |

## Sprig v3.3.0

### Supported (144)

`abbrev`, `add`, `adler32sum`, `append`, `atoi`, `b32dec`, `b32enc`, `b64dec`, `b64enc`, `base`, `bcrypt`, `camelcase`, `cat`, `ceil`, `clean`, `coalesce`, `compact`, `concat`, `contains`, `date`, `dateInZone`, `deepCopy`, `deepEqual`, `default`, `dict`, `dig`, `dir`, `div`, `duration`, `durationRound`, `empty`, `ext`, `fail`, `first`, `float64`, `floor`, `fromJson`, `genPrivateKey`, `get`, `has`, `hasKey`, `hasPrefix`, `hasSuffix`, `indent`, `initial`, `initials`, `int`, `int64`, `isAbs`, `join`, `kebabcase`, `keys`, `kindIs`, `kindOf`, `last`, `list`, `lower`, `max`, `merge`, `mergeOverwrite`, `min`, `mod`, `mul`, `mustAppend`, `mustCompact`, `mustDeepCopy`, `mustHas`, `mustMerge`, `mustMergeOverwrite`, `mustPrepend`, `mustRegexReplaceAllLiteral`, `mustReverse`, `mustToJson`, `mustUniq`, `mustWithout`, `nindent`, `nospace`, `now`, `omit`, `pick`, `pluck`, `plural`, `prepend`, `quote`, `randAlpha`, `randAlphaNum`, `randAscii`, `randInt`, `randNumeric`, `regexFind`, `regexFindAll`, `regexMatch`, `regexReplaceAll`, `regexReplaceAllLiteral`, `regexSplit`, `repeat`, `replace`, `rest`, `reverse`, `round`, `semver`, `semverCompare`, `set`, `sha1sum`, `sha256sum`, `sha512sum`, `shuffle`, `slice`, `snakecase`, `sortAlpha`, `split`, `splitList`, `squote`, `sub`, `substr`, `swapcase`, `ternary`, `title`, `toDecimal`, `toJson`, `toPrettyJson`, `toRawJson`, `toString`, `trim`, `trimAll`, `trimPrefix`, `trimSuffix`, `trunc`, `tuple`, `typeIs`, `typeIsLike`, `typeOf`, `uniq`, `unixEpoch`, `unset`, `until`, `untilStep`, `untitle`, `upper`, `uuidv4`, `values`, `without`, `wrap`, `wrapWith`.

### Unsupported (62)

`abbrevboth`, `add1`, `add1f`, `addf`, `ago`, `all`, `any`, `biggest`, `buildCustomCert`, `chunk`, `date_in_zone`, `date_modify`, `dateModify`, `decryptAES`, `derivePassword`, `divf`, `encryptAES`, `genCA`, `genCAWithKey`, `genSelfSignedCert`, `genSelfSignedCertWithKey`, `genSignedCert`, `genSignedCertWithKey`, `getHostByName`, `hello`, `htmlDate`, `htmlDateInZone`, `htpasswd`, `maxf`, `minf`, `mulf`, `must_date_modify`, `mustChunk`, `mustDateModify`, `mustFirst`, `mustFromJson`, `mustInitial`, `mustLast`, `mustRegexFind`, `mustRegexFindAll`, `mustRegexMatch`, `mustRegexReplaceAll`, `mustRegexSplit`, `mustRest`, `mustSlice`, `mustToDate`, `mustToPrettyJson`, `mustToRawJson`, `osBase`, `osClean`, `osDir`, `osExt`, `osIsAbs`, `randBytes`, `regexQuoteMeta`, `seq`, `splitn`, `subf`, `toDate`, `toStrings`, `urlJoin`, `urlParse`.

### Intentionally excluded by Helm (2)

`env`, `expandenv`. Helm removes both from `sprig.TxtFuncMap()`; rejecting them prevents chart rendering from depending on the host process environment. The `environment-function-exclusion` fixture verifies Helm CLI rejection and the managed renderer's actionable diagnostic.

## Helm-only additions

### Supported (6)

`fromYaml`, `include`, `lookup`, `required`, `toYaml`, `tpl`.

### Unsupported (5)

`fromJsonArray`, `fromToml`, `fromYamlArray`, `toToml`, `toYamlPretty`.

## Follow-up implementation groups

The unsupported names are deliberately grouped into focused follow-up issues:

1. Collection, logic, reflection, and numeric helpers.
2. String, regular-expression, URL, and path helpers.
3. Date/time and serialization helpers, including Helm's TOML/YAML additions.
4. Cryptography, certificates, passwords, and random bytes.
5. Direct Helm CLI parity coverage for each existing supported function (#214).

Each implementation issue must add normal, pipeline, and failure semantics to the Helm CLI parity suite. Any intentional divergence must update this matrix and retain the path-aware diagnostic contract.
