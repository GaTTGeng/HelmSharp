# Helm 兼容性

HelmSharp 不是命令行模拟器。它要解决的是：.NET 应用在进程内渲染 chart、管理 release 时，需要哪些可观测的 Helm 行为。

Helm CLI 只作为测试基准使用，消费者运行时不需要安装 Helm。

## 当前可信度

当前真实 chart golden suite 已经在 5 个公共 chart 上完成 **129/129 个模板**渲染，没有 parser exception：

| Chart | Version | Templates | Result |
| --- | --- | --- | --- |
| podinfo | 6.14.0 | 21/21 | Pass |
| metrics-server | 3.13.1 | 18/18 | Pass |
| external-dns | 1.21.1 | 7/7 | Pass |
| ingress-nginx | 4.12.1 | 42/42 | Pass |
| cert-manager | 1.17.1 | 41/41 | Pass |
| **Total** | - | **129/129** | **Pass** |

这个数字重要，是因为真实 chart 会暴露 helper template、嵌套 values、`.Files`、capabilities 和格式细节，手写小 fixture 很难覆盖这些情况。

## 兼容性契约

一个行为会被视为已支持，需要满足：

- 可以通过文档化的托管 API 使用；
- 有聚焦的自动化测试覆盖；
- 在 `net8.0`、`net9.0`、`net10.0` 上表现一致；
- 对用户可见的渲染输出、release 状态或失败行为与 Helm 语义一致。

CLI 颜色、进度文案、终端格式和插件执行不是目标，除非它们影响 chart 输出或自动化流程。

## 能力快照

| 区域 | 当前级别 | 对用户意味着什么 |
| --- | --- | --- |
| 从目录和 `.tgz` 加载 chart | Supported | 可以放心从渲染和打包工具开始使用。 |
| values 文件和 `--set` 风格覆盖 | Partial | 常见流程可用；部分类型转换边界仍在补齐。 |
| Helm 风格模板渲染 | Partial | 真实公共 chart 已可渲染；剩余差异由 golden tests 跟踪。 |
| chart 打包和 repository | Partial | 已有可用 API；归档和 repository 边界仍需扩展。 |
| install、upgrade、rollback、uninstall | Partial | dry-run 和托管流程存在；完整生命周期 parity 仍在推进。 |
| Kubernetes apply、delete、wait | Partial | 常见资源操作可用；少见 readiness 行为需要更多覆盖。 |
| Kubernetes Secrets release history | Supported | 不依赖 Helm CLI 也能保存 release 记录。 |
| OCI registry 和 provenance | Planned | API 或方向存在，但还不是完整生产 parity。 |

## 已知边界

如果生产流程依赖以下行为，接入前应先验证：

- 完整 Sprig 函数 parity；
- 少见 values 类型转换和 list 语法；
- byte-for-byte manifest 格式；
- OCI 认证和 registry 流程；
- provenance 校验；
- 少见 Kubernetes resource kind 的 readiness；
- Helm plugin 的安全替代模型。

## 报告兼容性差异

请提交包含以下信息的 compatibility issue：

- Helm CLI 和 HelmSharp 版本；
- 最小 chart 和 values 输入；
- 精确 Helm 命令和输出；
- 等价 HelmSharp API 调用和输出；
- 差异是否影响渲染、release 状态或集群变更。

小而可复现的 chart 比截图或大型私有 chart 更有价值。
