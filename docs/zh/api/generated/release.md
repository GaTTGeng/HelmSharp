# HelmSharp.Release API

> 生成内容。本页由 `docs/scripts/generate-api-reference.ps1` 根据公开 C# 声明生成。人工整理的使用建议在对应包页面中维护。

此页列出公开类型和成员，便于查找。使用建议、边界和示例请先阅读对应包文档。


## HelmReleaseHookRecord

| 字段 | 值 |
| --- | --- |
| 类型类别 | `record` |
| 源文件 | `src/HelmSharp.Release/HelmReleaseRecord.cs` |

### 属性
- `DeletePolicies`
- `Events`
- `Kind`
- `LastRunCompletedAt`
- `LastRunPhase`
- `LastRunStartedAt`
- `Manifest`
- `Name`
- `OutputLogPolicies`
- `Path`
- `Weight`

## HelmReleaseRecord

| 字段 | 值 |
| --- | --- |
| 类型类别 | `record` |
| 源文件 | `src/HelmSharp.Release/HelmReleaseRecord.cs` |

### 属性
- `AppVersion`
- `ChartApiVersion`
- `ChartDescription`
- `ChartKubeVersion`
- `ChartName`
- `ChartType`
- `ChartValuesYaml`
- `ChartVersion`
- `ComputedValuesYaml`
- `DeletedAt`
- `Description`
- `FirstDeployedAt`
- `Hooks`
- `Labels`
- `Manifest`
- `Name`
- `Namespace`
- `Notes`
- `RawChartJson`
- `Revision`
- `Status`
- `UpdatedAt`
- `ValuesYaml`

## HelmReleaseStore

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Release/HelmReleaseStore.cs` |

### 方法
- `DeleteAsync(...)`
- `GetLatestAsync(...)`
- `HistoryAsync(...)`
- `ListAsync(...)`
- `MarkStatusAsync(...)`
- `MarkUninstalledAsync(...)`
- `NextRevisionAsync(...)`
- `PurgeAsync(...)`
- `SaveAsync(...)`
- `TryCreateAsync(...)`
- `TryMarkPendingRollbackFailedAsync(...)`

## HelmReleaseStoreException

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Release/HelmReleaseStoreException.cs` |

### 属性
- `Format`
- `NamespaceName`
- `SecretName`
