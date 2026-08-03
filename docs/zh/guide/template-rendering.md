# 按目标集群渲染

Chart 常会根据 `.Capabilities.KubeVersion` 或 `.Capabilities.APIVersions` 走不同分支。预览应描述目标集群，而不是恰好运行预览服务的机器。

## 显式提供 capabilities

```csharp
var renderer = new HelmTemplateRenderer(
    chart,
    releaseName: "preview",
    releaseNamespace: "platform",
    values: values,
    kubeVersion: "1.30.0",
    apiVersions:
    [
        "monitoring.coreos.com/v1",
        "policy/v1"
    ],
    isUpgrade: false);

var manifests = renderer.Render();
var notes = renderer.RenderNotes();
```

`kubeVersion` 决定模板中 `.Capabilities.KubeVersion` 的值；`apiVersions` 会将目标集群存在的 API（包括 CRD）加入 `.Capabilities.APIVersions`。需要让模板看到 `.Release.IsUpgrade` 而非 `.Release.IsInstall` 时，设置 `isUpgrade`。

## 渲染器提供给模板的对象

| 模板对象 | 来源 |
| --- | --- |
| `.Values` | `HelmValues.BuildAsync` 返回的字典。 |
| `.Chart` | `Chart.yaml` 元数据与 Chart 依赖。 |
| `.Release` | 构造渲染器时传入的 release 名称、命名空间、revision、服务和安装/升级状态。 |
| `.Capabilities` | Kubernetes 版本与已知 API 版本。 |
| `.Files` | Chart 中打包的非模板文件。 |
| `.Template` | 当前模板名称和基础路径。 |

命名模板、`include`、`tpl`、`required`、`.Files` 以及已实现的 Helm/Sprig 函数都在进程内执行。单个 helper 是否可用，应以[函数矩阵](../template-function-compatibility.md)为准；不能因为别的 Helm 环境支持它，就假定这里也支持。

## 清单与 notes 分开处理

`NOTES.txt` 是 release 后给人的信息，因此 `RenderNotes()` 会单独返回它，而不是混入 Kubernetes YAML。可以将它与预览一起保存或显示，但绝不能把它交给清单提交器。

高层的 `HelmTemplateRequest` 在客户端边界提供 `KubeVersion`、`ApiVersions` 和 `IsUpgrade`。需要 notes 时应单独渲染；模板输出不会包含 Chart `crds/` 目录中的 CRD。需要变更集群时，请继续阅读[安装和升级 Release](release-workflows.md)。
