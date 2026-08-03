# HelmSharp.Action

`HelmSharp.Action` 是面向应用的 Helm 风格操作包。API、worker、operator 或 CLI 只要除了清单文本之外还拥有其他职责，就应从它开始。

```powershell
dotnet add package HelmSharp.Action --version 1.3.1
```

它会带入 chart、渲染、Kubernetes、release、仓库、registry、存储和 post-renderer 层。应用只渲染 YAML 时，应使用 `HelmSharp.Chart` 与 `HelmSharp.Engine`。

## 入口

`HelmClient` 实现 `IHelmClient`，并接收 `IHelmOptionsProvider`。将产品默认值——命名空间、field manager 和超时——放在这个 provider 中。`TemplateAsync` 从 `HelmTemplateRequest.KubeVersion` 和 `ApiVersions` 获取目标 capabilities，因此每个渲染请求都要设置这些字段。方法返回 `CommandResult`，调用方无需把异常再解析成命令行形态，就能处理输出与错误。

| 操作 | 请求或方法 | 先阅读 |
| --- | --- | --- |
| 渲染 Chart | `TemplateAsync(HelmTemplateRequest)` | [渲染 Chart](../guide/first-render.md) |
| 安装或升级 | `UpgradeInstallAsync(HelmUpgradeInstallRequest)` | [发布工作流](../guide/release-workflows.md) |
| 回滚或卸载 | `RollbackAsync`、`UninstallAsync` | [发布工作流](../guide/release-workflows.md) |
| 检查已存储 release | `StatusAsync`、`HistoryAsync`、`GetManifestAsync`、`GetValuesAsync` | [发布工作流](../guide/release-workflows.md) |
| 打包、生成索引、拉取或解析依赖 | `PackageAsync`、`DependencyBuildAsync` 等请求对象方法 | [Chart 交付](../guide/chart-distribution.md) |

检查读取的是已存储 revision，不会重新渲染今天版本的 Chart。revision `0` 选择最新存储记录，包括保留卸载记录。`ListReleasesAsync` 列出已部署 revision，并支持逗号分隔、完全匹配的 `key=value` 标签选择器。

## 重要的生命周期约束

评审时使用试运行。安装、升级、回滚和保留历史的卸载会留下供后续检查的 Secret 生命周期记录；默认卸载会清除历史。成功升级和回滚会 supersede 之前的已部署 revision。只有在构造生命周期记录并启动持久化处理后发生的失败才会保留失败 revision；验证、Chart 加载或渲染、客户端或历史初始化以及命名空间创建可能更早失败，因而不会留下 revision。未实现的选项会在变更集群前失败，不会被悄悄忽略。

传统 HTTP 仓库和本地依赖已支持。完整 OCI 认证、provenance 验证和全部 Helm CLI 开关并不是 `1.3.1` 的保证；当前边界请看[兼容性](../helm-compatibility.md)。

成员级信息请看[生成的 Action API](../api/generated/action.md)。
