# HelmSharp.Repo API

> 生成内容。本页由 `docs/scripts/generate-api-reference.ps1` 根据公开 C# 声明生成。人工整理的使用建议在对应包页面中维护。

此页列出公开类型和成员，便于查找。使用建议、边界和示例请先阅读对应包文档。

> **版本说明：** 本页反映当前 `master` 源码树。M2 API 已包含在最新发布的 1.2.0 包中。

## HelmChartRepository

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Repo/HelmChartRepository.cs` |

### 方法
- `AddRepositoryAsync(...)`
- `Dispose(...)`
- `FetchRepoIndexAsync(...)`
- `ListRepositoriesAsync(...)`
- `PullChartAsync(...)`
- `PushToOciAsync(...)`
- `RemoveRepositoryAsync(...)`
- `SearchRepoAsync(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmChartSearchResult

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Repo/HelmChartRepository.cs` |

### 属性
- `AppVersion`
- `Description`
- `Name`
- `Version`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmChartVersion

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Repo/HelmChartRepository.cs` |

### 属性
- `AppVersion`
- `Created`
- `Description`
- `Digest`
- `Name`
- `Urls`
- `Version`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmPullRequest

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Repo/HelmPullRequest.cs` |

### 属性
- `ChartReference`
- `Destination`
- `PassCredentialsAll`
- `Password`
- `RepositoryUrl`
- `Untar`
- `UntarDirectory`
- `Username`
- `VerifyDigest`
- `Version`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmRepoIndex

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Repo/HelmChartRepository.cs` |

### 属性
- `ApiVersion`
- `Entries`
- `Generated`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmRepoIndexDiagnostic

| 字段 | 值 |
| --- | --- |
| 类型类别 | `record` |
| 源文件 | `src/HelmSharp.Repo/HelmRepoIndexer.cs` |

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmRepoIndexer

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Repo/HelmRepoIndexer.cs` |

### 方法
- `GenerateIndexAsync(...)`
- `GenerateIndexWithDiagnosticsAsync(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmRepoIndexGenerationResult

| 字段 | 值 |
| --- | --- |
| 类型类别 | `record` |
| 源文件 | `src/HelmSharp.Repo/HelmRepoIndexer.cs` |

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmRepoIndexRequest

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Repo/HelmRepoIndexRequest.cs` |

### 属性
- `DirectoryPath`
- `FailOnInvalidPackage`
- `MergeIndexPath`
- `OutputPath`
- `Url`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmRepository

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Repo/HelmChartRepository.cs` |

### 属性
- `CaFile`
- `CertFile`
- `InsecureSkipTlsVerify`
- `KeyFile`
- `Name`
- `PassCredentialsAll`
- `Password`
- `Url`
- `Username`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmRepositoryOptions

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Repo/HelmRepositoryOptions.cs` |

### 属性
- `CacheDirectory`
- `ConfigDirectory`
- `RepositoryConfigPath`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。
