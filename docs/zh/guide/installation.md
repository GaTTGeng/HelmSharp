# 安装 HelmSharp

HelmSharp 支持 `net8.0`、`net9.0` 和 `net10.0`。同一个应用中的 HelmSharp 包应保持同一版本；下面以当前发布版 `1.3.1` 为例。

## 按任务选择最小包

| 你需要… | 安装 | 它负责什么 |
| --- | --- | --- |
| 加载 Chart、合并 values、渲染 YAML | `HelmSharp.Chart` 与 `HelmSharp.Engine` | Chart 文件、values 和模板渲染。 |
| 执行 template、安装、升级、回滚或历史查询 | `HelmSharp.Action` | 高层客户端及其依赖包。 |
| 提交已经渲染好的 YAML | `HelmSharp.Kube` | Kubernetes 提交、删除、资源标识和就绪等待。 |
| 搜索、拉取或生成传统 HTTP Chart 仓库索引 | `HelmSharp.Repo` | 仓库配置、缓存、搜索和拉取。 |

多数拥有部署职责的服务从 `HelmSharp.Action` 开始：

```powershell
dotnet add package HelmSharp.Action --version 1.3.1
```

预览工具则应只依赖渲染所需的两个包：

```powershell
dotnet add package HelmSharp.Chart --version 1.3.1
dotnet add package HelmSharp.Engine --version 1.3.1
```

`HelmSharp.Action` 已引用渲染、Kubernetes、release、仓库、存储、registry 和 post-renderer 包。除非代码确实需要低层 API，否则无需再逐一安装它们。

## 运行时需要什么

渲染只需要可读取的 Chart 目录或 `.tgz` 归档。它不会调用、打包或要求安装 `helm` 可执行文件。

会变更集群的操作则和任何 Kubernetes .NET 客户端一样，需要可访问的 API Server、凭据，以及对目标资源和 release Secret 的 RBAC 权限。把命名空间、field manager、Kubernetes 版本和超时等默认值收敛到应用自己的 `IHelmOptionsProvider`；不要让一次 Web 请求任意指定本地 kubeconfig 路径。

## 验证安装

按[渲染 Chart](first-render.md)运行一个从本地 Chart 生成清单的程序。如果应用还要部署，请继续阅读[安装和升级 Release](release-workflows.md)，为操作加上试运行与结果处理。

## 通常只作为扩展点的包

| 包 | 仅在以下场景直接使用 |
| --- | --- |
| `HelmSharp.Release` | 需要脱离 `HelmClient` 单独使用 release 模型或存储。 |
| `HelmSharp.Storage` | 要实现自定义 `IHelmReleaseStore`。 |
| `HelmSharp.PostRenderer` | 要实现确定性的清单转换。 |
| `HelmSharp.Registry` | 要接入试验性的 OCI registry 客户端。 |

这些契约和当前限制请看对应的[包参考](../packages/action.md)。
