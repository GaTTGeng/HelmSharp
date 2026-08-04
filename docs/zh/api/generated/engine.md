# HelmSharp.Engine API

> 生成内容。本页由 `docs/scripts/generate-api-reference.ps1` 根据公开 C# 声明生成。人工整理的使用建议在对应包页面中维护。

此页列出公开类型和成员，便于查找。使用建议、边界和示例请先阅读对应包文档。


## ActionNode

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateAst.cs` |

### 属性
- `Expression`
- `LeftTrim`
- `RightTrim`

## BlockNode

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateAst.cs` |

### 属性
- `ElseIfChain`
- `EndRightTrim`
- `Expression`
- `FalseBody`
- `Keyword`
- `LeftTrim`
- `RightTrim`
- `TrueBody`

## CommentNode

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateAst.cs` |

### 属性
- `Content`
- `LeftTrim`
- `RightTrim`

## DefineNode

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateAst.cs` |

### 属性
- `Body`
- `LeftTrim`
- `Name`
- `RightTrim`

## ElseIfBranch

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateAst.cs` |

### 属性
- `Body`
- `Condition`
- `TrimMarker`

## HelmTemplateRenderer

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/HelmTemplateRenderer.cs` |

### 方法
- `AsConfig(...)`
- `AsSecrets(...)`
- `Glob(...)`
- `Render(...)`
- `RenderNotes(...)`

## TemplateDocumentNode

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateAst.cs` |

### 属性
- `Children`

### 方法
- `SerializeToText(...)`

## TemplateNode

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateAst.cs` |

### 属性
- `EndLine`
- `EndOffset`
- `StartLine`
- `StartOffset`

## TemplateParseException

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateParseException.cs` |

### 属性
- `Column`
- `Line`
- `Offset`

## TemplateParser

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateParser.cs` |

### 方法
- `Parse(...)`

## TemplateTokenizer

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateTokenizer.cs` |

### 方法
- `TokenizeFlat(...)`

## TextNode

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateAst.cs` |

### 属性
- `Content`

## Token

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateTokenizer.cs` |

### 属性
- `Column`
- `Kind`
- `LeftTrim`
- `Line`
- `Offset`
- `RightTrim`
- `Value`

### 方法
- `ToString(...)`

## TokenKind

| 字段 | 值 |
| --- | --- |
| 类型类别 | `enum` |
| 源文件 | `src/HelmSharp.Engine/TemplateTokenizer.cs` |

## UnsupportedTemplateFeatureException

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/UnsupportedTemplateFeatureException.cs` |
