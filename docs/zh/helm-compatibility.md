# Helm 兼容性

HelmSharp 不是命令行模拟器。它要解决的是：.NET 应用在进程内渲染 chart、管理 release 时，需要哪些可观测的 Helm 行为。

Helm CLI 只作为测试基准使用，消费者运行时不需要安装 Helm。

## 当前可信度

当前真实 Chart golden suite 已经在 5 个公共 Chart 上完成 **129/129 个模板**渲染，没有解析器异常：

| Chart | 版本 | 模板数 | 结果 |
| --- | --- | --- | --- |
| podinfo | 6.14.0 | 21/21 | 通过 |
| metrics-server | 3.13.1 | 18/18 | 通过 |
| external-dns | 1.21.1 | 7/7 | 通过 |
| ingress-nginx | 4.12.1 | 42/42 | 通过 |
| cert-manager | 1.17.1 | 41/41 | 通过 |
| **总计** | - | **129/129** | **通过** |

这个数字重要，是因为真实 Chart 会暴露 helper template、嵌套 values、`.Files`、capabilities 和格式细节，手写小 fixture 很难覆盖这些情况。

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
| 从目录和 `.tgz` 加载 Chart | 已支持 | 可以放心从渲染和打包工具开始使用。 |
| values 文件和 `--set` 风格覆盖 | 部分支持 | 常见流程可用；部分类型转换边界仍在补齐。 |
| Helm 风格模板渲染 | 已支持 | 真实公共 Chart 已达到 1.1.0 Pass 基线；后续差异继续由 golden tests 跟踪。 |
| Chart 打包和仓库 | 部分支持 | 已有可用 API；归档和仓库边界仍需扩展。 |
| 安装、升级、回滚、卸载 | 部分支持 | 试运行和托管流程存在；完整生命周期对齐仍在推进。 |
| Kubernetes 提交、删除、等待 | 部分支持 | 常见资源操作可用；少见就绪行为需要更多覆盖。 |
| Kubernetes Secrets 发布历史 | 已支持 | 不依赖 Helm CLI 也能保存发布记录。 |
| OCI registry 和来源证明 | 计划中 | API 或方向存在，但还不是完整生产对齐。 |

## 已知边界

如果生产流程依赖以下行为，接入前应先验证：

- 完整 Sprig 函数对齐；
- 少见 values 类型转换和 list 语法；
- 逐字节清单格式；
- OCI 认证和 registry 流程；
- 来源证明校验；
- 少见 Kubernetes 资源类型的就绪判断；
- Helm plugin 的安全替代模型。

## 报告兼容性差异

请提交包含以下信息的兼容性 issue：

- Helm CLI 和 HelmSharp 版本；
- 最小 Chart 和 values 输入；
- 精确 Helm 命令和输出；
- 等价 HelmSharp API 调用和输出；
- 差异是否影响渲染、发布状态或集群变更。

小而可复现的 Chart 比截图或大型私有 Chart 更有价值。
