# HelmSharp.Chart API

> 生成内容。本页由 `docs/scripts/generate-api-reference.ps1` 根据公开 C# 声明生成。人工整理的使用建议在对应包页面中维护。

此页列出公开类型和成员，便于查找。使用建议、边界和示例请先阅读对应包文档。

## HelmChart

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Chart/HelmChartLoader.cs` |

### 属性
- `Annotations`
- `ApiVersion`
- `AppVersion`
- `Crds`
- `Dependencies`
- `Deprecated`
- `Description`
- `Files`
- `Home`
- `Icon`
- `Keywords`
- `KubeVersion`
- `LockEntries`
- `Maintainers`
- `Name`
- `Sources`
- `Subcharts`
- `Templates`
- `Type`
- `ValuesYaml`
- `Version`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmChartDependency

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Chart/HelmChartDependency.cs` |

### 属性
- `Alias`
- `Condition`
- `Enabled`
- `ImportValues`
- `Name`
- `Repository`
- `Tags`
- `Version`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmChartLoader

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Chart/HelmChartLoader.cs` |

### 方法
- `LoadAsync(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmChartLockEntry

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Chart/HelmChartLockEntry.cs` |

### 属性
- `Digest`
- `Name`
- `Repository`
- `Version`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmValues

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Chart/HelmValues.cs` |

### 方法
- `BuildAsync(...)`
- `BuildSubchartValues(...)`
- `ToYaml(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## HelmYaml

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Chart/HelmYaml.cs` |

### 方法
- `Accepts(...)`
- `DeserializeAny(...)`
- `DeserializeDictionary(...)`
- `GetString(...)`
- `Normalize(...)`
- `ReadYaml(...)`
- `Serialize(...)`
- `WriteYaml(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。
