# HelmSharp.Action API

> 生成内容。本页由 `docs/scripts/generate-api-reference.ps1` 根据公开 C# 声明生成。人工整理的使用建议在对应包页面中维护。

此页列出公开类型和成员，便于查找。使用建议、边界和示例请先阅读对应包文档。

## CommandResult

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Action/CommandResult.cs` |

### 属性
- `ExitCode`
- `StandardError`
- `StandardOutput`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmClient

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Action/HelmClient.cs` |

### 方法
- `CreateAsync(...)`
- `DependencyBuildAsync(...)`
- `DependencyListAsync(...)`
- `DependencyUpdateAsync(...)`
- `DiffAsync(...)`
- `EnvAsync(...)`
- `GenerateReleaseName(...)`
- `GetAllAsync(...)`
- `GetHooksAsync(...)`
- `GetManifestAsync(...)`
- `GetNotesAsync(...)`
- `GetValuesAsync(...)`
- `HistoryAsync(...)`
- `LintAsync(...)`
- `ListReleasesAsync(...)`
- `PackageAsync(...)`
- `PullAsync(...)`
- `PushAsync(...)`
- `RegistryLoginAsync(...)`
- `RegistryLogoutAsync(...)`
- `RepoAddAsync(...)`
- `RepoIndexAsync(...)`
- `RepoListAsync(...)`
- `RepoRemoveAsync(...)`
- `RepoUpdateAsync(...)`
- `RollbackAsync(...)`
- `SearchHubAsync(...)`
- `SearchRepoAsync(...)`
- `ShowAllAsync(...)`
- `ShowChartAsync(...)`
- `ShowCrdsAsync(...)`
- `ShowManifestAsync(...)`
- `ShowReadmeAsync(...)`
- `ShowValuesAsync(...)`
- `StatusAsync(...)`
- `TemplateAsync(...)`
- `TemplateWithNotesAsync(...)`
- `TestAsync(...)`
- `UninstallAsync(...)`
- `UpgradeInstallAsync(...)`
- `UpgradeInstallStreamAsync(...)`
- `VersionAsync(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmExecutionOptions

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Action/HelmExecutionOptions.cs` |

### 属性
- `ApiVersions`
- `DefaultNamespace`
- `FieldManager`
- `KubeConfigContent`
- `KubeConfigPath`
- `KubernetesContext`
- `KubeVersion`
- `MaxHistory`
- `ServerSideApply`
- `TimeoutSeconds`
- `WorkingDirectory`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmHookDeletePolicy

| 字段 | 值 |
| --- | --- |
| 类型类别 | `enum` |
| 源文件 | `src/HelmSharp.Action/HelmHookExecutor.cs` |

### 属性
- `DeletePolicies`
- `Events`
- `Kind`
- `Manifest`
- `Name`
- `Namespace`
- `Path`
- `Weight`

### 方法
- `ExecuteHooksAsync(...)`
- `ExecuteHooksWithFailureHandlingAsync(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmHookEvent

| 字段 | 值 |
| --- | --- |
| 类型类别 | `enum` |
| 源文件 | `src/HelmSharp.Action/HelmHookExecutor.cs` |

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmPluginInfo

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Action/HelmPluginManager.cs` |

### 属性
- `Description`
- `Name`
- `Path`
- `Version`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmPluginManager

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Action/HelmPluginManager.cs` |

### 方法
- `InstallAsync(...)`
- `List(...)`
- `RunAsync(...)`
- `Uninstall(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmProvenance

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Action/HelmProvenance.cs` |

### 方法
- `ExtractMetadata(...)`
- `ExtractSha256(...)`
- `GenerateProvFileAsync(...)`
- `VerifyAsync(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmTemplateRequest

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Action/HelmTemplateRequest.cs` |

### 属性
- `ApiVersions`
- `Chart`
- `IncludeCRDs`
- `IsUpgrade`
- `KubeVersion`
- `Namespace`
- `OutputDir`
- `ReleaseName`
- `SetFileValues`
- `SetJsonValues`
- `SetStringValues`
- `SetValues`
- `ShowNotes`
- `UseReleaseName`
- `ValuesContent`
- `ValuesFile`
- `ValuesFiles`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmUninstallRequest

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Action/HelmUninstallRequest.cs` |

### 属性
- `KubeConfigContent`
- `KubeConfigPath`
- `Namespace`
- `ReleaseName`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmUpgradeInstallRequest

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Action/HelmUpgradeInstallRequest.cs` |

### 属性
- `Atomic`
- `CaFile`
- `CertFile`
- `Chart`
- `CleanupOnFail`
- `CreateNamespace`
- `DependencyUpdate`
- `Description`
- `Devel`
- `DisableHooks`
- `DisableOpenApiValidation`
- `DryRun`
- `DryRunIsUpgrade`
- `DryRunRevision`
- `EnableDns`
- `Force`
- `GenerateName`
- `HideSecret`
- `InsecureSkipTlsVerify`
- `Install`
- `KeyFile`
- `Keyring`
- `KubeConfigContent`
- `KubeConfigPath`
- `Labels`
- `MaxHistory`
- `Namespace`
- `NameTemplate`
- `PassCredentials`
- `Password`
- `PlainHttp`
- `ReleaseName`
- `RenderSubchartNotes`
- `RepoUrl`
- `ResetValues`
- `ReuseValues`
- `RollbackOnFailure`
- `ServerSideApply`
- `SetFileValues`
- `SetJsonValues`
- `SetStringValues`
- `SetValues`
- `SkipCRDs`
- `SkipSchemaValidation`
- `TakeOwnership`
- `TimeoutSeconds`
- `Username`
- `ValuesContent`
- `ValuesFile`
- `ValuesFiles`
- `Verify`
- `Version`
- `Wait`
- `WaitForJobs`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## IHelmClient

| 字段 | 值 |
| --- | --- |
| 类型类别 | `interface` |
| 源文件 | `src/HelmSharp.Action/IHelmClient.cs` |

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## IHelmOptionsProvider

| 字段 | 值 |
| --- | --- |
| 类型类别 | `interface` |
| 源文件 | `src/HelmSharp.Action/IHelmOptionsProvider.cs` |

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## KubeVersionValidator

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Action/KubeVersionValidator.cs` |

### 方法
- `IsCompatible(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。
