# 模板函数兼容性矩阵

这是 HelmSharp 托管渲染器的维护中函数清单。它记录实现状态，不表示任意 Chart 都必然与 Helm 完全一致；golden 测试仍只是已覆盖 Chart 行为的回归证据。

## 基线与方法

审计使用以下上游版本：

- Helm [`v3.21.3` 的 `pkg/engine/funcs.go`](https://github.com/helm/helm/blob/v3.21.3/pkg/engine/funcs.go)
- Sprig [`v3.3.0` 的 `functions.go`](https://github.com/Masterminds/sprig/blob/v3.3.0/functions.go)；该版本由 [Helm 的 `go.mod`](https://github.com/helm/helm/blob/v3.21.3/go.mod) 声明

Helm 以 `sprig.TxtFuncMap()` 为起点，删除 `env` 和 `expandenv`，再加入自己的 helper。因此矩阵包含 217 个 Helm 可见名称：206 个保留的 Sprig 名称和 11 个 Helm 专用名称。渲染器调度和聚焦测试位于 `HelmTemplateRenderer` 与 `TemplateFunctionTests`；Helm CLI 测试在 CLI 不可用时会明确跳过。现有已支持函数的逐项 Helm CLI parity 覆盖由 #214 跟踪。

| 状态 | 数量 | 含义 |
| --- | ---: | --- |
| 已支持 | 150 | 托管渲染器会调度该函数。聚焦测试和 golden 测试保护当前表面；逐项 Helm CLI 覆盖由 #214 跟踪。 |
| 未支持 | 67 | 渲染器产生 `Helm template function '<name>' is not supported by the managed renderer.`；`Render()` 会附加模板路径。 |
| 有意排除 | 2 | Helm 从 Sprig 函数映射中删除该 helper；HelmSharp 同样拒绝并提供带路径的诊断。 |

## Sprig v3.3.0 清单

### 已支持（144）

`abbrev`, `add`, `adler32sum`, `append`, `atoi`, `b32dec`, `b32enc`, `b64dec`, `b64enc`, `base`, `bcrypt`, `camelcase`, `cat`, `ceil`, `clean`, `coalesce`, `compact`, `concat`, `contains`, `date`, `dateInZone`, `deepCopy`, `deepEqual`, `default`, `dict`, `dig`, `dir`, `div`, `duration`, `durationRound`, `empty`, `ext`, `fail`, `first`, `float64`, `floor`, `fromJson`, `genPrivateKey`, `get`, `has`, `hasKey`, `hasPrefix`, `hasSuffix`, `indent`, `initial`, `initials`, `int`, `int64`, `isAbs`, `join`, `kebabcase`, `keys`, `kindIs`, `kindOf`, `last`, `list`, `lower`, `max`, `merge`, `mergeOverwrite`, `min`, `mod`, `mul`, `mustAppend`, `mustCompact`, `mustDeepCopy`, `mustHas`, `mustMerge`, `mustMergeOverwrite`, `mustPrepend`, `mustRegexReplaceAllLiteral`, `mustReverse`, `mustToJson`, `mustUniq`, `mustWithout`, `nindent`, `nospace`, `now`, `omit`, `pick`, `pluck`, `plural`, `prepend`, `quote`, `randAlpha`, `randAlphaNum`, `randAscii`, `randInt`, `randNumeric`, `regexFind`, `regexFindAll`, `regexMatch`, `regexReplaceAll`, `regexReplaceAllLiteral`, `regexSplit`, `repeat`, `replace`, `rest`, `reverse`, `round`, `semver`, `semverCompare`, `set`, `sha1sum`, `sha256sum`, `sha512sum`, `shuffle`, `slice`, `snakecase`, `sortAlpha`, `split`, `splitList`, `squote`, `sub`, `substr`, `swapcase`, `ternary`, `title`, `toDecimal`, `toJson`, `toPrettyJson`, `toRawJson`, `toString`, `trim`, `trimAll`, `trimPrefix`, `trimSuffix`, `trunc`, `tuple`, `typeIs`, `typeIsLike`, `typeOf`, `uniq`, `unixEpoch`, `unset`, `until`, `untilStep`, `untitle`, `upper`, `uuidv4`, `values`, `without`, `wrap`, `wrapWith`。

### 未支持（62）

`abbrevboth`, `add1`, `add1f`, `addf`, `ago`, `all`, `any`, `biggest`, `buildCustomCert`, `chunk`, `date_in_zone`, `date_modify`, `dateModify`, `decryptAES`, `derivePassword`, `divf`, `encryptAES`, `genCA`, `genCAWithKey`, `genSelfSignedCert`, `genSelfSignedCertWithKey`, `genSignedCert`, `genSignedCertWithKey`, `getHostByName`, `hello`, `htmlDate`, `htmlDateInZone`, `htpasswd`, `maxf`, `minf`, `mulf`, `must_date_modify`, `mustChunk`, `mustDateModify`, `mustFirst`, `mustFromJson`, `mustInitial`, `mustLast`, `mustRegexFind`, `mustRegexFindAll`, `mustRegexMatch`, `mustRegexReplaceAll`, `mustRegexSplit`, `mustRest`, `mustSlice`, `mustToDate`, `mustToPrettyJson`, `mustToRawJson`, `osBase`, `osClean`, `osDir`, `osExt`, `osIsAbs`, `randBytes`, `regexQuoteMeta`, `seq`, `splitn`, `subf`, `toDate`, `toStrings`, `urlJoin`, `urlParse`。

### Helm 有意排除（2）

`env`, `expandenv`。Helm 会从 `sprig.TxtFuncMap()` 删除这两个函数；拒绝它们可避免 Chart 渲染依赖宿主进程的环境变量。`environment-function-exclusion` fixture 验证了 Helm CLI 的拒绝行为和托管渲染器可操作的诊断。

## Helm 专用新增函数

### 已支持（6）

`fromYaml`, `include`, `lookup`, `required`, `toYaml`, `tpl`。

### 未支持（5）

`fromJsonArray`, `fromToml`, `fromYamlArray`, `toToml`, `toYamlPretty`。

## 后续实现分组

未支持名称按以下专注 Issue 分组：

1. 集合、逻辑、反射和数值 helper。
2. 字符串、正则表达式、URL 和路径 helper。
3. 日期/时间和序列化 helper，包括 Helm 的 TOML/YAML 新增函数。
4. 加密、证书、密码和随机字节。
5. 对每个现有已支持函数补齐直接 Helm CLI parity 覆盖（#214）。

每个实现 Issue 都必须在 Helm CLI parity 测试中覆盖普通调用、pipeline 和失败语义。任何有意差异都必须更新本矩阵，并保持带模板路径的诊断契约。
