# HelmSharp.Repo API

> 生成内容。本页由 `docs/scripts/generate-api-reference.ps1` 根据公开 C# 声明生成。人工整理的使用建议在对应包页面中维护。

此页列出公开类型和成员，便于查找。使用建议、边界和示例请先阅读对应包文档。


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

## HelmRepoIndex

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Repo/HelmChartRepository.cs` |

### 属性
- `ApiVersion`
- `Entries`
- `Generated`

## HelmRepoIndexDiagnostic

| 字段 | 值 |
| --- | --- |
| 类型类别 | `record` |
| 源文件 | `src/HelmSharp.Repo/HelmRepoIndexer.cs` |

## HelmRepoIndexer

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Repo/HelmRepoIndexer.cs` |

### 方法
- `GenerateIndexAsync(...)`
- `GenerateIndexWithDiagnosticsAsync(...)`

## HelmRepoIndexGenerationResult

| 字段 | 值 |
| --- | --- |
| 类型类别 | `record` |
| 源文件 | `src/HelmSharp.Repo/HelmRepoIndexer.cs` |

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

## HelmRepositoryOptions

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Repo/HelmRepositoryOptions.cs` |

### 属性
- `CacheDirectory`
- `ConfigDirectory`
- `RepositoryConfigPath`
