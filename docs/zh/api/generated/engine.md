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

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

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

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## CommentNode

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateAst.cs` |

### 属性
- `Content`
- `LeftTrim`
- `RightTrim`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

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

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## ElseIfBranch

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateAst.cs` |

### 属性
- `Body`
- `Condition`
- `TrimMarker`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

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

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## TemplateDocumentNode

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateAst.cs` |

### 属性
- `Children`

### 方法
- `SerializeToText(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

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

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## TemplateParseException

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateParseException.cs` |

### 属性
- `Column`
- `Line`
- `Offset`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## TemplateParser

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateParser.cs` |

### 方法
- `Parse(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## TemplateTokenizer

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateTokenizer.cs` |

### 方法
- `TokenizeFlat(...)`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## TextNode

| 字段 | 值 |
| --- | --- |
| 类型类别 | `class` |
| 源文件 | `src/HelmSharp.Engine/TemplateAst.cs` |

### 属性
- `Content`

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

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

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。

## TokenKind

| 字段 | 值 |
| --- | --- |
| 类型类别 | `enum` |
| 源文件 | `src/HelmSharp.Engine/TemplateTokenizer.cs` |

### 使用提示
先查看对应包页面的场景示例，再使用此成员索引定位具体类型。
