# HelmSharp.Release API

> 生成内容。本页由 `docs/scripts/generate-api-reference.ps1` 根据公开 C# 声明生成。人工整理的使用建议在对应包页面中维护。

此页列出公开类型和成员，便于查找。使用建议、边界和示例请先阅读对应包文档。

> **版本说明：** 本页反映当前 `master` 源码树。M2 API 已包含在最新发布的 1.3.0 包中。

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

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

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

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

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

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmReleaseStoreException

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Release/HelmReleaseStoreException.cs` |

### 属性
- `Format`
- `NamespaceName`
- `SecretName`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。
